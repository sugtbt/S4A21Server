using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Raid;
using PvfLib;

namespace DfoServer.Network.Handlers;

public sealed partial class RaidHandler
{


	private readonly ICharacterRepository _characterRepository;

	private readonly ISessionDirectory _sessions;

	private readonly RaidManager _raids;

	private readonly ConcurrentDictionary<Guid, byte> _objectSent = new ConcurrentDictionary<Guid, byte>();

	private readonly ConcurrentDictionary<string, int> _timerVersions = new ConcurrentDictionary<string, int>();

	private readonly ConcurrentDictionary<(uint RaidId, uint SymbolId), uint> _symbolValues = new ConcurrentDictionary<(uint, uint), uint>();

	private readonly ConcurrentDictionary<uint, uint> _infectionDungeonByRaid = new ConcurrentDictionary<uint, uint>();

	private readonly ConcurrentDictionary<uint, byte> _blackVolcanoBarrierBroken = new ConcurrentDictionary<uint, byte>();

	private readonly ConcurrentDictionary<uint, object> _raidRuntimeLocks = new ConcurrentDictionary<uint, object>();

	private readonly ConcurrentDictionary<uint, PhaseRewardFlow> _phaseRewardFlows = new ConcurrentDictionary<uint, PhaseRewardFlow>();

	private readonly ConcurrentDictionary<(uint RaidId, byte BuffType), AntonRaidBuffActivation> _raidBuffActivations = new ConcurrentDictionary<(uint, byte), AntonRaidBuffActivation>();

	private readonly ConcurrentDictionary<(uint RaidId, ushort SituationIndex, uint SoloMemberKey, uint DungeonId), uint[]> _raidMonsterRuntimeValues = new ConcurrentDictionary<(uint, ushort, uint, uint), uint[]>();

	public RaidHandler(
		ICharacterRepository characterRepository,
		ISessionDirectory sessions,
		RaidManager raids)
	{
		_characterRepository = characterRepository
			?? throw new ArgumentNullException(nameof(characterRepository));
		_sessions = sessions
			?? throw new ArgumentNullException(nameof(sessions));
		_raids = raids ?? throw new ArgumentNullException(nameof(raids));
	}

	private static void RunInBackground(Task task, string operation)
	{
		_ = ObserveBackgroundTaskAsync(task, operation);
	}

	private static async Task ObserveBackgroundTaskAsync(
		Task task,
		string operation)
	{
		try
		{
			await task;
		}
		catch (Exception ex)
		{
			FileLogger.Log(
				$"[GameProtocol] RAID_BACKGROUND_TASK " +
				$"operation={operation} error={ex}");
		}
	}

	private static bool IsRaidSession(EnhancedClientSession session)
	{
		return session != null && GameNetworkConfig.IsRaidListener(session.ListenerPort);
	}

	public async Task HandleCreateRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !TryBuildMember(session, out var member) || !TryReadTitle(body, out var titleBytes))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		if (titleBytes.Length == 0)
		{
			titleBytes = BuildDefaultTitle(member.CharacterId);
		}
		RaidSnapshot raid = _raids.Create(titleBytes, member);
		await SendRaidObjectAsync(session, raid);
		_objectSent[session.SessionId] = 0;
		await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, header.type, RaidPacketBuilder.BuildCreateAck(raid.RaidId)));
		int channelId = GameNetworkConfig.ResolveGameChannel(session.ListenerPort).ChannelId;
		FileLogger.Log($"[GameProtocol] CREATE_RAID channel={channelId} cid={member.CharacterId} user={member.UserId} raid={raid.RaidId} title={BitConverter.ToString(raid.TitleBytes)}");
	}

	internal static bool ShouldRejectRaidWaitingListRequest(uint raidState, byte stage)
	{
		// Stage 0 is also the attack status-window refresh request.
		return false;
	}


	public async Task HandleLeaveRaid(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		RaidLeaveResult result = _raids.Leave(userId);
		await SendAckAsync(session, header.type, result.Ok);
		if (!result.Ok)
		{
			return;
		}
		_objectSent.TryRemove(session.SessionId, out var _);
		byte[] removePacket = GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidRemove(result.RaidId));
		await session.SendPacketAsync(removePacket);
		if (!result.Disbanded)
		{
			await BroadcastRaidMembersAsync(result.RemainingRaid);
		}
		else
		{
			CleanupRaidRuntimeState(result.RaidId);
			IEnumerable<int> others = from m in result.PreviousRaid.Members
				where m.UserId != userId
				select checked((int)m.CharacterId);
			await _sessions.BroadcastToAsync(others, removePacket);
		}
		FileLogger.Log($"[GameProtocol] LEAVE_RAID user={userId} raid={result.RaidId} disbanded={result.Disbanded}");
	}


	public async Task HandleDungeonAbortedAsync(EnhancedClientSession session, int dungeonId, string reason)
	{
		if (IsAntonRaidDungeon(dungeonId) && TryResolveUserId(session, out var userId) && _raids.TryAbandonDungeon(userId, (uint)dungeonId, out var raid, out var memberKeys))
		{
			ResetRaidMonsterRuntimeValues(raid, userId, (uint)dungeonId);
			await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_DUNGEON_PARTICIPATION_INFO, RaidPacketBuilder.BuildRaidDungeonParticipationInfo((uint)dungeonId, 0u, memberKeys));
			await BroadcastRaidMonsterStatusAsync(raid);
			FileLogger.Log($"[GameProtocol] RAID_DUNGEON_ABORT raid={raid.RaidId} dungeon={dungeonId} reason={reason} memberKeys={string.Join(",", memberKeys)}");
		}
	}

	public async Task HandleRaidDoBehavior(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId = 0;
		RaidSnapshot raid = null;
		bool ok = IsRaidDoBehaviorRequest(body) && TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid) && raid.State == 2;
		await SendAckAsync(session, header.type, ok);
		if (!ok)
		{
			FileLogger.Log("[GameProtocol] RAID_DO_BEHAVIOR rejected body=" + BitConverter.ToString(body ?? Array.Empty<byte>()));
			return;
		}
		await BroadcastRaidNotificationAsync(raid, NotiPacketType.RAID_DO_BEHAVIOR, body);
		FileLogger.Log($"[GameProtocol] RAID_DO_BEHAVIOR relayed raid={raid.RaidId} user={userId} target={BitConverter.ToUInt32(body, 0)} behavior={BitConverter.ToUInt32(body, 4)}");
	}

	internal static bool IsRaidDoBehaviorRequest(byte[] body)
	{
		return body != null && body.Length == 8;
	}

	public async Task HandleRaidSetSymbol(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		ushort userId = 0;
		RaidSnapshot raid = null;
		uint symbolId;
		uint operand;
		byte operation;
		bool ok = TryReadRaidSetSymbolRequest(body, out symbolId, out operand, out operation) && TryResolveUserId(session, out userId) && _raids.TryGetByUser(userId, out raid) && raid.State == 2 && raid.PhaseIndex == 1 && symbolId == 110 && _symbolValues.ContainsKey((raid.RaidId, symbolId));
		await SendAckAsync(session, header.type, ok);
		if (!ok)
		{
			FileLogger.Log("[GameProtocol] RAID_SET_SYMBOL rejected body=" + BitConverter.ToString(body ?? Array.Empty<byte>()));
			return;
		}
		await ChangeBlackVolcanoBarrierAsync(raid, operand, operation, "pvf-symbol-request");
		FileLogger.Log($"[GameProtocol] RAID_SET_SYMBOL applied raid={raid.RaidId} user={userId} symbol={symbolId} operation={operation} operand={operand}");
	}

	internal static bool TryReadRaidSetSymbolRequest(byte[] body, out uint symbolId, out uint operand, out byte operation)
	{
		symbolId = 0u;
		operand = 0u;
		operation = byte.MaxValue;
		if (body == null || body.Length != 9)
		{
			return false;
		}
		symbolId = BitConverter.ToUInt32(body, 0);
		operand = BitConverter.ToUInt32(body, 4);
		operation = body[8];
		return operation <= 2;
	}

	internal static bool TryApplyRaidSymbolOperation(uint currentValue, uint operand, byte operation, out uint nextValue)
	{
		switch (operation)
		{
		case 0:
			nextValue = operand;
			return true;
		case 1:
			nextValue = ((operand > (uint)(-1 - (int)currentValue)) ? uint.MaxValue : (currentValue + operand));
			return true;
		case 2:
			nextValue = ((operand < currentValue) ? (currentValue - operand) : 0u);
			return true;
		default:
			nextValue = currentValue;
			return false;
		}
	}

	public async Task HandleRaidManagerWork(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (body == null || body.Length < 12 || !TryResolveUserId(session, out var actingUserId))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		FileLogger.Log($"[GameProtocol] RAID_MANAGER_WORK_RAW user={actingUserId} body={BitConverter.ToString(body)}");
		uint op = BitConverter.ToUInt32(body, 0);
		ushort targetActorId = BitConverter.ToUInt16(body, 4);
		uint partyIndex = BitConverter.ToUInt32(body, 8);
		RaidSnapshot raid = null;
		bool ok = op == 0
			&& _raids.TryGetByUser(actingUserId, out var currentRaid)
			&& _raids.TryAssignParty(actingUserId, targetActorId, partyIndex, out raid);
		await SendAckAsync(session, header.type, ok);
		if (ok)
		{
			await BroadcastRaidObjectAsync(raid);
			await BroadcastRaidMembersAsync(raid);
			await BroadcastRaidMonsterStatusAsync(raid);
			FileLogger.Log($"[GameProtocol] RAID_MANAGER_WORK raid={raid.RaidId} user={actingUserId} actor={targetActorId} partyIndex={partyIndex}");
		}
	}

	public async Task<bool> HandleNormalPartyLeftAsync(ushort userId)
	{
		if (!_raids.TryGetByUser(userId, out var currentRaid))
			return false;

		var member = currentRaid.Members.FirstOrDefault(entry => entry.UserId == userId);
		if (member == null || member.PartyIndex == 0
			|| !_raids.TryAssignParty(userId, userId, 0, out var updatedRaid))
			return false;

		await BroadcastRaidObjectAsync(updatedRaid);
		await BroadcastRaidMembersAsync(updatedRaid);
		await BroadcastRaidMonsterStatusAsync(updatedRaid);
		FileLogger.Log($"[GameProtocol] RAID_PARTY_UNASSIGN raid={updatedRaid.RaidId} user={userId}");
		return true;
	}

	public async Task HandleModifyRaidInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!TryResolveUserId(session, out var userId) || !TryReadTitle(body, out var titleBytes) || !_raids.TryUpdateTitle(userId, titleBytes, out var raid))
		{
			await SendAckAsync(session, header.type, success: false);
			return;
		}
		await SendAckAsync(session, header.type, success: true);
		await BroadcastRaidInfoAsync(raid);
		FileLogger.Log($"[GameProtocol] MODIFY_RAID_INFO raid={raid.RaidId} user={userId} title={BitConverter.ToString(titleBytes)}");
	}

	public Task<bool> TryHandleCreatePopupClose(EnhancedClientSession session, GamePacketHeader header, byte[] body)
	{
		if (!IsRaidSession(session) || !IsCreatePopupCloseBody(body))
		{
			return Task.FromResult(result: false);
		}
		FileLogger.Log($"[GameProtocol] CLOSE_RAID_CREATE_POPUP session={session.SessionId} body={BitConverter.ToString(body)}");
		return Task.FromResult(result: true);
	}

	public static bool IsCreatePopupCloseBody(byte[] body)
	{
		return body != null && body.Length == 3 && body[0] == 1 && BitConverter.ToUInt16(body, 1) == 665;
	}

	public void ClearSession(Guid sessionId)
	{
		_objectSent.TryRemove(sessionId, out var _);
		RaidLeaveResult result = _raids.OnSessionDisconnected(sessionId);
		if (result.Disbanded)
		{
			CleanupRaidRuntimeState(result.RaidId);
		}
	}

	private Task SendRaidObjectAsync(EnhancedClientSession session, RaidSnapshot raid)
	{
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidCreate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader), members)));
	}

	private Task SendRaidStateValueAsync(EnhancedClientSession session, uint state, uint stateArgument)
	{
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0, 588, RaidPacketBuilder.BuildRaidState(state, stateArgument)));
	}

	private Task BroadcastRaidObjectAsync(RaidSnapshot raid)
	{
		IReadOnlyList<RaidMemberSnapshot> members = ToPacketMembers(raid);
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidModify(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader), members));
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private Task BroadcastRaidStateAsync(RaidSnapshot raid)
	{
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, 588, RaidPacketBuilder.BuildRaidState(raid.State, raid.StateArgument));
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private Task BroadcastRaidNotificationAsync(RaidSnapshot raid, NotiPacketType type, byte[] body)
	{
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, (ushort)type, body);
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private Task BroadcastRaidInfoAsync(RaidSnapshot raid)
	{
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, 592, RaidPacketBuilder.BuildRaidInfoUpdate(raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, ToPacketMember(raid.Leader)));
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private Task BroadcastRaidMembersAsync(RaidSnapshot raid)
	{
		byte[] packet = GamePacketEnvelopeBuilder.Build(0, 593, RaidPacketBuilder.BuildWaitingList(ToPacketMembers(raid)));
		return _sessions.BroadcastToAsync(ToCharacterIds(raid), packet);
	}

	private static Task SendAckAsync(EnhancedClientSession session, ushort type, bool success)
	{
		return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(1, type, new byte[1] { (byte)(success ? 1 : 0) }));
	}

	private bool TryBuildMember(EnhancedClientSession session, out RaidMember member)
	{
		member = null;
		int characterId = SessionOwnerResolver.Resolve(session).characterId;
		if (characterId <= 0 || characterId > 65535)
		{
			return false;
		}
		ushort userId = ((session.Player != null && session.Player.UserId != 0) ? session.Player.UserId : ((ushort)characterId));
		CharacterRecord record = _characterRepository.GetById(characterId);
		member = new RaidMember
		{
			UserId = userId,
			CharacterId = (uint)characterId,
			SessionId = session.SessionId,
			NameBytes = (record?.Name ?? session.Player?.Name ?? Array.Empty<byte>()),
			Job = record?.Job ?? session.Player?.Job ?? 0,
			GrowType = record?.GrowType ?? session.Player?.GrowType ?? 0
		};
		return true;
	}

	private static bool TryResolveUserId(EnhancedClientSession session, out ushort userId)
	{
		if (session.Player != null && session.Player.UserId != 0)
		{
			userId = session.Player.UserId;
			return true;
		}
		int characterId = SessionOwnerResolver.Resolve(session).characterId;
		if (characterId > 0 && characterId <= 65535)
		{
			userId = (ushort)characterId;
			return true;
		}
		userId = 0;
		return false;
	}

	private static bool TryReadTitle(byte[] body, out byte[] title)
	{
		title = Array.Empty<byte>();
		if (body == null || body.Length < 8)
		{
			return false;
		}
		int length = BitConverter.ToInt32(body, 4);
		if (length < 0 || length > body.Length - 8)
		{
			return false;
		}
		title = new byte[length];
		Buffer.BlockCopy(body, 8, title, 0, length);
		return true;
	}

	private static byte[] BuildDefaultTitle(uint characterId)
	{
		return ClientTextEncoding.GetBytes($"Raid: {characterId}");
	}


	private static RaidMemberSnapshot ToPacketMember(RaidMember member)
	{
		return new RaidMemberSnapshot
		{
			UserId = member.UserId,
			CharacterId = member.CharacterId,
			NameBytes = member.NameBytes,
			Job = member.Job,
			GrowType = member.GrowType,
			PartyIndex = member.PartyIndex
		};
	}

	private static IReadOnlyList<RaidMemberSnapshot> ToPacketMembers(RaidSnapshot raid)
	{
		List<RaidMemberSnapshot> result = new List<RaidMemberSnapshot>(raid.Members.Count);
		foreach (RaidMember member in raid.Members)
		{
			result.Add(ToPacketMember(member));
		}
		return result;
	}

	private static IEnumerable<int> ToCharacterIds(RaidSnapshot raid)
	{
		foreach (RaidMember member in raid.Members)
		{
			yield return checked((int)member.CharacterId);
		}
	}
}
