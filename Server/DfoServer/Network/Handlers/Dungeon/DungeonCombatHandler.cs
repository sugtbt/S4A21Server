using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.Progression;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Pets;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonCombatHandler
    {
        // 成长之契约经验加成从 PVF premiumlist_new.etc 读取(PremiumEffectProvider)。
        private static readonly TimeSpan DeathRespawnDelay = TimeSpan.FromSeconds(10);

        private readonly DungeonSharedServices _svc;
        private readonly DungeonSettlementHandler _settlement;
        private readonly DungeonKillApplicationService _kills;
        private readonly TournamentDungeonCoordinator _tournament;

        internal DungeonCombatHandler(
            DungeonSharedServices svc,
            DungeonSettlementHandler settlement,
            TournamentDungeonCoordinator tournament,
            BloodAltarDungeonCoordinator bloodAltar)
        {
            _svc = svc;
            _settlement = settlement;
            _tournament = tournament
                ?? throw new ArgumentNullException(nameof(tournament));
            _kills = new DungeonKillApplicationService(
                svc,
                settlement,
                tournament,
                bloodAltar);
        }

        internal Task ProcessMechanismKillAsync(KillContext context)
            => _kills.ProcessAsync(context);

        internal Task RecoverParticipantEffectsAsync(
            EnhancedClientSession session)
            => _kills.RecoverParticipantEffectsAsync(session);

        internal async Task HandleDieMonster(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            var request = DieMonsterRequest.Parse(body);
            var runIdentity = run.CaptureIdentity();
            run.TryCaptureCurrentRoomSnapshot(out var currentRoomSnapshot);
            var treatAsPassiveObject = ShouldTreatAsPassiveObject(
                request.IsPassiveObject,
                request.HasMapOwnedPassiveObjectSignature,
                request.LocalIndex,
                currentRoomSnapshot);
            if (request.IsPassiveObject && !treatAsPassiveObject)
            {
                FileLogger.Log(
                    $"[DungeonHandler] DIE_MONSTER passive marker downgraded " +
                    $"to current-room monster: cid={session.Player.CharacterId} " +
                    $"seq={request.LocalIndex} room={run.CurrentRoomInstanceId}");
            }

            if (treatAsPassiveObject)
            {
                var objectCode = (int)request.LocalIndex;
                var passiveObjectSource = request.IsPassiveObject
                    ? "packet-marker"
                    : request.HasMapOwnedPassiveObjectSignature
                        ? "frozen-map-sentinel"
                        : "frozen-map";
                var passiveObjectEvent = DungeonEventEnvelope.Create(
                    run,
                    session.Player.CharacterId,
                    "passive-object-destroyed",
                    sourceActorCode: objectCode);
                if (run.TryCaptureCurrentRoomSnapshot(
                        passiveObjectEvent.RoomIdentity,
                        out var roomSnapshot)
                    && roomSnapshot.RoomState?.InstanceRoom != null)
                {
                    var death = roomSnapshot.RoomState.InstanceRoom
                        .TryRecordNextMapOwnedPassiveObjectDeath(
                            passiveObjectEvent,
                            objectCode,
                            out var actorDefined);
                    if (actorDefined)
                    {
                        if (!death.Accepted || !death.Created)
                        {
                            FileLogger.Log(
                                $"[DungeonHandler] duplicate/exhausted passive object " +
                                $"ignored: cid={session.Player.CharacterId} " +
                                $"code={objectCode} room={run.CurrentRoomInstanceId}");
                            return;
                        }

                        passiveObjectEvent = death.Fact.Source;
                        objectCode = death.Fact.ActorCode;
                    }
                }

                FileLogger.Log(
                    $"[DungeonHandler] DIE_MONSTER: passive object " +
                    $"code={objectCode} source={passiveObjectSource}");
                await DungeonActorQuestSync.SyncAsync(
                    session,
                    objectCode,
                    actorType: 9,
                    passiveObjectEvent);
                if (!session.Player.IsCurrentDungeonRun(runIdentity))
                    return;

                DungeonMechanismCoordinator.OnPassiveObjectDestroyed(
                    session,
                    run,
                    objectCode);
                if (run.ClearCondition != null
                    && run.ClearCondition.Check(0, objectCode))
                {
                    await _settlement.SubmitClearIntentAsync(
                        session,
                        new DungeonClearIntent(
                            passiveObjectEvent,
                            $"destroy object {objectCode}",
                            bossCode: 0));
                }

                if (session.Player.IsCurrentDungeonRun(runIdentity)
                    && run.Tower == null)
                {
                    await _svc.QuestDrops.CheckPassiveObjectDrop(
                        session,
                        run,
                        passiveObjectEvent,
                        objectCode);
                }
                return;
            }

            var killEvent = DungeonEventEnvelope.Create(
                run,
                session.Player.CharacterId,
                "monster-killed",
                sourceActorId: request.LocalIndex);
            await _kills.ProcessAsync(new KillContext(
                session,
                killEvent,
                request.LocalIndex,
                session.Player.UserId,
                DungeonKillOrigin.LocalReport,
                request.IsCapture
                    ? DungeonActorDeathKind.Captured
                    : DungeonActorDeathKind.Defeated));
        }

        internal static bool ShouldTreatAsPassiveObject(
            bool passiveMarker,
            bool mapOwnedPassiveObjectSignature,
            ushort sequenceId,
            DungeonRunRoomSnapshot roomSnapshot)
        {
            var matchesFrozenMap = roomSnapshot
                ?.ContainsMapOwnedPassiveObjectCode(sequenceId) == true;
            if (matchesFrozenMap && mapOwnedPassiveObjectSignature)
                return true;

            if (roomSnapshot?.ContainsStaticActorSequence(sequenceId) == true)
                return false;

            return passiveMarker || matchesFrozenMap;
        }

        internal async Task HandleBossDieCheck(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || !BossDieCheckRequest.TryParse(body, out var request))
            {
                return;
            }
            var runIdentity = run.CaptureIdentity();

            var mechanismClear = DungeonMechanismCoordinator.OnBossDieCheck(
                session,
                run,
                request);
            var bossCheckEvent = DungeonEventEnvelope.Create(
                run,
                session.Player.CharacterId,
                "boss-die-check",
                sourceActorId: request.BossSequence,
                sourceActorCode: mechanismClear.BossCode > 0
                    ? mechanismClear.BossCode
                    : null);
            bossCheckEvent = await _kills.ProcessConfirmedBossDeathAsync(
                session,
                bossCheckEvent,
                mechanismClear.BossCode,
                session.Player.UserId);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;
            if (!mechanismClear.ShouldClearDungeon)
                return;
            await _settlement.SubmitClearIntentAsync(
                session,
                new DungeonClearIntent(
                    bossCheckEvent,
                    mechanismClear.ClearReason,
                    mechanismClear.BossCode));
        }

        internal static bool IsAiCharacterActorType(byte actorType)
        {
            return actorType >= 5 && actorType <= 8;
        }

        internal static bool ShouldClearDungeon(
            bool clearConditionMatched,
            bool reachedBossEndpoint,
            bool ignoreDefaultDungeonClear)
        {
            return clearConditionMatched
                || (reachedBossEndpoint && !ignoreDefaultDungeonClear);
        }


        internal async Task HandleDieCharacter(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var bodyHex = body != null ? BitConverter.ToString(body) : "null";
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DIE_CHARACTER: uid={session.Player.UserId} body={bodyHex}");
            var deathRun = session.Player.CurrentRun;
            var scriptedDeath =
                DungeonMechanismCoordinator.OnCharacterDied(session, deathRun);
            var deathEvent = scriptedDeath.ClearRequest.ShouldClearDungeon
                && deathRun != null
                ? DungeonEventEnvelope.Create(
                    deathRun,
                    session.Player.CharacterId,
                    "character-death-clear",
                    sourceActorId: session.Player.UserId)
                : null;
            if (scriptedDeath.SuppressRespawn)
                DungeonRunLifecycle.CancelDeathRespawn(session);
            else
                ScheduleDeathRespawn(session);

            // NOTI 32 (wire 0x0020) DIE_STATE: u16 actorId + u8 dieType(0=death) + u8 flag
            var w = new GamePacketWriter();
            w.WriteUInt16(session.Player.UserId);
            w.WriteByte(0x00);  // dieType=0 death confirmed
            w.WriteByte(0x00);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0020, w.ToArray()));

            if (deathEvent != null
                && session.Player.IsCurrentDungeonRun(deathEvent.RunIdentity))
            {
                await _settlement.SubmitClearIntentAsync(
                    session,
                    new DungeonClearIntent(
                        deathEvent,
                        scriptedDeath.ClearRequest.ClearReason,
                        scriptedDeath.ClearRequest.BossCode));
            }
        }

        internal async Task HandleDeathRespawn(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var bodyHex = body != null ? BitConverter.ToString(body) : "null";
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DEATH_RESPAWN: uid={session.Player.UserId} body={bodyHex}");

            var run = session?.Player?.CurrentRun;
            if (run == null)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DEATH_RESPAWN ignored: no active run");
                return;
            }
            if (!run.Timers.TryGetCurrentTicket(
                    DungeonRunTimerKeys.CombatDeathRespawn,
                    out var ticket))
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DEATH_RESPAWN ignored: no active timer");
                return;
            }

            await ResolveDeathRespawnDeadlineAsync(
                session,
                run,
                run.CaptureIdentity(),
                ticket,
                force: false,
                source: "client");
        }

        private void ScheduleDeathRespawn(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            DungeonRunLifecycle.CancelDeathRespawn(session);
            run = session.Player.CurrentRun;
            if (run == null)
                return;

            run.IsWaitingDeathRespawn = true;
            run.DeathRespawnAvailableAt = DateTime.UtcNow.Add(DeathRespawnDelay);

            var identity = run.CaptureIdentity();
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.CombatDeathRespawn,
                run.DeathRespawnAvailableAt,
                RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleDeathRespawnTimer(
                session,
                run,
                identity,
                ticket,
                run.DeathRespawnAvailableAt,
                "death");
        }

        internal bool RecoverDeathRespawnTimer(
            EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return false;
            if (!run.IsWaitingDeathRespawn
                || run.DeathRespawnAvailableAt == DateTime.MinValue)
            {
                run.Timers.Cancel(DungeonRunTimerKeys.CombatDeathRespawn);
                return false;
            }
            if (!run.Timers.TryResume(
                    DungeonRunTimerKeys.CombatDeathRespawn,
                    out var ticket,
                    out var deadlineUtc))
            {
                return false;
            }

            ScheduleDeathRespawnTimer(
                session,
                run,
                run.CaptureIdentity(),
                ticket,
                deadlineUtc,
                "rejoin");
            return true;
        }

        private void ScheduleDeathRespawnTimer(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket,
            DateTime deadlineUtc,
            string source)
        {
            var timerName = BuildDeathRespawnTimerName(session, run, ticket);
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                timerName,
                deadlineUtc,
                async _ => await OnDeathRespawnTimerElapsedAsync(
                    session,
                    run,
                    identity,
                    ticket));
            run.Timers.Attach(ticket, handle);
            var remaining = deadlineUtc - DateTime.UtcNow;
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] DIE_TIMER: " +
                $"scheduled uid={session.Player.UserId} source={source} " +
                $"deadline={deadlineUtc:O} " +
                $"remainingMs={Math.Max(0, remaining.TotalMilliseconds):F0}");
        }

        private async Task OnDeathRespawnTimerElapsedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket)
        {
            if (!IsDeathRespawnTimerCurrent(
                    session,
                    run,
                    identity,
                    ticket))
                return;

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DIE_TIMER: deadline uid={session.Player.UserId}");
            await ResolveDeathRespawnDeadlineAsync(
                session,
                run,
                identity,
                ticket,
                force: true,
                source: "timer");
        }

        private async Task ResolveDeathRespawnDeadlineAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity runIdentity,
            RunTimerTicket ticket,
            bool force,
            string source)
        {
            if (session?.Player == null || run == null)
                return;

            if (!session.Player.IsCurrentDungeonRun(runIdentity)
                || !run.IsWaitingDeathRespawn
                || !run.Timers.IsCurrent(ticket))
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DEATH_RESPAWN ignored: stale source={source}");
                return;
            }

            if (!force)
            {
                var remaining = run.DeathRespawnAvailableAt - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DEATH_RESPAWN delayed: {remaining.TotalMilliseconds:F0}ms remaining");
                    return;
                }
            }

            var tournamentDecision = _tournament.ResolveDeathDeadline(
                session,
                run,
                runIdentity);
            if (tournamentDecision.Action
                == TournamentDeathDeadlineAction.Ignore)
            {
                DungeonRunLifecycle.CancelDeathRespawn(session);
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"DEATH_RESPAWN mechanism decision ignored: source={source}");
                return;
            }
            if (tournamentDecision.Action
                == TournamentDeathDeadlineAction.PresentTournamentRewards)
            {
                DungeonRunLifecycle.CancelDeathRespawn(session);
                FileLogger.Log(
                    $"[Tournament] death deadline enters settlement: " +
                    $"cid={session.Player.CharacterId} " +
                    $"run={run.RunId}/{run.RunGeneration} " +
                    $"rounds={tournamentDecision.CompletedRounds} " +
                    $"new={tournamentDecision.NewlyEliminated} " +
                    $"source={source}");
                await _tournament.EnsureParticipantRewardsAsync(
                    session,
                    run,
                    forceProjection: false);
                return;
            }

            DungeonRunLifecycle.CancelDeathRespawn(session);
            if (!await DungeonRunLifecycle.EndRunAsync(
                    session,
                    DungeonRunEndReason.DeathRespawn,
                    runIdentity,
                    _svc.InstanceRegistry))
            {
                return;
            }
            if (!DungeonRunLifecycle.CanProjectTownState(
                    session,
                    runIdentity))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"DEATH_RESPAWN town projection skipped: stale source={source}");
                return;
            }
            session.Player.UserState = 0x00;
            // 死亡复活回城 → 状态回空闲：同频道在线好友推 USERINFO(0x0002) 更新场景实体状态。
            if (_svc.Sessions != null)
                await UnitedFriendSystem.NotifyUserStateChanged(
                    session, _svc.Sessions);

            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snapshot)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x007B,
                CommonPacketBodyBuilder.BuildSuccessAck()));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00CA,
                new byte[] { 0x00 }));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;

            // Future failure weakness state should be applied here before subtype0.
            await _svc.ProgressNotifications.SendUserInfoSubtype0Broadcast(session);
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DEATH_RESPAWN: complete source={source}");
        }

        private static bool IsDeathRespawnTimerCurrent(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket)
            => session?.Player != null
               && session.Player.IsCurrentDungeonRun(identity)
               && run.Matches(identity)
               && run.IsWaitingDeathRespawn
               && run.Timers.IsCurrent(ticket);

        private static string BuildDeathRespawnTimerName(
            EnhancedClientSession session,
            DungeonRun run,
            RunTimerTicket ticket)
            => "dungeon-death:" + session.SessionId.ToString("N")
                + ":" + run.RunId
                + ":" + ticket.Generation;

        private uint CalculateGrowthContractMonsterBonus(EnhancedClientSession session, uint baseMonsterExp)
        {
            if (baseMonsterExp == 0)
                return 0;

            var accountId = session.Account?.AccountId ?? 0;
            var connStr = _svc.ConnectionString;
            return Game.Premium.PremiumEffectProvider.GetCombinedEffects(connStr, accountId).ComputeBonusExp(baseMonsterExp);
        }

        internal async Task<bool> HandleUseCoin(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            // df_game_r: read = u16 targetActorId
            ushort targetId = body != null && body.Length >= 2 ? BitConverter.ToUInt16(body, 0) : session.Player.UserId;
            var characterId = session.Player?.CharacterId ?? 0;
            var run = session.Player?.CurrentRun;
            var runIdentity = run?.CaptureIdentity() ?? default;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] USE_COIN: uid={session.Player.UserId} target={targetId} cid={characterId}");

            if (run == null || !session.Player.IsCurrentDungeonRun(runIdentity))
                return false;

            // 先扣复活币, 成功才发复活通知(旧实现不扣币白送复活)
            short coinSlot;
            int coinRemaining;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId)
                || !_svc.ReviveCoin.TryConsume(
                    lease,
                    out coinSlot,
                    out coinRemaining))
            {
                var err = new GamePacketWriter();
                err.WriteByte(0x00);
                err.WriteUInt16(targetId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0029, err.ToArray()));
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] USE_COIN: no coin cid={characterId}");
                return false;
            }

            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return false;
            DungeonRunLifecycle.CancelDeathRespawn(run);

            // 1. NOTI 0x0020 DIE_STATE: set_charac_live(user, 1=revive)
            //    df_game_r body = u16 actorId + u8 state; 86JP has extra u8 flag
            var noti = new GamePacketWriter();
            noti.WriteUInt16(targetId);
            noti.WriteByte(0x01);  // state=1 revive
            noti.WriteByte(0x00);  // 86JP flag
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0020, noti.ToArray()));
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return false;

            // 2. CMD ACK 0x0029: resultCode=1 + u16 targetActorId
            //    不补发 0x000E: 客户端使用复活币时本地已预扣显示(PR#338 实测说明), 全量列表随下次进城刷新
            var ack = new GamePacketWriter();
            ack.WriteByte(0x01);           // resultCode = success
            ack.WriteUInt16(targetId);     // targetActorId
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0029, ack.ToArray()));
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] USE_COIN: OK cid={characterId} slot={coinSlot} remaining={coinRemaining}");
            return true;
        }

        internal async Task HandleGetItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            var runIdentity = run.CaptureIdentity();

            var req = GetItemRequest.Parse(body);
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: cid={session.Player.CharacterId} srcSlot={req.SrcSlot}");

            if (run.Tower != null
                && await _svc.DeathTower.TryHandleGetItem(session, req.SrcSlot))
            {
                return;
            }
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            var pickup = _svc.ItemAcquisition.AcquireGroundDrop(
                run,
                req.SrcSlot,
                session);

            if (!pickup.Success)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: {pickup.FailReason} srcSlot={req.SrcSlot}");
                if (pickup.FailReason == PickupFailReason.InventoryFull)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        (ushort)CmdPacketTypeA21.GET_ITEM,
                        new byte[] { 0x00, 0x04 }));
                }
                return;
            }

            PersistPickup(session, req.SrcSlot);

            // A21 客户端先消费 1B 成功 ACK，再解析 0x0027 拾取通知。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.GET_ITEM,
                DropItemBuilder.BuildGetItemSuccessAck()));
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            if (pickup.IsGold)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.GET_ITEM,
                    DropItemBuilder.BuildPickupGold(
                        req.SrcSlot,
                        session.Player.UserId,
                        pickup.GoldAmount,
                        pickup.ExtraGold)));
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: gold pickup srcSlot={req.SrcSlot} gold={pickup.GoldAmount} extra={pickup.ExtraGold}");
            }
            else
            {
                var pickupBody = pickup.IsEpicPiece
                    ? DropItemBuilder.BuildPickupEpicPiece(
                        req.SrcSlot,
                        session.Player.UserId)
                    : DropItemBuilder.BuildPickupItem(
                        req.SrcSlot,
                        session.Player.UserId,
                        (ushort)pickup.InventorySlot,
                        7);

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.GET_ITEM,
                    pickupBody));
                if (pickup.IsEpicPiece)
                {
                    await InventoryRefreshSender.SendEpicPieceInfo(
                        session,
                        pickup.PickedUpItemId,
                        pickup.EpicPieceBalance);
                }
                if (session.Player.IsCurrentDungeonRun(runIdentity)
                    && session.GameSession?.QuestManager != null
                    && pickup.PickedUpItemId > 0)
                {
                    session.GameSession.QuestManager
                        .RecalibrateItemSeekingQuestProgressWithoutNotification(
                            new[] { pickup.PickedUpItemId });
                }
                var inventorySlotLog = pickup.IsEpicPiece ? "none" : pickup.InventorySlot.ToString();
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: item pickup srcSlot={req.SrcSlot} templateId={pickup.PickedUpItemId} invSlot={inventorySlotLog}");
            }
        }

        private static void PersistPickup(EnhancedClientSession session, ushort srcSlot)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                return;
            }

            if (!InventoryPersistenceService.SaveDirty(lease))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] GET_ITEM: SaveDirty failed " +
                    $"cid={characterId} srcSlot={srcSlot}");
            }
        }

        internal async Task HandleDropItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null)
                return;
            var runIdentity = run.CaptureIdentity();

            DropItemRequest request;
            try
            {
                request = DropItemRequest.Parse(body);
            }
            catch (ArgumentException ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DROP_ITEM: rejected body({body?.Length ?? 0}B): {ex.Message}");
                return;
            }

            var result = _svc.Drops.TryDropInventoryItem(
                run,
                session,
                request.ListType,
                request.SlotIndex,
                request.Count);
            if (!result.Success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    DropItemBuilder.BuildDropFailureAck(17, (byte)request.ListType)));
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DROP_ITEM: {result.FailReason} cid={session.Player.CharacterId} list={request.ListType} slot={request.SlotIndex} count={request.Count}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                DropItemBuilder.BuildDropSuccessAck(
                    (byte)request.ListType,
                    unchecked((ushort)request.SlotIndex),
                    request.Count)));
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.DROP_ITEM,
                DropItemBuilder.BuildDrop(
                    session.Player.UserId,
                    request.PositionX,
                    request.PositionY,
                    result.Drop,
                    0)));
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] DROP_ITEM: cid={session.Player.CharacterId} slot={request.SlotIndex} templateId={result.Drop.TemplateId} count={result.Drop.StackCount} value={result.Drop.PacketValue} remaining={result.RemainingStackCount} sceneSlot={result.Drop.SceneSlot} pos=({request.PositionX},{request.PositionY})");
        }
    }
}
