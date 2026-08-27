using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Party;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonEntryHandler
    {
        internal const ushort StartGameResponseType = 0x000F;
        internal const byte MercenaryContentErrorCode = 0xEB;
        private const string RaidSelectionRestrictionMessage =
            "\u5FC5\u987B\u52A0\u5165\u653B\u575A\u961F\u5E76\u5F00\u59CB\u653B\u575A\u540E\u624D\u80FD\u8FDB\u5165\u5730\u4E0B\u57CE\u3002";

        private readonly DungeonSharedServices _svc;
        private readonly DungeonMapHandler _mapHandler;

        internal DungeonEntryHandler(DungeonSharedServices svc, DungeonMapHandler mapHandler)
        {
            _svc = svc;
            _mapHandler = mapHandler;
        }

        internal async Task HandleRequestCircleEnter(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var wireType = (ushort)CmdPacketTypeA21.REQUEST_CIRCLE_ENTER;
            var responseBody = CircleDungeonEntryResponseBuilder.BuildRejected();
            var activeSelection = session?.Player?.CurrentDungeonSelection;
            if (!CircleDungeonEntryRequest.TryParse(body, out var request))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"REQUEST_CIRCLE_ENTER rejected: invalid body length=" +
                    $"{body?.Length ?? 0} expected={CircleDungeonEntryRequest.BodySize}");
            }
            else if (session?.Player == null
                || session.Player.CharacterId <= 0
                || session.GameSession?.QuestManager == null)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"REQUEST_CIRCLE_ENTER rejected: missing active game session " +
                    $"dungeon={request.DungeonId} quest={request.CircleQuestId}");
            }
            else if (activeSelection == null
                || !session.Player.IsCurrentDungeonSelection(activeSelection))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"REQUEST_CIRCLE_ENTER rejected: no current dungeon selection " +
                    $"cid={session.Player.CharacterId} dungeon={request.DungeonId} " +
                    $"quest={request.CircleQuestId}");
            }
            else
            {
                var decision = CircleDungeonEntryPolicy.Evaluate(
                    request.DungeonId,
                    request.CircleQuestId);
                if (decision.Allowed
                    && activeSelection.TryBindCircleEntry(
                        (int)request.DungeonId,
                        decision.CircleQuestId))
                {
                    responseBody = CircleDungeonEntryResponseBuilder.BuildSuccess(
                        decision.CircleQuestId);
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"REQUEST_CIRCLE_ENTER accepted for quest handshake: " +
                        $"cid={session.Player.CharacterId} dungeon={request.DungeonId} " +
                        $"quest={decision.CircleQuestId} " +
                        $"selection={activeSelection.SelectionId} gate=" +
                        $"{CircleDungeonEntryResponseBuilder.SuccessGateCandidate}");
                }
                else
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"REQUEST_CIRCLE_ENTER rejected: " +
                        $"cid={session.Player.CharacterId} dungeon={request.DungeonId} " +
                        $"quest={request.CircleQuestId} reason=" +
                        $"{(decision.Allowed ? "selection_state_changed" : decision.RejectReason.ToString())}");
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                wireType,
                responseBody));
        }

        internal async Task HandleEnterSelectDungeon(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!EnterSelectDungeonRequest.TryParse(body, out var request))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"ENTER_SELECT_DUNGEON rejected: invalid body length={body?.Length ?? 0} " +
                    $"minimum={EnterSelectDungeonRequest.MinimumBodyLength}");
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.InvalidSelectionState);
                return;
            }

            await HandleEnterSelectDungeonCore(session, header, request);
        }

        private async Task HandleEnterSelectDungeonCore(
            EnhancedClientSession session,
            GamePacketHeader header,
            EnterSelectDungeonRequest? request)
        {
            var isA21TutorialEntry = IsFirstA21TutorialEntry(session);
            var requestDiagnostic = request.HasValue
                ? $"source=wire dungeon={request.Value.DungeonId} " +
                    $"bodyLength={request.Value.BodyLength} " +
                    $"trailing={request.Value.TrailingLength} " +
                    $"tailNonZero={request.Value.HasNonZeroTrailingBytes}"
                : "source=party-projection";
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: " +
                $"cid={session.Player.CharacterId} uid={session.Player.UserId} " +
                $"{requestDiagnostic} town={session.Player.CurTownId} " +
                $"area={session.Player.CurAreaId}");
            if (!CanEnterRaidDungeonSelection(session))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"ENTER_SELECT_DUNGEON rejected by raid state: " +
                    $"cid={session.Player.CharacterId} uid={session.Player.UserId}");
                await SendRaidSelectionRejectedAsync(session, header.type);
                return;
            }
            if (_svc.MercenaryRestrictions != null
                && !_svc.MercenaryRestrictions.CanEnterContent(session.Player.CharacterId))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: " +
                    $"MERCENARY_CONTENT_BLOCKED cid={session.Player.CharacterId}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    StartGameResponseType,
                    BuildMercenaryContentErrorBody()));
                return;
            }

            try
            {
                var selection = BeginDungeonSelection(
                    session.Player,
                    isA21TutorialEntry);
                if (selection == null)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"ENTER_SELECT_DUNGEON rejected by active or returning state: " +
                        $"cid={session.Player.CharacterId} " +
                        $"run={session.Player.CurrentRun?.RunId ?? 0} " +
                        $"selection={session.Player.CurrentDungeonSelection?.SelectionId ?? 0}");
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        header.type,
                        DungeonAdmissionReject.InvalidSelectionState);
                    return;
                }
                else
                {
                    var anchor = selection.ReturnAnchor;
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"ENTER_SELECT_DUNGEON return anchor: " +
                        $"selection={selection.SelectionId} town={anchor.TownId} " +
                        $"area={anchor.AreaId} pos=({anchor.X},{anchor.Y})");
                }
                session.Player.UserState = 0x01;
                // 进本 → 状态繁忙：同频道在线好友推 USERINFO(0x0002) 更新场景实体状态。
                await UnitedFriendSystem.NotifyUserStateChanged(
                    session, _svc.Sessions);

                // NOTI 0x0002 subtype1 (ADDITION): dynamically built from structured table (same path as init flow)
                int cid = session.Player.CharacterId;
                HonorLevelSummary honorSummary = null;
                if (cid <= 0)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON ERROR: CharacterId<=0, USERINFO not sent");
                }
                else
                {
                    var record = _svc.CharacterRepository.GetById(cid);
                    var addition = _svc.Subtype1Repository.HasData(cid) ? _svc.Subtype1Repository.Load(cid) : null;
                    if (record != null && addition != null)
                    {
                        var accountId = session.Account?.AccountId ?? record.AccountId;
                        var accountCharacters = _svc.CharacterRepository.ListByAccount(accountId);
                        honorSummary = _svc.HonorLevel.LoadSummary(accountId, accountCharacters);
                        AdventureGroupUserInfoSynchronizer.ApplyToUserInfoAddition(addition, accountCharacters);
                        _svc.HonorLevel.ApplyToUserInfoAddition(
                            addition, accountId, accountCharacters, honorSummary);
                        var skillSnap = _svc.ProgressNotifications
                            .LoadSyncedSkillState(cid, record.Level).Skills;
                        var w = new GamePacketWriter();
                        UserInfoBodyBuilder.WriteA21Subtype1Prefix(
                            w,
                            (ushort)record.CharacterId,
                            addition.ManageLevel,
                            addition.AuraSkinFlag);
                        w.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(
                            addition,
                            skillSnap,
                            record.Appearance));
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, w.ToArray()));
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: NOTI 2 type1 dynamic body");
                    }
                    else
                    {
                        FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON ERROR: record={record != null} addition={addition != null}, USERINFO not sent (no fallback)");
                    }
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    new byte[] { 0x01 }));

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003, EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001A, UdpHostBuilder.BuildUnavailable()));
                await _svc.PersistentMechanisms.RestoreBeforeSelectionAsync(session);
                if (!isA21TutorialEntry)
                {
                    var hellPartySelection = BuildHellPartySelectionState(
                        session.Player);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x001B,
                        EnterSelectDungeonStateBuilder.BuildA21EnterSelectDungeon(
                            hellPartySelection.UserIds,
                            hellPartySelection.BlockedSlots)));
                }
                else
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"ENTER_SELECT_DUNGEON: defer A21 tutorial NOTI 27 " +
                        "until CHANGE_TUTORIAL_FLAG");
                }
                await _svc.GrowthCapsuleSync.SendExpProgressAsync(
                    session, "enter-select-dungeon", honor: honorSummary);
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: state packets and account EXP progress sent OK");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON EXCEPTION: {ex}");
            }
        }

        internal static bool IsRaidDungeonSelectionAllowed(
            int listenerPort,
            Game.Raid.RaidSnapshot raid)
            => !GameNetworkConfig.IsRaidListener(listenerPort)
               || raid?.State == 2;

        private bool CanEnterRaidDungeonSelection(
            EnhancedClientSession session)
        {
            if (session?.Player == null)
                return false;
            if (!GameNetworkConfig.IsRaidListener(session.ListenerPort))
                return true;

            return _svc.RaidManager != null
                && _svc.RaidManager.TryGetByUser(
                    session.Player.UserId,
                    out var raid)
                && IsRaidDungeonSelectionAllowed(
                    session.ListenerPort,
                    raid);
        }

        private async Task SendRaidSelectionRejectedAsync(
            EnhancedClientSession session,
            ushort wireType)
        {
            await _svc.AdmissionRejects.SendAsync(
                session,
                wireType,
                DungeonAdmissionReject.InvalidSelectionState);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
                ServerNoticeMessageBuilder.Build(
                    RaidSelectionRestrictionMessage)));
        }

        private static DungeonSelectionContext BeginDungeonSelection(
            Game.Session.PlayerContext player,
            bool isA21TutorialEntry)
        {
            if (player == null)
                return null;

            var townId = player.CurTownId;
            var areaId = player.CurAreaId;
            var x = player.CurPosX;
            var y = player.CurPosY;
            if (Town.TryGetDungeonGateReturnInfo(
                    townId,
                    areaId,
                    out var configured))
            {
                townId = configured.Town;
                areaId = configured.Area;
                x = configured.X;
                y = configured.Y;
            }

            return player.BeginDungeonSelection(new DungeonTownReturnAnchor(
                townId,
                areaId,
                x,
                y,
                player.CurDirection,
                player.CurAreaState),
                isA21TutorialEntry);
        }

        private HellPartySelectionState BuildHellPartySelectionState(
            Game.Session.PlayerContext player)
        {
            var state = new HellPartySelectionState();
            if (player == null)
                return state;

            var party = _svc.PartyManager?.GetPartyByUser(player.UserId);
            if (party == null || party.Count == 0)
            {
                state.Members.Add(new HellPartySelectionMember
                {
                    UserId = player.UserId,
                    CharacterId = player.CharacterId,
                    SlotIndex = 0,
                });
            }
            else
            {
                foreach (var member in party.MembersBySlot())
                {
                    state.Members.Add(new HellPartySelectionMember
                    {
                        UserId = member.UserId,
                        CharacterId = member.CharacterId,
                        SlotIndex = member.SlotIndex,
                    });
                }
            }

            if (Town.TryGetDungeonGateReturnInfo(
                    player.CurTownId,
                    player.CurAreaId,
                    out var gate)
                && gate.WorldMapAreaId > 0)
            {
                state.WorldMapArea =
                    WorldMap.GetAreaById(gate.WorldMapAreaId);
            }

            foreach (var member in state.Members)
            {
                state.UserIds.Add(member.UserId);
                if (state.WorldMapArea?.HellDungeon != true)
                    continue;

                try
                {
                    if (_svc.EntryAdmission.CheckHellQuestRequirement(
                            member.CharacterId,
                            state.WorldMapArea,
                            out var missingQuestId))
                    {
                        continue;
                    }

                    state.BlockedSlots.Add(member.SlotIndex);
                    state.BlockReasons.Add(
                        $"slot={member.SlotIndex}:quest={missingQuestId}");
                }
                catch (Exception ex)
                {
                    state.BlockedSlots.Add(member.SlotIndex);
                    state.BlockReasons.Add(
                        $"slot={member.SlotIndex}:error={ex.Message}");
                }
            }

            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"ENTER_SELECT_DUNGEON hell eligibility: " +
                $"cid={player.CharacterId} worldMapArea=" +
                $"{state.WorldMapArea?.AreaId ?? -1} " +
                $"hell={state.WorldMapArea?.HellDungeon == true} " +
                $"blocked=[{string.Join(",", state.BlockReasons)}]");
            return state;
        }

        private sealed class HellPartySelectionState
        {
            internal WorldMapArea WorldMapArea;
            internal List<HellPartySelectionMember> Members { get; } =
                new List<HellPartySelectionMember>();
            internal List<ushort> UserIds { get; } = new List<ushort>();
            internal List<ushort> BlockedSlots { get; } = new List<ushort>();
            internal List<string> BlockReasons { get; } = new List<string>();
        }

        private sealed class HellPartySelectionMember
        {
            internal ushort UserId;
            internal int CharacterId;
            internal ushort SlotIndex;
        }

        private bool IsFirstA21TutorialEntry(
            EnhancedClientSession session)
        {
            var player = session?.Player;
            if (player == null
                || player.CharacterId <= 0
                || player.Level != 1)
            {
                return false;
            }

            var snapshot = new SelectCharacterInitializationSnapshot();
            try
            {
                _svc.CharacterStateRepository.LoadFlags(
                    player.CharacterId,
                    snapshot);
                return snapshot.AckTutorialSkipable == 0;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"A21 tutorial state probe failed: cid={player.CharacterId} " +
                    $"error={ex.Message}");
                return false;
            }
        }

        internal Task HandleSelectDungeon(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => HandleSelectDungeonCore(
                session,
                header,
                body,
                linkedSourceDungeonId: 0,
                expectedPredecessorIdentity: null);

        private async Task HandleSelectDungeonCore(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            int linkedSourceDungeonId,
            DungeonRunIdentity? expectedPredecessorIdentity)
        {
            if (!CanEnterRaidDungeonSelection(session))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON rejected by raid state: " +
                    $"cid={session?.Player?.CharacterId} uid={session?.Player?.UserId}");
                await SendRaidSelectionRejectedAsync(session, header.type);
                return;
            }

            var predecessorRun = session?.Player?.CurrentRun;
            var predecessorGeneration =
                session?.Player?.CurrentDungeonRunGeneration ?? 0;
            var expectedSelection = expectedPredecessorIdentity.HasValue
                ? null
                : session?.Player?.CurrentDungeonSelection;
            var isA21TutorialEntry = expectedSelection?.IsA21TutorialEntry == true;
            if (expectedPredecessorIdentity.HasValue
                && (predecessorRun == null
                    || !predecessorRun.Matches(
                        expectedPredecessorIdentity.Value)))
            {
                return;
            }
            if (!expectedPredecessorIdentity.HasValue
                && (predecessorRun != null
                    || expectedSelection == null
                    || !IsEntrySourceCurrent(
                        session,
                        predecessorRun,
                        predecessorGeneration,
                        expectedSelection)))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON rejected outside the current selection: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"run={predecessorRun?.RunId ?? 0} " +
                    $"selection={expectedSelection?.SelectionId ?? 0}");
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.InvalidSelectionState);
                return;
            }

            var req = Network.Parsers.Dungeon.SelectDungeonRequest.Parse(body);
            var entryLimitDungeonId = req.DungeonId;
            try
            {
                var resolvedDungeonId = _svc.TowerOfDespairProgress.ResolveEntryDungeonId(
                    session.Player.CharacterId,
                    req.DungeonId);
                if (resolvedDungeonId != req.DungeonId)
                {
                    entryLimitDungeonId = req.DungeonId;
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] TOWER_OF_DESPAIR_ENTRY: cid={session.Player.CharacterId} requested={req.DungeonId} resolved={resolvedDungeonId}");
                    req = new Network.Parsers.Dungeon.SelectDungeonRequest(
                        resolvedDungeonId,
                        req.Difficulty,
                        req.Flag1,
                        req.Flag2);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] TOWER_OF_DESPAIR_ENTRY ERROR: cid={session.Player.CharacterId} requested={req.DungeonId}: {ex.Message}");
            }

            if (!WorldMap.IsStoryDungeon(req.DungeonId)
                && !DungeonData.MeetsMinimumRequiredLevel(
                    req.DungeonId,
                    session.Player.Level,
                    out var minimumRequiredLevel))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON level rejected: " +
                    $"cid={session.Player.CharacterId} dungeon={req.DungeonId} " +
                    $"level={session.Player.Level} required={minimumRequiredLevel}");
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.DungeonUnavailable);
                return;
            }

            linkedSourceDungeonId =
                await ResolveLinkedDungeonSelectionSourceAsync(
                    session,
                    header,
                    req.DungeonId,
                    req.Difficulty,
                    linkedSourceDungeonId);
            if (linkedSourceDungeonId < 0)
            {
                return;
            }
            if (!IsEntrySourceCurrent(
                    session,
                    predecessorRun,
                    predecessorGeneration,
                    expectedSelection))
            {
                return;
            }

            ushort preferredCircleQuestId = 0;
            if (expectedSelection?.TryConsumeCircleEntry(
                    req.DungeonId,
                    out var pendingCircleQuestId) == true)
            {
                preferredCircleQuestId = pendingCircleQuestId;
            }

            List<ActiveQuest> activeQuests = null;
            HashSet<int> activeQuestIds = null;
            HashSet<int> clearedQuestIds = null;
            try
            {
                var connStr = _svc.ConnectionString;
                activeQuests = QuestService.LoadActiveQuests(
                    connStr,
                    session.Player.CharacterId);
                if (activeQuests.Count > 0)
                {
                    activeQuestIds = new HashSet<int>(
                        activeQuests.ConvertAll(q => (int)q.QuestId));
                }
                var clearedFlags = new Game.Quests.QuestRepository(connStr)
                    .LoadClearedFlags(session.Player.CharacterId);
                if (clearedFlags.Count > 0)
                    clearedQuestIds = new HashSet<int>(clearedFlags.Keys);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] SELECT_DUNGEON ERROR: " +
                    $"quest load failed: {ex.Message}");
            }

            (PvfLib.MazeInfo Maze, int Index)? preferredCircleSelection = null;
            string preferredCircleDiagnostic = null;
            if (preferredCircleQuestId != 0)
            {
                if (activeQuestIds?.Contains(preferredCircleQuestId) != true
                    || !DungeonData.TrySelectActiveQuestMaze(
                        req.DungeonId,
                        req.Difficulty,
                        preferredCircleQuestId,
                        out var circleSelection,
                        diagnostic => preferredCircleDiagnostic = diagnostic))
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"SELECT_DUNGEON circle route rejected: " +
                        $"cid={session.Player.CharacterId} dungeon={req.DungeonId} " +
                        $"quest={preferredCircleQuestId} active=" +
                        $"{(activeQuestIds?.Contains(preferredCircleQuestId) == true ? 1 : 0)} " +
                        $"diagnostic={preferredCircleDiagnostic ?? "not_active"}");
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        header.type,
                        DungeonAdmissionReject.DungeonUnavailable);
                    return;
                }

                preferredCircleSelection = circleSelection;
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON circle route bound: " +
                    $"cid={session.Player.CharacterId} dungeon={req.DungeonId} " +
                    $"quest={preferredCircleQuestId} maze={circleSelection.Index}");
            }

            var admission = WorldMap.EvaluateDungeonAdmission(
                req.DungeonId,
                session.Player.Level,
                activeQuestIds,
                clearedQuestIds);
            if (!admission.Allowed)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON admission rejected: " +
                    $"cid={session.Player.CharacterId} dungeon={req.DungeonId} " +
                    $"mode={admission.Mode} reason={admission.Reason} " +
                    $"requiredQuests={string.Join(",", admission.RequiredQuestIds)}");
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.DungeonUnavailable);
                return;
            }

            var entryParty = session?.Player == null
                ? null
                : _svc.PartyManager?.GetPartyByUser(session.Player.UserId);
            var entryPartyMemberCount = entryParty == null
                ? 1
                : Math.Max(1, Math.Min(4, entryParty.Count));
            var entryRewardPolicy = DungeonRewardPolicyData.Resolve(req.DungeonId);
            if (!DungeonInteractionPolicy.Resolve(entryRewardPolicy)
                .AllowsPartyState(entryParty != null))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON interaction policy rejected party: " +
                    $"cid={session.Player.CharacterId} dungeon={req.DungeonId} " +
                    $"policy={entryRewardPolicy.Kind} partyId={entryParty.PartyId} " +
                    $"partyCount={entryPartyMemberCount}");
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.DungeonUnavailable);
                return;
            }

            var experienceBonusPlan =
                DungeonEntryExperienceBonusPlan.Capture(
                    session,
                    entryParty,
                    _svc.Sessions,
                    entryPartyMemberCount);
            var isDimensionDungeon = DungeonData.IsDimensionDungeon(req.DungeonId);
            if (!await TryValidateEntryLimitAsync(
                    session,
                    header.type,
                    entryLimitDungeonId,
                    isDimensionDungeon))
            {
                return;
            }

            // 塔类副本分流: dungeonKind==1 走专属流程(NOTI 142+143, 非普通副本的 START_MAP)
            if (_svc.DeathTower.TryCreateSession(req.DungeonId, out var tower))
            {
                await DungeonMechanismCoordinator.ClearRunEffectsAsync(
                    session,
                    "select_tower_replace_run");
                if (!IsEntrySourceCurrent(
                        session,
                        predecessorRun,
                        predecessorGeneration,
                        expectedSelection))
                {
                    return;
                }
                EntryCostResult towerValidation = null;
                if (!TryGetOwnedInventoryLease(session, out var towerLease)
                    || !_svc.EntryAdmission.TryPrepareTower(
                        towerLease,
                        tower,
                        out var towerPreparation,
                        out towerValidation))
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"SELECT_DUNGEON tower entry item rejected: " +
                        $"cid={session.Player.CharacterId} " +
                        $"dungeon={req.DungeonId} " +
                        $"reason={towerValidation?.FailReason ?? "inventory lease missing"}");
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        header.type,
                        ResolveEntryAdmissionReject(
                            towerValidation,
                            ResolvePartySlot(session)));
                    return;
                }
                if (!DungeonRunLifecycle.BeginTowerRun(
                    session,
                    req.DungeonId,
                    tower,
                    req.Difficulty,
                    _svc.InstanceRegistry,
                    experienceBonusPlan.ForParticipant(session),
                    expectedSelection))
                {
                    return;
                }
                var towerRun = session.Player.CurrentRun;
                if (towerRun == null || !ReferenceEquals(towerRun.Tower, tower))
                    return;
                var towerRunIdentity = towerRun.CaptureIdentity();
                var towerEntryCost = _svc.EntryAdmission.TryCommit(
                    towerLease,
                    towerPreparation);
                if (!towerEntryCost.Success)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"SELECT_DUNGEON tower entry commit rejected: " +
                        $"cid={session.Player.CharacterId} " +
                        $"dungeon={req.DungeonId} " +
                        $"reason={towerEntryCost.FailReason}");
                    await RejectEntryAdmissionAsync(
                        session,
                        header.type,
                        towerRun,
                        ResolveEntryAdmissionReject(
                            towerEntryCost,
                            ResolvePartySlot(session)));
                    return;
                }
                if (!await TryConsumeEntryLimitAsync(
                        session,
                        header.type,
                        towerRun,
                        entryLimitDungeonId,
                        isDimensionDungeon))
                {
                    return;
                }
                RegisterActiveParticipant(session, towerRun);
                // 城镇残留白影：塔进本提交后离开城镇，向旧区域广播不含离开者的名册清残留白影。
                await NotifyTownAreaRosterDepartureAsync(session);
                await SendEntryCostUpdates(
                    session,
                    towerRunIdentity,
                    towerEntryCost,
                    "death-tower-ticket");
                if (!session.Player.IsCurrentDungeonRun(towerRunIdentity))
                    return;
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON tower entry item accepted: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={req.DungeonId} " +
                    $"alternative={towerEntryCost.AlternativeIndex} " +
                    $"updates={towerEntryCost.ConsumedItems.Count}");
                await _svc.DeathTower.SendEntryPacketsAsync(session, tower, req.Difficulty);
                if (!session.Player.IsCurrentDungeonRun(towerRunIdentity))
                    return;
                return;
            }

            await DungeonMechanismCoordinator.ClearRunEffectsAsync(
                session,
                "select_dungeon_replace_run");
            if (!IsEntrySourceCurrent(
                    session,
                    predecessorRun,
                    predecessorGeneration,
                    expectedSelection))
            {
                return;
            }
            if (!DungeonRunLifecycle.BeginRun(
                session,
                req.DungeonId,
                req.Difficulty,
                instanceRegistry: _svc.InstanceRegistry,
                experienceBonusSnapshot:
                    experienceBonusPlan.ForParticipant(session),
                expectedSelection: expectedSelection))
            {
                return;
            }
            var run = session.Player.CurrentRun;
            var runIdentity = run.CaptureIdentity();
            // The first A21 tutorial is a normal flow regardless of the
            // dungeon id selected by the character's job. Ignore stale hell
            // flags so they cannot alter its map projection.
            run.HellMode = !isA21TutorialEntry
                && req.HellPartyRequestFlag != 0
                && DungeonData.IsHellDungeon(req.DungeonId);

            WarmUpDropConfigs(run.HellMode);

            if (req.HellPartyRequestFlag != 0)
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON: manual hell requested dungeon={req.DungeonId} flag1={req.HellPartyRequestFlag} flag2={req.HellPartyDifficultyFlag} tutorial={isA21TutorialEntry} enabled={run.HellMode}");

            run.QuestSnapshot = QuestRunSnapshot.Capture(activeQuests);
            string mazeSelectionDiagnostic = preferredCircleDiagnostic;
            var selection = preferredCircleSelection.HasValue
                ? preferredCircleSelection.Value
                : DungeonData.SelectDungeonMaze(
                    req.DungeonId,
                    req.Difficulty,
                    activeQuestIds,
                    clearedQuestIds,
                    diagnostic => mazeSelectionDiagnostic = diagnostic);
            run.MazeIndex = selection.Index;
            run.MazeQuestConnected = DungeonData.IsQuestConnectedSelection(
                req.DungeonId,
                selection.Maze,
                activeQuestIds,
                req.Difficulty);
            run.ActiveQuestMazeQuestId = DungeonData.ResolveActiveQuestMazeQuestId(
                req.DungeonId,
                selection.Maze,
                activeQuestIds,
                req.Difficulty);
            var storyExperienceBonus =
                DungeonStoryExperienceProfilePolicy.Capture(run);
            if (storyExperienceBonus.IsStoryRun)
            {
                run.TryFreezeStoryExperienceProfile(
                    storyExperienceBonus.RatePercent,
                    storyExperienceBonus.ExperienceDifficulty);
                FileLogger.Log(
                    $"[DungeonExperience] story profile frozen: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={req.DungeonId} " +
                    $"difficulty={req.Difficulty} " +
                    $"experienceDifficulty=" +
                    $"{storyExperienceBonus.ExperienceDifficulty} " +
                    $"quest={storyExperienceBonus.QuestId} " +
                    $"rate={storyExperienceBonus.RatePercent}%");
            }
            var bossPos = DungeonData.RandomizeBossPosition(selection.Maze.BossMap);
            run.BossMapPos = bossPos;
            var startPos = DungeonData.RandomizeStartPosition(selection.Maze.StartMap);
            run.MazeStartX = startPos != null ? startPos[0] : -1;
            run.MazeStartY = startPos != null ? startPos[1] : -1;
            run.MazeStartMapId = ResolveSelectedRoomMapId(
                req.DungeonId,
                selection.Index,
                run.MazeStartX,
                run.MazeStartY,
                bossPos);
            FileLogger.Log(
                $"[DungeonHandler] SELECT_DUNGEON route: " +
                $"cid={session.Player.CharacterId} dungeon={req.DungeonId} " +
                $"{mazeSelectionDiagnostic ?? $"difficulty={req.Difficulty} selectedMaze={selection.Index}"} " +
                $"flags=({req.HellPartyRequestFlag},{req.HellPartyDifficultyFlag}) hell={run.HellMode} " +
                $"questConnected={run.MazeQuestConnected} " +
                $"activeQuestMaze={run.ActiveQuestMazeQuestId} " +
                $"start=({run.MazeStartX},{run.MazeStartY}) startMap={run.MazeStartMapId} " +
                $"boss=({(bossPos != null && bossPos.Length >= 2 ? bossPos[0] : -1)}," +
                $"{(bossPos != null && bossPos.Length >= 2 ? bossPos[1] : -1)})");
            var randomizedObjectDefinition =
                DungeonRandomizedObjectDefinitionProjector.Project(selection.Maze);
            var randomizedObjects = DungeonRandomizedObjectSelectionService.Select(
                randomizedObjectDefinition);
            var clearConditionTemplate = new ClearConditionState(
                selection.Maze.ClearConditions);
            DungeonMechanismCoordinator.ConfigureSelection(
                session,
                selection.Maze,
                bossPos,
                activeQuests,
                "select_dungeon");
            if (!await PrepareTournamentEntryAsync(
                    session,
                    header,
                    run,
                    entryPartyMemberCount))
            {
                return;
            }
            if (!await PrepareBloodAltarEntryAsync(
                    session,
                    header,
                    run))
            {
                return;
            }
            ConfigureLinkedDungeonRunState(req.DungeonId, run);
            EntryCostResult entryValidation = null;
            if (!TryGetOwnedInventoryLease(session, out var entryLease)
                || !_svc.EntryAdmission.TryPrepareRun(
                    entryLease,
                    run,
                    run.Instance.Mechanisms.Tournament?.Definition,
                    run.HellMode,
                    req.HellPartyDifficultyFlag,
                    selection.Maze,
                    selection.Index,
                    session.Player.HellPartyGorgeousChallengeEnabled,
                    out var entryPreparation,
                    out entryValidation))
            {
                entryValidation ??= new EntryCostResult().Fail(
                    "owned inventory lease is missing",
                    EntryCostFailureKind.InvalidState);
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON admission preparation rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={req.DungeonId} " +
                    $"reason={entryValidation.FailReason}");
                await RejectEntryAdmissionAsync(
                    session,
                    header.type,
                    run,
                    ResolveEntryAdmissionReject(
                        entryValidation,
                        ResolvePartySlot(session)));
                return;
            }
            entryPreparation.ApplyTo(run);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            var selectionSnapshot = CaptureSelectionSnapshot(
                run,
                selection.Maze,
                entryPartyMemberCount,
                randomizedObjects,
                clearConditionTemplate);
            if (!run.Instance.TryFreezeSelection(selectionSnapshot))
                throw new InvalidOperationException("Dungeon selection was already frozen for this instance.");
            var entryCost = _svc.EntryAdmission.TryCommit(
                entryLease,
                entryPreparation);
            if (!entryCost.Success)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON admission commit rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={req.DungeonId} " +
                    $"reason={entryCost.FailReason}");
                await RejectEntryAdmissionAsync(
                    session,
                    header.type,
                    run,
                    ResolveEntryAdmissionReject(
                        entryCost,
                        ResolvePartySlot(session)));
                return;
            }
            if (!await TryConsumeEntryLimitAsync(
                    session,
                    header.type,
                    run,
                    entryLimitDungeonId,
                    isDimensionDungeon))
            {
                return;
            }
            selectionSnapshot.ApplyTo(run);
            if (!run.TryActivate())
                throw new InvalidOperationException("Dungeon run could not enter the active state after selection.");
            RegisterActiveParticipant(session, run);
            // 城镇残留白影：进本提交后离开城镇，向旧区域广播不含离开者的名册清残留白影。
            await NotifyTownAreaRosterDepartureAsync(session);

            await SendEntryCostUpdates(
                session,
                runIdentity,
                entryCost,
                entryPreparation.CostPlan.Source);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            if (entryPreparation.HellParty != null)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON hell accepted: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={req.DungeonId} " +
                    $"room=({run.HellMapX},{run.HellMapY}) " +
                    $"map={run.HellMapId} mode={run.HellPartyMode} " +
                    $"ticket={(entryCost.IsFreePass ? "freepass" : "normal")} " +
                    $"gorgeous={run.HellGorgeousChallenge}");
            }

            if (run.ClearCondition.HasConditions)
                FileLogger.Log($"[DungeonHandler] ClearCondition init: {selection.Maze.ClearConditions.Count} conditions, totalRequired={run.ClearCondition.TotalRequired}");
            else
                FileLogger.Log($"[DungeonHandler] WARNING: dungeon={req.DungeonId} maze={selection.Index} has no [clear condition]");
            if (isA21TutorialEntry)
            {
                run.TutorialEntryProjectionPending = true;
                run.TutorialEntryProjectionSent = false;
                run.TutorialEntryUsesInitialLayout = true;
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON: defer A21 tutorial projection " +
                    $"run={run.RunId} generation={run.RunGeneration} dungeon={req.DungeonId} " +
                    $"until CHANGE_TUTORIAL_FLAG");
                return;
            }

            await SendDungeonSelectPacketsTo(session, req, bossPos, (byte)selection.Index);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            // ★组队副本联机: 队长进本时把整队队员也驱动进【同一实例】。⚠️待真机验证(见 DFO_PARTY_DUNGEON_COOP)。
            await TryFanOutDungeonEntryToPartyAsync(
                session,
                header,
                req,
                bossPos,
                (byte)selection.Index,
                experienceBonusPlan);
        }

        internal static byte[] BuildMercenaryContentErrorBody()
            => CommonPacketBodyBuilder.BuildCmdError(MercenaryContentErrorCode);

        internal async Task CompletePendingTutorialEntryAsync(
            EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            var runIdentity = run.CaptureIdentity();
            lock (run.SyncRoot)
            {
                if (!run.TutorialEntryProjectionPending
                    || run.TutorialEntryProjectionSent)
                {
                    return;
                }

                run.TutorialEntryProjectionSent = true;
                run.TutorialEntryProjectionPending = false;
            }

            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    StartGameResponseType,
                    CommonPacketBodyBuilder.BuildSuccessAck()));
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            var body = EnterSelectDungeonStateBuilder.BuildA21EnterSelectDungeon(
                session.Player.UserId);
            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0x00, 0x001B, body));
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            var req = new Network.Parsers.Dungeon.SelectDungeonRequest(
                run.DungeonId,
                run.Difficulty,
                0,
                0);
            await SendDungeonSelectPacketsTo(
                session,
                req,
                run.BossMapPos,
                (byte)Math.Max(0, run.MazeIndex));
        }

        internal async Task EnterLinkedDungeonAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            int dungeonId,
            byte difficulty)
        {
            if (session?.Player == null
                || dungeonId <= 0
                || dungeonId > ushort.MaxValue)
            {
                return;
            }

            var sourceRun = session.Player.CurrentRun;
            if (sourceRun == null)
                return;
            var sourceRunIdentity = sourceRun.CaptureIdentity();
            var sourceDungeonId = sourceRun.DungeonId;
            if (!DungeonData.CanEnterLinkedDungeonFrom(
                    dungeonId,
                    sourceDungeonId))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"LINKED_DUNGEON enter rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"source={sourceDungeonId} target={dungeonId}");
                return;
            }

            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"LINKED_DUNGEON enter next: " +
                $"cid={session.Player.CharacterId} " +
                $"source={sourceDungeonId} dungeon={dungeonId} " +
                $"diff={difficulty}");
            if (!session.Player.IsCurrentDungeonRun(sourceRunIdentity))
                return;
            await HandleSelectDungeonCore(
                session,
                header,
                BuildLinkedDungeonSelectBody(dungeonId, difficulty),
                sourceDungeonId,
                sourceRunIdentity);
        }

        internal static byte[] BuildLinkedDungeonSelectBody(
            int dungeonId,
            byte difficulty)
        {
            if (dungeonId <= 0 || dungeonId > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dungeonId));

            return new[]
            {
                (byte)(dungeonId & 0xFF),
                (byte)((dungeonId >> 8) & 0xFF),
                difficulty,
                (byte)0,
                (byte)0,
            };
        }

        internal static bool IsLinkedDungeonSelectionAllowed(
            IReadOnlyCollection<int> previousDungeonIds,
            int linkedSourceDungeonId)
        {
            if (previousDungeonIds == null || previousDungeonIds.Count == 0)
                return linkedSourceDungeonId <= 0;
            if (linkedSourceDungeonId <= 0)
                return false;

            foreach (var previousDungeonId in previousDungeonIds)
            {
                if (previousDungeonId == linkedSourceDungeonId)
                    return true;
            }

            return false;
        }

        private async Task<int> ResolveLinkedDungeonSelectionSourceAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            int dungeonId,
            byte difficulty,
            int linkedSourceDungeonId)
        {
            var previousDungeonIds =
                DungeonData.GetLinkedDungeonPreviousIds(dungeonId);

            // A server-internal transition already carries its predecessor. Any
            // notification authorization for the same transition is now stale.
            if (linkedSourceDungeonId > 0)
            {
                LinkedDungeonEntryAuthorizationStore.Clear(session?.Player);
                if (IsLinkedDungeonSelectionAllowed(
                        previousDungeonIds,
                        linkedSourceDungeonId))
                {
                    return linkedSourceDungeonId;
                }

                LogLinkedDungeonSelectionRejected(
                    session,
                    dungeonId,
                    linkedSourceDungeonId,
                    previousDungeonIds,
                    "internal predecessor mismatch");
                return -1;
            }

            if (previousDungeonIds.Count == 0)
            {
                // Choosing an ordinary dungeon abandons any pending linked offer.
                // The ordinary selection itself remains valid.
                LinkedDungeonEntryAuthorizationStore.TryConsume(
                    session?.Player,
                    dungeonId,
                    difficulty,
                    out _,
                    out var discardReason);
                if (!string.Equals(
                        discardReason,
                        "no authorization",
                        StringComparison.Ordinal))
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"SELECT_DUNGEON discarded linked authorization: " +
                        $"cid={session?.Player?.CharacterId ?? 0} " +
                        $"target={dungeonId} diff={difficulty} " +
                        $"reason={discardReason}");
                }
                return 0;
            }

            if (!LinkedDungeonEntryAuthorizationStore.TryConsume(
                    session?.Player,
                    dungeonId,
                    difficulty,
                    out linkedSourceDungeonId,
                    out var authorizationReason))
            {
                LogLinkedDungeonSelectionRejected(
                    session,
                    dungeonId,
                    linkedSourceDungeonId,
                    previousDungeonIds,
                    authorizationReason);
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.DungeonUnavailable);
                return -1;
            }

            if (IsLinkedDungeonSelectionAllowed(
                    previousDungeonIds,
                    linkedSourceDungeonId))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON linked authorization consumed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"source={linkedSourceDungeonId} target={dungeonId} " +
                    $"diff={difficulty}");
                return linkedSourceDungeonId;
            }

            LogLinkedDungeonSelectionRejected(
                session,
                dungeonId,
                linkedSourceDungeonId,
                previousDungeonIds,
                "PVF predecessor mismatch");
            await _svc.AdmissionRejects.SendAsync(
                session,
                header.type,
                DungeonAdmissionReject.DungeonUnavailable);
            return -1;
        }

        private static void LogLinkedDungeonSelectionRejected(
            EnhancedClientSession session,
            int dungeonId,
            int linkedSourceDungeonId,
            IReadOnlyCollection<int> previousDungeonIds,
            string reason)
        {
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"SELECT_DUNGEON linked destination rejected: " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"source={linkedSourceDungeonId} target={dungeonId} " +
                $"prev={string.Join(",", previousDungeonIds)} " +
                $"reason={reason}");
        }

        // 给指定会话发送 SELECT_DUNGEON 出站序列；秘密商店 NPC 上下文只在通关后发送。
        // Hell 等参数从该会话自己的 CurrentRun 读(队员的 run 已拷贝队长 selection)。
        private async Task SendDungeonSelectPacketsTo(
            EnhancedClientSession s,
            Network.Parsers.Dungeon.SelectDungeonRequest req,
            int[] bossPos,
            byte selectedMazeIndex)
        {
            var run = s.Player.CurrentRun;
            if (run == null)
                return;
            var runIdentity = run.CaptureIdentity();
            var extraPairGroups =
                DungeonMechanismCoordinator.ResolveSelectionMinimapIconGroups(
                    run,
                    req.DungeonId,
                    selectedMazeIndex);
            if (StrikerSupportTagCharacterPacketBuilder.TryBuildOwnerSupportBody(
                    s.Player.CharacterId,
                    _svc.Database,
                    out var strikerBody))
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x019F, strikerBody));
            else
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x019F,
                    StrikerSupportTagCharacterBodyBuilder.BuildEmptyBody()));
            if (!s.Player.IsCurrentDungeonRun(runIdentity))
                return;

            var bloodAltar = run.Instance.Mechanisms.BloodAltar;
            var tournament = run.Instance.Mechanisms.Tournament;
            if (bloodAltar != null)
            {
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.BLOOD_INFO,
                    BloodAltarPacketBuilder.BuildInfo(
                        bloodAltar.Definition.DungeonId,
                        bloodAltar.Definition.Kind)));
            }
            else if (tournament == null)
            {
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.DUNGEON_INFO,
                    DungeonNotificationBuilder.BuildDungeonInfo(
                        dungeonId: req.DungeonId,
                        difficulty: req.Difficulty,
                        mazeIndex: selectedMazeIndex,
                        bossX: bossPos != null ? (byte)bossPos[0] : (byte)0,
                        bossY: bossPos != null ? (byte)bossPos[1] : (byte)0,
                        hellPartyRoomX: run.HellMode ? run.HellMapX : (byte)0xFF,
                        hellPartyRoomY: run.HellMode ? run.HellMapY : (byte)0xFF,
                        dungeonMode: 0,
                        extraPairGroups: extraPairGroups,
                        hellPartyEnabled: run.HellMode ? (ushort)1 : (ushort)0,
                        value2: run.HellMode ? (byte)0x0B : (byte)0,
                        flagA: extraPairGroups != null ? (byte)1 : (byte)0)));
                if (!s.Player.IsCurrentDungeonRun(runIdentity))
                    return;

                await DungeonMechanismCoordinator.SendSelectionStateAsync(
                    s,
                    "after_dungeon_info");
            }
            if (!s.Player.IsCurrentDungeonRun(runIdentity))
                return;
            var hasSelectedStart = run.MazeStartX >= 0 && run.MazeStartY >= 0;
            var startRoomIdentity = await _mapHandler.SendStartMapAsync(
                s,
                run,
                hasSelectedStart ? run.MazeStartX : 0xFF,
                hasSelectedStart ? run.MazeStartY : 0xFF,
                overrideMapId: -1);
            if (!startRoomIdentity.HasValue
                || !s.Player.IsCurrentDungeonParticipantRoom(
                    startRoomIdentity.Value))
                return;

        }

        // ★组队副本联机 fan-out(⚠️协议+客户端渲染, 待真机验证; DFO_PARTY_DUNGEON_COOP=0 可隔离):
        // df 模型=队长进本调 CParty::dungeon_start, 建【一个共享实例】广播全队、goto_dungeon 把每个队员推进去。
        // 队员是【服务端驱动】换图、不走传送门→不触发本地"该地下城已锁定"门。这里复刻: 拷队长迷宫 selection
        // 到每个队员 run(同一实例) → 给队员重放 SELECT 序列 → 全队进入同一副本实例。
        private async Task TryFanOutDungeonEntryToPartyAsync(
            EnhancedClientSession leader,
            GamePacketHeader header,
            Network.Parsers.Dungeon.SelectDungeonRequest req,
            int[] bossPos,
            byte selectedMazeIndex,
            DungeonEntryExperienceBonusPlan experienceBonusPlan)
        {
            if (System.Environment.GetEnvironmentVariable("DFO_PARTY_DUNGEON_COOP") == "0") return;
            var pm = _svc.PartyManager;
            var sessions = _svc.Sessions;
            if (pm == null || sessions == null) return;

            var leaderUid = (ushort)leader.Player.CharacterId;   // 队伍成员 UserId==(ushort)CharacterId(见 BuildMember)
            var party = pm.GetPartyByUser(leaderUid);
            if (party == null || party.Count <= 1 || !party.IsLeader(leaderUid)) return;

            var lr = leader.Player.CurrentRun;
            if (lr == null)
                return;
            var leaderRunIdentity = lr.CaptureIdentity();
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] PARTY_DUNGEON_COOP: leader={leader.Player.CharacterId} party={party.PartyId} members={party.Count} dungeon={req.DungeonId} → fan-out");
            foreach (var m in party.MembersBySlot())
            {
                if (m.UserId == leaderUid) continue;
                sessions.TryGet(m.CharacterId, out var bs);
                if (bs?.Player == null || bs.TcpClient == null || !bs.TcpClient.Connected)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] PARTY_DUNGEON_COOP: member uid={m.UserId} 不在线/无会话, 跳过");
                    continue;
                }
                try
                {
                    if (!leader.Player.IsCurrentDungeonRun(leaderRunIdentity))
                        return;
                    var memberPredecessorRun = bs.Player.CurrentRun;
                    var memberPredecessorGeneration =
                        bs.Player.CurrentDungeonRunGeneration;
                    // ★前奏: 队员从没"打开副本选择页", 直接收 SELECT 会半悬空(显示进房间但不真换图)。
                    //   先给队员补发 ENTER_SELECT(0x17/0x02/0x03/0x1A/0x1B, =A 发 0x000F 时收到的),
                    //   让其客户端进入"进副本"状态, 再重放 SELECT 才能真换图。
                    await HandleEnterSelectDungeonCore(bs, header, request: null);
                    if (!leader.Player.IsCurrentDungeonRun(leaderRunIdentity))
                        return;
                    var memberSelection = bs.Player.CurrentDungeonSelection;
                    if (memberPredecessorRun != null
                        || memberSelection == null
                        || !IsEntrySourceCurrent(
                            bs,
                            memberPredecessorRun,
                            memberPredecessorGeneration,
                            memberSelection))
                        continue;

                    await DungeonMechanismCoordinator.ClearRunEffectsAsync(
                        bs,
                        "party_select_dungeon_replace_run");
                    if (!leader.Player.IsCurrentDungeonRun(leaderRunIdentity))
                        return;
                    if (!IsEntrySourceCurrent(
                            bs,
                            memberPredecessorRun,
                            memberPredecessorGeneration,
                            memberSelection))
                        continue;
                    if (!DungeonRunLifecycle.BeginRun(
                        bs,
                        req.DungeonId,
                        req.Difficulty,
                        lr.Instance,
                        _svc.InstanceRegistry,
                        experienceBonusPlan?.ForParticipant(bs),
                        memberSelection))
                    {
                        continue;
                    }
                    var br = bs.Player.CurrentRun;
                    if (br == null)
                        continue;
                    var memberRunIdentity = br.CaptureIdentity();
                    var sharedSelection = lr.Instance.Selection;
                    if (sharedSelection == null)
                        throw new InvalidOperationException("Party dungeon selection snapshot is missing.");
                    sharedSelection.ApplyTo(br);
                    var memberStoryExperienceBonus =
                        DungeonStoryExperienceProfilePolicy.Capture(br);
                    if (memberStoryExperienceBonus.IsStoryRun)
                    {
                        br.TryFreezeStoryExperienceProfile(
                            memberStoryExperienceBonus.RatePercent,
                            memberStoryExperienceBonus
                                .ExperienceDifficulty);
                    }
                    br.HellMode = lr.HellMode;
                    br.HellPartyMode = lr.HellPartyMode;
                    br.HellMapId = lr.HellMapId;
                    br.HellMapX = lr.HellMapX;
                    br.HellMapY = lr.HellMapY;
                    br.HellRoomInfo = lr.HellRoomInfo;
                    br.LinkedDungeonNextId = lr.LinkedDungeonNextId;
                    br.LinkedDungeonNextRate = lr.LinkedDungeonNextRate;
                    br.LinkedDungeonNextCondition =
                        lr.LinkedDungeonNextCondition;
                    DungeonMechanismCoordinator.CloneSelection(
                        bs,
                        lr,
                        br,
                        "party_select_dungeon");
                    if (!br.TryActivate())
                        throw new InvalidOperationException("Party member run could not enter the active state.");
                    RegisterActiveParticipant(bs, br);
                    // 城镇残留白影：队员进本同样离开城镇，向旧区域广播不含离开者的名册清残留白影。
                    await NotifyTownAreaRosterDepartureAsync(bs);
                    bs.Player.UserState = 0x01;
                    await SendDungeonSelectPacketsTo(bs, req, bossPos, selectedMazeIndex);
                    if (!leader.Player.IsCurrentDungeonRun(leaderRunIdentity))
                        return;
                    if (!bs.Player.IsCurrentDungeonRun(memberRunIdentity))
                        continue;
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] PARTY_DUNGEON_COOP: member cid={bs.Player.CharacterId} 驱动进副本 maze={br.MazeIndex}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] PARTY_DUNGEON_COOP: member uid={m.UserId} 驱动异常: {ex.Message}");
                }
            }
        }

        internal Task HandleGorgeousChallengeToggle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Player == null)
                return Task.CompletedTask;

            var enabled = ParseGorgeousChallengeEnabled(body);
            session.Player.HellPartyGorgeousChallengeEnabled = enabled;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GORGEOUS_CHALLENGE_TOGGLE: enabled={enabled} cmd=0x{header.cmd:X2} type=0x{header.type:X4} bodyLen={body?.Length ?? 0} body={(body != null ? BitConverter.ToString(body) : string.Empty)}");
            return Task.CompletedTask;
        }

        private static void ConfigureLinkedDungeonRunState(
            int dungeonId,
            DungeonRun run)
        {
            if (run == null)
                return;

            run.LinkedDungeonNextId = 0;
            run.LinkedDungeonNextRate = 0;
            run.LinkedDungeonNextCondition = 0;

            if (!DungeonData.SupportsLinkedDungeonContinue(dungeonId))
                return;

            var next = DungeonData.PickLinkedDungeonNext(dungeonId);
            if (next == null)
                return;

            run.LinkedDungeonNextId = next.DungeonId;
            run.LinkedDungeonNextRate = next.Rate;
            run.LinkedDungeonNextCondition = next.Condition;
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"LINKED_DUNGEON next selected: dungeon={dungeonId} " +
                $"next={next.DungeonId} rate={next.Rate} " +
                $"condition={next.Condition}");
        }

        private static bool ParseGorgeousChallengeEnabled(byte[] body)
        {
            if (body == null || body.Length <= 13)
                return false;

            // A21 客户端 VERY_DIFFICULT_HELL_PARTY: body[12] 固定为7, body[13] 为0表示勾选, 为1表示取消。
            return body[13] == 0;
        }

        private static int ResolveSelectedRoomMapId(
            int dungeonId,
            int mazeIndex,
            int x,
            int y,
            int[] bossPosition)
        {
            if (dungeonId <= 0 || mazeIndex < 0 || x < 0 || y < 0)
                return 0;

            try
            {
                var room = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId,
                    x,
                    y,
                    mazeIndex,
                    overrideMapId: -1,
                    bossPos: bossPosition);
                return room.Index > 0 ? room.Index : 0;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] selection room resolution failed: " +
                    $"dungeon={dungeonId} maze={mazeIndex} room=({x},{y}) " +
                    $"error={ex.Message}");
                return 0;
            }
        }

        private static DungeonSelectionSnapshot CaptureSelectionSnapshot(
            DungeonRun run,
            PvfLib.MazeInfo maze,
            int partyMemberCount,
            IReadOnlyList<RidableObjectSpawnEntry> randomizedObjects,
            ClearConditionState clearConditionTemplate)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));

            return new DungeonSelectionSnapshot
            {
                MazeIndex = run.MazeIndex,
                MazeQuestConnected = run.MazeQuestConnected,
                ActiveQuestMazeQuestId = run.ActiveQuestMazeQuestId,
                MazeStartMapId = run.MazeStartMapId,
                MazeStartX = run.MazeStartX,
                MazeStartY = run.MazeStartY,
                TotalRoomCount = DungeonRoomTopology.CountConfiguredRooms(maze),
                PartyMemberCount = Math.Max(1, Math.Min(4, partyMemberCount)),
                BossMapPosition = run.BossMapPos == null
                    ? null
                    : (int[])run.BossMapPos.Clone(),
                RidableObjects = randomizedObjects,
                ClearConditionTemplate = clearConditionTemplate,
            };
        }

        private async Task<bool> PrepareTournamentEntryAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            DungeonRun run,
            int partyMemberCount)
        {
            if (!_svc.Tournaments.TryPrepareRun(
                    run,
                    partyMemberCount,
                    ServerRandom.Next,
                    out var definition,
                    out var failureReason))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON tournament rejected: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"dungeon={run?.DungeonId ?? 0} " +
                    $"map={run?.MazeStartMapId ?? 0} " +
                    $"partyCount={partyMemberCount} reason={failureReason}");
                var selection = await DungeonRunLifecycle
                    .RejectSelectingRunAsync(
                        session,
                        run.CaptureIdentity(),
                        _svc.InstanceRegistry);
                if (selection != null)
                {
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        header.type,
                        DungeonAdmissionReject.DungeonUnavailable);
                }
                return false;
            }

            if (definition == null)
                return true;

            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] Tournament ready: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"map={definition.MapId} partyCount={partyMemberCount} " +
                $"pathActors={run.Instance.Mechanisms.Tournament.PathActors.Count} " +
                $"roundFatigue={definition.RoundFatigue} " +
                $"goldRate={definition.ClearRewardGoldRate}");
            return true;
        }

        private async Task<bool> PrepareBloodAltarEntryAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            DungeonRun run)
        {
            if (_svc.BloodAltars.TryPrepareRun(
                    run,
                    out var definition,
                    out var failureReason))
            {
                if (definition != null)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"Blood Altar ready: cid={session.Player.CharacterId} " +
                        $"dungeon={run.DungeonId} kind={definition.Kind} " +
                        $"rounds={definition.MaxRounds}");
                }
                return true;
            }

            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"SELECT_DUNGEON blood altar rejected: " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"dungeon={run?.DungeonId ?? 0} reason={failureReason}");
            var selection = await DungeonRunLifecycle.RejectSelectingRunAsync(
                session,
                run.CaptureIdentity(),
                _svc.InstanceRegistry);
            if (selection != null)
            {
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.DungeonUnavailable);
            }
            return false;
        }

        private byte ResolvePartySlot(EnhancedClientSession session)
        {
            var party = session?.Player == null
                ? null
                : _svc.PartyManager?.GetPartyByUser(session.Player.UserId);
            var member = party?.GetMember(session.Player.UserId);
            return member?.SlotIndex ?? 0;
        }

        private async Task<bool> TryValidateEntryLimitAsync(
            EnhancedClientSession session,
            ushort wireType,
            int entryLimitDungeonId,
            bool isDimensionDungeon)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            var accountId = session?.Account?.AccountId ?? 0;
            if (characterId <= 0 || accountId <= 0)
            {
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    wireType,
                    DungeonAdmissionReject.InvalidSelectionState);
                return false;
            }

            if (isDimensionDungeon)
            {
                var config = DimensionGateEntryLimitConfigProvider.Get();
                if (!_svc.EntryLimits.TryCheckDimensionGateLimit(
                        characterId,
                        config.DailyDefaultEnterCount,
                        config.DailyDefaultExtraEnterCount,
                        1,
                        out var dimensionResult)
                    || dimensionResult?.Allowed != true)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"SELECT_DUNGEON dimension entry limit rejected: " +
                        $"cid={characterId} dungeon={entryLimitDungeonId} " +
                        $"current={dimensionResult?.CurrentCount ?? 0} " +
                        $"extra={dimensionResult?.ExtraCount ?? 0} " +
                        $"used={dimensionResult?.UsedCount ?? 0} " +
                        $"reason={dimensionResult?.Reason ?? "unknown"}");
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        wireType,
                        DungeonAdmissionReject.DailyEntryLimitReached);
                    return false;
                }

                return true;
            }

            if (!_svc.EntryLimits.TryCheckSpecialDungeonLimit(
                    accountId,
                    characterId,
                    entryLimitDungeonId,
                    1,
                    out var result)
                || result?.Allowed != true)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON entry limit rejected: " +
                    $"aid={accountId} cid={characterId} " +
                    $"dungeon={entryLimitDungeonId} " +
                    $"current={result?.CurrentCount ?? 0} " +
                    $"extra={result?.ExtraCount ?? 0} " +
                    $"used={result?.UsedCount ?? 0} " +
                    $"reason={result?.Reason ?? "unknown"}");
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    wireType,
                    DungeonAdmissionReject.DailyEntryLimitReached);
                return false;
            }

            return true;
        }

        private async Task<bool> TryConsumeEntryLimitAsync(
            EnhancedClientSession session,
            ushort wireType,
            DungeonRun run,
            int entryLimitDungeonId,
            bool isDimensionDungeon)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            var accountId = session?.Account?.AccountId ?? 0;
            if (characterId <= 0 || accountId <= 0)
            {
                await RejectEntryLimitAsync(
                    session,
                    wireType,
                    run,
                    DungeonAdmissionReject.InvalidSelectionState);
                return false;
            }

            if (isDimensionDungeon)
            {
                var config = DimensionGateEntryLimitConfigProvider.Get();
                if (!_svc.EntryLimits.TryConsumeDimensionGateLimit(
                        characterId,
                        config.DailyDefaultEnterCount,
                        config.DailyDefaultExtraEnterCount,
                        1,
                        out var dimensionResult)
                    || dimensionResult?.Allowed != true)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"SELECT_DUNGEON dimension entry consume rejected: " +
                        $"cid={characterId} dungeon={entryLimitDungeonId} " +
                        $"current={dimensionResult?.CurrentCount ?? 0} " +
                        $"extra={dimensionResult?.ExtraCount ?? 0} " +
                        $"used={dimensionResult?.UsedCount ?? 0} " +
                        $"reason={dimensionResult?.Reason ?? "unknown"}");
                    await RejectEntryLimitAsync(
                        session,
                        wireType,
                        run,
                        DungeonAdmissionReject.DailyEntryLimitReached);
                    return false;
                }

                await SendDimensionGateEntranceInfoAsync(
                    session,
                    dimensionResult.CurrentCount,
                    dimensionResult.ExtraCount);
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON dimension entry consumed: " +
                    $"cid={characterId} dungeon={entryLimitDungeonId} " +
                    $"current={dimensionResult.CurrentCount} " +
                    $"extra={dimensionResult.ExtraCount} " +
                    $"used={dimensionResult.UsedCount}");
                return true;
            }

            if (!_svc.EntryLimits.TryConsumeSpecialDungeonLimit(
                    accountId,
                    characterId,
                    entryLimitDungeonId,
                    1,
                    out var result)
                || result?.Allowed != true)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON entry limit consume rejected: " +
                    $"aid={accountId} cid={characterId} " +
                    $"dungeon={entryLimitDungeonId} " +
                    $"current={result?.CurrentCount ?? 0} " +
                    $"extra={result?.ExtraCount ?? 0} " +
                    $"used={result?.UsedCount ?? 0} " +
                    $"reason={result?.Reason ?? "unknown"}");
                await RejectEntryLimitAsync(
                    session,
                    wireType,
                    run,
                    DungeonAdmissionReject.DailyEntryLimitReached);
                return false;
            }

            if (result.IsLimited)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON entry limit consumed: " +
                    $"aid={accountId} cid={characterId} " +
                    $"dungeon={entryLimitDungeonId} " +
                    $"current={result.CurrentCount} " +
                    $"extra={result.ExtraCount} " +
                    $"used={result.UsedCount}");
            }

            return true;
        }

        private async Task RejectEntryLimitAsync(
            EnhancedClientSession session,
            ushort wireType,
            DungeonRun run,
            DungeonAdmissionReject rejection)
        {
            if (run?.RunState == DungeonRunState.Active)
            {
                var identity = run.CaptureIdentity();
                await DungeonRunLifecycle.EndRunAsync(
                    session,
                    DungeonRunEndReason.EntryRejected,
                    identity,
                    _svc.InstanceRegistry);
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    wireType,
                    rejection);
                return;
            }

            await RejectEntryAdmissionAsync(
                session,
                wireType,
                run,
                rejection);
        }

        private static Task SendDimensionGateEntranceInfoAsync(
            EnhancedClientSession session,
            int remainingCount,
            int extraCount)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.DIMENSION_GATE_ENTRANCE_INFO,
                    DimensionGateEntranceInfoBodyBuilder.Build(
                        remainingCount,
                        extraCount)));
        }

        private async Task RejectEntryAdmissionAsync(
            EnhancedClientSession session,
            ushort wireType,
            DungeonRun run,
            DungeonAdmissionReject rejection)
        {
            if (run == null)
                return;

            var selection = await DungeonRunLifecycle.RejectSelectingRunAsync(
                session,
                run.CaptureIdentity(),
                _svc.InstanceRegistry);
            if (selection != null)
            {
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    wireType,
                    rejection);
            }
        }

        internal static DungeonAdmissionReject ResolveEntryAdmissionReject(
            EntryCostResult result,
            byte memberSlot)
        {
            switch (result?.FailureKind ?? EntryCostFailureKind.InvalidState)
            {
                case EntryCostFailureKind.MissingRequiredItem:
                    return DungeonAdmissionReject.MissingRequiredItem(memberSlot);
                case EntryCostFailureKind.MissingPermission:
                    return DungeonAdmissionReject.MissingPermission(memberSlot);
                case EntryCostFailureKind.Unavailable:
                    return DungeonAdmissionReject.DungeonUnavailable;
                case EntryCostFailureKind.InvalidState:
                case EntryCostFailureKind.None:
                default:
                    return DungeonAdmissionReject.InvalidSelectionState;
            }
        }

        private async Task SendEntryCostUpdates(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity,
            EntryCostResult entryCost,
            string source)
        {
            foreach (var update in entryCost.ConsumedItems)
            {
                if (!session.Player.IsCurrentDungeonRun(runIdentity))
                    return;
                if (_svc.InventoryRefresh != null)
                    await _svc.InventoryRefresh.SendUpdateItemList(session, InventoryListType.Main, update.SlotIndex);
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON: entry cost consumed source={source} " +
                    $"item={update.ItemId} count={update.Count} " +
                    $"slot={update.SlotIndex} remain={update.RemainingCount}");
            }
            if (entryCost.GoldCost <= 0
                || !session.Player.IsCurrentDungeonRun(runIdentity))
            {
                return;
            }
            if (_svc.InventoryRefresh != null)
                await _svc.InventoryRefresh.SendGoldUpdate(session);
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"SELECT_DUNGEON: entry gold consumed source={source} " +
                $"cost={entryCost.GoldCost} " +
                $"gold={entryCost.GoldBefore}->{entryCost.GoldAfter}");
        }

        private static void WarmUpDropConfigs(bool includeHellParty)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (includeHellParty)
                        DropService.WarmUpAbyssParty();
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DROP_CONFIG_WARMUP ERROR: {ex.Message}");
                }
            });
        }

        private static bool IsRunSlotUnchanged(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            long expectedGeneration)
        {
            var player = session?.Player;
            return player != null
                && player.CurrentDungeonRunGeneration == expectedGeneration
                && ReferenceEquals(player.CurrentRun, expectedRun);
        }

        private static bool IsEntrySourceCurrent(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            long expectedGeneration,
            DungeonSelectionContext expectedSelection)
        {
            if (!IsRunSlotUnchanged(
                    session,
                    expectedRun,
                    expectedGeneration))
            {
                return false;
            }

            return expectedSelection == null
                || (session.Player.IsCurrentDungeonSelection(expectedSelection)
                    && !expectedSelection.IsReturning);
        }

        // 城镇残留白影修复(与切区域同一机制, 参照 86JP 已知协议验证, 见 TownAreaRosterDepartureNotifier):
        // 进本提交后玩家离开城镇, 向旧区域(CurTownId/CurAreaId 仍是进本前城镇值)广播不含
        // 离开者的权威名册, 移除其它玩家屏幕上冻结的城镇残影。CurrentRun 已置位 → 离开者被
        // IsTownPresence 自然排除; 重复广播(如链式进本)名册不变, 幂等无害。
        private async Task NotifyTownAreaRosterDepartureAsync(
            EnhancedClientSession session)
        {
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;
            if (_svc.Sessions == null)
                return;
            try
            {
                await TownAreaRosterDepartureNotifier.NotifyOldAreaDepartureAsync(
                    _svc.Sessions,
                    session,
                    session.Player.CurTownId,
                    session.Player.CurAreaId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] SELECT_DUNGEON area-leave roster notify failed cid={session.Player.CharacterId}: {ex.Message}");
            }
        }

        private void RegisterActiveParticipant(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null || run == null)
                return;

            var (characterId, accountId) =
                SessionOwnerResolver.Resolve(session);
            if (characterId <= 0 || accountId <= 0)
            {
                FileLogger.Log(
                    $"[DungeonInstanceRegistry] registration skipped " +
                    $"cid={characterId} aid={accountId} " +
                    $"instance={run.PartyDungeonInstanceId}");
                return;
            }

            var party = _svc.PartyManager?.GetPartyByUser(
                session.Player.UserId);
            var attachment = _svc.InstanceRegistry.RegisterActive(
                new DungeonParticipantRegistration(
                    accountId,
                    characterId,
                    session.Player.UserId,
                    party?.PartyId ?? 0,
                    session.SessionId,
                    run));
            FileLogger.Log(
                $"[DungeonInstanceRegistry] participant registered " +
                $"cid={characterId} party={attachment.PartyId} " +
                $"instance={attachment.RunIdentity.PartyDungeonInstanceId} " +
                $"run={attachment.RunIdentity.RunId}/" +
                $"{attachment.RunIdentity.RunGeneration} " +
                $"attachmentGeneration={attachment.AttachmentGeneration}");
        }

        private static bool TryGetOwnedInventoryLease(EnhancedClientSession session, out InventoryLease lease)
        {
            lease = null;
            return session?.Player != null
                && InventoryContext.TryGetLease(session.Player.CharacterId, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

    }
}
