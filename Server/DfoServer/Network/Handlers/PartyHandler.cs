using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Friends;
using DfoServer.Game.Party;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Party;
using DfoServer.Network.Parsers.Party;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // 组队 wire 层(Phase A: 本地三包 SET_PARTY_INFO 0x0C / LEAVE_PARTY 0x0D / WALKOUT 0x0E)。
    // 状态走 PartyManager(格式无关), 队伍窗口靠 PARTY_INFO(Noti 0x09) 重发整份名册刷新(df 新建/更新即如此)。
    // ⚠️ Phase A 只向请求者本会话下发 PARTY_INFO(单人队够用); 多人广播需 UserId→session 注册表, 留待 Phase B。
    // ⚠️ 响应帧照 df 逆向(PARTY_INFO 无活体样本), 需真机验证客户端渲染(参 compound #432 教训)。
    public sealed class PartyHandler : IDisposable
    {
        private readonly PartyManager _partyManager;
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly Game.Session.ISessionDirectory _sessions;   // 跨会话定位(邀请/广播); 单人/自测时可为 null。采用上游会话注册表(按 charId 查, 抗重连)
        private readonly PartyUdpRelay _udpRelay;
        private byte[] _cachedRelayIpBytes;
        private readonly Func<EnhancedClientSession, Task<bool>>
            _announceTownArrival;
        private readonly Func<EnhancedClientSession, Task<bool>>
            _announceTownArrivalWithinTransition;
        private readonly Game.Session.CharacterTransitionCoordinator
            _characterTransitions;
        private PvpRoomHandler _pvpRoomHandler;
        private readonly object _broadcastGatesLock = new object();
        private readonly Dictionary<int, BroadcastGateEntry> _broadcastGates =
            new Dictionary<int, BroadcastGateEntry>();
        private readonly SemaphoreSlim _partyListPublishGate =
            new SemaphoreSlim(1, 1);
        private readonly Dictionary<Guid, HashSet<int>>
            _publishedTownPartyIds =
                new Dictionary<Guid, HashSet<int>>();
        internal Func<int, int, Task>
            GenerationPublishAfterCurrentCheckAsync { get; set; }
        private const string ProtocolName = "GameProtocol";

        public PartyHandler(PartyManager partyManager, ICharacterRepository characterRepository,
            Game.Session.ISessionDirectory sessions = null,
            PartyUdpRelay udpRelay = null,
            Func<EnhancedClientSession, Task<bool>>
                announceTownArrival = null,
            Game.Session.CharacterTransitionCoordinator
                characterTransitions = null,
            Func<EnhancedClientSession, Task<bool>>
                announceTownArrivalWithinTransition = null,
            IGameDatabase database = null)
        {
            _partyManager = partyManager;
            _characterRepository = characterRepository;
            database ??= GameDatabase.CreateDefault();
            _subtype0Repository = new SqliteSubtype0FieldsRepository(database);
            _honorLevel = new HonorLevelSyncService(characterRepository, database);
            _sessions = sessions;
            _udpRelay = udpRelay;
            _announceTownArrival = announceTownArrival;
            _characterTransitions = characterTransitions;
            _announceTownArrivalWithinTransition =
                announceTownArrivalWithinTransition;
            // 断线清理走会话目录的生命周期事件: 断线者自动退队, 剩余成员收到名册刷新。
            // 不订阅就会产生幽灵队员(断线者永久留在名册里)。事件在 session 从目录移除前触发,
            // 只向【剩余成员】发包, 绝不向垂死会话本身发(其 socket 已在关闭流程中)。
            if (_sessions != null)
                _sessions.SessionEnding += OnSessionEndingAsync;
        }

        internal void AttachPvpRoomHandler(
            PvpRoomHandler pvpRoomHandler)
        {
            _pvpRoomHandler =
                pvpRoomHandler
                ?? throw new ArgumentNullException(
                    nameof(pvpRoomHandler));
        }

        public async Task Handle_SET_UDP_IP_PORT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Player == null)
                return;
            if (!SetUdpEndpointRequest.TryParse(
                    body,
                    out var request,
                    out var failure))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SET_UDP_IP_PORT rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"reason={FormatUdpParseFailure(failure)} " +
                    $"bodyLength={body?.Length ?? -1}");
                return;
            }

            session.Player.UpdateReportedUdpEndpoint(
                request.NatType,
                request.InnerIpv4,
                request.OuterIpv4,
                request.Port,
                request.Mtu);
            var ownerKey = (ushort)session.Player.CharacterId;
            if (_partyManager.TryUpdateMemberP2pPort(
                    ownerKey,
                    session.SessionId,
                    request.Port,
                    out var party))
            {
                // PartyUdpRelay keeps the existing room when membership is
                // unchanged, so this refreshes the self endpoint without
                // rotating any tested relay ports.
                await BroadcastPartyInfo(party);
            }
            FileLogger.Log(
                $"[{ProtocolName}] SET_UDP_IP_PORT registered: " +
                $"cid={session.Player.CharacterId} " +
                $"endpoint={request.InnerIpv4}:{request.Port} " +
                $"outer={request.OuterIpv4} nat={request.NatType} " +
                $"mtu={request.Mtu} bodyLength={body.Length}");
        }

        private static string FormatUdpParseFailure(
            SetUdpEndpointParseFailure failure)
        {
            return failure switch
            {
                SetUdpEndpointParseFailure.NullBody => "null",
                SetUdpEndpointParseFailure.ShortBody => "short",
                SetUdpEndpointParseFailure.InnerIpv4Class =>
                    "inner-ip-class",
                SetUdpEndpointParseFailure.OuterIpv4Class =>
                    "outer-ip-class",
                SetUdpEndpointParseFailure.ZeroPort => "zero-port",
                SetUdpEndpointParseFailure.MtuRange => "mtu-range",
                _ => "unknown",
            };
        }

        private async Task OnSessionEndingAsync(int characterId, EnhancedClientSession dying)
        {
            await _partyListPublishGate.WaitAsync();
            try
            {
                _publishedTownPartyIds.Remove(dying.SessionId);
            }
            finally
            {
                _partyListPublishGate.Release();
            }

            var uid = (ushort)characterId;
            var result = _partyManager.OnSessionDisconnected(
                uid, dying.SessionId);
            if (!result.Ok)
                return;   // 不在任何队伍, 无事可做

            FileLogger.Log($"[{ProtocolName}] PARTY disconnect cleanup: uid={uid} disbanded={result.Disbanded} leaderChanged={result.LeaderChanged} newLeader={result.NewLeaderUserId} remaining={result.RemainingMembers.Count}");
            await PublishCommittedDepartureAsync(
                result,
                $"disconnect uid={uid}");
            if (result.LeaderChanged &&
                !result.Disbanded &&
                result.Party != null)
            {
                await PublishPartyHostProjectionAsync(
                    result.Party.PartyId,
                    result.NewLeaderUserId,
                    includeRealtime: false,
                    reason: $"disconnect-successor uid={uid}");
            }
        }

        // 建队/入队时若目标玩家原本在别的队伍, 原队剩余成员需要收到名册刷新(否则那边永远显示旧名册)。
        private async Task NotifyPriorPartyAsync(PartyOpResult prior)
        {
            if (prior == null || !prior.Ok || prior.Party == null)
                return;

            try
            {
                var movingSession =
                    _sessions == null
                        ? null
                        : FindSessionByUserId(prior.TargetUserId);
                if (movingSession != null &&
                    movingSession.TcpClient != null &&
                    movingSession.TcpClient.Connected)
                {
                    await SendPartyClearBestEffortAsync(
                        movingSession,
                        GetDepartureClearParty(prior),
                        $"leave-prior-party-" +
                        $"{GetDepartureClearParty(prior)?.PartyId}");
                }
            }
            finally
            {
                // PartyManager has already committed the move. The old relay
                // generation must shrink/close even if the moving client's
                // clear notification cannot be delivered.
                await PublishCommittedDepartureAsync(
                    prior,
                    $"prior-party uid={prior.TargetUserId}");
            }
        }

        private async Task ClearPartyViewAsync(
            EnhancedClientSession session,
            Game.Party.Party packetParty,
            string reason)
        {
            if (session?.Player == null || packetParty == null)
                return;

            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0009,
                    PartyInfoNotiBuilder.Build(packetParty, 3)));
            FileLogger.Log(
                $"[{ProtocolName}] PARTY_INFO type=3 clear: " +
                $"cid={session.Player.CharacterId} reason={reason}");
        }

        private static Task SendPartyClearBestEffortAsync(
            EnhancedClientSession session,
            Party party,
            string target)
        {
            if (session == null || party == null)
                return Task.CompletedTask;
            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0009,
                PartyInfoNotiBuilder.Build(party, 3));
            return Game.Session.SessionDirectory
                .TrySendBestEffortAsync(
                    cancellationToken =>
                        session.SendPacketAsync(
                            packet, cancellationToken),
                    target);
        }

        private static Party GetDepartureClearParty(
            PartyOpResult result)
            => result?.RetiredParty ?? result?.Party;

        private async Task PublishCommittedDepartureAsync(
            PartyOpResult result,
            string reason)
        {
            if (result == null || !result.Ok)
                return;

            var committedParty = result.Party;
            if (result.Disbanded)
            {
                if (committedParty != null)
                    await CloseRelayRoomAsync(committedParty.PartyId);
                await PublishTownPartyListsAsync();
                return;
            }

            if (committedParty == null)
                return;

            var replacementPartyId = committedParty.PartyId;
            var retiredParty = result.RetiredParty;
            if (retiredParty != null)
            {
                var retiredPartyId = retiredParty.PartyId;

                // Drain any in-flight publication for the old id before the
                // destructive clear. A PARTY_INFO type=3 is a generation
                // teardown, not an in-place roster refresh.
                await CloseRelayRoomAsync(retiredPartyId);

                // Serialize the complete destructive transition against every
                // ordinary publication for the replacement id. Without this
                // lease, a concurrent non-leader shrink can publish id2 after
                // the first check but before old-id type3, leaving the client
                // cleared after the second stale check returns.
                using var replacementGate =
                    await AcquireBroadcastGateAsync(
                        replacementPartyId);

                // PartyOpResult.Party can still be the manager-owned mutable
                // instance. Never enumerate or inspect it outside the manager
                // lock; use one detached snapshot for the clear recipients.
                var clearSnapshot =
                    _partyManager.GetPartySnapshot(
                        replacementPartyId);
                if (clearSnapshot == null)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] PARTY generation publish stale: " +
                        $"old={retiredPartyId} " +
                        $"new={replacementPartyId} reason={reason}; " +
                        "old relay closed, wire publish skipped");
                    await PublishTownPartyListsAsync();
                    return;
                }

                if (GenerationPublishAfterCurrentCheckAsync != null)
                {
                    await GenerationPublishAfterCurrentCheckAsync(
                        retiredPartyId,
                        replacementPartyId);
                }

                var clearTasks = new List<Task>();
                foreach (var member in clearSnapshot.MembersBySlot())
                {
                    if (_sessions == null ||
                        !_sessions.TryGet(
                            member.CharacterId,
                            out var survivor) ||
                        survivor.SessionId != member.SessionId)
                    {
                        continue;
                    }

                    clearTasks.Add(
                        SendPartyClearBestEffortAsync(
                            survivor,
                            retiredParty,
                            $"{reason} oldParty=" +
                            $"{retiredPartyId} " +
                            $"newParty={replacementPartyId} " +
                            $"uid={member.UserId}"));
                }

                if (clearTasks.Count > 0)
                    await Task.WhenAll(clearTasks);

                // A second leader departure can retire the replacement while
                // the clear sends are in flight. Never follow a clear with a
                // stale formation from the superseded generation.
                var formationSnapshot =
                    _partyManager.GetPartySnapshot(
                        replacementPartyId);
                if (formationSnapshot == null)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] PARTY generation superseded " +
                        $"during publish: old={retiredPartyId} " +
                        $"new={replacementPartyId} reason={reason}");
                    await PublishTownPartyListsAsync();
                    return;
                }

                await BroadcastPartyInfoWithinGate(
                    replacementPartyId);
                await PublishTownPartyListsAsync();
                return;
            }

            await BroadcastPartyInfo(replacementPartyId);
            await PublishTownPartyListsAsync();
        }

        private PartyMember BuildMember(EnhancedClientSession session, int cid)
        {
            var rec = _characterRepository.GetById(cid);
            // P2P 端点: 用会话 TCP 远端的 LAN IP(局域网可达地址, 比客户端报的虚拟网卡 IP 更可靠)。
            byte[] ipBytes = new byte[] { 127, 0, 0, 1 };
            try
            {
                if (session.TcpClient?.Client?.RemoteEndPoint is System.Net.IPEndPoint ep)
                    ipBytes = ep.Address.MapToIPv4().GetAddressBytes();   // 4 字节 octets a.b.c.d
            }
            catch { /* 端点不可用则回环兜底 */ }
            return new PartyMember
            {
                UserId = (ushort)cid,
                CharacterId = cid,
                SessionId = session.SessionId,
                Name = rec?.DisplayName ?? string.Empty,
                Level = rec?.Level ?? 1,
                Job = rec?.Job ?? 0,
                IpBytes = ipBytes,
                // 客户端真实 UDP 端口(SET_UDP 0x0002 上报, 每次开游戏动态变); 未上报回落 10000。
                P2pPort = (session.Player != null && session.Player.P2pPort != 0) ? session.Player.P2pPort : (ushort)10000,
                AccId = (uint)cid,
            };
        }

        private async Task<bool> RunCurrentPartyMutationAsync(
            EnhancedClientSession session,
            Action mutation)
        {
            if (mutation == null)
                throw new ArgumentNullException(nameof(mutation));

            if (_characterTransitions != null)
            {
                return await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () =>
                    {
                        mutation();
                        return Task.CompletedTask;
                    });
            }

            if (!IsDirectoryCurrent(session))
                return false;
            mutation();
            return true;
        }

        private async Task<bool> RunCurrentPartyPairMutationAsync(
            EnhancedClientSession left,
            EnhancedClientSession right,
            Action mutation)
        {
            if (mutation == null)
                throw new ArgumentNullException(nameof(mutation));
            if (left?.Player == null ||
                right?.Player == null ||
                left.Player.CharacterId == right.Player.CharacterId)
            {
                return false;
            }

            if (_characterTransitions == null)
            {
                if (!IsDirectoryCurrent(left) ||
                    !IsDirectoryCurrent(right))
                {
                    return false;
                }
                mutation();
                return true;
            }

            return await _characterTransitions
                .RunIfBothCurrentAsync(
                    left,
                    right,
                    () =>
                    {
                        mutation();
                        return Task.CompletedTask;
                    });
        }

        private bool IsDirectoryCurrent(
            EnhancedClientSession session)
        {
            if (session?.Player == null)
                return false;
            if (_sessions == null)
                return true;
            return _sessions.TryGet(
                       session.Player.CharacterId,
                       out var current) &&
                   ReferenceEquals(current, session);
        }

        // 0x000C 创建/更新队伍。无队则建(请求者=队长), 更新设置, 回整份 PARTY_INFO(type=0)。
        private static Game.Dungeon.DungeonPartySelectionCohort
            ResolveEntryPartyTransitionCohort(EnhancedClientSession session)
            => session?.Player?.CurrentRun?.EntryPartySelectionCohort
               ?? session?.Player?.CurrentDungeonSelection?.PartyCohort;

        private static async Task RunWithEntryPartyTransitionGatesAsync(
            Func<Task> action,
            params EnhancedClientSession[] sessions)
        {
            var cohorts = (sessions ?? Array.Empty<EnhancedClientSession>())
                .Select(ResolveEntryPartyTransitionCohort)
                .Where(cohort => cohort != null)
                .Distinct()
                .OrderBy(cohort => cohort.PartyId)
                .ThenBy(cohort => cohort.ProjectionId)
                .ToList();
            foreach (var cohort in cohorts)
                await cohort.TransitionGate.WaitAsync();
            try
            {
                await action();
            }
            finally
            {
                for (var index = cohorts.Count - 1; index >= 0; index--)
                    cohorts[index].TransitionGate.Release();
            }
        }

        private static async Task
            RunWithDungeonSelectionAndEntryPartyTransitionGatesAsync(
                Func<Task> action,
                params EnhancedClientSession[] sessions)
        {
            var ownerGates = (sessions ?? Array.Empty<EnhancedClientSession>())
                .Where(session => session?.Player != null)
                .OrderBy(session => session.Player.CharacterId)
                .ThenBy(session => session.SessionId)
                .Select(session => session.Player.DungeonRunTransitionGate)
                .Distinct()
                .ToList();
            foreach (var gate in ownerGates)
                await gate.WaitAsync();
            try
            {
                await RunWithEntryPartyTransitionGatesAsync(
                    action,
                    sessions);
            }
            finally
            {
                for (var index = ownerGates.Count - 1;
                     index >= 0;
                     index--)
                {
                    ownerGates[index].Release();
                }
            }
        }

        public Task Handle_SET_PARTY_INFO(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => RunWithEntryPartyTransitionGatesAsync(
                () => HandleSetPartyInfoCore(session, header, body),
                session);

        private async Task HandleSetPartyInfoCore(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] SET_PARTY_INFO recv({body?.Length ?? 0}B): {(body != null ? System.BitConverter.ToString(body) : "null")}");
            if (!SetPartyInfoRequest.TryParse(body, out var req))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x000C, new byte[] { 0x00, 0x04 }));
                return;
            }
            if (!PartyConstants.IsSupportedCapacity(req.UserMax))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SET_PARTY_INFO rejected: " +
                    $"reason=invalid_user_max userMax={req.UserMax}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x000C,
                    new byte[] { 0x00, 0x04 }));
                return;
            }

            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var uid = (ushort)cid;

            var leaderName = _characterRepository.GetById(cid)?.Name ?? System.Array.Empty<byte>();
            var member = BuildMember(session, cid);
            Party party = null;
            PartyOpResult createdResult = null;
            PartyOpResult settingsResult = null;
            string failureReason = null;
            if (!await RunCurrentPartyMutationAsync(
                    session,
                    () =>
                    {
                        party = _partyManager.GetPartyByUser(uid);
                        if (party == null)
                        {
                            createdResult =
                                _partyManager.CreateParty(member);
                            party = createdResult.Party;
                        }
                        var titleBytes =
                            (req.Title != null &&
                             req.Title.Length > 0)
                                ? req.Title
                                : leaderName;
                        settingsResult = _partyManager.UpdateSettings(
                            uid,
                            session.SessionId,
                            titleBytes,
                            req.UserMax,
                            req.Raw);
                        if (!settingsResult.Ok)
                        {
                            failureReason = settingsResult.Reason;
                            return;
                        }
                        party = settingsResult.Party;
                    }))
            {
                return;
            }
            if (failureReason != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SET_PARTY_INFO rejected: " +
                    $"uid={uid} reason={failureReason}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x000C,
                    new byte[] { 0x00, 0x04 }));
                return;
            }

            await NotifyPriorPartyAsync(
                createdResult?.PriorPartyLeave);

            FileLogger.Log($"[{ProtocolName}] SET_PARTY_INFO uid={uid} party={party.PartyId} settings={System.BitConverter.ToString(req.Raw)} members={party.Count}");

            if (createdResult != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0009,
                    PartyInfoNotiBuilder.Build(party, 0)));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0099,
                    PartyRealtimeInfoBuilder.Build(party)));
            }
            else
            {
                await BroadcastPartySettings(party.PartyId);
            }
            await PublishTownPartyListsAsync();
        }

        // 0x000D 退队。df 成功无对本人回包(走广播刷新剩余成员); 失败回 0x12(不在队)。
        public async Task Handle_LEAVE_PARTY(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var transitionGate =
                session?.Player?.CurrentRun?.EntryPartySelectionCohort
                    ?.TransitionGate
                ?? session?.Player?.CurrentDungeonSelection?.PartyCohort
                    ?.TransitionGate;
            if (transitionGate != null)
                await transitionGate.WaitAsync();
            try
            {
                await HandleLeavePartyCore(session, header, body);
            }
            finally
            {
                transitionGate?.Release();
            }
        }

        private async Task HandleLeavePartyCore(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var uid = (ushort)cid;
            var expectedRun = session?.Player?.CurrentRun;

            PartyOpResult result = null;
            if (!await RunCurrentPartyMutationAsync(
                    session,
                    () =>
                    {
                        result = _partyManager.Leave(
                            uid, session.SessionId);
                    }))
            {
                return;
            }
            if (!result.Ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x000D, new byte[] { 0x00, 0x12 }));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] LEAVE_PARTY uid={uid} disbanded={result.Disbanded} leaderChanged={result.LeaderChanged} newLeader={result.NewLeaderUserId}");
            try
            {
                // 给【离队者本人】发 PARTY_INFO type=3(清空组队窗口)——本客户端不自己清。
                await SendPartyClearBestEffortAsync(
                    session,
                    GetDepartureClearParty(result),
                    $"leave-party-clear uid={uid}");
            }
            finally
            {
                try
                {
                    // ★副本内退队: 若退队者仍在副本实例里, 把他清 run + 拉回城镇。
                    await PullBackToTownIfInDungeonAsync(
                        session,
                        "LEAVE_PARTY 退队者",
                        expectedRun);
                }
                finally
                {
                    // 无论离队者 socket/回城是否失败，都必须发布服务端已提交的队伍代际。
                    await PublishCommittedDepartureAsync(
                        result,
                        $"leave-party uid={uid}");
                }
            }
        }

        // 未通关副本中主动放弃后的队伍策略。调用点仍持有本次入场 cohort
        // 的 TransitionGate，因此多名成员接近同时回城时，离队顺序与 EndRun
        // 顺序一致。前面的成员离队；最后一名保留为单人队。
        internal async Task HandleDungeonGiveupWithinTransitionAsync(
            EnhancedClientSession session,
            ushort expectedUserId,
            Guid expectedSessionId,
            int expectedPartyId)
        {
            if (expectedUserId == 0 || expectedSessionId == Guid.Empty)
                return;

            var uid = expectedUserId;
            if (expectedPartyId <= 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DUNGEON_GIVEUP leave skip " +
                    $"uid={uid} reason=no_expected_party");
                return;
            }

            var identityCurrent = session?.SessionId == expectedSessionId &&
                                  session.Player?.UserId == uid;
            var result = identityCurrent
                ? _partyManager.LeaveForDungeonReturn(
                    uid,
                    expectedSessionId,
                    expectedPartyId)
                : _partyManager.LeaveExpectedParty(
                    uid,
                    expectedSessionId,
                    expectedPartyId);
            if (!result.Ok)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DUNGEON_GIVEUP leave failed " +
                    $"uid={uid} party={expectedPartyId} " +
                    $"reason={result.Reason}");
                return;
            }

            if (!identityCurrent)
            {
                await PublishCommittedDepartureAsync(
                    result,
                    $"dungeon-giveup-stale-identity uid={uid}");
                FileLogger.Log(
                    $"[{ProtocolName}] DUNGEON_GIVEUP stale identity cleanup " +
                    $"uid={uid} party={expectedPartyId}");
                return;
            }

            if (result.SoleMemberPreserved)
            {
                await PublishPartyHostProjectionAsync(
                    result.Party.PartyId,
                    uid,
                    includeRealtime: true,
                    reason: "dungeon-giveup-last-member");
                FileLogger.Log(
                    $"[{ProtocolName}] DUNGEON_GIVEUP preserved solo party " +
                    $"uid={uid} party={result.Party.PartyId} " +
                    $"slot={result.Party.GetMember(uid)?.SlotIndex ?? 0}");
                return;
            }

            try
            {
                await SendPartyClearBestEffortAsync(
                    session,
                    GetDepartureClearParty(result),
                    $"dungeon-giveup-clear uid={uid}");
            }
            finally
            {
                await PublishCommittedDepartureAsync(
                    result,
                    $"dungeon-giveup uid={uid}");
            }
            FileLogger.Log(
                $"[{ProtocolName}] DUNGEON_GIVEUP left party " +
                $"uid={uid} party={expectedPartyId} " +
                $"disbanded={result.Disbanded} " +
                $"newLeader={result.NewLeaderUserId} " +
                $"remaining=[{string.Join(",", result.RemainingMembers.Select(m => m.UserId))}]");
        }

        // 退队/被踢共用: 若该会话仍在副本实例里, 清 run + 发 4 包城镇序列拉回城镇(否则人离队却卡本里)。
        // 对应 df leave_user 的 set_state + sendInoutConditionDungeon。
        private async Task PullBackToTownIfInDungeonAsync(
            EnhancedClientSession s,
            string reason,
            Game.Dungeon.DungeonRun expectedRun)
        {
            if (expectedRun == null)
                return;
            if (_characterTransitions != null)
            {
                using (var lease =
                       await _characterTransitions
                           .AcquireIfCurrentAsync(s))
                {
                    if (lease == null)
                        return;
                    if (!ReferenceEquals(
                            s.Player.CurrentRun,
                            expectedRun))
                    {
                        return;
                    }
                    await PullBackToTownWithinTransitionAsync(
                        s, reason);
                }
                return;
            }

            if (ReferenceEquals(
                    s?.Player?.CurrentRun,
                    expectedRun))
            {
                await PullBackToTownWithinTransitionAsync(
                    s, reason);
            }
        }

        private async Task PullBackToTownWithinTransitionAsync(
            EnhancedClientSession s,
            string reason)
        {
            if (s?.Player?.CurrentRun == null) return;
            await Dungeon.DungeonRunLifecycle.EndRunToTownAsync(s);
            s.Player.UserState = 0x00;
            // 队伍跟随回城 → 状态回空闲：同频道在线好友推 USERINFO(0x0002) 更新场景实体状态。
            if (_sessions != null)
                await UnitedFriendSystem.NotifyUserStateChanged(
                    s, _sessions);

            var snap = TownAreaNotificationBuilder.CreateCurrentSnapshot(s.Player);
            await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003, EnterSelectDungeonStateBuilder.BuildUserState(s.Player)));
            await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017, TownAreaNotificationBuilder.BuildUserArea(snap)));
            if (_announceTownArrivalWithinTransition != null)
            {
                if (!await _announceTownArrivalWithinTransition(s))
                    return;
            }
            else if (_announceTownArrival != null &&
                     _characterTransitions == null)
            {
                if (!await _announceTownArrival(s))
                    return;
            }
            else
            {
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0018,
                    TownAreaNotificationBuilder.BuildAreaUsers(snap)));
                s.Player.TownPresenceReady = true;
            }
            await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00CA, new byte[] { 0x00 }));
            await UserInfoBroadcastService.SendSubtype0Async(
                s,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                $"{reason} subtype0");
            FileLogger.Log($"[{ProtocolName}] {reason}: cid={s.Player.CharacterId} 在副本内 → 清 run + 拉回城镇");
        }

        // 0x00A6 CALL_PARTY_MEMBER_REALTIME_INFO: 客户端请求成员实时信息(空 body)→ 回 0x0099(HP% + 成员列表)。
        // 该字节 = HP 百分比(不是体力), 客户端 HP 取到后再取 MP; 不回则组队窗口"信息异常"。
        internal async Task<bool> TryRestoreDungeonParticipantAsync(
            EnhancedClientSession session,
            int partyId)
        {
            if (session?.Player == null
                || partyId <= 0
                || partyId > ushort.MaxValue)
            {
                return false;
            }

            var characterId = session.Player.CharacterId;
            var userId = session.Player.UserId;
            var current = _partyManager.GetPartyByUser(userId);
            if (current != null && current.PartyId == partyId)
                return true;
            if (_partyManager.GetPartyById(partyId) == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DUNGEON_REJOIN party missing: " +
                    $"cid={characterId} party={partyId}");
                return false;
            }

            var join = _partyManager.Join(
                partyId,
                BuildMember(session, characterId));
            if (!join.Ok)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DUNGEON_REJOIN party restore rejected: " +
                    $"cid={characterId} party={partyId} reason={join.Reason}");
                return false;
            }

            await NotifyPriorPartyAsync(join.PriorPartyLeave);
            await BroadcastPartyInfo(join.Party);
            return true;
        }

        internal async Task RollbackDungeonParticipantRestoreAsync(
            EnhancedClientSession session,
            int partyId)
        {
            var userId = session?.Player?.UserId ?? 0;
            if (userId == 0)
                return;
            var current = _partyManager.GetPartyByUser(userId);
            if (current == null || current.PartyId != partyId)
                return;
            var leave = _partyManager.Leave(userId);
            if (leave.Ok
                && !leave.Disbanded
                && leave.Party != null
                && leave.Party.Count > 0)
            {
                await BroadcastPartyInfo(leave.Party);
            }
        }

        public async Task Handle_CALL_PARTY_MEMBER_REALTIME_INFO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var party = _partyManager.GetPartyByUser((ushort)cid);
            if (party == null)
                return;   // 不在队则不回(df: check_error 要求在队/正确状态)
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0099, PartyRealtimeInfoBuilder.Build(party)));
        }

        // 0x000E 踢人。请求 = byte 目标 slot。仅队长可踢; 失败回 0x13。成功后剩余成员重发 PARTY_INFO。
        public async Task Handle_WALKOUT_PARTY_MEMBER(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var initialParty = session?.Player == null
                ? null
                : _partyManager.GetPartyByUser(session.Player.UserId);
            var target = body != null && body.Length >= 1
                ? initialParty?.MembersBySlot().FirstOrDefault(
                    member => member.SlotIndex == body[0])
                : null;
            EnhancedClientSession targetSession = null;
            if (target != null)
                _sessions?.TryGet(target.CharacterId, out targetSession);
            var transitionGate =
                session?.Player?.CurrentRun?.EntryPartySelectionCohort
                    ?.TransitionGate
                ?? session?.Player?.CurrentDungeonSelection?.PartyCohort
                    ?.TransitionGate
                ?? targetSession?.Player?.CurrentRun
                    ?.EntryPartySelectionCohort?.TransitionGate
                ?? targetSession?.Player?.CurrentDungeonSelection
                    ?.PartyCohort?.TransitionGate;
            if (transitionGate != null)
                await transitionGate.WaitAsync();
            try
            {
                await HandleWalkoutPartyMemberCore(session, header, body);
            }
            finally
            {
                transitionGate?.Release();
            }
        }

        private async Task HandleWalkoutPartyMemberCore(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var uid = (ushort)cid;

            var initialParty =
                _partyManager.GetPartyByUser(uid);
            var initialTarget =
                body != null &&
                body.Length >= 1 &&
                initialParty != null
                    ? initialParty.MembersBySlot()
                        .FirstOrDefault(
                            m => m.SlotIndex == body[0])
                    : null;
            if (initialTarget == null ||
                _sessions == null ||
                !_sessions.TryGet(
                    initialTarget.CharacterId,
                    out var kickedSession) ||
                kickedSession.SessionId !=
                initialTarget.SessionId)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x000E, new byte[] { 0x00, 0x13 }));
                return;
            }

            Party party = null;
            PartyMember target = null;
            PartyOpResult result = null;
            Game.Dungeon.DungeonRun expectedRun = null;
            if (!await RunCurrentPartyPairMutationAsync(
                    session,
                    kickedSession,
                    () =>
                    {
                        party = _partyManager.GetPartyByUser(uid);
                        if (body != null &&
                            body.Length >= 1 &&
                            party != null)
                        {
                            target = party.MembersBySlot()
                                .FirstOrDefault(
                                    m => m.SlotIndex == body[0]);
                        }
                        if (target != null &&
                            target.SessionId ==
                            kickedSession.SessionId)
                        {
                            expectedRun =
                                kickedSession.Player?.CurrentRun;
                            result = _partyManager.Kick(
                                uid,
                                session.SessionId,
                                target.UserId,
                                target.SessionId);
                        }
                    }))
            {
                return;
            }

            if (party == null || target == null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x000E, new byte[] { 0x00, 0x13 }));
                return;
            }

            if (result == null || !result.Ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x000E, new byte[] { 0x00, 0x13 }));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] WALKOUT uid={uid} kickedSlot={body[0]} target={target.UserId} disbanded={result.Disbanded}");

            // 1) 给【被踢者】发 PARTY_INFO type=3 清空其组队窗口(否则被踢者仍显示在队里, 不知已被踢)+ 副本内拉回城。
            try
            {
                await SendPartyClearBestEffortAsync(
                    kickedSession,
                    result.Party ?? party,
                    $"walkout-clear uid={target.UserId}");
            }
            finally
            {
                try
                {
                    await PullBackToTownIfInDungeonAsync(
                        kickedSession,
                        "WALKOUT 被踢者",
                        expectedRun);
                }
                finally
                {
                    // 2) 广播更新后的 PARTY_INFO 给剩余成员(含踢人者自己)。
                    if (!result.Disbanded)
                    {
                        await PublishCommittedDepartureAsync(
                            result,
                            $"walkout uid={target.UserId}");
                    }
                    else
                    {
                        await PublishCommittedDepartureAsync(
                            result,
                            $"walkout-disband uid={target.UserId}");
                        await SendPartyClearBestEffortAsync(
                            session,
                            party,
                            $"walkout-disband-clear uid={uid}");
                    }
                }
            }
        }

        // ==== Phase B: 组队邀请流(需 ISessionDirectory 跨会话定位)。假人两连接可端到端联调状态机。====
        // 0x0079 CHANGE_HOST: body[0] 是目标成员的稳定槽位。
        // A21 NOTI 0x001A consumer 与 100 神迹 send_host_info 都把一字节
        // body 当作 host 槽；委任只切 leader/host，不重排成员槽或重建队伍。
        public Task Handle_CHANGE_HOST(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => RunWithEntryPartyTransitionGatesAsync(
                () => HandleChangeHostCore(session, header, body),
                session);

        private async Task HandleChangeHostCore(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 1) return;
            byte slot = body[0];
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var byUid = (ushort)cid;
            var liveParty = _partyManager.GetPartyByUser(byUid);
            var party = liveParty == null
                ? null
                : _partyManager.GetPartySnapshot(
                    liveParty.PartyId);
            if (party == null)
            {
                FileLogger.Log($"[{ProtocolName}] CHANGE_HOST: by={cid} 无队伍, 忽略");
                return;
            }
            if (party.LeaderUserId != byUid ||
                party.GetMember(byUid)?.SessionId !=
                session.SessionId)   // 仅当前队长可委托
            {
                FileLogger.Log($"[{ProtocolName}] CHANGE_HOST: by={cid} 非队长(leader={party.LeaderUserId}), 忽略");
                return;
            }
            // 按 SlotIndex 映射(不是 list 下标!与 BroadcastPartyInfo 下发的槽位一致)。
            var target = party.MembersBySlot().FirstOrDefault(m => m.SlotIndex == slot);
            if (target == null || target.UserId == byUid)
            {
                FileLogger.Log($"[{ProtocolName}] CHANGE_HOST: by={cid} slot={slot} 空槽/无此成员/委托给自己, 忽略");
                return;
            }
            if (_sessions == null ||
                !_sessions.TryGet(
                    target.CharacterId,
                    out var targetSession) ||
                targetSession.SessionId != target.SessionId)
            {
                return;
            }

            var partyId = party.PartyId;
            PartyOpResult transfer = null;
            if (!await RunCurrentPartyPairMutationAsync(
                    session,
                    targetSession,
                    () =>
                    {
                        transfer =
                            _partyManager.TransferLeader(
                                partyId,
                                byUid,
                                session.SessionId,
                                target.UserId,
                                target.SessionId);
                    }))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] CHANGE_HOST transfer aborted: " +
                    $"by={byUid} party={partyId} reason=stale_transition");
                return;
            }
            if (transfer == null || !transfer.Ok)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] CHANGE_HOST transfer aborted: " +
                    $"by={byUid} party={partyId} " +
                    $"reason={transfer?.Reason ?? "stale_session"}");
                return;
            }

            await PublishPartyHostProjectionAsync(
                partyId,
                target.UserId,
                includeRealtime: false,
                reason: "change-host");
            var committed = _partyManager.GetPartySnapshot(partyId);
            FileLogger.Log(
                $"[{ProtocolName}] CHANGE_HOST: 委托队长 {byUid} → " +
                $"{target.UserId}(slot={slot}); party={partyId} " +
                $"成员槽保持=[{string.Join(",", committed?.MembersBySlot().Select(
                    x => $"uid{x.UserId}@slot{x.SlotIndex}") ??
                    Enumerable.Empty<string>())}]");
            await PublishTownPartyListsAsync();
        }

        // 0x01A3 belongs to the chat-group/1:1 conversation flow, not party
        // invitations. The matching 0x016A handler is a no-op in this client
        // build because the conversation UI is opened locally. Accept the
        // intent for compatibility, but never mutate party state or emit
        // PARTY_INFO. Real party invitations use 0x000A/0x000B.
        public Task Handle_CREATE_GROUP(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var targetName = ParseNameArg(body);
            FileLogger.Log(
                $"[{ProtocolName}] CREATE_GROUP chat intent: " +
                $"by={SessionOwnerResolver.Resolve(session).characterId} " +
                $"targetBytes={targetName?.Length ?? 0}; " +
                "party state unchanged");
            return Task.CompletedTask;
        }

        // 0x000A REQUEST_PEER: 真机实测——右键同屏玩家→组队邀请发的就是它(不是 0x01A3 按名, 是按 uid)。
        // 请求 body = 目标 uid(u16) + type(byte) + int32(peer id) [+尾]。
        // df DisPatcher_ReqPeer(cmd10, @0x081EED08): 按 uid find_from_world 找目标 → 给目标发 REQUEST_PEER(SC 0x0007,
        //   put_header(0,7)+put_short(uid)+put_byte(结果类型)+put_int+put_short×3) 让其弹"X 邀请你组队"框。
        // 布局/值先按 df 结构走, 用真客户端 2 迭代校准。
        public async Task Handle_REQUEST_PEER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (_sessions == null || body == null || body.Length < 3)
                return;
            ushort targetUid = System.BitConverter.ToUInt16(body, 0);
            byte reqType = body[2];
            int peerInt = body.Length >= 7 ? System.BitConverter.ToInt32(body, 3) : 0;
            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            ushort inviterUid = (ushort)cid;
            FileLogger.Log($"[{ProtocolName}] REQUEST_PEER by={cid} targetUid={targetUid} type={reqType} peerInt={peerInt} body={System.BitConverter.ToString(body)}");

            var targetSession = FindSessionByUserId(targetUid);
            if (targetSession == null)
            {
                FileLogger.Log($"[{ProtocolName}] REQUEST_PEER: 目标 uid={targetUid} 不在线");
                return;
            }
            if (!IsSameGameChannel(session, targetSession))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] REQUEST_PEER: 跨频道请求已拒绝 " +
                    $"from={session.ListenerPort} to={targetSession.ListenerPort}");
                return;
            }
            if (targetUid == inviterUid)
                return;

            // ★交易 阶段1: reqType==1 = ENUM_PEER_REQUEST_TYPE TRADE → 给对方弹【交易确认窗】(而非组队框)。
            //   交易形态 body = 11B [A.uid:2][01][peer:4][createTime:4](含 peer, 漏了长度不符被客户端静默丢弃→不弹窗)。
            //   阶段2(放置道具窗/换物)待专项; 此处保证交易请求不弹成组队框, 且 accept 不误组队(见 RES_PEER)。
            if (reqType == 2)
            {
                if (body.Length != 7 ||
                    _pvpRoomHandler == null)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] REQUEST_PEER: " +
                        "invalid/unavailable PvP room invite");
                    return;
                }

                await _pvpRoomHandler.HandleRoomInviteRequestAsync(
                    session,
                    targetSession,
                    peerInt);
                return;
            }

            if (reqType == 1)
            {
                if (!await RunCurrentPartyPairMutationAsync(
                        session,
                        targetSession,
                        () => { }))
                {
                    return;
                }

                var tw = new GamePacketWriter();
                tw.WriteUInt16(inviterUid);   // A.uid
                tw.WriteByte(1);              // ENUM_PEER_REQUEST_TYPE = 1 TRADE
                tw.WriteInt32(peerInt);       // peer(回传请求里的 peer)
                tw.WriteInt32(0);             // A.createTime(阶段1先填0)
                var tradeSent = await Game.Session.SessionDirectory
                    .TrySendBestEffortAsync(
                        cancellationToken =>
                            targetSession.SendPacketAsync(
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    0x0007,
                                    tw.ToArray()),
                                cancellationToken),
                        $"trade invite target={targetUid}");
                if (tradeSent)
                {
                    FileLogger.Log($"[{ProtocolName}] TRADE REQUEST_PEER A={inviterUid}->B={targetUid} → SC 0x0007 交易形态(11B, ⚠️阶段2待实现)");
                }
                return;
            }

            if (reqType != 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] REQUEST_PEER: unsupported type={reqType}");
                return;
            }

            // type0 同时承载普通邀请与申请加入。登记双方当前 SessionId
            // 以及请求时各自的 PartyId，接受时按同一状态重新校验。
            var inviteRecorded = false;
            string inviteFailure = null;
            if (!await RunCurrentPartyPairMutationAsync(
                    session,
                    targetSession,
                    () =>
                    {
                        inviteRecorded = _partyManager.RecordInvite(
                            targetUid,
                            targetSession.SessionId,
                            inviterUid,
                            session.SessionId,
                            out inviteFailure);
                    }))
            {
                return;
            }
            if (!inviteRecorded)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] REQUEST_PEER: " +
                    $"邀请登记失败 {inviteFailure ?? "unknown"}");
                return;
            }

            var inviteContext =
                BuildCrossAreaPartyInviteContextPacket(
                    session,
                    targetSession);

            // 给目标发 REQUEST_PEER(0x0007) 弹邀请框。body 照 df type=7 包: 邀请者 uid + 结果类型0 + int + 3×u16。
            var w = new GamePacketWriter();
            w.WriteUInt16(inviterUid);   // 邀请者 uid(目标据此显示"谁邀请")
            w.WriteByte(0);              // 结果类型 0 = 请求
            w.WriteInt32(peerInt);       // 回传 peer id
            w.WriteUInt16(0);            // 疲劳(待真机校准)
            w.WriteUInt16(0);            // 体力
            w.WriteUInt16(0);
            var inviteSent = await Game.Session.SessionDirectory
                .TrySendBestEffortAsync(
                    async cancellationToken =>
                    {
                        if (inviteContext != null)
                        {
                            await targetSession.SendPacketAsync(
                                inviteContext,
                                cancellationToken);
                        }
                        await targetSession.SendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                (ushort)NotiPacketTypeA21.REQUEST_PEER,
                                w.ToArray()),
                            cancellationToken);
                    },
                    $"party invite target={targetUid}");
            if (inviteSent)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] REQUEST_PEER → 给 uid={targetUid} " +
                    $"发 REQUEST_PEER(0x0007) 邀请弹框 " +
                    $"crossAreaContext={inviteContext != null}");
            }
            else
            {
                _partyManager.CancelInvite(
                    targetUid,
                    targetSession.SessionId,
                    inviterUid,
                    session.SessionId);
            }
        }

        // 0x000B RES_PEER: type0 接受为精确 7B；拒绝为同一前缀后追加 u16 0x0085 的 9B。
        // 只有接受形态才允许进入组队 mutation；拒绝/异常形态只消费 exact pending。
        internal static bool IsAcceptedTypeZeroPeerResponse(byte[] body)
            => body != null && body.Length == 7 && body[2] == 0;

        public Task Handle_RES_PEER(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var inviterSession = body != null && body.Length >= 2
                ? FindSessionByUserId(BitConverter.ToUInt16(body, 0))
                : null;
            var requestType = body != null && body.Length >= 3
                ? body[2]
                : (byte)0;
            if (body != null &&
                body.Length >= 2 &&
                requestType == 0)
            {
                return RunWithDungeonSelectionAndEntryPartyTransitionGatesAsync(
                    () => HandleResPeerCore(session, header, body),
                    session,
                    inviterSession);
            }
            return RunWithEntryPartyTransitionGatesAsync(
                () => HandleResPeerCore(session, header, body),
                session,
                inviterSession);
        }

        private async Task HandleResPeerCore(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (_sessions == null || body == null || body.Length < 2)
                return;
            ushort inviterUid = System.BitConverter.ToUInt16(body, 0);
            // ★body[2]=reqType(与 REQUEST_PEER 同域): 0=组队 1=交易。真机 ground truth:
            //   组队 accept body=EB-03-`00`-..., 交易 accept body=EB-03-`01`-...。
            //   之前无视 reqType 一律 party join → 交易【同意】被误组队(1v1交易窗点同意冒出组队 + 交易无后续)。
            byte reqType = body.Length >= 3 ? body[2] : (byte)0;
            var (acid, aaid) = SessionOwnerResolver.Resolve(session);
            ushort accepterUid = (ushort)acid;
            FileLogger.Log($"[{ProtocolName}] RES_PEER recv: accepter={accepterUid} inviter={inviterUid} type={reqType} body={System.BitConverter.ToString(body)}");

            if (inviterUid == accepterUid)
            {
                if (reqType == 2)
                    await SendPvpInviteFailureAsync(session, 19);
                return;
            }
            if (reqType != 0 &&
                reqType != 1 &&
                reqType != 2)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] RES_PEER: unsupported type={reqType}");
                return;
            }
            var inviterSession = FindSessionByUserId(inviterUid);
            if (inviterSession == null)
            {
                FileLogger.Log($"[{ProtocolName}] RES_PEER: 邀请者 uid={inviterUid} 不在线");
                if (reqType == 2)
                    await SendPvpInviteFailureAsync(session, 3);
                return;
            }
            if (!IsSameGameChannel(session, inviterSession))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] RES_PEER: 跨频道响应已拒绝 " +
                    $"from={session.ListenerPort} to={inviterSession.ListenerPort}");
                if (reqType == 2)
                    await SendPvpInviteFailureAsync(session, 19);
                return;
            }

            if (reqType == 0 && !IsAcceptedTypeZeroPeerResponse(body))
            {
                var exactInviteCanceled = false;
                if (!await RunCurrentPartyPairMutationAsync(
                        inviterSession,
                        session,
                        () =>
                        {
                            exactInviteCanceled = _partyManager.CancelInvite(
                                accepterUid,
                                session.SessionId,
                                inviterUid,
                                inviterSession.SessionId);
                        }))
                {
                    return;
                }

                var capturedRefusal = body.Length == 9 &&
                    BitConverter.ToUInt16(body, 7) == 0x0085;
                FileLogger.Log(
                    $"[{ProtocolName}] RES_PEER: " +
                    $"{(capturedRefusal ? "拒绝" : "invalid type0 response")} " +
                    $"accepter={accepterUid} inviter={inviterUid} " +
                    $"pendingCanceled={exactInviteCanceled} " +
                    $"body={BitConverter.ToString(body)}");
                return;
            }

            if (reqType == 2)
            {
                if (body.Length != 7 ||
                    _pvpRoomHandler == null)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] RES_PEER: " +
                        "invalid/unavailable PvP room response");
                    await SendPvpInviteFailureAsync(session, 19);
                    return;
                }

                await _pvpRoomHandler.HandleRoomInviteResponseAsync(
                    inviterSession,
                    session,
                    BitConverter.ToInt32(body, 3),
                    checkoutCommitted =>
                        CheckoutPartyForPvpInviteWithinTransitionAsync(
                            session,
                            checkoutCommitted));
                return;
            }

            // ★交易 accept(reqType==1)绝不组队。df 交易走独立 CTradeSpace 路径, 与 party join 无关。
            //   阶段2(开道具放置窗 + 换物)待专项; 此处止血: 交易同意不再误组队。
            if (reqType == 1)
            {
                FileLogger.Log($"[{ProtocolName}] RES_PEER TRADE accept: A={inviterUid} B={accepterUid} → 交易已确认(不组队); ⚠️阶段2放置窗/换物待实现");
                return;
            }
            var (icid, iaid) = SessionOwnerResolver.Resolve(inviterSession);

            var inviterMember =
                BuildMember(inviterSession, icid);
            var accepterMember =
                BuildMember(session, acid);
            Party party = null;
            PartyOpResult join = null;
            string joinMode = null;
            string failureReason = null;
            if (!await RunCurrentPartyPairMutationAsync(
                    inviterSession,
                    session,
                    () =>
                    {
                        if (inviterSession.Player?.CurrentRun != null ||
                            session.Player?.CurrentRun != null)
                        {
                            failureReason = _partyManager.CancelInvite(
                                accepterUid,
                                session.SessionId,
                                inviterUid,
                                inviterSession.SessionId)
                                    ? "party_accept_during_dungeon_run"
                                    : "invite_not_found_or_stale";
                            return;
                        }
                        join = _partyManager.AcceptInvite(
                            accepterUid,
                            session.SessionId,
                            inviterUid,
                            inviterSession.SessionId,
                            inviterMember,
                            accepterMember,
                            out joinMode);
                        if (!join.Ok)
                        {
                            failureReason = join.Reason;
                            return;
                        }
                        party = join.Party;
                    }))
            {
                return;
            }

            await NotifyPriorPartyAsync(
                join?.PriorPartyLeave);
            if (failureReason != null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] RES_PEER: " +
                    $"入队失败 {failureReason}");
                return;
            }
            if (join?.Ok == true && join.PriorPartyLeave == null)
            {
                var joiningSession = join.TargetUserId == inviterUid
                    ? inviterSession
                    : session;
                await ClearPartyViewAsync(
                    joiningSession,
                    party,
                    "res-peer-new-member");
            }
            FileLogger.Log(
                $"[{ProtocolName}] RES_PEER: 组队成功 mode={joinMode} " +
                $"party={party.PartyId} members={party.Count} " +
                $"leader={party.LeaderUserId}");

            // df 0x081F14D2: 接受成功后单发 SC 0x08 "peer已接受" 回执给【邀请者A】(body=B.uid + 0 + A.uid, 7B)。
            // 疑为让 A 客户端认领队伍单例(dword_3091F50), 使随后的 PARTY_INFO(0x09) 落到被渲染的那个队伍对象上。
            var w08 = new GamePacketWriter();
            w08.WriteUInt16(accepterUid);       // B.uid (responder)
            w08.WriteByte(0);
            w08.WriteInt32((int)inviterUid);    // A.uid (inviter, u32 LE)
            var acceptedPacket = GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0008,
                w08.ToArray());
            await BroadcastPartyInfo(
                party,
                includeP2p: true,
                afterRealtime: async () =>
                {
                    if (!IsDirectoryCurrent(inviterSession))
                        return;

                    var ackSent = await Game.Session.SessionDirectory
                        .TrySendBestEffortAsync(
                            cancellationToken =>
                                inviterSession.SendPacketAsync(
                                    acceptedPacket,
                                    cancellationToken),
                            $"party accept ack inviter={inviterUid}");
                    if (ackSent)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] RES_PEER: sent SC 0x08 " +
                            $"after 0x99 to inviter uid={inviterUid} " +
                            $"(B={accepterUid})");
                    }
                });
            await PublishTownPartyListsAsync();
        }
        // 按 UserId 找在线会话。
        private EnhancedClientSession FindSessionByUserId(ushort uid)
        {
            foreach (var s in _sessions.GetAllGameSessions())
            {
                var (scid, _) = SessionOwnerResolver.Resolve(s);
                if (scid > 0 && (ushort)scid == uid)
                    return s;
            }
            return null;
        }

        internal async Task PublishTownPartyListsAsync()
        {
            if (_sessions == null)
                return;

            var gateAcquired = false;
            try
            {
                await _partyListPublishGate.WaitAsync();
                gateAcquired = true;
                foreach (var recipient in _sessions.GetAllGameSessions())
                {
                    var player = recipient?.Player;
                    if (player == null ||
                        player.CharacterId <= 0 ||
                        !player.TownPresenceReady ||
                        player.UserState != 0x00 ||
                        player.CurrentRun != null ||
                        !IsDirectoryCurrent(recipient) ||
                        _partyManager.GetPartySnapshotByUser(
                            player.UserId) != null)
                    {
                        continue;
                    }

                    var expectedSessionId = recipient.SessionId;
                    var expectedCharacterId = player.CharacterId;
                    var expectedUserId = player.UserId;
                    var expectedTownId = player.CurTownId;
                    var expectedAreaId = player.CurAreaId;
                    var expectedListenerPort = recipient.ListenerPort;

                    var visibleParties = new List<Party>();
                    var seenPartyIds = new HashSet<int>();
                    foreach (var visibleSession in _sessions.GetSessionsInArea(
                                 player.CurTownId,
                                 player.CurAreaId,
                                 excludeCharacterId: -1,
                                 recipient.ListenerPort))
                    {
                        var visibleUserId =
                            visibleSession?.Player?.UserId ?? (ushort)0;
                        var party = visibleUserId == 0
                            ? null
                            : _partyManager.GetPartySnapshotByUser(
                                visibleUserId);
                        if (party == null ||
                            party.IsSinglePlay ||
                            !seenPartyIds.Add(party.PartyId))
                        {
                            continue;
                        }
                        visibleParties.Add(party);
                    }
                    visibleParties.Sort(
                        (left, right) =>
                            left.PartyId.CompareTo(right.PartyId));
                    var currentPartyIds = new HashSet<int>(
                        visibleParties.Select(value => value.PartyId));
                    _publishedTownPartyIds.TryGetValue(
                        expectedSessionId,
                        out var priorPartyIds);
                    var removedPartyIds = priorPartyIds == null
                        ? Array.Empty<int>()
                        : priorPartyIds
                            .Where(value => !currentPartyIds.Contains(value))
                            .OrderBy(value => value)
                            .ToArray();

                    var packet = GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.PARTY_INFO,
                        PartyInfoNotiBuilder.BuildList(
                            visibleParties,
                            removedPartyIds));
                    var projectionSent = false;
                    var sendCompleted = await Game.Session.SessionDirectory
                        .TrySendBestEffortAsync(
                            async cancellationToken =>
                            {
                                projectionSent = await recipient
                                    .TrySendPacketAsync(
                                        packet,
                                        cancellationToken,
                                        () => IsCurrentPartyListRecipient(
                                            recipient,
                                            expectedSessionId,
                                            expectedCharacterId,
                                            expectedUserId,
                                            expectedTownId,
                                            expectedAreaId,
                                            expectedListenerPort));
                            },
                            $"party list uid={expectedUserId}");
                    var sent = sendCompleted && projectionSent;
                    if (sent)
                    {
                        _publishedTownPartyIds[expectedSessionId] =
                            currentPartyIds;
                        FileLogger.Log(
                            $"[{ProtocolName}] PARTY_LIST projection: " +
                            $"uid={player.UserId} " +
                            $"town={player.CurTownId} " +
                            $"area={player.CurAreaId} " +
                            $"parties=[{string.Join(",", currentPartyIds.OrderBy(
                                value => value))}] " +
                            $"removed=[{string.Join(",", removedPartyIds)}]");
                    }
                }
            }
            catch (Exception ex)
            {
                // Public discovery is supplemental. It must never abort an
                // already-committed party mutation, town arrival, or dungeon
                // entry projection.
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_LIST projection failed: " +
                    ex.Message);
            }
            finally
            {
                if (gateAcquired)
                    _partyListPublishGate.Release();
            }
        }

        private bool IsCurrentPartyListRecipient(
            EnhancedClientSession session,
            Guid expectedSessionId,
            int expectedCharacterId,
            ushort expectedUserId,
            byte expectedTownId,
            byte expectedAreaId,
            int expectedListenerPort)
        {
            var player = session?.Player;
            return player != null
                   && session.SessionId == expectedSessionId
                   && session.ListenerPort == expectedListenerPort
                   && player.CharacterId == expectedCharacterId
                   && player.UserId == expectedUserId
                   && player.CurTownId == expectedTownId
                   && player.CurAreaId == expectedAreaId
                   && player.TownPresenceReady
                   && player.UserState == 0x00
                   && player.CurrentRun == null
                   && IsDirectoryCurrent(session)
                   && _partyManager.GetPartySnapshotByUser(
                       expectedUserId) == null;
        }

        internal static bool IsSameGameChannel(
            EnhancedClientSession left,
            EnhancedClientSession right)
        {
            if (left == null || right == null)
                return false;

            // listenerPort=0 只存在于旧的单元/协议自测；真实游戏会话始终
            // 携带实际监听端口，必须完全相同才允许直接社交。
            return left.ListenerPort <= 0 ||
                   right.ListenerPort <= 0 ||
                   left.ListenerPort == right.ListenerPort;
        }

        internal static byte[] BuildCrossAreaPartyInviteContextPacket(
            EnhancedClientSession inviter,
            EnhancedClientSession invitee)
        {
            var inviterPlayer = inviter?.Player;
            var inviteePlayer = invitee?.Player;
            if (!IsSameGameChannel(inviter, invitee) ||
                inviterPlayer?.UserId == 0 ||
                inviteePlayer?.UserId == 0 ||
                !Game.Session.SessionDirectory.IsTownPresence(
                    inviterPlayer) ||
                !Game.Session.SessionDirectory.IsTownPresence(
                    inviteePlayer) ||
                (inviterPlayer.CurTownId == inviteePlayer.CurTownId &&
                 inviterPlayer.CurAreaId == inviteePlayer.CurAreaId))
            {
                return null;
            }

            // 0x0007 只带邀请者 UID。单向好友关系下，接收端可能没有
            // 邀请者的全局用户记录；先复用好友系统已验证的 subtype0
            // 注册该 UID，不发 USER_AREA/AREA_USERS，不制造跨区域位置实体。
            var body = UserInfoSubtype0Builder.BuildNotificationBody(
                UnitedFriendSystem.BuildUserInfoRecord(inviterPlayer));
            return GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.USERINFO,
                body);
        }

        internal static byte[][] BuildPartyHostProjectionPackets(
            Party party,
            bool includeRealtime)
        {
            var leader = party?.GetMember(party.LeaderUserId);
            if (leader == null ||
                leader.SlotIndex >= PartyConstants.MaxMembers)
            {
                return Array.Empty<byte[]>();
            }

            var packets = new List<byte[]>(includeRealtime ? 3 : 2)
            {
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x001A,
                    UdpHostBuilder.BuildHostSlot(leader.SlotIndex)),
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0009,
                    PartyInfoNotiBuilder.Build(party, 2)),
            };
            if (includeRealtime)
            {
                packets.Add(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0099,
                    PartyRealtimeInfoBuilder.Build(party)));
            }
            return packets.ToArray();
        }

        private async Task PublishPartyHostProjectionAsync(
            int partyId,
            ushort expectedLeaderUserId,
            bool includeRealtime,
            string reason)
        {
            if (_sessions == null || partyId <= 0)
                return;

            using var gate = await AcquireBroadcastGateAsync(partyId);
            var party = _partyManager.GetPartySnapshot(partyId);
            if (party == null ||
                party.LeaderUserId != expectedLeaderUserId)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY host projection stale: " +
                    $"party={partyId} leader={expectedLeaderUserId} " +
                    $"reason={reason}");
                return;
            }

            var packets = BuildPartyHostProjectionPackets(
                party,
                includeRealtime);
            if (packets.Length == 0)
                return;

            foreach (var member in party.MembersBySlot())
            {
                if (!_sessions.TryGet(member.CharacterId, out var recipient) ||
                    recipient?.TcpClient == null ||
                    recipient.SessionId != member.SessionId)
                {
                    continue;
                }

                foreach (var packet in packets)
                {
                    await Game.Session.SessionDirectory
                        .TrySendBestEffortAsync(
                            cancellationToken =>
                                recipient.SendPacketAsync(
                                    packet,
                                    cancellationToken),
                            $"party={partyId} host projection " +
                            $"reason={reason} uid={member.UserId}");
                }
            }

            var leader = party.GetMember(party.LeaderUserId);
            FileLogger.Log(
                $"[{ProtocolName}] PARTY host projection: " +
                $"party={partyId} leader={party.LeaderUserId} " +
                $"slot={leader?.SlotIndex ?? 0} " +
                $"realtime={includeRealtime} reason={reason}");
        }

        // 向队伍全体在线成员广播 PARTY_INFO(0x09)+ 实时信息(0x99)+ P2P 端点(0x0B)。
        // A21 当前最稳的 formation 基线是双方统一发送完整 type=0。
        // type=2 分流是本轮双端退出的最强回归点，先回到该基线再实机闭环。
        // includeP2p=false: 只发 0x09 名册刷新, 不重发 0x0B/0x99。委托队长用: 队伍已 P2P 连着,
        //   swap 槽位后再重发 0x0B 会触发客户端 P2P 重握手 → 崩溃/超时(真机实测 A 断连)。换队长只需名册刷新。
        private Task BroadcastPartyInfo(
            Game.Party.Party party,
            bool includeP2p = true,
            Func<Task> afterRealtime = null)
        {
            if (_sessions == null || party == null)
                return Task.CompletedTask;

            return BroadcastPartyInfo(
                party.PartyId,
                includeP2p,
                afterRealtime);
        }

        private async Task BroadcastPartySettings(int partyId)
        {
            if (_sessions == null || partyId <= 0)
                return;

            using var gate = await AcquireBroadcastGateAsync(partyId);
            var party = _partyManager.GetPartySnapshot(partyId);
            if (party == null)
                return;
            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0009,
                PartyInfoNotiBuilder.Build(party, 1));
            var sent = 0;
            foreach (var member in party.MembersBySlot())
            {
                if (!_sessions.TryGet(member.CharacterId, out var recipient) ||
                    recipient?.TcpClient == null ||
                    recipient.SessionId != member.SessionId)
                {
                    continue;
                }

                if (await Game.Session.SessionDirectory
                        .TrySendBestEffortAsync(
                            cancellationToken => recipient.SendPacketAsync(
                                packet,
                                cancellationToken),
                            $"party={partyId} phase=settings " +
                            $"characterId={member.CharacterId}"))
                {
                    sent++;
                }
            }
            FileLogger.Log(
                $"[{ProtocolName}] PARTY settings projection: " +
                $"party={partyId} leader={party.LeaderUserId} " +
                $"sent={sent}/{party.Count} " +
                $"settings={BitConverter.ToString(party.PartyInfoBlock)}");
        }

        private async Task BroadcastPartyInfo(
            int partyId,
            bool includeP2p = true,
            Func<Task> afterRealtime = null)
        {
            if (_sessions == null || partyId <= 0)
                return;

            using var gate = await AcquireBroadcastGateAsync(partyId);
            await BroadcastPartyInfoWithinGate(
                partyId,
                includeP2p,
                afterRealtime);
        }

        private async Task BroadcastPartyInfoWithinGate(
            int partyId,
            bool includeP2p = true,
            Func<Task> afterRealtime = null)
        {
            // PartyManager returns a detached generation. Every packet and
            // relay binding below is derived from this same immutable view.
            var party = _partyManager.GetPartySnapshot(partyId);
            if (party == null)
            {
                _udpRelay?.CloseRoom(partyId);
                return;
            }

            var members = party.MembersBySlot();
            var fullInfo0x09 = PartyInfoNotiBuilder.Build(party, 0);
            FileLogger.Log(
                $"[{ProtocolName}] BroadcastPartyInfo " +
                $"party={party.PartyId} leader={party.LeaderUserId} " +
                $"includeP2p={includeP2p} " +
                $"members=[{string.Join(",", members.Select(
                    m => $"uid{m.UserId}@slot{m.SlotIndex}"))}] " +
                $"recipients={members.Count} " +
                $"info0x09={System.BitConverter.ToString(fullInfo0x09)}");
            var fullInfoPacket = GamePacketEnvelopeBuilder.Build(
                0x00, 0x0009, fullInfo0x09);
            var rtPacket = includeP2p
                ? GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0099,
                    PartyRealtimeInfoBuilder.Build(party))
                : null;

            PartyUdpRelay.RoomSnapshot relaySnapshot = null;
            var relayMode =
                includeP2p &&
                TrySyncTestedRelayRoom(
                    _udpRelay,
                    party.PartyId,
                    members,
                    out relaySnapshot);
            if (!relayMode && _udpRelay != null && members.Count < 2)
            {
                _udpRelay.CloseRoom(party.PartyId);
            }
            else if (
                includeP2p &&
                !relayMode &&
                _udpRelay != null &&
                members.Count >= 2)
            {
                // Every recipient must see one coherent transport mode. If a
                // complete relay matrix cannot be published, discard any
                // older matrix before advertising the all-direct roster.
                _udpRelay.CloseRoom(party.PartyId);
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_IP_INFO relay matrix " +
                    $"unavailable; using one consistent direct roster " +
                    $"party={party.PartyId} members={members.Count}");
            }

            // P2P endpoint exchange (SC 0x0B). Direct mode has one shared
            // packet; relay mode is personalized for each recipient.
            var directIpPacket =
                includeP2p && !relayMode
                    ? GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000B,
                        PartyIpInfoBuilder.Build(members))
                     : null;
            var relayIpBytes =
                relayMode ? ResolveRelayIpBytes() : null;
            if (includeP2p)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_IP_INFO(0x0B) party=" +
                    $"{party.PartyId} relay={relayMode} " +
                    $"members={members.Count} endpoints=[" +
                    string.Join(
                        ",",
                        members.Select(m =>
                            $"{m.UserId}@{m.SlotIndex}=" +
                            $"{(m.IpBytes != null ? string.Join(".", m.IpBytes) : "?")}" +
                            $":{m.P2pPort}")) +
                    "]");
            }

            var recipients =
                new List<(
                    PartyMember Member,
                    EnhancedClientSession Session,
                    byte[] EndpointPacket)>(
                    members.Count);
            foreach (var member in members)
            {
                _sessions.TryGet(
                    member.CharacterId, out var session);
                if (session?.TcpClient == null ||
                    session.SessionId != member.SessionId)
                {
                    continue;
                }
                var endpointPacket = directIpPacket;
                if (relayMode)
                {
                    var relayBody =
                        PartyIpInfoBuilder.BuildForRelay(
                            members,
                            member.UserId,
                            relayIpBytes,
                            peer =>
                                relaySnapshot.TryGetPort(
                                    member.UserId,
                                    peer,
                                    out var port)
                                    ? port
                                    : 0);
                    endpointPacket = GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000B,
                        relayBody);
                }
                recipients.Add((member, session, endpointPacket));
            }

            // PARTY_INFO carries only UIDs. Before any roster refresh, make
            // every recipient aware of every peer so all member objects can
            // be created, including an existing follower and a fresh third
            // or fourth member in another town/area.
            var contextSends = new List<Task>();
            var recipientsByUid = recipients.ToDictionary(
                recipient => recipient.Member.UserId);
            foreach (var pair in BuildPartyFormationContextPlan(members))
            {
                if (!recipientsByUid.TryGetValue(
                        pair.RecipientUid,
                        out var recipient)
                    || !recipientsByUid.TryGetValue(
                        pair.SourceUid,
                        out var source))
                {
                    continue;
                }

                var contextPacket =
                    BuildPartyFormationUserContextPacket(source.Session);
                if (contextPacket == null)
                    continue;
                contextSends.Add(
                    Game.Session.SessionDirectory.TrySendBestEffortAsync(
                        cancellationToken =>
                            recipient.Session.SendPacketAsync(
                                contextPacket,
                                cancellationToken),
                        $"party={party.PartyId} phase=user-context " +
                        $"recipient={pair.RecipientUid} " +
                        $"source={pair.SourceUid}"));
            }
            if (contextSends.Count > 0)
                await Task.WhenAll(contextSends);

            // Preserve the client-proven cross-recipient formation phases:
            // realtime to all -> acceptance ACK -> endpoints to all -> roster.
            // PARTY_INFO type=3 is reserved for sessions that actually leave;
            // survivors must retain their live Party object while this
            // formation refresh is applied.
            if (includeP2p)
            {
                var realtimeSends = new List<Task>(recipients.Count);
                foreach (var recipient in recipients)
                {
                    realtimeSends.Add(
                        Game.Session.SessionDirectory
                            .TrySendBestEffortAsync(
                                cancellationToken =>
                                    recipient.Session.SendPacketAsync(
                                        rtPacket,
                                        cancellationToken),
                                $"party={party.PartyId} phase=realtime " +
                                $"characterId={recipient.Member.CharacterId}"));
                }
                if (realtimeSends.Count > 0)
                    await Task.WhenAll(realtimeSends);
            }

            if (includeP2p)
            {
                var endpointSends = new List<Task>(recipients.Count);
                foreach (var recipient in recipients)
                {
                    endpointSends.Add(
                        Game.Session.SessionDirectory
                            .TrySendBestEffortAsync(
                                cancellationToken =>
                                    recipient.Session.SendPacketAsync(
                                        recipient.EndpointPacket,
                                        cancellationToken),
                                $"party={party.PartyId} " +
                                "phase=endpoints-before-roster " +
                                $"characterId={recipient.Member.CharacterId}"));
                }
                if (endpointSends.Count > 0)
                    await Task.WhenAll(endpointSends);
            }

            if (afterRealtime != null)
                await afterRealtime();

            var rosterSends = new List<Task>(recipients.Count);
            foreach (var recipient in recipients)
            {
                rosterSends.Add(
                    Game.Session.SessionDirectory
                        .TrySendBestEffortAsync(
                            cancellationToken =>
                                recipient.Session.SendPacketAsync(
                                    fullInfoPacket,
                                    cancellationToken),
                            $"party={party.PartyId} phase=roster " +
                            $"type=0 " +
                            $"characterId={recipient.Member.CharacterId}"));
            }
            if (rosterSends.Count > 0)
                await Task.WhenAll(rosterSends);

            // The final type-0 roster has now created/replaced every member
            // object. Reapply each recipient's exact bootstrap endpoint bytes
            // so the binding lands on the final objects rather than a
            // temporary pre-roster object.
            if (includeP2p)
            {
                var endpointSends = new List<Task>(recipients.Count);
                foreach (var recipient in recipients)
                {
                    endpointSends.Add(
                        Game.Session.SessionDirectory.TrySendBestEffortAsync(
                            cancellationToken =>
                                recipient.Session.SendPacketAsync(
                                    recipient.EndpointPacket,
                                    cancellationToken),
                            $"party={party.PartyId} " +
                            "phase=endpoints-after-roster " +
                            $"characterId={recipient.Member.CharacterId}"));
                }
                if (endpointSends.Count > 0)
                    await Task.WhenAll(endpointSends);
            }

            if (includeP2p && rtPacket != null)
            {
                var postRosterRealtimeSends = new List<Task>(recipients.Count);
                foreach (var recipient in recipients)
                {
                    postRosterRealtimeSends.Add(
                        Game.Session.SessionDirectory
                            .TrySendBestEffortAsync(
                                cancellationToken =>
                                    recipient.Session.SendPacketAsync(
                                        rtPacket,
                                        cancellationToken),
                                $"party={party.PartyId} phase=realtime-after-roster " +
                                $"characterId={recipient.Member.CharacterId}"));
                }
                await Task.WhenAll(postRosterRealtimeSends);
            }
        }

        internal static byte[][] BuildDirectP2pProjectionPackets(
            Party party)
        {
            if (party == null)
                return Array.Empty<byte[]>();

            return new[]
            {
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x000B,
                    PartyIpInfoBuilder.Build(party)),
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0099,
                    PartyRealtimeInfoBuilder.Build(party)),
            };
        }

        internal static IReadOnlyList<(
            ushort RecipientUid,
            ushort SourceUid)> BuildPartyFormationContextPlan(
                IReadOnlyList<PartyMember> members)
        {
            var plan = new List<(ushort, ushort)>();
            if (members == null)
                return plan;

            foreach (var recipient in members)
            {
                foreach (var source in members)
                {
                    if (recipient.UserId != source.UserId)
                        plan.Add((recipient.UserId, source.UserId));
                }
            }
            return plan;
        }

        internal static byte[] BuildPartyFormationUserContextPacket(
            EnhancedClientSession source)
        {
            if (source?.Player == null || source.Player.UserId == 0)
                return null;

            var body = UserInfoSubtype0Builder.BuildNotificationBody(
                UnitedFriendSystem.BuildUserInfoRecord(source.Player));
            return GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.USERINFO,
                body);
        }

        internal static bool TrySyncTestedRelayRoom(
            PartyUdpRelay relay,
            int partyId,
            IReadOnlyList<PartyMember> members)
        {
            return TrySyncTestedRelayRoom(
                relay,
                partyId,
                members,
                out _);
        }

        internal static bool TrySyncTestedRelayRoom(
            PartyUdpRelay relay,
            int partyId,
            IReadOnlyList<PartyMember> members,
            out PartyUdpRelay.RoomSnapshot snapshot)
        {
            snapshot = null;
            if (relay == null ||
                partyId <= 0 ||
                members == null ||
                members.Count < 2)
            {
                return false;
            }

            // This is the field-tested control path: room allocation depends
            // only on immutable party member keys. It must not fail because
            // SessionDirectory is between generations.
            return relay.TrySyncRoom(
                partyId,
                members.Select(
                    member => (int)member.UserId).ToList(),
                out snapshot);
        }

        private byte[] ResolveRelayIpBytes()
        {
            if (_cachedRelayIpBytes != null)
                return _cachedRelayIpBytes;

            try
            {
                _cachedRelayIpBytes =
                    IPAddress.Parse(_udpRelay.PublicIp)
                        .MapToIPv4()
                        .GetAddressBytes();
            }
            catch
            {
                _cachedRelayIpBytes = new byte[] { 127, 0, 0, 1 };
            }
            return _cachedRelayIpBytes;
        }

        private async Task CloseRelayRoomAsync(int partyId)
        {
            using var gate = await AcquireBroadcastGateAsync(partyId);
            _udpRelay?.CloseRoom(partyId);
        }

        internal async Task<IDisposable> AcquireBroadcastGateAsync(int partyId)
        {
            BroadcastGateEntry entry;
            lock (_broadcastGatesLock)
            {
                if (!_broadcastGates.TryGetValue(partyId, out entry))
                {
                    entry = new BroadcastGateEntry();
                    _broadcastGates.Add(partyId, entry);
                }
                entry.ReferenceCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync();
                return new BroadcastGateLease(this, partyId, entry);
            }
            catch
            {
                ReleaseBroadcastGateReference(partyId, entry);
                throw;
            }
        }

        private void ReleaseBroadcastGate(
            int partyId, BroadcastGateEntry entry)
        {
            entry.Semaphore.Release();
            ReleaseBroadcastGateReference(partyId, entry);
        }

        private void ReleaseBroadcastGateReference(
            int partyId, BroadcastGateEntry entry)
        {
            lock (_broadcastGatesLock)
            {
                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0 &&
                    _broadcastGates.TryGetValue(partyId, out var current) &&
                    ReferenceEquals(current, entry))
                {
                    _broadcastGates.Remove(partyId);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class BroadcastGateEntry
        {
            internal readonly SemaphoreSlim Semaphore =
                new SemaphoreSlim(1, 1);
            internal int ReferenceCount;
        }

        private sealed class BroadcastGateLease : IDisposable
        {
            private PartyHandler _owner;
            private readonly int _partyId;
            private readonly BroadcastGateEntry _entry;

            internal BroadcastGateLease(
                PartyHandler owner, int partyId, BroadcastGateEntry entry)
            {
                _owner = owner;
                _partyId = partyId;
                _entry = entry;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseBroadcastGate(_partyId, _entry);
            }
        }

        private async Task
            CheckoutPartyForPvpInviteWithinTransitionAsync(
                EnhancedClientSession session,
                Action checkoutCommitted)
        {
            if (session?.Player == null)
            {
                throw new InvalidOperationException(
                    "PvP invite checkout requires a player");
            }

            var uid = session.Player.UserId;
            var result =
                _partyManager.Leave(
                    uid,
                    session.SessionId);
            if (!result.Ok)
            {
                if (_partyManager.GetPartyByUser(uid) != null)
                {
                    throw new InvalidOperationException(
                        "PvP invite party checkout rejected: " +
                        result.Reason);
                }

                checkoutCommitted?.Invoke();
                return;
            }

            // Leave is already durable at this point. Signal the PvP join
            // before any best-effort legacy party publications can block or
            // fail, so the room membership is never rolled back into a split
            // party/room state.
            checkoutCommitted?.Invoke();

            try
            {
                await SendPartyClearBestEffortAsync(
                    session,
                    GetDepartureClearParty(result),
                    $"pvp-invite-checkout uid={uid}");
                await PublishCommittedDepartureAsync(
                    result,
                    $"pvp-invite-checkout uid={uid}");
            }
            catch (Exception ex)
            {
                // Leave has already committed, matching native
                // CheckOutParty. Notification failure cannot roll back the
                // subsequent PvP join into a split party/room state.
                FileLogger.Log(
                    $"[{ProtocolName}] PvP invite party checkout " +
                    $"publication failed after commit: uid={uid} " +
                    $"error={ex.GetType().Name}: {ex.Message}");
            }
        }

        private static Task SendPvpInviteFailureAsync(
            EnhancedClientSession session,
            byte errorCode)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x000B,
                    new byte[] { 0, errorCode, 2 }));
        }

        public void Dispose()
        {
            if (_sessions != null)
                _sessions.SessionEnding -= OnSessionEndingAsync;
            // Process-lifetime gate: shutdown may still have a town/list or
            // SessionEnding callback in flight. Disposing it here can abort
            // committed party cleanup for no practical resource benefit.
        }

        // 在线会话里按"角色名字节"逐字节匹配目标(避开 GBK/字符串编码歧义)。
        private EnhancedClientSession FindSessionByCharacterName(byte[] nameBytes)
        {
            foreach (var s in _sessions.GetAllGameSessions())
            {
                var (scid, _) = SessionOwnerResolver.Resolve(s);
                if (scid <= 0) continue;
                var rec = _characterRepository.GetById(scid);
                if (rec?.Name != null && BytesEqual(rec.Name, nameBytes))
                    return s;
            }
            return null;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // 解析 [int32 len][len bytes] 的角色名参数(REQUEST_MEMBER_ENTER)。宽松容错。
        private static byte[] ParseNameArg(byte[] body)
        {
            if (body == null || body.Length < 4) return null;
            int len = System.BitConverter.ToInt32(body, 0);
            if (len < 0 || 4 + len > body.Length) return null;
            var name = new byte[len];
            System.Array.Copy(body, 4, name, 0, len);
            return name;
        }
    }
}
