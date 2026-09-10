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
using System.Linq;
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
        private long _partySelectionProjectionGeneration;
        private Func<EnhancedClientSession, DungeonSelectionContext, Task>
            _returnRejectedPartySelectionToTown;
        private Func<Task> _publishTownPartyLists;

        private readonly struct EnterSelectDungeonResult
        {
            internal EnterSelectDungeonResult(
                DungeonSelectionContext selection,
                bool created)
            {
                Selection = selection;
                Created = created;
            }

            internal DungeonSelectionContext Selection { get; }
            internal bool Created { get; }
        }

        private sealed class PartyEntryAdmissionPlan
        {
            internal EnhancedClientSession Session;
            internal DungeonSelectionContext Selection;
            internal DungeonRun Run;
            internal InventoryLease Lease;
            internal DungeonEntryAdmissionPreparation Preparation;
            internal EntryCostResult CostResult;
            internal byte PartySlot;
        }

        internal void ConfigureRejectedPartySelectionReturn(
            Func<EnhancedClientSession, DungeonSelectionContext, Task> callback)
        {
            _returnRejectedPartySelectionToTown = callback
                ?? throw new ArgumentNullException(nameof(callback));
        }

        internal void ConfigureTownPartyListPublisher(Func<Task> publisher)
        {
            _publishTownPartyLists = publisher
                ?? throw new ArgumentNullException(nameof(publisher));
        }

        internal static bool TryBuildDungeonUserInfoProjectionOrder(
            DungeonPartySelectionCohort cohort,
            DungeonPartySelectionParticipant receiver,
            Func<DungeonPartySelectionParticipant, bool> isCurrent,
            out IReadOnlyList<DungeonPartySelectionParticipant> ordered,
            out string error)
        {
            ordered = Array.Empty<DungeonPartySelectionParticipant>();
            error = string.Empty;
            if (!HasValidDungeonUserInfoIdentity(receiver)
                || isCurrent == null)
            {
                error = "invalid_receiver";
                return false;
            }

            if (cohort == null)
            {
                if (!isCurrent(receiver))
                {
                    error = "stale_receiver";
                    return false;
                }

                ordered = new[] { receiver };
                return true;
            }

            DungeonPartySelectionParticipant? frozenReceiver = null;
            var peers = new List<DungeonPartySelectionParticipant>();
            var userIds = new HashSet<ushort>();
            var slots = new HashSet<byte>();
            foreach (var participant in cohort.Participants)
            {
                if (!HasValidDungeonUserInfoIdentity(participant)
                    || !userIds.Add(participant.UserId)
                    || !slots.Add(participant.SlotIndex))
                {
                    error = "invalid_frozen_roster";
                    return false;
                }
                if (!isCurrent(participant))
                {
                    error = $"stale_uid_{participant.UserId}";
                    return false;
                }

                if (SameDungeonUserInfoIdentity(participant, receiver))
                    frozenReceiver = participant;
                else
                    peers.Add(participant);
            }

            if (!frozenReceiver.HasValue)
            {
                error = "receiver_not_in_frozen_roster";
                return false;
            }

            peers.Sort((left, right) =>
                left.SlotIndex.CompareTo(right.SlotIndex));
            var result = new List<DungeonPartySelectionParticipant>(
                cohort.Participants.Count)
            {
                frozenReceiver.Value,
            };
            result.AddRange(peers);
            ordered = result;
            return true;
        }

        internal static bool TryBindDungeonPeerUserInfoIdentity(
            byte[] body,
            ushort userId,
            ushort characterId,
            out byte[] boundBody)
        {
            boundBody = null;
            if (body == null
                || body.Length < 20
                || userId == 0
                || body[0] != 1
                || BitConverter.ToUInt16(body, 1) != 1
                || BitConverter.ToUInt16(body, 18) != characterId)
            {
                return false;
            }

            boundBody = (byte[])body.Clone();
            BitConverter.GetBytes(userId).CopyTo(boundBody, 3);
            return true;
        }

        internal static IReadOnlyList<byte[]> BuildEnterSelectDungeonPrefix(
            IReadOnlyList<byte[]> userInfoPackets,
            ushort responseType,
            IReadOnlyList<ushort> dungeonUserIds,
            byte hostSlotIndex)
        {
            var packets = new List<byte[]>(
                (userInfoPackets?.Count ?? 0) + 3);
            if (userInfoPackets != null)
                packets.AddRange(userInfoPackets);
            packets.Add(GamePacketEnvelopeBuilder.Build(
                0x01,
                responseType,
                new byte[] { 0x01 }));
            packets.Add(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(
                    dungeonUserIds,
                    0x01)));
            packets.Add(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x001A,
                UdpHostBuilder.BuildHostSlot(hostSlotIndex)));
            return packets;
        }

        internal static bool TryResolveDungeonHostSlot(
            DungeonPartySelectionCohort cohort,
            Game.Party.Party currentParty,
            out byte slotIndex)
        {
            slotIndex = 0;
            if (cohort != null)
            {
                foreach (var participant in cohort.Participants)
                {
                    if (participant.UserId != cohort.LeaderUserId)
                        continue;
                    if (participant.SlotIndex >= Game.Party.PartyConstants.MaxMembers)
                        return false;

                    slotIndex = participant.SlotIndex;
                    return true;
                }

                return false;
            }

            if (currentParty == null)
                return true;
            var leader = currentParty.GetMember(currentParty.LeaderUserId);
            if (leader == null ||
                leader.SlotIndex >= Game.Party.PartyConstants.MaxMembers)
            {
                return false;
            }

            slotIndex = leader.SlotIndex;
            return true;
        }

        internal static bool ShouldRejectPartySelectionRequest(
            Game.Party.Party party,
            ushort userId,
            Guid sessionId)
        {
            if (party == null || party.Count <= 1)
                return false;

            var member = party.GetMember(userId);
            return !party.IsLeader(userId) ||
                   member == null ||
                   member.SessionId != sessionId;
        }

        internal static bool ShouldRejectUnboundSelectionAfterPartyChange(
            DungeonSelectionContext selection,
            Game.Party.Party party)
            => selection != null
               && selection.PartyCohort == null
               && party?.Count > 1;

        private static bool HasValidDungeonUserInfoIdentity(
            DungeonPartySelectionParticipant participant)
            => participant.UserId != 0
               && participant.CharacterId > 0
               && participant.SessionId != Guid.Empty
               && participant.SlotIndex < Game.Party.PartyConstants.MaxMembers;

        private static bool SameDungeonUserInfoIdentity(
            DungeonPartySelectionParticipant left,
            DungeonPartySelectionParticipant right)
            => left.UserId == right.UserId
               && left.CharacterId == right.CharacterId
               && left.SessionId == right.SessionId
               && left.SlotIndex == right.SlotIndex;

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

        // A21: 客户端进入副本选择界面后会用 CMD SEQUENTIAL_DUNGEON_INFO(0x035D)
        // 询问当前区域的连续副本序列进度(抓包: body = int32 configKey,
        // 镇魂/远古区域 key=26)。之前服务端未注册该 CMD, 客户端拿不到应答会
        // 反复重发并卡死选择界面。这里始终按请求的 key 应答
        // NOTI SEQUENTIAL_DUNGEON_INFO(0x025B, int32 key + byte progress +
        // int32 routeMask, 与既有主动推送同布局); 无对应序列或无进度记录时
        // progress 按 0(未开始)应答。
        internal async Task HandleSequentialDungeonInfo(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (body == null || body.Length < sizeof(int))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SEQUENTIAL_DUNGEON_INFO rejected: invalid body " +
                    $"length={body?.Length ?? 0} expected={sizeof(int)}");
                return;
            }
            var player = session?.Player;
            if (player == null || player.CharacterId <= 0)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    "SEQUENTIAL_DUNGEON_INFO rejected: missing active character");
                return;
            }

            var configKey = BitConverter.ToInt32(body, 0);
            var progress = _svc.PersistentMechanisms.ResolveSequentialProgress(
                player.CharacterId,
                configKey);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.SEQUENTIAL_DUNGEON_INFO,
                DungeonNotificationBuilder.BuildSequentialDungeonInfo(
                    configKey, progress, 0)));
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"SEQUENTIAL_DUNGEON_INFO answered: " +
                $"cid={player.CharacterId} key={configKey} progress={progress}");
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

            // A duplicate ENTER_SELECT can arrive before its first async
            // projection has returned. Serialize the capture/create step on
            // the owner so both requests either reuse the live cohort or see
            // a completed state, never two independently frozen cohorts.
            var entryGate = session?.Player?.DungeonRunTransitionGate;
            if (entryGate != null)
                await entryGate.WaitAsync();
            try
            {
                var existingSelection = session?.Player?.CurrentDungeonSelection;
                var hasCurrentSelection = session?.Player?.IsCurrentDungeonSelection(
                    existingSelection) == true
                    && !existingSelection.IsReturning;
                var retrySelection = hasCurrentSelection
                    ? existingSelection
                    : null;
                var currentParty = hasCurrentSelection
                    && existingSelection.PartyCohort == null
                    ? _svc.PartyManager?.GetPartySnapshotByUser(
                        session.Player.UserId)
                    : null;
                if (ShouldRejectUnboundSelectionAfterPartyChange(
                        existingSelection,
                        currentParty))
                {
                    session.Player.TryInvalidateDungeonSelection(
                        existingSelection);
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        "ENTER_SELECT_DUNGEON rejected unbound selection " +
                        "after party change: " +
                        $"cid={session.Player.CharacterId} " +
                        $"uid={session.Player.UserId} " +
                        $"selection={existingSelection.SelectionId} " +
                        $"party={currentParty.PartyId} " +
                        $"count={currentParty.Count}");
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        header.type,
                        DungeonAdmissionReject.InvalidSelectionState);
                    return;
                }
                // ENTER_SELECT can be retransmitted while its original selection
                // is still live. That retry must reuse the frozen cohort rather
                // than manufacture a second identity and reject itself as stale.
                var partySelectionRejected = false;
                var partyCohort = hasCurrentSelection
                    ? existingSelection.PartyCohort
                    : CapturePartySelectionCohort(
                        session,
                        request.DungeonId,
                        out partySelectionRejected);
                if (partySelectionRejected)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        "ENTER_SELECT_DUNGEON rejected non-leader party member: " +
                        $"cid={session?.Player?.CharacterId ?? 0} " +
                        $"uid={session?.Player?.UserId ?? 0}");
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        header.type,
                        DungeonAdmissionReject.InvalidSelectionState);
                    return;
                }
                if (partyCohort != null)
                    await partyCohort.TransitionGate.WaitAsync();
                try
                {
                    // A return may have won while this retry waited for the
                    // cohort gate. Never recreate a canceled selection from
                    // the captured cohort identity.
                    if (retrySelection != null
                        && !IsPartySelectionRetryCurrent(
                            session?.Player,
                            retrySelection,
                            partyCohort))
                    {
                        FileLogger.Log(
                            $"[{DungeonSharedServices.ProtocolLogName}] " +
                            "ENTER_SELECT_DUNGEON rejected stale retry: " +
                            $"cid={session?.Player?.CharacterId ?? 0} " +
                            $"selection={retrySelection.SelectionId}");
                        await _svc.AdmissionRejects.SendAsync(
                            session,
                            header.type,
                            DungeonAdmissionReject.InvalidSelectionState);
                        return;
                    }

                    var result = await HandleEnterSelectDungeonCore(
                        session,
                        header,
                        request,
                        partyCohort);
                    if (partyCohort != null
                        && result.Created)
                    {
                        // Project followers only for the first creation. A
                        // retry must never recreate a member who already
                        // canceled this cohort; failed projection is recovered
                        // by canceling and opening a fresh selection.
                        await ProjectPartyDungeonSelectionAsync(
                            session,
                            header,
                            result.Selection,
                            partyCohort);
                    }
                }
                finally
                {
                    partyCohort?.TransitionGate.Release();
                }
            }
            finally
            {
                entryGate?.Release();
            }
        }

        private async Task<EnterSelectDungeonResult> HandleEnterSelectDungeonCore(
            EnhancedClientSession session,
            GamePacketHeader header,
            EnterSelectDungeonRequest? request,
            DungeonPartySelectionCohort partyCohort)
        {
            var responseType = ResolveEnterSelectDungeonResponseType(
                header.type,
                request.HasValue);
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
                await SendRaidSelectionRejectedAsync(session, responseType);
                return default;
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
                return default;
            }

            try
            {
                var selection = BeginDungeonSelection(
                    session.Player,
                    isA21TutorialEntry,
                    partyCohort,
                    out var selectionCreated);
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
                        responseType,
                        DungeonAdmissionReject.InvalidSelectionState);
                    return default;
                }
                if (partyCohort != null
                    && !ReferenceEquals(selection.PartyCohort, partyCohort))
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"ENTER_SELECT_DUNGEON rejected by stale party cohort: " +
                        $"cid={session.Player.CharacterId} " +
                        $"selection={selection.SelectionId} " +
                        $"party={partyCohort.PartyId} " +
                        $"projection={partyCohort.ProjectionId}");
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        responseType,
                        DungeonAdmissionReject.InvalidSelectionState);
                    return default;
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
                HonorLevelSummary honorSummary = null;
                IReadOnlyList<byte[]> userInfoPackets;
                if (partyCohort != null)
                {
                    if (!TryBuildPartyDungeonUserInfoPackets(
                            session,
                            selection,
                            partyCohort,
                            out userInfoPackets,
                            out honorSummary,
                            out var partyUserInfoError))
                    {
                        FileLogger.Log(
                            $"[{DungeonSharedServices.ProtocolLogName}] " +
                            "ENTER_SELECT_DUNGEON party USERINFO rejected: " +
                            $"cid={session.Player.CharacterId} " +
                            $"party={partyCohort.PartyId} " +
                            $"projection={partyCohort.ProjectionId} " +
                            $"reason={partyUserInfoError}");
                        if (selectionCreated
                            && session.Player.IsCurrentDungeonSelection(
                                selection))
                        {
                            session.Player.ClearDungeonSelection();
                        }
                        await _svc.AdmissionRejects.SendAsync(
                            session,
                            responseType,
                            DungeonAdmissionReject.InvalidSelectionState);
                        return default;
                    }
                }
                else
                {
                    var soloPackets = new List<byte[]>(1);
                    if (TryBuildDungeonUserInfoPacket(
                            session,
                            session.Player.UserId,
                            false,
                            out var selfUserInfoPacket,
                            out honorSummary))
                    {
                        soloPackets.Add(selfUserInfoPacket);
                    }
                    userInfoPackets = soloPackets;
                }

                await SendLicensedDungeonSelectionStateAsync(session);

                var hellPartySelection = isA21TutorialEntry
                    ? null
                    : BuildHellPartySelectionState(session.Player);
                var dungeonUserIds = hellPartySelection?.UserIds
                    ?? new List<ushort> { session.Player.UserId };
                Game.Party.Party currentParty = null;
                if (partyCohort == null)
                {
                    var liveParty = _svc.PartyManager?.GetPartyByUser(
                        session.Player.UserId);
                    currentParty = liveParty == null
                        ? null
                        : _svc.PartyManager.GetPartySnapshot(
                            liveParty.PartyId);
                }
                if (!TryResolveDungeonHostSlot(
                        partyCohort,
                        currentParty,
                        out var hostSlotIndex))
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        "ENTER_SELECT_DUNGEON host slot rejected: " +
                        $"cid={session.Player.CharacterId} " +
                        $"party={partyCohort?.PartyId ?? 0} " +
                        $"leader={partyCohort?.LeaderUserId ?? 0}");
                    if (selectionCreated
                        && session.Player.IsCurrentDungeonSelection(selection))
                    {
                        session.Player.ClearDungeonSelection();
                    }
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        responseType,
                        DungeonAdmissionReject.InvalidSelectionState);
                    return default;
                }

                // A21 keeps one party-member object per roster slot. Every
                // receiver must see every dungeon participant transition to
                // state 1; a self-only USER_STATE leaves remote slots at 0.
                session.Player.UserState = 0x01;
                if (_svc.Sessions != null)
                    await UnitedFriendSystem.NotifyUserStateChanged(
                        session,
                        _svc.Sessions);
                foreach (var packet in BuildEnterSelectDungeonPrefix(
                    userInfoPackets,
                    responseType,
                    dungeonUserIds,
                    hostSlotIndex))
                {
                    await session.SendPacketAsync(packet);
                }
                await _svc.PersistentMechanisms.RestoreBeforeSelectionAsync(session);
                if (!isA21TutorialEntry)
                {
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
                // 进本过图后客户端重置结婚属性 UI：USERINFO subtype1/
                // USER_STATE 投影之后补发婚礼回放三包。只覆盖进/出本
                // 触发点，不挂城镇内每次过图。
                await InventoryRefreshSender.SendWeddingReplayRefresh(session);
                if (!selection.TryCompletePartyProjection())
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"ENTER_SELECT_DUNGEON projection invalidated: " +
                        $"cid={session.Player.CharacterId} " +
                        $"selection={selection.SelectionId}");
                    return default;
                }
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON: state packets and account EXP progress sent OK");
                return new EnterSelectDungeonResult(
                    selection,
                    selectionCreated);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ENTER_SELECT_DUNGEON EXCEPTION: {ex}");
                return default;
            }
        }

        private bool TryBuildPartyDungeonUserInfoPackets(
            EnhancedClientSession receiver,
            DungeonSelectionContext selection,
            DungeonPartySelectionCohort cohort,
            out IReadOnlyList<byte[]> packets,
            out HonorLevelSummary receiverHonorSummary,
            out string error)
        {
            packets = Array.Empty<byte[]>();
            receiverHonorSummary = null;
            error = string.Empty;
            if (receiver?.Player == null
                || cohort == null
                || !receiver.Player.IsCurrentDungeonSelection(selection)
                || selection.IsReturning
                || !ReferenceEquals(selection.PartyCohort, cohort))
            {
                error = "receiver_selection_mismatch";
                return false;
            }

            var party = _svc.PartyManager?.GetPartySnapshot(cohort.PartyId);
            if (party == null
                || party.Count != cohort.Participants.Count
                || party.LeaderUserId != cohort.LeaderUserId
                || _svc.Sessions == null)
            {
                error = "party_generation_mismatch";
                return false;
            }

            var receiverMember = party.GetMember(receiver.Player.UserId);
            if (receiverMember == null)
            {
                error = "receiver_not_in_current_party";
                return false;
            }

            var receiverIdentity = new DungeonPartySelectionParticipant(
                receiver.Player.UserId,
                receiver.Player.CharacterId,
                receiver.SessionId,
                receiverMember.SlotIndex);
            var resolved = new Dictionary<ushort, EnhancedClientSession>();
            bool IsCurrent(DungeonPartySelectionParticipant participant)
            {
                if (!TryResolveCurrentDungeonUserInfoSession(
                        receiver,
                        party,
                        participant,
                        out var current))
                {
                    return false;
                }

                resolved[participant.UserId] = current;
                return true;
            }

            if (!TryBuildDungeonUserInfoProjectionOrder(
                    cohort,
                    receiverIdentity,
                    IsCurrent,
                    out var ordered,
                    out error))
            {
                return false;
            }

            var built = new List<byte[]>(ordered.Count);
            foreach (var participant in ordered)
            {
                if (!resolved.TryGetValue(
                        participant.UserId,
                        out var source)
                    || !TryBuildDungeonUserInfoPacket(
                        source,
                        participant.UserId,
                        participant.UserId != receiver.Player.UserId,
                        out var packet,
                        out var honorSummary))
                {
                    error = $"userinfo_build_failed_uid_{participant.UserId}";
                    return false;
                }

                if (participant.UserId == receiver.Player.UserId)
                    receiverHonorSummary = honorSummary;
                built.Add(packet);
            }

            // Building a full subtype-1 body reads several character tables.
            // Re-sample the party/session generation after those reads so a
            // concurrent leave or reconnect cannot publish the frozen roster.
            var currentParty =
                _svc.PartyManager?.GetPartySnapshot(cohort.PartyId);
            if (receiver?.Player == null
                || !receiver.Player.IsCurrentDungeonSelection(selection)
                || selection.IsReturning
                || !ReferenceEquals(selection.PartyCohort, cohort)
                || currentParty == null
                || currentParty.Count != cohort.Participants.Count
                || currentParty.LeaderUserId != cohort.LeaderUserId)
            {
                error = "party_generation_changed_during_userinfo_build";
                return false;
            }
            foreach (var participant in ordered)
            {
                if (!resolved.TryGetValue(
                        participant.UserId,
                        out var resolvedSession)
                    || !TryResolveCurrentDungeonUserInfoSession(
                        receiver,
                        currentParty,
                        participant,
                        out var currentSession)
                    || !ReferenceEquals(resolvedSession, currentSession))
                {
                    error = $"session_generation_changed_uid_{participant.UserId}";
                    return false;
                }
            }

            packets = built;
            return true;
        }

        private bool TryResolveCurrentDungeonUserInfoSession(
            EnhancedClientSession receiver,
            Game.Party.Party party,
            DungeonPartySelectionParticipant participant,
            out EnhancedClientSession current)
        {
            current = null;
            var member = party?.GetMember(participant.UserId);
            if (receiver?.Player == null
                || member == null
                || member.CharacterId != participant.CharacterId
                || member.SessionId != participant.SessionId
                || member.SlotIndex != participant.SlotIndex
                || _svc.Sessions == null
                || !_svc.Sessions.TryGet(
                    participant.CharacterId,
                    out current)
                || current?.Player == null
                || current.SessionId != participant.SessionId
                || current.Player.CharacterId != participant.CharacterId
                || current.Player.UserId != participant.UserId
                || current.ListenerPort != receiver.ListenerPort
                || current.Player.CurTownId != receiver.Player.CurTownId
                || current.Player.CurAreaId != receiver.Player.CurAreaId
                || current.Player.CurrentRun != null
                || current.TcpClient == null
                || !current.TcpClient.Connected)
            {
                current = null;
                return false;
            }

            return true;
        }

        private bool TryBuildDungeonUserInfoPacket(
            EnhancedClientSession source,
            ushort expectedUserId,
            bool isRemote,
            out byte[] packet,
            out HonorLevelSummary honorSummary)
        {
            packet = null;
            honorSummary = null;
            var cid = source?.Player?.CharacterId ?? 0;
            if (cid <= 0
                || cid > ushort.MaxValue
                || expectedUserId == 0
                || source.Player.UserId != expectedUserId)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    "ENTER_SELECT_DUNGEON ERROR: invalid USERINFO identity " +
                    $"cid={cid} uid={expectedUserId}");
                return false;
            }

            try
            {
                var record = _svc.CharacterRepository.GetById(cid);
                var addition = _svc.Subtype1Repository.HasData(cid)
                    ? _svc.Subtype1Repository.Load(cid)
                    : null;
                if (record == null
                    || record.CharacterId != cid
                    || addition == null)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        "ENTER_SELECT_DUNGEON ERROR: " +
                        $"cid={cid} record={record != null} " +
                        $"addition={addition != null}, USERINFO not sent " +
                        "(no fallback)");
                    return false;
                }

                var accountId = source.Account?.AccountId
                    ?? record.AccountId;
                var accountCharacters =
                    _svc.CharacterRepository.ListByAccount(accountId);
                honorSummary = _svc.HonorLevel.LoadSummary(
                    accountId,
                    accountCharacters);
                AdventureGroupUserInfoSynchronizer.ApplyToUserInfoAddition(
                    addition,
                    accountCharacters);
                _svc.HonorLevel.ApplyToUserInfoAddition(
                    addition,
                    accountId,
                    accountCharacters,
                    honorSummary);
                var skillSnapshot = _svc.ProgressNotifications
                    .LoadSyncedSkillState(cid, record.Level).Skills;
                var writer = new GamePacketWriter();
                UserInfoBodyBuilder.WriteA21Subtype1Prefix(
                    writer,
                    (ushort)record.CharacterId,
                    addition.ManageLevel,
                    addition.AuraSkinFlag);
                writer.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(
                    addition,
                    skillSnapshot,
                    record.Appearance));
                var body = writer.ToArray();
                if (body.Length < 20
                    || body[0] != 1
                    || BitConverter.ToUInt16(body, 1) != 1
                    || BitConverter.ToUInt16(body, 18) != (ushort)cid)
                {
                    return false;
                }
                if (isRemote
                    && !TryBindDungeonPeerUserInfoIdentity(
                        body,
                        expectedUserId,
                        (ushort)cid,
                        out body))
                {
                    return false;
                }

                packet = GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0002,
                    body);
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    "ENTER_SELECT_DUNGEON: NOTI 2 type1 dynamic body " +
                    $"cid={cid} uid={expectedUserId} remote={isRemote}");
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    "ENTER_SELECT_DUNGEON USERINFO build failed: " +
                    $"cid={cid} uid={expectedUserId} error={ex.Message}");
                return false;
            }
        }

        internal static ushort ResolveEnterSelectDungeonResponseType(
            ushort requestType,
            bool isWireRequest)
        {
            // Party followers do not send ENTER_SELECT_DUNGEON themselves.
            // They are projected when the leader opens the selection screen;
            // the follower still expects the semantic response type 0x000F.
            return isWireRequest ? requestType : StartGameResponseType;
        }

        internal static bool IsPartySelectionRetryCurrent(
            Game.Session.PlayerContext player,
            DungeonSelectionContext capturedSelection,
            DungeonPartySelectionCohort capturedCohort)
            => capturedSelection != null
               && player?.IsCurrentDungeonSelection(capturedSelection) == true
               && !capturedSelection.IsReturning
               && ReferenceEquals(
                   capturedSelection.PartyCohort,
                   capturedCohort);

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

        private DungeonPartySelectionCohort CapturePartySelectionCohort(
            EnhancedClientSession leader,
            int dungeonId,
            out bool rejected)
        {
            rejected = false;
            var returnToTownOnEntryReject =
                leader?.Player?.ConsumePendingPartyRetryEntry(dungeonId)
                == true;
            if (Environment.GetEnvironmentVariable(
                    "DFO_PARTY_DUNGEON_COOP") == "0"
                || leader?.Player == null
                || IsFirstA21TutorialEntry(leader)
                || _svc.PartyManager == null)
            {
                return null;
            }

            var leaderUserId = leader.Player.UserId;
            var party = _svc.PartyManager.GetPartySnapshotByUser(
                leaderUserId);
            if (party == null || party.Count <= 1)
                return null;
            if (ShouldRejectPartySelectionRequest(
                    party,
                    leaderUserId,
                    leader.SessionId))
            {
                rejected = true;
                return null;
            }

            var participants = new List<DungeonPartySelectionParticipant>(
                party.Count);
            foreach (var member in party.MembersBySlot())
            {
                participants.Add(new DungeonPartySelectionParticipant(
                    member.UserId,
                    member.CharacterId,
                    member.SessionId,
                    member.SlotIndex));
            }

            return new DungeonPartySelectionCohort(
                System.Threading.Interlocked.Increment(
                    ref _partySelectionProjectionGeneration),
                party.PartyId,
                leaderUserId,
                participants,
                returnToTownOnEntryReject);
        }

        private async Task ProjectPartyDungeonSelectionAsync(
            EnhancedClientSession leader,
            GamePacketHeader header,
            DungeonSelectionContext leaderSelection,
            DungeonPartySelectionCohort cohort)
        {
            if (leader?.Player == null
                || cohort == null
                || cohort.LeaderUserId != leader.Player.UserId
                || !leader.Player.IsCurrentDungeonSelection(leaderSelection)
                || !ReferenceEquals(leaderSelection.PartyCohort, cohort)
                || _svc.Sessions == null)
            {
                return;
            }

            var projected = 0;
            var failed = 0;
            foreach (var participant in cohort.Participants)
            {
                if (participant.UserId == cohort.LeaderUserId)
                    continue;
                if (!leader.Player.IsCurrentDungeonSelection(leaderSelection))
                    return;

                if (!_svc.Sessions.TryGet(
                        participant.CharacterId, out var follower)
                    || follower?.Player == null
                    || follower.SessionId != participant.SessionId
                    || follower.Player.UserId != participant.UserId
                    || follower.ListenerPort != leader.ListenerPort
                    || follower.Player.CurTownId != leader.Player.CurTownId
                    || follower.Player.CurAreaId != leader.Player.CurAreaId
                    || follower.TcpClient == null
                    || !follower.TcpClient.Connected
                    || follower.Player.CurrentRun != null)
                {
                    failed++;
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"PARTY_SELECTION_PROJECT skipped: " +
                        $"party={cohort.PartyId} " +
                        $"projection={cohort.ProjectionId} " +
                        $"uid={participant.UserId} reason=session_or_area_mismatch");
                    continue;
                }

                var existing = follower.Player.CurrentDungeonSelection;
                if (existing != null
                    && (!follower.Player.IsCurrentDungeonSelection(existing)
                        || !ReferenceEquals(existing.PartyCohort, cohort)))
                {
                    failed++;
                    continue;
                }
                if (existing?.IsPartyProjectionComplete == true)
                {
                    projected++;
                    continue;
                }

                var result = await HandleEnterSelectDungeonCore(
                    follower,
                    header,
                    request: null,
                    partyCohort: cohort);
                if (result.Selection != null
                    && follower.Player.IsCurrentDungeonSelection(
                        result.Selection)
                    && ReferenceEquals(
                        result.Selection.PartyCohort,
                        cohort)
                    && result.Selection.IsPartyProjectionComplete)
                {
                    projected++;
                }
                else
                {
                    failed++;
                }
            }

            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"PARTY_SELECTION_PROJECT: leader={leader.Player.CharacterId} " +
                $"party={cohort.PartyId} projection={cohort.ProjectionId} " +
                $"projected={projected}/{cohort.Participants.Count - 1} " +
                $"failed={failed}");
            if (failed > 0
                && cohort.ReturnToTownOnEntryReject
                && leader.Player.IsCurrentDungeonSelection(leaderSelection))
            {
                await RejectSelectionAsync(
                    leader,
                    leaderSelection,
                    header.type,
                    DungeonAdmissionReject.InvalidSelectionState);
            }
        }

        private bool TryValidatePartySelectionCohort(
            EnhancedClientSession leader,
            DungeonSelectionContext leaderSelection,
            out string error)
        {
            error = string.Empty;
            var cohort = leaderSelection?.PartyCohort;
            if (cohort == null)
                return true;
            if (leader?.Player == null
                || cohort.LeaderUserId != leader.Player.UserId
                || !leader.Player.IsCurrentDungeonSelection(leaderSelection)
                || leaderSelection.IsReturning
                || !leaderSelection.IsPartyProjectionComplete)
            {
                error = "leader_selection_mismatch";
                return false;
            }

            var party = _svc.PartyManager?.GetPartySnapshot(cohort.PartyId);
            if (party == null
                || party.Count != cohort.Participants.Count
                || !party.IsLeader(cohort.LeaderUserId))
            {
                error = "party_generation_mismatch";
                return false;
            }

            foreach (var participant in cohort.Participants)
            {
                var member = party.GetMember(participant.UserId);
                if (member == null
                    || member.CharacterId != participant.CharacterId
                    || member.SessionId != participant.SessionId
                    || member.SlotIndex != participant.SlotIndex)
                {
                    error = $"roster_mismatch_uid_{participant.UserId}";
                    return false;
                }
                if (participant.UserId == cohort.LeaderUserId)
                {
                    if (leader.SessionId != participant.SessionId)
                    {
                        error = "leader_session_mismatch";
                        return false;
                    }
                    continue;
                }

                if (_svc.Sessions == null
                    || !_svc.Sessions.TryGet(
                        participant.CharacterId, out var follower)
                    || follower?.Player == null
                    || follower.SessionId != participant.SessionId
                    || follower.Player.UserId != participant.UserId
                    || follower.ListenerPort != leader.ListenerPort
                    || follower.Player.CurTownId != leader.Player.CurTownId
                    || follower.Player.CurAreaId != leader.Player.CurAreaId
                    || follower.TcpClient == null
                    || !follower.TcpClient.Connected
                    || follower.Player.CurrentRun != null)
                {
                    error = $"follower_session_mismatch_uid_{participant.UserId}";
                    return false;
                }

                var followerSelection =
                    follower.Player.CurrentDungeonSelection;
                if (!follower.Player.IsCurrentDungeonSelection(
                        followerSelection)
                    || !ReferenceEquals(
                        followerSelection.PartyCohort,
                        cohort)
                    || followerSelection.IsReturning
                    || !followerSelection.IsPartyProjectionComplete)
                {
                    error = $"follower_selection_mismatch_uid_{participant.UserId}";
                    return false;
                }
            }

            return true;
        }

        private static DungeonSelectionContext BeginDungeonSelection(
            Game.Session.PlayerContext player,
            bool isA21TutorialEntry,
            DungeonPartySelectionCohort partyCohort,
            out bool created)
        {
            created = false;
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
                isA21TutorialEntry,
                partyCohort,
                out created);
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

        private async Task SendLicensedDungeonSelectionStateAsync(
            EnhancedClientSession session)
        {
            var player = session?.Player;
            if (player == null
                || !Town.TryGetDungeonGateReturnInfo(
                    player.CurTownId,
                    player.CurAreaId,
                    out var gate)
                || gate.WorldMapAreaId
                    != LicensedDungeonCatalog.WorldMapAreaId)
            {
                return;
            }

            var remainingEnterCount = (byte)0;
            var utcNow = DateTime.UtcNow;
            var dayIndex = LicensedDungeonPeriod.FromUtc(utcNow).DayIndex;
            if (!_svc.LicensedDungeons.TryGetSelectionProjection(
                    player.CharacterId,
                    utcNow,
                    out dayIndex,
                    out remainingEnterCount,
                    out var failureReason))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"LICENSE_DUNGEON projection failed: " +
                    $"cid={player.CharacterId} reason={failureReason}");
            }

            var licenseRecords =
                LicensedDungeonCatalog.GetInitialLicenseRecords();
            if (!_svc.LicensedDungeons.TryGetLicenseProjection(
                    player.CharacterId,
                    out licenseRecords,
                    out failureReason))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"LICENSE_DUNGEON level projection failed: " +
                    $"cid={player.CharacterId} reason={failureReason}");
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.LICENSE_DUNGEON_DAY_INDEX_INFO,
                LicensedDungeonPacketBuilder.BuildDayIndex(dayIndex)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.LICENSE_DUNGEON_INCOUNT_INFO,
                LicensedDungeonPacketBuilder.BuildRemainingEnterCount(
                    remainingEnterCount)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.CHARAC_DUNGEON_LICENSE_INFO,
                LicensedDungeonPacketBuilder.BuildCharacterLicenseInfo(
                    licenseRecords)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.LICENSE_DUNGEON_SHOT_COUNT_INFO,
                LicensedDungeonPacketBuilder.BuildShotCount()));
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"LICENSE_DUNGEON selection state sent: " +
                $"cid={player.CharacterId} area={gate.WorldMapAreaId} " +
                $"day={dayIndex} remaining={remainingEnterCount}");
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
                expectedPredecessorIdentity: null,
                anotherAradSelection: null);

        internal async Task HandleCrackOfDimension(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!Network.Parsers.Dungeon.CrackOfDimensionRequest.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"CRACK_OF_DIMENSION rejected: invalid body length=" +
                    $"{body?.Length ?? 0}");
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.InvalidSelectionState);
                return;
            }

            if (!AnotherAradSelectionResolver.TryResolve(
                    request.HistoricalDungeonId,
                    request.CrackQuestId,
                    out var selection,
                    out var reason))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"CRACK_OF_DIMENSION rejected: cid=" +
                    $"{session?.Player?.CharacterId ?? 0} " +
                    $"historical={request.HistoricalDungeonId} " +
                    $"quest={request.CrackQuestId} reason={reason}");
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.DungeonUnavailable);
                return;
            }

            if (session?.Player?.CurrentDungeonSelection == null
                || session.Player.CurrentRun != null)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"CRACK_OF_DIMENSION rejected: no active selection " +
                    $"cid={session?.Player?.CharacterId ?? 0}");
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    header.type,
                    DungeonAdmissionReject.InvalidSelectionState);
                return;
            }

            var syntheticBody = new byte[9];
            Array.Copy(
                BitConverter.GetBytes(selection.HistoricalDungeonId),
                syntheticBody,
                4);
            syntheticBody[4] = (byte)AnotherAradSelectionResolver.ResolveMaximumDifficulty(
                selection.HistoricalDungeonId);
            syntheticBody[7] = 0xFF;
            syntheticBody[8] = 0xFF;

            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"CRACK_OF_DIMENSION accepted: cid=" +
                $"{session.Player.CharacterId} wrapper={selection.WrapperDungeonId} " +
                $"historical={selection.HistoricalDungeonId} " +
                $"quest={selection.CrackQuestId} " +
                $"difficulty={syntheticBody[4]}");
            await HandleSelectDungeonCore(
                session,
                header,
                syntheticBody,
                linkedSourceDungeonId: 0,
                expectedPredecessorIdentity: null,
                anotherAradSelection: selection);
        }

        internal async Task<bool> TryHandleAnotherAradQuestAcceptAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || !run.AnotherAradActive
                || run.AnotherAradCrackQuestId <= 0
                || run.AnotherAradQuest == null)
            {
                return false;
            }

            var questId = body != null && body.Length >= 4
                ? BitConverter.ToUInt16(body, 2)
                : body != null && body.Length >= 2
                    ? BitConverter.ToUInt16(body, 0)
                    : (ushort)0;
            if (questId == 0
                || questId != run.AnotherAradCrackQuestId
                || questId > ushort.MaxValue)
            {
                return false;
            }

            run.AnotherAradQuest.TryAccept(
                DateTime.UtcNow,
                out var initialTrigger,
                out var duplicate);
            run.AnotherAradQuestAccepted = true;
            var result = new QuestAcceptResult
            {
                QuestId = questId,
                InitTrigger = initialTrigger,
            };
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                QuestAckBuilder.BuildAccept(result)));
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"CRACK_OF_DIMENSION quest accepted session-local: " +
                $"cid={session.Player.CharacterId} quest={questId} " +
                $"trigger={initialTrigger} duplicate={(duplicate ? 1 : 0)}");
            return true;
        }

        internal async Task<bool> TryHandleAnotherAradQuestSetTriggerAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var runtime = run?.AnotherAradQuest;
            if (run == null
                || runtime == null
                || !run.AnotherAradActive
                || !TryReadQuestCommandId(body, out var questId)
                || questId != runtime.Definition.QuestId)
            {
                return false;
            }

            var trigger = runtime.CurrentTrigger;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                QuestAckBuilder.BuildSetTrigger(new QuestSetTriggerResult
                {
                    QuestId = questId,
                    PreviousTriggerValue = trigger,
                    TriggerValue = trigger,
                })));
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"CRACK_OF_DIMENSION quest trigger echoed session-local: " +
                $"cid={session.Player.CharacterId} quest={questId} " +
                $"trigger={trigger}");
            return true;
        }

        internal async Task<bool> TryHandleAnotherAradQuestFinishAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var runtime = run?.AnotherAradQuest;
            if (run == null
                || runtime == null
                || !run.AnotherAradActive
                || !TryReadQuestCommandId(body, out var requestedQuestId)
                || requestedQuestId != runtime.Definition.QuestId)
            {
                return false;
            }

            var commandBody = StripQuestCommandEcho(body);
            if (!Network.Parsers.Quest.QuestCommandParser.TryParseFinish(
                    commandBody,
                    out var command)
                || command.QuestId != runtime.Definition.QuestId
                || command.CompletionCount != 1)
            {
                await SendAnotherAradFinishFailureAsync(session, header.type);
                return true;
            }

            var claim = runtime.TryReserveRewardClaim();
            if (claim == AnotherAradQuestClaimDisposition.Rejected)
            {
                await SendAnotherAradFinishFailureAsync(session, header.type);
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"CRACK_OF_DIMENSION quest finish rejected: " +
                    $"cid={session.Player.CharacterId} quest={command.QuestId} " +
                    $"accepted={(runtime.Accepted ? 1 : 0)} " +
                    $"settled={(runtime.SettlementEvaluated ? 1 : 0)} " +
                    $"completed={(runtime.Completed ? 1 : 0)}");
                return true;
            }

            if (claim == AnotherAradQuestClaimDisposition.AlreadyClaimed)
            {
                await SendAnotherAradFinishSuccessAsync(
                    session,
                    header.type,
                    runtime,
                    null,
                    completionCount: 0);
                return true;
            }

            InventoryRewardGrantBatchResult rewardGrant = null;
            RewardInventoryRollback rollback = null;
            try
            {
                if (!AnotherAradConfigCatalog.TryResolveReward(
                        session.Player.Level,
                        out var rewardItemId,
                        out var rewardItemCount)
                    || !InventoryContext.TryGetOwnedLease(
                        session.SessionId,
                        session.Player.CharacterId,
                        out var lease))
                {
                    throw new InvalidOperationException(
                        "Another Arad reward or owned inventory lease is unavailable.");
                }

                lock (lease.SyncRoot)
                {
                    if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity())
                        || !lease.IsOwnedBy(session.SessionId))
                    {
                        throw new InvalidOperationException(
                            "Another Arad finish owner changed.");
                    }

                    var requests = new[]
                    {
                        InventoryRewardGrantRequest.Create(
                            rewardItemId,
                            rewardItemCount,
                            ItemCreateReason.QuestReward),
                    };
                    if (!InventoryRewardGrantService.TryPlanBatch(
                            lease.Inventory,
                            requests,
                            out var rewardPlan)
                        || rewardPlan.Entries.Count != 1
                        || !RewardInventoryRollback.CanRestore(
                            rewardPlan.Entries[0]))
                    {
                        throw new InvalidOperationException(
                            "Another Arad reward planning failed.");
                    }

                    rollback = RewardInventoryRollback.Capture(
                        lease.Inventory,
                        rewardPlan.Entries[0]);
                    if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                            lease.Inventory,
                            rewardPlan,
                            out rewardGrant)
                        || rewardGrant == null
                        || !rewardGrant.Success
                        || !InventoryPersistenceService.SaveDirty(lease))
                    {
                        RewardInventoryRollback.Restore(
                            lease.Inventory,
                            rollback,
                            rewardGrant);
                        throw new InvalidOperationException(
                            "Another Arad reward apply or persistence failed.");
                    }
                }

                runtime.CommitRewardClaim();
                await SendAnotherAradFinishSuccessAsync(
                    session,
                    header.type,
                    runtime,
                    rewardGrant,
                    completionCount: 1);
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"CRACK_OF_DIMENSION quest finished session-local: " +
                    $"cid={session.Player.CharacterId} quest={command.QuestId} " +
                    $"reward={rewardItemId}x{rewardItemCount}");
                return true;
            }
            catch (Exception ex)
            {
                runtime.AbortRewardClaim();
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"CRACK_OF_DIMENSION quest finish failed: " +
                    $"cid={session.Player.CharacterId} quest={command.QuestId} " +
                    $"error={ex.Message}");
                await SendAnotherAradFinishFailureAsync(session, header.type);
                return true;
            }
        }

        private static async Task SendAnotherAradFinishSuccessAsync(
            EnhancedClientSession session,
            ushort wireType,
            AnotherAradQuestRuntime runtime,
            InventoryRewardGrantBatchResult rewardGrant,
            uint completionCount)
        {
            var result = new QuestFinishResult
            {
                QuestId = runtime.Definition.QuestId,
                FinishType = runtime.Definition.FinishType,
                CompletionCount = completionCount,
                RewardAcquiredAtUnixTime = rewardGrant?.Results.Count > 0
                    ? unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    : 0,
            };
            if (rewardGrant != null)
            {
                foreach (var granted in rewardGrant.Results)
                {
                    if (granted == null
                        || !granted.Success
                        || granted.SlotIndex < 0
                        || granted.Kind == InventoryRewardGrantKind.Premium)
                    {
                        continue;
                    }

                    result.InsertedEntries.Add(new InsertedItemEntry
                    {
                        SlotIndex = (ushort)granted.SlotIndex,
                        ItemId = granted.ItemTemplateId,
                        GrantedCount = (uint)Math.Max(0, granted.GrantedCount),
                    });
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                wireType,
                QuestAckBuilder.BuildFinish(result)));
        }

        private static Task SendAnotherAradFinishFailureAsync(
            EnhancedClientSession session,
            ushort wireType)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                wireType,
                QuestAckBuilder.BuildFinish(QuestFinishResult.Fail(22))));

        private static bool TryReadQuestCommandId(
            byte[] body,
            out ushort questId)
        {
            questId = 0;
            if (body == null)
                return false;
            if (body.Length >= 4)
            {
                questId = BitConverter.ToUInt16(body, 2);
                return questId != 0;
            }
            if (body.Length < 2)
                return false;

            questId = BitConverter.ToUInt16(body, 0);
            return questId != 0;
        }

        private static byte[] StripQuestCommandEcho(byte[] body)
        {
            if (body == null || body.Length <= 2)
                return body;
            var stripped = new byte[body.Length - 2];
            Buffer.BlockCopy(body, 2, stripped, 0, stripped.Length);
            return stripped;
        }

        private async Task HandleSelectDungeonCore(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            int linkedSourceDungeonId,
            DungeonRunIdentity? expectedPredecessorIdentity,
            AnotherAradSelection? anotherAradSelection)
        {
            // A party selection has one shared transition: either the leader
            // consumes it to create the run, or a member returns it to town.
            // Acquire the owner gate before sampling its cohort. Party accept
            // uses the same owner gate, so a formerly solo selection cannot
            // race a new member into a leader-only run.
            var ownerGate = expectedPredecessorIdentity.HasValue
                ? null
                : session?.Player?.DungeonRunTransitionGate;
            if (ownerGate != null)
                await ownerGate.WaitAsync();
            DungeonPartySelectionCohort cohort = null;
            try
            {
                cohort = expectedPredecessorIdentity.HasValue
                    ? null
                    : session?.Player?.CurrentDungeonSelection?.PartyCohort;
                if (cohort != null)
                    await cohort.TransitionGate.WaitAsync();
                await HandleSelectDungeonCoreUngated(
                    session,
                    header,
                    body,
                    linkedSourceDungeonId,
                    expectedPredecessorIdentity,
                    anotherAradSelection);
            }
            finally
            {
                cohort?.TransitionGate.Release();
                ownerGate?.Release();
            }
        }

        private async Task HandleSelectDungeonCoreUngated(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            int linkedSourceDungeonId,
            DungeonRunIdentity? expectedPredecessorIdentity,
            AnotherAradSelection? anotherAradSelection)
        {
            var initialSelection = session?.Player?.CurrentDungeonSelection;
            if (!CanEnterRaidDungeonSelection(session))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON rejected by raid state: " +
                    $"cid={session?.Player?.CharacterId} uid={session?.Player?.UserId}");
                try
                {
                    await SendRaidSelectionRejectedAsync(session, header.type);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"raid selection rejection projection failed cid=" +
                        $"{session?.Player?.CharacterId ?? 0}: {ex.Message}");
                }
                if (initialSelection?.PartyCohort
                        ?.ReturnToTownOnEntryReject == true
                    && session?.Player?.IsCurrentDungeonSelection(
                        initialSelection) == true
                    && _returnRejectedPartySelectionToTown != null)
                {
                    await _returnRejectedPartySelectionToTown(
                        session,
                        initialSelection);
                }
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
                await RejectSelectionAsync(
                    session,
                    expectedSelection,
                    header.type,
                    DungeonAdmissionReject.InvalidSelectionState);
                return;
            }
            var currentParty = expectedSelection?.PartyCohort == null
                && session?.Player != null
                ? _svc.PartyManager?.GetPartySnapshotByUser(
                    session.Player.UserId)
                : null;
            if (ShouldRejectUnboundSelectionAfterPartyChange(
                    expectedSelection,
                    currentParty))
            {
                session.Player.TryInvalidateDungeonSelection(
                    expectedSelection);
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    "SELECT_DUNGEON rejected unbound selection after " +
                    $"party change: cid={session.Player.CharacterId} " +
                    $"selection={expectedSelection.SelectionId} " +
                    $"party={currentParty.PartyId} " +
                    $"count={currentParty.Count}");
                await RejectSelectionAsync(
                    session,
                    expectedSelection,
                    header.type,
                    DungeonAdmissionReject.InvalidSelectionState);
                return;
            }
            if (expectedSelection?.PartyCohort != null
                && !TryValidatePartySelectionCohort(
                    session,
                    expectedSelection,
                    out var partySelectionError))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON rejected by party selection cohort: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"selection={expectedSelection.SelectionId} " +
                    $"party={expectedSelection.PartyCohort.PartyId} " +
                    $"projection={expectedSelection.PartyCohort.ProjectionId} " +
                    $"reason={partySelectionError}");
                await RejectSelectionAsync(
                    session,
                    expectedSelection,
                    header.type,
                    DungeonAdmissionReject.InvalidSelectionState);
                return;
            }

            var req = Network.Parsers.Dungeon.SelectDungeonRequest.Parse(body);
            var entryLimitDungeonId = req.DungeonId;
            if (anotherAradSelection.HasValue)
            {
                req = new Network.Parsers.Dungeon.SelectDungeonRequest(
                    anotherAradSelection.Value.HistoricalDungeonId,
                    req.Difficulty,
                    req.Flag1,
                    req.Flag2,
                    req.A21Sentinel,
                    req.TrailingLength,
                    req.HasNonZeroTrailingBytes);
            }
            if (!anotherAradSelection.HasValue)
            {
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
            }

            if (!anotherAradSelection.HasValue
                && !WorldMap.IsStoryDungeon(req.DungeonId)
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
                await RejectSelectionAsync(
                    session,
                    expectedSelection,
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
                    await RejectSelectionAsync(
                        session,
                        expectedSelection,
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

            var admission = anotherAradSelection.HasValue
                ? new DungeonAdmissionDecision(
                    allowed: true,
                    mode: DungeonAdmissionMode.Unrestricted,
                    reason: "another_arad_pair_validated",
                    requiredQuestIds: Array.Empty<int>())
                : WorldMap.EvaluateDungeonAdmission(
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
                await RejectSelectionAsync(
                    session,
                    expectedSelection,
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
                await RejectSelectionAsync(
                    session,
                    expectedSelection,
                    header.type,
                    DungeonAdmissionReject.DungeonUnavailable);
                return;
            }

            LicensedDungeonEntryPlan licensedDungeonPlan = null;
            if (_svc.LicensedDungeons.IsLicensedDungeon(req.DungeonId))
            {
                if (!_svc.LicensedDungeons.TryPrepareEntry(
                        session.Player.CharacterId,
                        req.DungeonId,
                        DateTime.UtcNow,
                        ServerRandom.Next,
                        out licensedDungeonPlan,
                        out var licensedFailureReason))
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"SELECT_DUNGEON licensed admission rejected: " +
                        $"cid={session.Player.CharacterId} " +
                        $"dungeon={req.DungeonId} " +
                        $"reason={licensedFailureReason}");
                    await _svc.AdmissionRejects.SendAsync(
                        session,
                        header.type,
                        DungeonAdmissionReject.DungeonUnavailable);
                    return;
                }

                if (req.Difficulty
                    != licensedDungeonPlan.Definition.Difficulty)
                {
                    req = new Network.Parsers.Dungeon.SelectDungeonRequest(
                        req.DungeonId,
                        licensedDungeonPlan.Definition.Difficulty,
                        req.Flag1,
                        req.Flag2,
                        req.A21Sentinel,
                        req.TrailingLength,
                        req.HasNonZeroTrailingBytes);
                }
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON licensed plan: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={req.DungeonId} " +
                    $"license={licensedDungeonPlan.Definition.LicenseLevel} " +
                    $"difficulty={req.Difficulty} " +
                    $"maze={licensedDungeonPlan.MazeIndex} " +
                    $"groop={(licensedDungeonPlan.GroupBossPresent ? 1 : 0)} " +
                    $"rate={licensedDungeonPlan.GroupAppearRate}");
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
                    await RejectSelectionAsync(
                        session,
                        expectedSelection,
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
                // 城镇残留白影：塔进本提交后离开城镇，向旧区域发 USER_AREA 远程移除。
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
            run.AnotherAradActive = anotherAradSelection.HasValue;
            run.AnotherAradWrapperDungeonId = anotherAradSelection.HasValue
                ? anotherAradSelection.Value.WrapperDungeonId
                : 0;
            run.AnotherAradHistoricalDungeonId = anotherAradSelection.HasValue
                ? anotherAradSelection.Value.HistoricalDungeonId
                : 0;
            run.AnotherAradCrackQuestId = anotherAradSelection.HasValue
                ? anotherAradSelection.Value.CrackQuestId
                : 0;
            run.AnotherAradQuest = anotherAradSelection.HasValue
                && anotherAradSelection.Value.QuestDefinition != null
                ? new AnotherAradQuestRuntime(
                    anotherAradSelection.Value.QuestDefinition)
                : null;
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
            (PvfLib.MazeInfo Maze, int Index) selection;
            if (licensedDungeonPlan != null)
            {
                selection = (
                    DungeonData.GetDungeonMaze(
                        req.DungeonId,
                        licensedDungeonPlan.MazeIndex),
                    licensedDungeonPlan.MazeIndex);
                mazeSelectionDiagnostic =
                    $"licensed_maze={licensedDungeonPlan.MazeIndex} " +
                    $"groop={(licensedDungeonPlan.GroupBossPresent ? 1 : 0)}";
            }
            else
            {
                selection = preferredCircleSelection.HasValue
                    ? preferredCircleSelection.Value
                    : DungeonData.SelectDungeonMaze(
                        req.DungeonId,
                        req.Difficulty,
                        anotherAradSelection.HasValue ? null : activeQuestIds,
                        anotherAradSelection.HasValue ? null : clearedQuestIds,
                        diagnostic => mazeSelectionDiagnostic = diagnostic);
            }
            run.MazeIndex = selection.Index;
            run.MazeQuestConnected = !anotherAradSelection.HasValue
                && DungeonData.IsQuestConnectedSelection(
                    req.DungeonId,
                    selection.Maze,
                    activeQuestIds,
                    req.Difficulty);
            run.ActiveQuestMazeQuestId = anotherAradSelection.HasValue
                ? 0
                : DungeonData.ResolveActiveQuestMazeQuestId(
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
            selectionSnapshot.ApplyTo(run);

            if (!TryBuildPartyEntryAdmissionPlans(
                    session,
                    run,
                    expectedSelection,
                    entryLease,
                    entryPreparation,
                    out var partyEntryPlans,
                    out var partyEntryRejection,
                    out var partyEntryFailure))
            {
                await RejectPreparedPartyEntryAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    partyEntryRejection,
                    partyEntryFailure);
                return;
            }
            if (!await TryPreparePartyDungeonEntryRunsAsync(
                    session,
                    req,
                    experienceBonusPlan,
                    expectedSelection,
                    partyEntryPlans))
            {
                await RejectPreparedPartyEntryAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    DungeonAdmissionReject.InvalidSelectionState,
                    "party_run_prepare_failed");
                return;
            }

            var preparedFollowers = new List<EnhancedClientSession>();
            if (!TryActivatePreparedPartyEntry(
                    session,
                    partyEntryPlans,
                    preparedFollowers))
            {
                await ReturnPreparedPartyRunsToTownAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    DungeonAdmissionReject.InvalidSelectionState,
                    "party_run_activation_failed");
                return;
            }

            foreach (var plan in partyEntryPlans)
                await NotifyTownAreaRosterDepartureAsync(plan.Session);

            // 暗精灵遗迹入场在组队费用提交前最后提交; 此后任何失败路径都必须回滚 licensed entry
            var licensedCommittedStatus = default(LicensedDungeonStatus);
            if (licensedDungeonPlan != null
                && !_svc.LicensedDungeons.TryCommitEntry(
                    licensedDungeonPlan,
                    out licensedCommittedStatus,
                    out var licensedCommitFailure))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON licensed commit rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={req.DungeonId} reason={licensedCommitFailure}");
                await ReturnPreparedPartyRunsToTownAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    DungeonAdmissionReject.DungeonUnavailable,
                    "licensed_commit_rejected");
                return;
            }

            var commitRequests = partyEntryPlans
                .Select(plan => new DungeonEntryAdmissionCommitRequest(
                    plan.Lease,
                    plan.Preparation))
                .ToList();
            IReadOnlyList<EntryCostResult> entryCosts;
            try
            {
                if (!_svc.EntryAdmission.TryCommitGroup(
                        commitRequests,
                        out entryCosts)
                    || entryCosts.Count != partyEntryPlans.Count)
                {
                    RollbackLicensedDungeonEntry(licensedDungeonPlan);
                    await ReturnPreparedPartyRunsToTownAsync(
                        session,
                        header.type,
                        partyEntryPlans,
                        DungeonAdmissionReject.InvalidSelectionState,
                        "party_cost_commit_failed");
                    return;
                }
            }
            catch
            {
                RollbackLicensedDungeonEntry(licensedDungeonPlan);
                throw;
            }
            for (var index = 0; index < partyEntryPlans.Count; index++)
                partyEntryPlans[index].CostResult = entryCosts[index];
            var entryCost = partyEntryPlans
                .First(plan => ReferenceEquals(plan.Session, session))
                .CostResult;
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
            {
                RollbackLicensedDungeonEntry(licensedDungeonPlan);
                await ReturnPreparedPartyRunsToTownAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    DungeonAdmissionReject.InvalidSelectionState,
                    "leader_changed_after_cost_commit");
                return;
            }
            if (!await TryConsumeEntryLimitAsync(
                    session,
                    header.type,
                    run,
                    entryLimitDungeonId,
                    isDimensionDungeon))
            {
                RollbackLicensedDungeonEntry(licensedDungeonPlan);
                await ReturnPreparedPartyRunsToTownAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    DungeonAdmissionReject.DailyEntryLimitReached,
                    "leader_entry_limit_consume_failed",
                    sendAdmissionReject: false);
                return;
            }
            var consumedDungeonBuffUse = _svc.DevilContracts.TryConsume(
                session.Player.CharacterId,
                session.Account?.AccountId ?? 0,
                Game.Premium.DevilContractUsagePolicy.DungeonBuffSlot);
            if (consumedDungeonBuffUse)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    "DEVIL_CONTRACT_DUNGEON_BUFF: consumed " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId}");
            }
            if (licensedDungeonPlan != null)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"SELECT_DUNGEON licensed entry committed: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={req.DungeonId} " +
                    $"daily={licensedCommittedStatus.DailyEntryCount}/" +
                    $"{LicensedDungeonCatalog.DailyEnterCount} " +
                    $"monthly={licensedCommittedStatus.MonthlyEntryCount} " +
                    $"groop={licensedCommittedStatus.MonthlyGroupAppearCount}/" +
                    $"{LicensedDungeonCatalog.GroupAppearCountPerMonth}");
            }
            if (_publishTownPartyLists != null)
                await _publishTownPartyLists();

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
                await SendPreparedPartyEntryCostUpdatesAsync(partyEntryPlans);
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

            // All frozen participants were prepared, charged, activated, and
            // registered before the first START_MAP projection.
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
            {
                await ReturnPreparedPartyRunsToTownAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    DungeonAdmissionReject.InvalidSelectionState,
                    "leader_changed_before_loading_projection");
                return;
            }

            var loadingProjectionId = DungeonMapHandler.CreateLoadingProjectionId();
            if (!run.TryClaimLoadingProjection(loadingProjectionId))
            {
                await ReturnPreparedPartyRunsToTownAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    DungeonAdmissionReject.InvalidSelectionState,
                    "leader_loading_projection_claim_failed");
                return;
            }

            var projectedFollowers = new List<EnhancedClientSession>();
            var loadingParticipants = new List<DungeonRunIdentity>
            {
                runIdentity,
            };
            foreach (var follower in preparedFollowers)
            {
                var followerRun = follower?.Player?.CurrentRun;
                var followerIdentity = followerRun?.CaptureIdentity()
                    ?? default;
                if (followerIdentity.IsValid
                    && followerRun.SharesPhysicalInstanceWith(run)
                    && followerRun.TryClaimLoadingProjection(loadingProjectionId)
                    && !loadingParticipants.Contains(followerIdentity))
                {
                    loadingParticipants.Add(followerIdentity);
                    projectedFollowers.Add(follower);
                }
            }
            if (projectedFollowers.Count != preparedFollowers.Count)
            {
                await ReturnPreparedPartyRunsToTownAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    DungeonAdmissionReject.InvalidSelectionState,
                    "follower_loading_projection_claim_failed");
                return;
            }

            var leaderProjectionSent = false;
            try
            {
                leaderProjectionSent = await SendDungeonSelectPacketsTo(
                    session,
                    req,
                    bossPos,
                    (byte)selection.Index,
                    loadingParticipants,
                    loadingProjectionId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"leader dungeon projection failed cid=" +
                    $"{session.Player.CharacterId}: {ex.Message}");
            }
            if (!leaderProjectionSent)
            {
                await ReturnPreparedPartyRunsToTownAsync(
                    session,
                    header.type,
                    partyEntryPlans,
                    DungeonAdmissionReject.InvalidSelectionState,
                    "leader_start_map_projection_failed");
                return;
            }
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            var followerProjections = new List<Task>();
            foreach (var follower in projectedFollowers)
            {
                followerProjections.Add(
                    SendPreparedPartyDungeonProjectionAsync(
                        follower,
                        run,
                        req,
                        bossPos,
                        (byte)selection.Index,
                        loadingParticipants,
                        loadingProjectionId));
            }

            // Each follower projection catches and logs its own failures. Do
            // not make the leader's command loop wait on a half-open follower
            // socket; the shared loading barrier owns the 45-second timeout.
            _ = Task.WhenAll(followerProjections);
            _ = SendPreparedPartyEntryCostUpdatesAsync(partyEntryPlans);
        }

        private async Task SendPreparedPartyDungeonProjectionAsync(
            EnhancedClientSession follower,
            DungeonRun leaderRun,
            Network.Parsers.Dungeon.SelectDungeonRequest request,
            int[] bossPosition,
            byte selectedMazeIndex,
            IReadOnlyList<DungeonRunIdentity> loadingParticipants,
            long loadingProjectionId)
        {
            try
            {
                var followerRun = follower?.Player?.CurrentRun;
                if (followerRun == null
                    || !followerRun.SharesPhysicalInstanceWith(leaderRun))
                {
                    return;
                }

                await SendDungeonSelectPacketsTo(
                    follower,
                    request,
                    bossPosition,
                    selectedMazeIndex,
                    loadingParticipants,
                    loadingProjectionId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"PARTY_DUNGEON_COOP projection failed: " +
                    $"cid={follower?.Player?.CharacterId ?? 0} " +
                    $"error={ex.Message}");
            }
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
                sourceRunIdentity,
                anotherAradSelection: null);
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
        private async Task<bool> SendDungeonSelectPacketsTo(
            EnhancedClientSession s,
            Network.Parsers.Dungeon.SelectDungeonRequest req,
            int[] bossPos,
            byte selectedMazeIndex,
            IReadOnlyList<DungeonRunIdentity> loadingParticipants = null,
            long loadingProjectionId = 0)
        {
            var run = s.Player.CurrentRun;
            if (run == null)
                return false;
            var runIdentity = run.CaptureIdentity();
            if (loadingProjectionId <= 0)
                loadingProjectionId = DungeonMapHandler.CreateLoadingProjectionId();
            if (!run.TryClaimLoadingProjection(loadingProjectionId))
                return false;
            if (run.AnotherAradActive && run.AnotherAradCrackQuestId > 0)
            {
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.CRACK_OF_DIMENSION,
                    BitConverter.GetBytes(
                        (uint)run.AnotherAradCrackQuestId)));
                if (!s.Player.IsCurrentDungeonRun(runIdentity))
                    return false;
            }
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
            if (!s.Player.IsCurrentDungeonRun(runIdentity)
                || !run.IsCurrentLoadingProjection(loadingProjectionId))
                return false;

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
                if (!s.Player.IsCurrentDungeonRun(runIdentity)
                    || !run.IsCurrentLoadingProjection(loadingProjectionId))
                    return false;

                await DungeonMechanismCoordinator.SendSelectionStateAsync(
                    s,
                    "after_dungeon_info");
            }
            if (!s.Player.IsCurrentDungeonRun(runIdentity)
                || !run.IsCurrentLoadingProjection(loadingProjectionId))
                return false;
            var hasSelectedStart = run.MazeStartX >= 0 && run.MazeStartY >= 0;
            var startRoomIdentity = await _mapHandler.SendStartMapAsync(
                s,
                run,
                hasSelectedStart ? run.MazeStartX : 0xFF,
                hasSelectedStart ? run.MazeStartY : 0xFF,
                overrideMapId: -1,
                expectedLoadingParticipants: loadingParticipants,
                loadingProjectionId: loadingProjectionId);
            if (!startRoomIdentity.HasValue
                || !s.Player.IsCurrentDungeonParticipantRoom(
                    startRoomIdentity.Value))
                return false;

            return true;
        }

        private bool TryBuildPartyEntryAdmissionPlans(
            EnhancedClientSession leader,
            DungeonRun leaderRun,
            DungeonSelectionContext leaderSelection,
            InventoryLease leaderLease,
            DungeonEntryAdmissionPreparation leaderPreparation,
            out List<PartyEntryAdmissionPlan> plans,
            out DungeonAdmissionReject rejection,
            out string failureReason)
        {
            plans = new List<PartyEntryAdmissionPlan>();
            rejection = DungeonAdmissionReject.InvalidSelectionState;
            failureReason = string.Empty;
            if (leader?.Player == null
                || leaderRun == null
                || leaderLease == null
                || leaderPreparation == null)
            {
                failureReason = "leader_preparation_missing";
                return false;
            }
            plans.Add(new PartyEntryAdmissionPlan
            {
                Session = leader,
                Selection = leaderSelection,
                Run = leaderRun,
                Lease = leaderLease,
                Preparation = leaderPreparation,
                PartySlot = leaderRun.EntryPartySlotIndex,
            });

            var cohort = leaderSelection?.PartyCohort;
            if (cohort == null)
                return true;
            if (_svc.Sessions == null
                || cohort.LeaderUserId != leader.Player.UserId)
            {
                failureReason = "party_cohort_unavailable";
                return false;
            }

            foreach (var participant in cohort.Participants)
            {
                if (participant.UserId == cohort.LeaderUserId)
                    continue;
                if (!_svc.Sessions.TryGet(
                        participant.CharacterId,
                        out var candidate)
                    || candidate?.Player == null
                    || candidate.SessionId != participant.SessionId
                    || candidate.Player.UserId != participant.UserId
                    || candidate.ListenerPort != leader.ListenerPort
                    || candidate.TcpClient == null
                    || !candidate.TcpClient.Connected
                    || candidate.Player.CurrentRun != null)
                {
                    failureReason =
                        $"session_mismatch_uid_{participant.UserId}";
                    return false;
                }
                var selection = candidate.Player.CurrentDungeonSelection;
                DungeonEntryAdmissionPreparation preparation = null;
                InventoryLease lease = null;
                EntryCostResult validation = null;
                if (!candidate.Player.IsCurrentDungeonSelection(selection)
                    || !ReferenceEquals(selection.PartyCohort, cohort)
                    || !TryValidateExistingRunEntry(
                        candidate,
                        leaderRun,
                        out preparation,
                        out lease,
                        out validation,
                        requireCurrentRun: false))
                {
                    rejection = ResolveEntryAdmissionReject(
                        validation,
                        participant.SlotIndex);
                    failureReason = validation?.FailReason
                        ?? $"selection_mismatch_uid_{participant.UserId}";
                    return false;
                }
                plans.Add(new PartyEntryAdmissionPlan
                {
                    Session = candidate,
                    Selection = selection,
                    Lease = lease,
                    Preparation = preparation,
                    PartySlot = participant.SlotIndex,
                });
            }

            if (plans.Count != cohort.Participants.Count)
            {
                failureReason = "party_plan_count_mismatch";
                return false;
            }
            plans.Sort((left, right) =>
                left.PartySlot.CompareTo(right.PartySlot));
            return true;
        }

        // The ENTER_SELECT stage has already frozen and projected the party
        // cohort. SELECT only consumes those exact member selections, attaches
        // them to the leader's shared instance, and replays START_MAP.
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
                AnotherAradActive = run.AnotherAradActive,
                AnotherAradWrapperDungeonId = run.AnotherAradWrapperDungeonId,
                AnotherAradHistoricalDungeonId = run.AnotherAradHistoricalDungeonId,
                AnotherAradCrackQuestId = run.AnotherAradCrackQuestId,
                AnotherAradQuestAccepted = run.AnotherAradQuestAccepted,
                AnotherAradQuestDefinition = run.AnotherAradQuest?.Definition,
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
                    await RejectSelectionAsync(
                        session,
                        selection,
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
                await RejectSelectionAsync(
                    session,
                    selection,
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

        internal bool TryValidateExistingRunEntry(
            EnhancedClientSession session,
            DungeonRun run,
            out DungeonEntryAdmissionPreparation preparation,
            out InventoryLease lease,
            out EntryCostResult validation,
            bool requireCurrentRun = true)
        {
            preparation = null;
            lease = null;
            validation = null;
            if (session?.Player == null
                || run == null
                || (requireCurrentRun
                    && !session.Player.IsCurrentDungeonRun(
                        run.CaptureIdentity())))
            {
                validation = new EntryCostResult().Fail(
                    "stale participant run",
                    EntryCostFailureKind.InvalidState);
                return false;
            }
            if (!WorldMap.IsStoryDungeon(run.DungeonId)
                && !DungeonData.MeetsMinimumRequiredLevel(
                    run.DungeonId,
                    session.Player.Level,
                    out var minimumLevel))
            {
                validation = new EntryCostResult().Fail(
                    $"level {session.Player.Level} requires {minimumLevel}",
                    EntryCostFailureKind.MissingPermission);
                return false;
            }
            if (_svc.MercenaryRestrictions != null
                && !_svc.MercenaryRestrictions.CanEnterContent(
                    session.Player.CharacterId))
            {
                validation = new EntryCostResult().Fail(
                    "mercenary content restricted",
                    EntryCostFailureKind.MissingPermission);
                return false;
            }
            if (!TryLoadEntryQuestSets(
                    session.Player.CharacterId,
                    out var activeQuestIds,
                    out var clearedQuestIds,
                    out var questError))
            {
                validation = new EntryCostResult().Fail(
                    questError,
                    EntryCostFailureKind.InvalidState);
                return false;
            }
            var admission = WorldMap.EvaluateDungeonAdmission(
                run.DungeonId,
                session.Player.Level,
                activeQuestIds,
                clearedQuestIds);
            if (!admission.Allowed)
            {
                validation = new EntryCostResult().Fail(
                    "worldmap " + admission.Reason,
                    EntryCostFailureKind.MissingPermission);
                return false;
            }

            PvfLib.MazeInfo maze;
            try
            {
                maze = DungeonData.GetDungeonMaze(
                    run.DungeonId,
                    run.MazeIndex);
            }
            catch (Exception ex)
            {
                validation = new EntryCostResult().Fail(
                    "maze unavailable: " + ex.Message,
                    EntryCostFailureKind.Unavailable);
                return false;
            }
            if (maze == null
                || !TryGetOwnedInventoryLease(session, out lease)
                || !_svc.EntryAdmission.TryPrepareRun(
                    lease,
                    run,
                    run.Instance.Mechanisms.Tournament?.Definition,
                    run.HellMode,
                    run.HellPartyMode,
                    maze,
                    run.MazeIndex,
                    run.HellGorgeousChallenge,
                    out preparation,
                    out validation))
            {
                validation ??= new EntryCostResult().Fail(
                    maze == null
                        ? "maze unavailable"
                        : "owned inventory lease is missing",
                    EntryCostFailureKind.InvalidState);
                return false;
            }
            return true;
        }

        private bool TryLoadEntryQuestSets(
            int characterId,
            out HashSet<int> activeQuestIds,
            out HashSet<int> clearedQuestIds,
            out string error)
        {
            activeQuestIds = null;
            clearedQuestIds = null;
            error = string.Empty;
            try
            {
                var active = QuestService.LoadActiveQuests(
                    _svc.ConnectionString,
                    characterId);
                if (active.Count > 0)
                {
                    activeQuestIds = new HashSet<int>(
                        active.ConvertAll(quest => (int)quest.QuestId));
                }
                var cleared = new QuestRepository(_svc.ConnectionString)
                    .LoadClearedFlags(characterId);
                if (cleared.Count > 0)
                    clearedQuestIds = new HashSet<int>(cleared.Keys);
                return true;
            }
            catch (Exception ex)
            {
                error = "quest probe failed: " + ex.Message;
                return false;
            }
        }

        private async Task<bool> TryPreparePartyDungeonEntryRunsAsync(
            EnhancedClientSession leader,
            Network.Parsers.Dungeon.SelectDungeonRequest req,
            DungeonEntryExperienceBonusPlan experienceBonusPlan,
            DungeonSelectionContext leaderSelection,
            IReadOnlyList<PartyEntryAdmissionPlan> plans)
        {
            var cohort = leaderSelection?.PartyCohort;
            if (cohort == null)
                return true;
            if (plans == null
                || plans.Count != cohort.Participants.Count
                || cohort.LeaderUserId != leader.Player.UserId)
            {
                return false;
            }

            var leaderRun = leader.Player.CurrentRun;
            if (leaderRun == null)
                return false;
            var leaderIdentity = leaderRun.CaptureIdentity();
            foreach (var plan in plans)
            {
                var member = plan.Session;
                if (ReferenceEquals(member, leader))
                    continue;
                try
                {
                    if (!leader.Player.IsCurrentDungeonRun(leaderIdentity)
                        || member?.Player == null
                        || member.Player.CurrentRun != null
                        || !member.Player.IsCurrentDungeonSelection(
                            plan.Selection)
                        || !ReferenceEquals(
                            plan.Selection.PartyCohort,
                            cohort))
                    {
                        return false;
                    }
                    await DungeonMechanismCoordinator.ClearRunEffectsAsync(
                        member,
                        "party_select_dungeon_prepare_run");
                    if (!leader.Player.IsCurrentDungeonRun(leaderIdentity)
                        || !member.Player.IsCurrentDungeonSelection(
                            plan.Selection)
                        || !DungeonRunLifecycle.BeginRun(
                            member,
                            req.DungeonId,
                            req.Difficulty,
                            leaderRun.Instance,
                            _svc.InstanceRegistry,
                            experienceBonusPlan?.ForParticipant(member),
                            plan.Selection))
                    {
                        return false;
                    }

                    var run = member.Player.CurrentRun;
                    var sharedSelection = leaderRun.Instance.Selection;
                    if (run == null)
                        return false;
                    plan.Run = run;
                    if (sharedSelection == null)
                        return false;
                    sharedSelection.ApplyTo(run);
                    plan.Preparation.ApplyTo(run);
                    var storyBonus =
                        DungeonStoryExperienceProfilePolicy.Capture(run);
                    if (storyBonus.IsStoryRun)
                    {
                        run.TryFreezeStoryExperienceProfile(
                            storyBonus.RatePercent,
                            storyBonus.ExperienceDifficulty);
                    }
                    run.LinkedDungeonNextId = leaderRun.LinkedDungeonNextId;
                    run.LinkedDungeonNextRate = leaderRun.LinkedDungeonNextRate;
                    run.LinkedDungeonNextCondition =
                        leaderRun.LinkedDungeonNextCondition;
                    DungeonMechanismCoordinator.CloneSelection(
                        member,
                        leaderRun,
                        run,
                        "party_select_dungeon_prepare_run");
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"PARTY_DUNGEON_COOP prepare failed: " +
                        $"cid={member?.Player?.CharacterId ?? 0} " +
                        $"error={ex.Message}");
                    return false;
                }
            }
            return plans.All(plan => plan.Run != null);
        }

        private bool TryActivatePreparedPartyEntry(
            EnhancedClientSession leader,
            IReadOnlyList<PartyEntryAdmissionPlan> plans,
            ICollection<EnhancedClientSession> preparedFollowers)
        {
            var cohort = plans?
                .FirstOrDefault(plan => ReferenceEquals(plan.Session, leader))
                ?.Run?.EntryPartySelectionCohort;
            if (cohort != null)
            {
                var party = _svc.PartyManager?.GetPartySnapshot(cohort.PartyId);
                if (party == null
                    || party.Count != cohort.Participants.Count
                    || !party.IsLeader(cohort.LeaderUserId))
                {
                    return false;
                }
                foreach (var participant in cohort.Participants)
                {
                    var member = party.GetMember(participant.UserId);
                    if (member == null
                        || member.CharacterId != participant.CharacterId
                        || member.SessionId != participant.SessionId
                        || member.SlotIndex != participant.SlotIndex
                        || !plans.Any(plan =>
                            plan.Session?.SessionId == participant.SessionId
                            && plan.Session.Player?.CharacterId
                                == participant.CharacterId
                            && plan.PartySlot == participant.SlotIndex))
                    {
                        return false;
                    }
                }
            }

            foreach (var plan in plans)
            {
                var session = plan.Session;
                var run = plan.Run;
                if (session?.Player == null
                    || run == null
                    || run.RunState != DungeonRunState.Selecting
                    || !session.Player.IsCurrentDungeonRun(
                        run.CaptureIdentity()))
                {
                    return false;
                }
            }

            foreach (var plan in plans)
            {
                var session = plan.Session;
                var run = plan.Run;
                try
                {
                    if (!run.TryActivate())
                        return false;
                    RegisterActiveParticipant(session, run);
                    session.Player.UserState = 0x01;
                    if (!ReferenceEquals(session, leader))
                        preparedFollowers.Add(session);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"party run activation failed cid=" +
                        $"{session.Player.CharacterId}: {ex.Message}");
                    return false;
                }
            }
            return true;
        }

        private async Task SendPreparedPartyEntryCostUpdatesAsync(
            IReadOnlyList<PartyEntryAdmissionPlan> plans)
        {
            foreach (var plan in plans)
            {
                try
                {
                    await SendEntryCostUpdates(
                        plan.Session,
                        plan.Run.CaptureIdentity(),
                        plan.CostResult,
                        plan.Preparation.CostPlan.Source);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"party entry cost projection failed cid=" +
                        $"{plan.Session?.Player?.CharacterId ?? 0}: " +
                        ex.Message);
                }
            }
        }

        private async Task RejectPreparedPartyEntryAsync(
            EnhancedClientSession leader,
            ushort wireType,
            IReadOnlyList<PartyEntryAdmissionPlan> plans,
            DungeonAdmissionReject rejection,
            string reason)
        {
            DungeonSelectionContext leaderSelection = null;
            if (plans != null)
            {
                foreach (var plan in plans)
                {
                    var run = plan.Run;
                    if (run == null
                        || run.RunState != DungeonRunState.Selecting
                        || plan.Session?.Player?.IsCurrentDungeonRun(
                            run.CaptureIdentity()) != true)
                    {
                        continue;
                    }
                    var restored = await DungeonRunLifecycle
                        .RejectSelectingRunAsync(
                            plan.Session,
                            run.CaptureIdentity(),
                            _svc.InstanceRegistry);
                    if (ReferenceEquals(plan.Session, leader))
                        leaderSelection = restored;
                }
            }
            try
            {
                await _svc.AdmissionRejects.SendAsync(
                    leader,
                    wireType,
                    rejection);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"party entry rejection projection failed cid=" +
                    $"{leader?.Player?.CharacterId ?? 0}: {ex.Message}");
            }
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"PARTY_DUNGEON_COOP rejected: " +
                $"cid={leader?.Player?.CharacterId ?? 0} reason={reason}");
            if (leaderSelection?.PartyCohort?.ReturnToTownOnEntryReject == true
                && _returnRejectedPartySelectionToTown != null)
            {
                await _returnRejectedPartySelectionToTown(
                    leader,
                    leaderSelection);
            }
        }

        private async Task ReturnPreparedPartyRunsToTownAsync(
            EnhancedClientSession leader,
            ushort wireType,
            IReadOnlyList<PartyEntryAdmissionPlan> plans,
            DungeonAdmissionReject rejection,
            string reason,
            bool sendAdmissionReject = true)
        {
            var ended = new List<(EnhancedClientSession Session,
                DungeonRunIdentity Identity,
                DungeonTownReturnAnchor Anchor)>();
            foreach (var plan in plans ?? Array.Empty<PartyEntryAdmissionPlan>())
            {
                var session = plan.Session;
                var run = plan.Run;
                if (run == null
                    || session?.Player?.IsCurrentDungeonRun(
                        run.CaptureIdentity()) != true)
                {
                    continue;
                }
                var identity = run.CaptureIdentity();
                var anchor = run.TownReturnAnchor;
                try
                {
                    var detached = await DungeonRunLifecycle.EndRunAsync(
                        session,
                        DungeonRunEndReason.EntryRejected,
                        identity,
                        _svc.InstanceRegistry);
                    if (detached
                        || DungeonRunLifecycle.CanProjectTownState(
                            session,
                            identity))
                    {
                        ended.Add((session, identity, anchor));
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"party entry rollback failed cid=" +
                        $"{session.Player.CharacterId}: {ex.Message}");
                    if (DungeonRunLifecycle.CanProjectTownState(
                            session,
                            identity))
                    {
                        ended.Add((session, identity, anchor));
                    }
                }
            }

            foreach (var entry in ended)
            {
                DungeonRunLifecycle.ApplyTownReturnAnchor(
                    entry.Session.Player,
                    entry.Anchor,
                    entry.Session.ListenerPort);
                entry.Session.Player.UserState = 0x00;
            }
            leader?.Player?.ClearPendingPartyRetryEntry();
            if (sendAdmissionReject)
            {
                try
                {
                    await _svc.AdmissionRejects.SendAsync(
                        leader,
                        wireType,
                        rejection);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"party entry rejection projection failed cid=" +
                        $"{leader?.Player?.CharacterId ?? 0}: {ex.Message}");
                }
            }
            foreach (var entry in ended)
            {
                try
                {
                    await _svc.TownReturn.ProjectEndedRunAsync(
                        entry.Session,
                        entry.Identity,
                        entry.Anchor);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"party entry town projection failed cid=" +
                        $"{entry.Session.Player.CharacterId}: {ex.Message}");
                }
            }
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"PARTY_DUNGEON_COOP returned after rejection: " +
                $"leader={leader?.Player?.CharacterId ?? 0} " +
                $"returned={ended.Count}/{plans?.Count ?? 0} " +
                $"reason={reason}");
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
                await RejectSelectionAsync(
                    session,
                    selection,
                    wireType,
                    rejection);
            }
        }

        private async Task RejectSelectionAsync(
            EnhancedClientSession session,
            DungeonSelectionContext selection,
            ushort wireType,
            DungeonAdmissionReject rejection)
        {
            try
            {
                await _svc.AdmissionRejects.SendAsync(
                    session,
                    wireType,
                    rejection);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"selection rejection projection failed cid=" +
                    $"{session?.Player?.CharacterId ?? 0}: {ex.Message}");
            }
            if (selection?.PartyCohort?.ReturnToTownOnEntryReject == true
                && session?.Player?.IsCurrentDungeonSelection(selection) == true
                && _returnRejectedPartySelectionToTown != null)
            {
                await _returnRejectedPartySelectionToTown(
                    session,
                    selection);
            }
        }

        private void RollbackLicensedDungeonEntry(
            LicensedDungeonEntryPlan plan)
        {
            if (plan == null)
                return;
            if (_svc.LicensedDungeons.TryRollbackEntry(
                    plan,
                    out var failureReason))
            {
                return;
            }

            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"licensed dungeon entry rollback failed: " +
                $"cid={plan.CharacterId} " +
                $"dungeon={plan.Definition.DungeonId} " +
                $"reason={failureReason}");
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

        // 城镇残留白影修复(与切区域同一机制, 见 TownAreaRosterDepartureNotifier):
        // 进本提交后玩家离开城镇, 向旧区域广播离开者 USER_AREA(0x0017) 远程移除。
        // 不得广播 AREA_USERS(0x0018)：已在场客户端会 setDrawLoadingMode 并关掉界面。
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
            run.Instance.RegisterParticipantLife(attachment.RunIdentity);
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
