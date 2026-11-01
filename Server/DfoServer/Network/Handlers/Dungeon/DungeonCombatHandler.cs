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
    internal enum DungeonCharacterDeathFlow
    {
        Scripted = 0,
        DeathTower = 1,
        Tournament = 2,
        PartyWipe = 3,
    }

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
            svc.InstanceRegistry.ConfigurePartyWipeStarted(
                SchedulePartyWipeTimer);
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
                IReadOnlyList<DungeonParticipantRosterEntry> passiveDeathRoster
                    = null;
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
                        // 在任何 await 之前捕获死亡时点的参战名册（值快照），
                        // 之后的扇出只使用该快照，不回查注册表/组队。
                        passiveDeathRoster = _svc.InstanceRegistry
                            .CaptureParticipantRoster(
                                passiveObjectEvent.RoomIdentity,
                                partyId: 0);
                    }
                }

                FileLogger.Log(
                    $"[DungeonHandler] DIE_MONSTER: passive object " +
                    $"cid={session.Player.CharacterId} " +
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

                if (session.Player.IsCurrentDungeonRun(runIdentity)
                    && passiveDeathRoster != null)
                {
                    await FanoutPassiveObjectDeathAsync(
                        session,
                        run,
                        passiveDeathRoster,
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

        // 地图自带被动对象死亡：向死亡时点名册中的其他参战队员逐人投影事件，
        // 各自执行任务进度同步与任务物品掉落判定（与杀怪 RelayToFrozenRosterAsync
        // 语义对齐；队员此前完全拿不到这类任务物品）。
        // 过滤只做身份/在线/run 一致性，不做任务相关预过滤；
        // Tower 仅限制掉落，与上报者路径保持一致。
        // 已知边界：队员侧单点失败不重试；Detached 队员通常因无在线连接被过滤，
        // 没有杀怪路径那样的断线重放（participant effect 恢复只覆盖 MonsterKill）。
        internal async Task FanoutPassiveObjectDeathAsync(
            EnhancedClientSession sourceSession,
            DungeonRun sourceRun,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            DungeonEventEnvelope canonicalEvent,
            int objectCode)
        {
            if (sourceSession?.Player == null || sourceRun == null)
                return;

            var sourceCharacterId = sourceSession.Player.CharacterId;
            var tasks = new List<Task>();
            foreach (var entry in roster)
            {
                if (entry == null
                    || entry.CharacterId == sourceCharacterId)
                {
                    continue;
                }
                if (!_svc.Sessions.TryGet(entry.CharacterId, out var memberSession)
                    || memberSession?.Player?.CurrentRun == null
                    || memberSession.TcpClient == null
                    || !memberSession.TcpClient.Connected)
                {
                    continue;
                }

                var memberRun = memberSession.Player.CurrentRun;
                if (!IsFanoutMemberCurrent(memberRun, entry))
                {
                    continue;
                }

                var memberEvent = canonicalEvent.ForAffectedPlayer(
                    entry.RunIdentity,
                    entry.RoomIdentity.RoomInstanceId,
                    entry.CharacterId);
                var memberIdentity = memberRun.CaptureIdentity();
                tasks.Add(FanoutMemberAsync(
                    memberSession,
                    memberRun,
                    memberIdentity,
                    memberEvent,
                    objectCode));
            }

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        // 单队员扇出资格（纯函数，供扇出循环与自测直接断言）：
        // run 引用、run 身份与房间身份三者一致才算当前队员。
        internal static bool IsFanoutMemberCurrent(
            DungeonRun memberRun,
            DungeonParticipantRosterEntry entry)
            => entry != null
                && memberRun != null
                && ReferenceEquals(memberRun, entry.Run)
                && memberRun.Matches(entry.RunIdentity)
                && memberRun.Matches(entry.RoomIdentity);

        // 单队员扇出：异常独立捕获并留痕，不影响其他队员与上报者链路。
        private async Task FanoutMemberAsync(
            EnhancedClientSession memberSession,
            DungeonRun memberRun,
            DungeonRunIdentity memberIdentity,
            DungeonEventEnvelope memberEvent,
            int objectCode)
        {
            try
            {
                await DungeonActorQuestSync.SyncAsync(
                    memberSession,
                    objectCode,
                    actorType: 9,
                    memberEvent);
                if (!memberSession.Player
                        .IsCurrentDungeonRun(memberIdentity))
                {
                    return;
                }

                if (memberRun.Tower == null)
                {
                    await _svc.QuestDrops.CheckPassiveObjectDrop(
                        memberSession,
                        memberRun,
                        memberEvent,
                        objectCode);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GameProtocol] PASSIVE_QUEST_DROP fanout failed " +
                    $"cid={memberSession.Player?.CharacterId ?? 0} " +
                    $"object={objectCode} error={ex.Message}");
            }
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
            if (mechanismClear.ShouldClearDungeon)
            {
                await _settlement.SubmitClearIntentAsync(
                    session,
                    new DungeonClearIntent(
                        bossCheckEvent,
                        mechanismClear.ClearReason,
                        mechanismClear.BossCode));
            }

            // A licensed run can be cleared by the room endpoint before the
            // client sends 0x0075. Do not return before projecting the
            // verified 0x0073 -> 0x001F settlement preamble.
            if (session.Player.IsCurrentDungeonRun(runIdentity))
            {
                await _settlement.ProjectLicensedBossClearNotificationsAsync(
                    session,
                    run,
                    request);
            }
        }

        internal static bool IsAiCharacterActorType(byte actorType)
        {
            return actorType >= 5 && actorType <= 8;
        }

        internal static bool IsBossActorType(byte actorType) =>
            actorType == 3 || actorType == 8;

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
            var deathRunIdentity = deathRun?.CaptureIdentity() ?? default;
            var deathFlow = ResolveCharacterDeathFlow(
                scriptedDeath.SuppressRespawn,
                deathRun?.Tower != null,
                deathRun != null
                    && _svc.Tournaments.IsTournamentRun(deathRun));
            switch (deathFlow)
            {
                case DungeonCharacterDeathFlow.Scripted:
                case DungeonCharacterDeathFlow.DeathTower:
                    DungeonRunLifecycle.CancelDeathRespawn(deathRun);
                    break;
                case DungeonCharacterDeathFlow.Tournament:
                    ScheduleDeathRespawn(session);
                    break;
                default:
                    await SchedulePartyWipeIfNeededAsync(deathRun);
                    break;
            }

            // NOTI 32 (wire 0x0020) DIE_STATE: u16 actorId + u8 dieType(0=death) + u8 flag
            await BroadcastParticipantLifeStateAsync(
                session,
                deathRun,
                BuildParticipantLifeStateBody(
                    session.Player.UserId,
                    state: 0x00));

            if (deathFlow == DungeonCharacterDeathFlow.DeathTower)
            {
                await _svc.DeathTower.HandleTowerCharacterDeathAsync(
                    session,
                    deathRunIdentity);
            }

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

        internal static DungeonCharacterDeathFlow ResolveCharacterDeathFlow(
            bool suppressRespawn,
            bool isDeathTower,
            bool isTournament)
        {
            if (suppressRespawn)
                return DungeonCharacterDeathFlow.Scripted;
            if (isDeathTower)
                return DungeonCharacterDeathFlow.DeathTower;
            if (isTournament)
                return DungeonCharacterDeathFlow.Tournament;
            return DungeonCharacterDeathFlow.PartyWipe;
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

        private async Task SchedulePartyWipeIfNeededAsync(DungeonRun run)
        {
            if (run == null)
                return;

            await run.Instance.ParticipantLifeGate.WaitAsync();
            try
            {
                var runIdentity = run.CaptureIdentity();
                var roster = _svc.InstanceRegistry
                    .CaptureInstanceParticipantRoster(
                        run.CaptureInstanceIdentity());
                var activeRuns = new List<DungeonRunIdentity>();
                foreach (var participant in roster)
                {
                    if (!participant.RunIdentity.IsValid
                        || activeRuns.Contains(participant.RunIdentity))
                    {
                        continue;
                    }
                    run.Instance.RegisterParticipantLife(
                        participant.RunIdentity);
                    activeRuns.Add(participant.RunIdentity);
                }
                if (!activeRuns.Contains(runIdentity))
                {
                    run.Instance.RegisterParticipantLife(runIdentity);
                    activeRuns.Add(runIdentity);
                }

                var transition = run.Instance.MarkParticipantDead(
                    runIdentity,
                    activeRuns,
                    DateTime.UtcNow,
                    DungeonInstance.PartyWipeDelay);
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] PARTY_DEATH: " +
                    $"instance={run.PartyDungeonInstanceId} " +
                    $"run={run.RunId}/{run.RunGeneration} " +
                    $"changed={transition.Changed} " +
                    $"wipeStarted={transition.WipeStarted} " +
                    $"participants={activeRuns.Count}");
                if (!transition.WipeStarted)
                    return;

                SchedulePartyWipeTimer(
                    run.Instance,
                    transition.Generation,
                    transition.DeadlineUtc,
                    "last-participant-death");
            }
            finally
            {
                run.Instance.ParticipantLifeGate.Release();
            }
        }

        private void SchedulePartyWipeTimer(
            DungeonInstance instance,
            long generation,
            DateTime deadlineUtc,
            string source)
        {
            if (instance == null || generation <= 0)
                return;

            ClockService.Instance.ScheduleOneShotAsync(
                BuildPartyWipeTimerName(instance),
                deadlineUtc,
                utcNow => ResolvePartyWipeDeadlineAsync(
                    instance,
                    generation,
                    utcNow));
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] PARTY_WIPE_TIMER: " +
                $"scheduled instance={instance.PartyDungeonInstanceId} " +
                $"generation={generation} source={source} " +
                $"deadline={deadlineUtc:O}");
        }

        private async Task ResolvePartyWipeDeadlineAsync(
            DungeonInstance instance,
            long generation,
            DateTime utcNow)
        {
            if (instance == null)
                return;

            IReadOnlyList<DungeonParticipantRosterEntry> roster;
            var activeRuns = new List<DungeonRunIdentity>();
            var committed = false;
            await instance.ParticipantLifeGate.WaitAsync();
            try
            {
                roster = _svc.InstanceRegistry
                    .CaptureInstanceParticipantRoster(instance.Identity);
                foreach (var participant in roster)
                {
                    if (participant.RunIdentity.IsValid
                        && !activeRuns.Contains(participant.RunIdentity))
                    {
                        activeRuns.Add(participant.RunIdentity);
                    }
                }
                committed = instance.TryCommitPartyWipe(
                    generation,
                    activeRuns,
                    utcNow);
            }
            finally
            {
                instance.ParticipantLifeGate.Release();
            }
            if (!committed)
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] PARTY_WIPE_TIMER: " +
                    $"stale instance={instance.PartyDungeonInstanceId} " +
                    $"generation={generation} participants={activeRuns.Count}");
                return;
            }

            var returned = 0;
            foreach (var participant in roster)
            {
                if (_svc.Sessions == null
                    || !_svc.Sessions.TryGet(
                        participant.CharacterId,
                        out var candidate)
                    || candidate?.Player == null
                    || candidate.TcpClient == null
                    || !candidate.TcpClient.Connected
                    || !candidate.Player.IsCurrentDungeonRun(
                        participant.RunIdentity))
                {
                    continue;
                }

                try
                {
                    if (await _svc.TownReturn.ReturnAsync(
                            candidate,
                            participant.RunIdentity,
                            DungeonRunEndReason.DeathRespawn,
                            successAckPacketType: 0x007B))
                    {
                        returned++;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"PARTY_WIPE_TIMER: return failed " +
                        $"instance={instance.PartyDungeonInstanceId} " +
                        $"generation={generation} " +
                        $"cid={participant.CharacterId} " +
                        $"run={participant.RunIdentity.RunId}/" +
                        $"{participant.RunIdentity.RunGeneration}: " +
                        ex.Message);
                }
            }
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] PARTY_WIPE_TIMER: " +
                $"complete instance={instance.PartyDungeonInstanceId} " +
                $"generation={generation} returned={returned}/{roster.Count}");
        }

        private async Task BroadcastParticipantLifeStateAsync(
            EnhancedClientSession sourceSession,
            DungeonRun sourceRun,
            byte[] body)
        {
            if (sourceSession?.Player == null || body == null)
                return;

            var sourceSent = false;
            var roster = sourceRun != null
                ? _svc.InstanceRegistry.CaptureInstanceParticipantRoster(
                    sourceRun.CaptureInstanceIdentity())
                : Array.Empty<DungeonParticipantRosterEntry>();
            foreach (var participant in roster)
            {
                if (_svc.Sessions == null
                    || !_svc.Sessions.TryGet(
                        participant.CharacterId,
                        out var candidate)
                    || candidate?.Player == null
                    || candidate.TcpClient == null
                    || !candidate.TcpClient.Connected
                    || !candidate.Player.IsCurrentDungeonRun(
                        participant.RunIdentity))
                {
                    continue;
                }

                try
                {
                    await candidate.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            0x0020,
                            body));
                    sourceSent |= ReferenceEquals(candidate, sourceSession);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"DIE_STATE projection failed cid={participant.CharacterId}: " +
                        ex.Message);
                }
            }

            if (!sourceSent)
            {
                await sourceSession.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x0020,
                        body));
            }
        }

        private static string BuildPartyWipeTimerName(DungeonInstance instance)
            => "dungeon-party-wipe:" + instance.PartyDungeonInstanceId;

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
            if (!await _svc.TownReturn.ReturnAsync(
                    session,
                    runIdentity,
                    DungeonRunEndReason.DeathRespawn,
                    successAckPacketType: 0x007B))
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

            DungeonParticipantRosterEntry reviveTarget = null;
            var isTournament = _svc.Tournaments.IsTournamentRun(run);
            short coinSlot;
            int coinRemaining;
            var reviveCommitted = false;
            if (isTournament)
            {
                reviveCommitted = TryConsumeReviveCoin(
                    session,
                    characterId,
                    out coinSlot,
                    out coinRemaining);
                if (reviveCommitted)
                    DungeonRunLifecycle.CancelDeathRespawn(run);
            }
            else
            {
                await run.Instance.ParticipantLifeGate.WaitAsync();
                try
                {
                    if (session.Player.IsCurrentDungeonRun(runIdentity))
                    {
                        foreach (var participant in _svc.InstanceRegistry
                                     .CaptureInstanceParticipantRoster(
                                         run.CaptureInstanceIdentity()))
                        {
                            if (participant.ParticipantUserId == targetId)
                            {
                                reviveTarget = participant;
                                break;
                            }
                        }
                    }

                    if (reviveTarget != null
                        && run.Instance.CanReviveParticipant(
                            reviveTarget.RunIdentity)
                        && TryConsumeReviveCoin(
                            session,
                            characterId,
                            out coinSlot,
                            out coinRemaining))
                    {
                        reviveCommitted = run.Instance.MarkParticipantAlive(
                            reviveTarget.RunIdentity).Changed;
                    }
                    else
                    {
                        coinSlot = -1;
                        coinRemaining = 0;
                    }
                }
                finally
                {
                    run.Instance.ParticipantLifeGate.Release();
                }
            }

            if (!reviveCommitted)
            {
                var err = new GamePacketWriter();
                err.WriteByte(0x00);
                err.WriteUInt16(targetId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0029,
                    err.ToArray()));
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"USE_COIN rejected target={targetId} cid={characterId}");
                return false;
            }

            // 复活已提交: 镜像阿拉德/暗精灵遗迹在此刻才标记复活使用(失败不误标)
            run.AnotherAradQuest?.MarkReviveUsed();
            if (GameWorld.LicensedDungeonCatalog.TryGetDefinition(
                    run.DungeonId,
                    out _))
            {
                run.LicensedDungeonReviveUsed = true;
            }

            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return false;
            DungeonRunLifecycle.CancelDeathRespawn(run);

            // 1. NOTI 0x0020 DIE_STATE: set_charac_live(user, 1=revive)
            //    df_game_r body = u16 actorId + u8 state; 86JP has extra u8 flag
            await BroadcastParticipantLifeStateAsync(
                session,
                run,
                BuildParticipantLifeStateBody(
                    targetId,
                    state: 0x01));

            // 2. CMD ACK 0x0029: resultCode=1 + u16 targetActorId
            //    不补发 0x000E: 客户端使用复活币时本地已预扣显示(PR#338 实测说明), 全量列表随下次进城刷新
            var ack = new GamePacketWriter();
            ack.WriteByte(0x01);           // resultCode = success
            ack.WriteUInt16(targetId);     // targetActorId
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0029, ack.ToArray()));
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] USE_COIN: OK cid={characterId} slot={coinSlot} remaining={coinRemaining}");
            return true;
        }

        private bool TryConsumeReviveCoin(
            EnhancedClientSession session,
            int characterId,
            out short coinSlot,
            out int coinRemaining)
        {
            coinSlot = -1;
            coinRemaining = 0;
            return characterId > 0
                && InventoryContext.TryGetLease(characterId, out var lease)
                && lease.IsOwnedBy(session.SessionId)
                && _svc.ReviveCoin.TryConsume(
                    lease,
                    out coinSlot,
                    out coinRemaining);
        }

        internal static byte[] BuildParticipantLifeStateBody(
            ushort actorId,
            byte state)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(actorId);
            writer.WriteByte(state);
            writer.WriteByte(0x00);
            return writer.ToArray();
        }

        internal async Task HandleGetItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            var runIdentity = run.CaptureIdentity();
            if (run.Instance.IsParticipantDead(runIdentity))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"GET_ITEM ignored for dead participant " +
                    $"cid={session.Player.CharacterId} " +
                    $"instance={run.PartyDungeonInstanceId}");
                return;
            }

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
                        pickup.ExtraGold,
                        run.EntryPartySlotIndex)));
                // 金币入账只发 0x0027 时非主机端客户端背包金币不刷新（要等翻牌结算），
                // 按 DungeonEntryHandler 入场扣金的惯例补主背包 0 号金币虚拟槽的 0x000E 刷新。
                if (_svc.InventoryRefresh != null)
                    await _svc.InventoryRefresh.SendGoldUpdate(session);
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
