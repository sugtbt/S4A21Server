using DfoServer.Game.Dungeon;
using DfoServer.Game.Progression;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Pets;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal enum DungeonKillOrigin
    {
        LocalReport,
        PartyRelay,
        Recovery,
    }

    internal sealed class KillContext
    {
        internal KillContext(
            EnhancedClientSession session,
            DungeonEventEnvelope envelope,
            ushort sequenceId,
            ushort sourceUserId,
            DungeonKillOrigin origin,
            DungeonActorDeathKind deathKind = DungeonActorDeathKind.Defeated)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            SequenceId = sequenceId;
            SourceUserId = sourceUserId;
            Origin = origin;
            DeathKind = deathKind;
        }

        internal EnhancedClientSession Session { get; }
        internal DungeonEventEnvelope Envelope { get; }
        internal ushort SequenceId { get; }
        internal ushort SourceUserId { get; }
        internal DungeonKillOrigin Origin { get; }
        internal DungeonActorDeathKind DeathKind { get; }
        internal bool IsLocalReport => Origin == DungeonKillOrigin.LocalReport;
    }

    internal sealed class DungeonKillApplicationService
    {
        private readonly DungeonSharedServices _services;
        private readonly DungeonSettlementHandler _settlement;
        private readonly TournamentDungeonCoordinator _tournament;
        private readonly BloodAltarDungeonCoordinator _bloodAltar;

        internal DungeonKillApplicationService(
            DungeonSharedServices services,
            DungeonSettlementHandler settlement,
            TournamentDungeonCoordinator tournament,
            BloodAltarDungeonCoordinator bloodAltar)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
            _tournament = tournament
                ?? throw new ArgumentNullException(nameof(tournament));
            _bloodAltar = bloodAltar
                ?? throw new ArgumentNullException(nameof(bloodAltar));
        }

        internal async Task ProcessAsync(KillContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var session = context.Session;
            var run = session.Player?.CurrentRun;
            if (!IsCurrent(run, context.Envelope))
                return;
            if (run.Instance.State == DungeonInstanceState.Ending
                || run.Instance.State == DungeonInstanceState.Ended)
            {
                await SendMonsterDieAsync(session, context, null);
                return;
            }

            if (run.Phase >= DungeonRunPhase.Cleared
                && context.Origin != DungeonKillOrigin.Recovery)
            {
                await SendMonsterDieAsync(session, context, null);
                return;
            }

            if (run.Tower != null)
            {
                bool firstApplication;
                lock (run.SyncRoot)
                    firstApplication = run.RoomKilledSeqIds.Add(context.SequenceId);
                if (!firstApplication)
                {
                    await SendMonsterDieAsync(session, context, null);
                    return;
                }

                await ProcessCoreAsync(context, run);
                return;
            }

            if (!context.Envelope.RoomIdentity.IsValid)
            {
                FileLogger.Log(
                    $"[DungeonKill] participant event rejected: invalid room identity " +
                    $"cid={session.Player.CharacterId} seq={context.SequenceId} " +
                    $"event={context.Envelope.SourceEventId:N}");
                await SendMonsterDieAsync(session, context, null);
                return;
            }

            if (!TryCanonicalizeSharedDeath(
                    context,
                    run,
                    out var canonicalContext,
                    out var canonicalWorldDeath,
                    out var canonicalDynamicActor))
            {
                await SendMonsterDieAsync(session, context, null);
                return;
            }
            context = canonicalContext;

            var roster = CaptureEligibleRoster(context, run);
            run.Instance.ParticipantEffects.TryFreeze(
                context.Envelope,
                DungeonParticipantEffectAudience.Room,
                roster,
                out var frozenRoster);
            var participant = FindParticipant(
                frozenRoster,
                session.Player.CharacterId,
                run.CaptureIdentity());
            if (participant == null)
            {
                FileLogger.Log(
                    $"[DungeonKill] participant event rejected: not in frozen roster " +
                    $"cid={session.Player.CharacterId} seq={context.SequenceId} " +
                    $"event={context.Envelope.SourceEventId:N}");
                await SendMonsterDieAsync(session, context, null);
                return;
            }

            if (!run.Instance.ParticipantEffects.TryBegin(
                    context.Envelope.SourceEventId,
                    DungeonParticipantEffectAudience.Room,
                    participant,
                    DungeonParticipantEffectKinds.MonsterKill,
                    out var reservation,
                    out var existingState))
            {
                await SendMonsterDieAsync(session, context, null);
                if (context.IsLocalReport
                    && existingState == DungeonParticipantEffectState.Committed)
                {
                    await RelayAndCompleteDeferredClearFanoutAsync(context, run);
                }
                return;
            }

            try
            {
                var completed = await ProcessCoreAsync(
                    context,
                    run,
                    canonicalWorldDeath,
                    canonicalDynamicActor);
                if (!completed)
                {
                    run.Instance.ParticipantEffects.TryFail(reservation);
                    return;
                }

                if ((canonicalDynamicActor?.Policy.CountsTowardRoomClear
                        ?? true)
                    && DungeonRoomTopology.IsTrackedForRoomProgress(
                        canonicalWorldDeath.Fact.ActorType))
                {
                    lock (run.SyncRoot)
                        run.RoomKilledSeqIds.Add(context.SequenceId);
                }
                if (!run.Instance.ParticipantEffects.TryCommit(reservation))
                {
                    throw new InvalidOperationException(
                        "Participant kill effect reservation was lost before commit.");
                }

                if (context.IsLocalReport)
                    await RelayAndCompleteDeferredClearFanoutAsync(context, run);
            }
            catch
            {
                run.Instance.ParticipantEffects.TryFail(reservation);
                throw;
            }
        }

        internal async Task<DungeonEventEnvelope>
            ProcessConfirmedBossDeathAsync(
                EnhancedClientSession session,
                DungeonEventEnvelope source,
                int actorCode,
                ushort sourceUserId)
        {
            var run = session?.Player?.CurrentRun;
            if (!IsCurrent(run, source)
                || !TryResolvePendingBossActor(
                    run,
                    source.RoomIdentity,
                    actorCode,
                    source.SourceActorId,
                    out var sequenceId,
                    out var resolvedActorCode))
            {
                return source;
            }

            var actorSource = new DungeonEventEnvelope(
                source.SourceEventId,
                source.RunIdentity,
                source.RoomInstanceId,
                source.SourcePlayerId,
                source.AffectedPlayerId,
                sequenceId,
                resolvedActorCode,
                source.Cause,
                source.OccurredTick);
            await ProcessAsync(new KillContext(
                session,
                actorSource,
                sequenceId,
                sourceUserId,
                DungeonKillOrigin.LocalReport));
            return actorSource;
        }

        // Replays only frozen, unfinished participant effects after the same run
        // is attached to a new session. The shared death fact remains the source
        // of truth; this method never invents a new actor death event.
        internal async Task RecoverParticipantEffectsAsync(
            EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || run.Tower != null)
                return;

            var participantIdentity = run.CaptureParticipantIdentity();
            var workItems = run.Instance.ParticipantEffects
                .GetRecoverableForParticipant(
                    participantIdentity,
                    DungeonParticipantEffectAudience.Room,
                    DungeonParticipantEffectKinds.MonsterKill);
            foreach (var work in workItems)
            {
                if (!ReferenceEquals(work.Participant.Run, run)
                    || !run.Matches(work.Participant.RunIdentity)
                    || !run.Matches(work.Participant.RoomIdentity)
                    || !work.Source.SourceActorId.HasValue
                    || work.Source.SourceActorId.Value <= 0
                    || work.Source.SourceActorId.Value > ushort.MaxValue)
                {
                    FileLogger.Log(
                        $"[DungeonKill] recovery deferred: " +
                        $"cid={session.Player.CharacterId} " +
                        $"event={work.Source.SourceEventId:N} " +
                        $"reason=participant_or_actor_identity_mismatch");
                    continue;
                }

                var sourceUserId = work.Source.SourcePlayerId > 0
                    && work.Source.SourcePlayerId <= ushort.MaxValue
                    ? (ushort)work.Source.SourcePlayerId
                    : (ushort)0;
                var projected = work.Source.ForAffectedPlayer(
                    run.CaptureIdentity(),
                    work.Participant.RoomIdentity.RoomInstanceId,
                    session.Player.CharacterId);
                try
                {
                    await ProcessAsync(new KillContext(
                        session,
                        projected,
                        (ushort)work.Source.SourceActorId.Value,
                        sourceUserId,
                        DungeonKillOrigin.Recovery));
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DungeonKill] recovery failed: " +
                        $"cid={session.Player.CharacterId} " +
                        $"event={work.Source.SourceEventId:N} " +
                        $"error={ex.Message}");
                }
            }
        }

        private async Task<bool> ProcessCoreAsync(
            KillContext context,
            DungeonRun run,
            DungeonRoomActorDeathApplication? recordedWorldDeath = null,
            DungeonDynamicActorDefinition dynamicActor = null)
        {
            var session = context.Session;

            var identity = run.CaptureIdentity();
            DungeonRunRoomSnapshot roomSnapshot = null;
            run.TryCaptureCurrentRoomSnapshot(
                context.Envelope.RoomIdentity,
                out roomSnapshot);
            var roomStartSequence = roomSnapshot?.RoomStartSequence
                ?? run.RoomStartSequence;
            var roomLocalIndex = context.SequenceId - roomStartSequence;
            var monsters = roomSnapshot?.Monsters ?? run.RoomMonsters;
            DungeonData.MonsterSumInfo? monster = null;
            if (dynamicActor == null
                && roomLocalIndex >= 0
                && roomLocalIndex < monsters.Count)
                monster = monsters[roomLocalIndex];

            var sharedRoomState = roomSnapshot?.RoomState;
            var hasSharedRoom = run.Tower == null
                && sharedRoomState != null
                && sharedRoomState.InstanceRoom != null;
            var worldDeath = recordedWorldDeath
                ?? default(DungeonRoomActorDeathApplication);
            if (monster != null && hasSharedRoom)
            {
                if (!recordedWorldDeath.HasValue)
                {
                    worldDeath = sharedRoomState.InstanceRoom.TryRecordActorDeath(
                        context.Envelope,
                        context.SequenceId,
                        monster.Value.Code,
                        monster.Value.Type,
                        context.DeathKind);
                }
                if (!worldDeath.Accepted)
                {
                    FileLogger.Log(
                        $"[DungeonKill] shared death rejected: " +
                        $"cid={session.Player.CharacterId} seq={context.SequenceId} " +
                        $"instance={context.Envelope.PartyDungeonInstanceId} " +
                        $"room={context.Envelope.RoomInstanceId.GetValueOrDefault()} " +
                        $"event={context.Envelope.SourceEventId:N}");
                    await SendMonsterDieAsync(session, context, null);
                    return false;
                }
            }

            IReadOnlyList<DropInfo> drops = null;
            if (monster != null)
            {
                if (!hasSharedRoom || worldDeath.Created)
                {
                    run.Instance.TryRecordMonsterKill(
                        run.CurrentRoomInstanceId,
                        run.RoomKey,
                        context.SequenceId,
                        monster.Value.Type);
                }
                drops = await ApplyParticipantRewardAsync(
                    session,
                    run,
                    identity,
                    context.SequenceId,
                    monster.Value);
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return false;
            }
            else if (dynamicActor != null)
            {
                if (worldDeath.Created
                    && dynamicActor.Policy.TracksKillStatistics)
                {
                    run.Instance.TryRecordMonsterKill(
                        run.CurrentRoomInstanceId,
                        run.RoomKey,
                        context.SequenceId,
                        dynamicActor.ActorType);
                }

                if (dynamicActor.Policy.GrantsMonsterExperience
                    || dynamicActor.Policy.GeneratesMonsterDrops)
                {
                    drops = await ApplyParticipantRewardAsync(
                        session,
                        run,
                        identity,
                        context.SequenceId,
                        new DungeonData.MonsterSumInfo
                        {
                            Code = dynamicActor.ActorCode,
                            Type = dynamicActor.ActorType,
                            Level = dynamicActor.ActorLevel,
                        },
                        dynamicActor.Policy);
                    if (!session.Player.IsCurrentDungeonRun(identity))
                        return false;
                }
            }
            else if (TryGetCurrentRoomState(run, out var outOfRangeRoomState)
                && outOfRangeRoomState.IsHellPartyRoom)
            {
                FileLogger.Log(
                    $"[DungeonKill] HELLPARTY out-of-start-map: " +
                    $"cid={session.Player.CharacterId} seq={context.SequenceId} " +
                    $"local={roomLocalIndex} tracked={outOfRangeRoomState.MonsterCount} " +
                    $"killed={run.RoomKilledSeqIds.Count}");
            }

            if (monster != null
                && context.DeathKind == DungeonActorDeathKind.Captured)
            {
                var captureDrops = _services.QuestDrops.CheckMonsterCaptureDrop(
                    session,
                    run,
                    context.Envelope,
                    monster.Value);
                drops = MergeDrops(drops, captureDrops);
            }

            await SendMonsterDieAsync(session, context, drops);
            if (!IsCurrent(run, context.Envelope))
                return false;

            if (worldDeath.Accepted
                && worldDeath.Fact != null
                && (dynamicActor == null
                    || dynamicActor.Policy.AppliesGeneralMechanisms))
            {
                await _tournament.OnActorDeathAsync(
                    session,
                    run,
                    worldDeath.Fact);
                if (!IsCurrent(run, context.Envelope))
                    return false;
            }
            await _tournament.EnsureParticipantRewardsAsync(
                session,
                run,
                forceProjection: false);
            if (!IsCurrent(run, context.Envelope))
                return false;

            if (dynamicActor != null
                && worldDeath.Accepted
                && worldDeath.Fact != null)
            {
                await _bloodAltar.OnDynamicActorDeathAsync(
                    session,
                    run,
                    dynamicActor,
                    worldDeath.Fact.Source);
                if (!IsCurrent(run, context.Envelope))
                    return false;
            }

            var killedMonsterCode = dynamicActor?.ActorCode
                ?? monster?.Code
                ?? 0;
            var killedMonsterType = dynamicActor?.ActorType
                ?? monster?.Type
                ?? (byte)0;
            var generatesQuestDrops = dynamicActor == null
                || dynamicActor.Policy.GeneratesQuestDrops;
            var advancesQuestObjectives = dynamicActor == null
                || dynamicActor.Policy.AdvancesQuestObjectives;
            if (killedMonsterCode > 0 && generatesQuestDrops)
            {
                if (DungeonCombatHandler.IsAiCharacterActorType(killedMonsterType))
                    await _services.QuestDrops.CheckAiCharacterDrop(
                        session,
                        run,
                        context.Envelope,
                        killedMonsterCode);
                else if (run.Tower == null)
                    await _services.QuestDrops.CheckMonsterDrop(
                        session,
                        run,
                        context.Envelope,
                        killedMonsterCode);
                if (!IsCurrent(run, context.Envelope))
                    return false;
            }

            if (advancesQuestObjectives)
            {
                await DungeonActorQuestSync.SyncAsync(
                    session,
                    killedMonsterCode,
                    killedMonsterType,
                    context.Envelope);
                if (!IsCurrent(run, context.Envelope))
                    return false;
            }

            if (advancesQuestObjectives
                && killedMonsterType == 3
                && run.RewardPolicy.AllowsQuestProgress)
            {
                await ApplySuitableDungeonBossKillChallengeAsync(
                    session,
                    run,
                    context.Envelope,
                    killedMonsterCode,
                    killedMonsterType);
                if (!IsCurrent(run, context.Envelope))
                    return false;
            }

            var appliesGeneralMechanisms = dynamicActor == null
                || dynamicActor.Policy.AppliesGeneralMechanisms;
            var mechanismKill = default(DungeonMechanismCoordinator.ClearRequest);
            if (appliesGeneralMechanisms)
            {
                mechanismKill = await DungeonMechanismCoordinator
                    .OnMonsterKilledAsync(
                        session,
                        context.Envelope,
                        context.SequenceId,
                        killedMonsterCode,
                        killedMonsterType);
                if (!IsCurrent(run, context.Envelope))
                    return false;
            }

            var roomCleared = false;
            var blockingCount = 0;
            var killedBlockingCount = 0;
            DungeonEventEnvelope roomClearSource = null;
            if (dynamicActor == null
                || dynamicActor.Policy.CountsTowardRoomClear)
            {
                roomCleared = DungeonRoomTopology.TryCommitCurrentRoomClear(
                    run,
                    context.Envelope,
                    context.SequenceId,
                    out blockingCount,
                    out killedBlockingCount,
                    out roomClearSource);
            }

            if (roomCleared)
            {
                // Room effects stay bound to the first clear fact; a later APC Boss death
                // may only provide the event that releases deferred dungeon settlement.
                var projectedRoomClearSource = (roomClearSource ?? context.Envelope)
                    .ForAffectedPlayer(
                        run.CaptureIdentity(),
                        run.CurrentRoomInstanceId > 0
                            ? run.CurrentRoomInstanceId
                            : null,
                        session.Player.CharacterId);
                var projectedSettlementSource = context.Envelope.ForAffectedPlayer(
                    run.CaptureIdentity(),
                    run.CurrentRoomInstanceId > 0
                        ? run.CurrentRoomInstanceId
                        : null,
                    session.Player.CharacterId);
                await ApplyRoomClearedAsync(
                    session,
                    run,
                    new KillContext(
                        session,
                        projectedSettlementSource,
                        context.SequenceId,
                        context.SourceUserId,
                        context.Origin,
                        context.DeathKind),
                    projectedRoomClearSource,
                    killedMonsterCode,
                    blockingCount,
                    killedBlockingCount);
                if (!IsCurrent(run, context.Envelope))
                    return false;
            }

            if (roomCleared
                && TryGetCurrentRoomState(run, out var hellRoomState)
                && hellRoomState.IsHellPartyRoom
                && hellRoomState.HellPartyPhase == HellPartyPhase.Started)
            {
                hellRoomState.HellPartyPhase = HellPartyPhase.Complete;
                FileLogger.Log("[DungeonKill] HELLPARTY complete: tracked monsters cleared");
            }

            if (appliesGeneralMechanisms && run.ClearCondition != null)
            {
                var conditionType = IsBossActorType(killedMonsterType)
                    ? 4
                    : killedMonsterType >= 5 ? 3 : 2;
                if (DungeonCombatHandler.ShouldClearDungeon(
                        run.ClearCondition.Check(conditionType, killedMonsterCode),
                        reachedBossEndpoint: false,
                        run.IgnoreDefaultDungeonClear))
                {
                    await _settlement.SubmitClearIntentAsync(
                        session,
                        new DungeonClearIntent(
                            context.Envelope,
                            $"ClearCondition type={conditionType} target={killedMonsterCode}",
                            killedMonsterCode),
                        deferParticipantFanout: true);
                }
                if (!IsCurrent(run, context.Envelope))
                    return false;
            }

            if (mechanismKill.ShouldClearDungeon)
            {
                await _settlement.SubmitClearIntentAsync(
                    session,
                    new DungeonClearIntent(
                        context.Envelope,
                        mechanismKill.ClearReason,
                        mechanismKill.BossCode),
                    deferParticipantFanout: true);
                if (!IsCurrent(run, context.Envelope))
                    return false;
            }

            if (appliesGeneralMechanisms
                && IsBossActorType(killedMonsterType)
                && run.Phase < DungeonRunPhase.Cleared)
            {
                WriteUnclearedBossDiagnostic(
                    session,
                    run,
                    context.SequenceId,
                    killedMonsterCode,
                    killedMonsterType,
                    roomCleared,
                    blockingCount,
                    killedBlockingCount);
            }

            return true;
        }

        private async Task ApplySuitableDungeonBossKillChallengeAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonEventEnvelope source,
            int monsterCode,
            byte monsterType)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || run == null
                || source == null
                || source.SourceEventId == Guid.Empty
                || !IsCurrent(run, source))
            {
                return;
            }

            try
            {
                var result = _services.DailyChallenges
                    .ApplySuitableDungeonBossKill(
                        characterId,
                        run.DungeonId,
                        run.Difficulty,
                        session.Player.Level,
                        monsterCode,
                        monsterType,
                        source.SourceEventId);
                if (!result.HasRelevantProgress || !IsCurrent(run, source))
                    return;

                // Boss-kill challenges are snapshot state in A21. Unlike a
                // successful dungeon clear, they do not consume the 0x0287
                // edge-triggered completion notification.
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.DAILY_CHALLENGE,
                    DailyChallengeBodyBuilder.Build(result.Snapshot)));
                FileLogger.Log(
                    $"[DungeonKill] DAILY_CHALLENGE boss kill noti "
                    + $"cid={characterId} dungeon={run.DungeonId} "
                    + $"monster={monsterCode} event={source.SourceEventId:N} "
                    + $"changed={result.ChangedEntries}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonKill] DAILY_CHALLENGE boss kill ERROR: "
                    + $"cid={characterId} dungeon={run.DungeonId} "
                    + $"monster={monsterCode} event={source.SourceEventId:N} "
                    + ex.Message);
                // Preserve the canonical actor-death fact but fail this
                // participant-effect attempt. Recovery replays the same stable
                // source id, so a committed DB update is not counted twice and
                // a failed update is not silently lost.
                throw;
            }
        }

        private static IReadOnlyList<DropInfo> MergeDrops(
            IReadOnlyList<DropInfo> first,
            IReadOnlyList<DropInfo> second)
        {
            if (second == null || second.Count == 0)
                return first;
            if (first == null || first.Count == 0)
                return second;

            var result = new List<DropInfo>(first.Count + second.Count);
            result.AddRange(first);
            result.AddRange(second);
            return result;
        }

        private async Task<IReadOnlyList<DropInfo>> ApplyParticipantRewardAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            ushort sequenceId,
            DungeonData.MonsterSumInfo monster,
            DungeonDynamicActorPolicy dynamicPolicy = null)
        {
            var isDeathTowerRun = run.Tower != null;
            IReadOnlyList<DropInfo> towerDrops = null;
            if (isDeathTowerRun)
                _services.DeathTower.TryGenerateDropsForMonster(
                    session,
                    sequenceId,
                    out towerDrops);

            TryGetCurrentRoomState(run, out var roomState);
            var actorAllowsRewards = DungeonActorRewardEligibility
                .AllowsParticipantCombatRewards(monster);
            var allowsExperience = run.RewardPolicy.AllowsMonsterExperience
                && (dynamicPolicy?.GrantsMonsterExperience ?? true)
                && actorAllowsRewards;
            var allowsDrops = run.RewardPolicy.AllowsMonsterDrops
                && (dynamicPolicy?.GeneratesMonsterDrops ?? true)
                && actorAllowsRewards;
            var rewardMonsterType = GetRewardMonsterType(monster.Type);
            var isBoss = IsBossActorType(monster.Type);
            var isChampion = monster.Type == 1;
            var isNamed = !isBoss
                && DungeonData.IsNamedMonster(run.DungeonId, monster.Code);
            var isSuperChampion = monster.Type == 2 && !isNamed;
            var partyMemberCount = Math.Max(
                1,
                run.Instance?.Selection?.PartyMemberCount
                    ?? run.EntryPartyMemberCount);
            var experienceContext = new DungeonMonsterExperienceContext(
                session.Player.Level,
                monster.Level,
                run.Difficulty,
                rewardMonsterType,
                isNamed,
                partyMemberCount);
            var baseExperience = allowsExperience
                ? run.ExperienceDefinition?.UsesStandardFormula == true
                    ? DungeonExperienceCalculator.CalculateStandardMonster(
                        run.ExperienceDefinition,
                        experienceContext)
                    : DungeonExperienceCalculator
                        .CalculateNonStandardCompatibilityMonster(
                            run.ExperienceDefinition,
                            experienceContext)
                : default;
            var scaledExp = baseExperience.ParticipantBaseExperience;
            var experienceBonusSnapshot = run.CaptureExperienceBonusSnapshot();
            var growthContractBonus = allowsExperience
                ? CalculateGrowthContractMonsterBonus(session, scaledExp)
                : 0;
            var channelBonus = allowsExperience
                ? DungeonExperienceCalculator.CalculateChannelMonsterBonus(
                    scaledExp,
                    experienceBonusSnapshot)
                : 0;
            var awardedExp = CharacterExperienceService.AddSaturating(
                CharacterExperienceService.AddSaturating(
                    scaledExp,
                    growthContractBonus),
                channelBonus);

            var dungeonBasisLevel = (int)monster.Level;
            var dungeonMinimumLevel = (int)monster.Level;
            if (allowsDrops)
            {
                try
                {
                    dungeonBasisLevel = DungeonData.GetDungeonBasicLv(run.DungeonId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DungeonKill] basic level fallback: dungeon={run.DungeonId} " +
                        $"default={dungeonBasisLevel}: {ex.Message}");
                }

                try
                {
                    dungeonMinimumLevel = DungeonData.GetDungeonMinimumRequiredLevel(
                        run.DungeonId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DungeonKill] minimum level fallback: dungeon={run.DungeonId} " +
                        $"default={dungeonMinimumLevel}: {ex.Message}");
                }
            }

            IReadOnlyList<DropInfo> generatedDrops;
            int goldGained;
            if (!allowsDrops)
            {
                generatedDrops = Array.Empty<DropInfo>();
                goldGained = 0;
            }
            else if (isDeathTowerRun)
            {
                generatedDrops = towerDrops ?? Array.Empty<DropInfo>();
                goldGained = 0;
            }
            else if (monster.IsHellPartyActor
                && roomState != null
                && roomState.IsHellPartyRoom)
            {
                generatedDrops = _services.Drops.GenerateAbyssPartyAndRegister(
                    run,
                    BuildAbyssPartyDropRequest(
                        roomState,
                        monster,
                        dungeonMinimumLevel,
                        dungeonBasisLevel));
                goldGained = 0;
            }
            else
            {
                var dropRateLevel = run.HellMode
                    ? dungeonBasisLevel
                    : monster.Level;
                var dropResult = _services.Drops.GenerateAndRegister(
                    run,
                    new MonsterDropRequest
                    {
                        DropRateLevel = dropRateLevel,
                        MonsterType = rewardMonsterType,
                        MonsterCode = monster.Code,
                        DungeonBasisLevel = dungeonBasisLevel,
                    });
                generatedDrops = dropResult.Drops;
                goldGained = dropResult.GoldAmount;
            }

            ExperienceGrantResult grant = null;
            if (allowsExperience && awardedExp > 0)
            {
                grant = _services.CharacterExperience.Grant(
                    session.Player,
                    session.Account?.AccountId ?? 0,
                    awardedExp,
                    ExperiencePersistMode.OnLevelUpOnly,
                    "dungeon-kill");
            }

            lock (run.SyncRoot)
            {
                run.Combat.Experience.RecordMonster(
                    scaledExp,
                    growthContractBonus,
                    isBoss,
                    isChampion,
                    isSuperChampion,
                    isNamed,
                    channelBonus,
                    actorSequenceId: sequenceId);
                run.TotalGold = checked(run.TotalGold + goldGained);
            }

            if (allowsExperience)
            {
                var definition = run.ExperienceDefinition;
                FileLogger.Log(
                    $"[DungeonExperience] kill result dungeon={run.DungeonId} " +
                    $"difficulty={run.Difficulty} actorType={monster.Type} " +
                    $"rewardKind={rewardMonsterType} code={monster.Code} " +
                    $"level={monster.Level} named={isNamed} " +
                    $"kindRate={definition?.GetMonsterKindRate(rewardMonsterType):R} " +
                    $"difficultyRate={definition?.GetDifficultyRate(run.Difficulty):R} " +
                    $"sharedBaseExp={baseExperience.SharedBaseExperience} " +
                    $"participantBaseExp={baseExperience.ParticipantBaseExperience} " +
                    $"growthContract={growthContractBonus} channel={channelBonus} " +
                    $"awarded={awardedExp}");
            }

            if (grant != null)
            {
                await _services.ProgressNotifications.SendExpGrantNotificationAsync(
                    session,
                    grant,
                    "DUNGEON_KILL",
                    growthContractBonus,
                    channelBonus);
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return generatedDrops;
                if (grant.LeveledUp)
                    await _services.ProgressNotifications.SendInDungeonLevelUpFollowups(session);
            }

            return generatedDrops;
        }

        private async Task ApplyRoomClearedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            KillContext context,
            DungeonEventEnvelope roomClearSource,
            int killedMonsterCode,
            int blockingCount,
            int killedBlockingCount)
        {
            TryGetCurrentRoomState(run, out var roomState);
            var clearSource = roomClearSource ?? context.Envelope;
            DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    clearSource,
                    DungeonEncounterDirectiveKind.Succeed,
                    cause: "tracked room actors cleared"));
            roomState?.TryClear();

            var endPoint = roomState != null
                && run.BossMapPos != null
                && run.BossMapPos.Length >= 2
                && roomState.Maze.X == run.BossMapPos[0]
                && roomState.Maze.Y == run.BossMapPos[1];
            var currentMapId = roomState?.Maze.Index ?? 0;
            var explicitMapClear = run.ClearCondition != null
                && run.ClearCondition.Check(1, currentMapId);
            var shouldClearDungeon = DungeonCombatHandler.ShouldClearDungeon(
                explicitMapClear,
                endPoint,
                run.IgnoreDefaultDungeonClear);
            var hasPendingHostileApcBoss = roomState?.InstanceRoom != null
                && roomState.InstanceRoom.HasPendingHostileApcBoss();

            await PetCreatureRuntimeService.GrantRoomClearExperienceOnceAsync(
                session,
                roomState,
                1);
            if (!IsCurrent(run, context.Envelope))
                return;

            if (shouldClearDungeon && !hasPendingHostileApcBoss)
            {
                await _settlement.SubmitClearIntentAsync(
                    session,
                    new DungeonClearIntent(
                        context.Envelope,
                        $"prepare_dungeon_clear ccType1={explicitMapClear} endPoint={endPoint}",
                        killedMonsterCode),
                    deferParticipantFanout: true);
            }
            if (!IsCurrent(run, context.Envelope))
                return;

            FileLogger.Log(
                $"[DungeonKill] room cleared: cid={session.Player.CharacterId} " +
                $"origin={context.Origin} dungeon={run.DungeonId} " +
                $"room=({run.RoomKey.X},{run.RoomKey.Y}) map={currentMapId} " +
                $"blocking={killedBlockingCount}/{blockingCount} " +
                $"killedTotal={run.RoomKilledSeqIds.Count}");

            if (currentMapId <= 0)
                return;

            if (ShouldDeferQuestConnectedStartMapSync(run, currentMapId)
                && session.GameSession?.QuestManager != null
                && session.GameSession.QuestManager
                    .HasDeferredQuestConnectedStartMapClearQuest(
                        session.Player.CharacterId,
                        currentMapId))
            {
                FileLogger.Log(
                    $"[DungeonKill] CLEAR_MAP deferred: dungeon={run.DungeonId} " +
                    $"maze={run.MazeIndex} map={currentMapId}");
                return;
            }

            await DungeonClearMapQuestSync.SyncAsync(
                session,
                0,
                currentMapId,
                "room_clear",
                clearSource);
        }

        private bool TryCanonicalizeSharedDeath(
            KillContext context,
            DungeonRun run,
            out KillContext canonicalContext,
            out DungeonRoomActorDeathApplication worldDeath,
            out DungeonDynamicActorDefinition dynamicActor)
        {
            canonicalContext = null;
            worldDeath = default;
            dynamicActor = null;
            if (context?.Session?.Player == null
                || run == null
                || !run.TryCaptureCurrentRoomSnapshot(
                    context.Envelope.RoomIdentity,
                    out var roomSnapshot)
                || roomSnapshot.RoomState?.InstanceRoom == null)
            {
                return false;
            }

            var localIndex = context.SequenceId - roomSnapshot.RoomStartSequence;
            var hasStaticActor = localIndex >= 0
                && localIndex < roomSnapshot.Monsters.Count;
            var actorCode = 0;
            var actorType = (byte)0;
            if (hasStaticActor)
            {
                var monster = roomSnapshot.Monsters[localIndex];
                actorCode = monster.Code;
                actorType = monster.Type;
            }
            else if (run.Instance.Mechanisms.DynamicActors.TryResolve(
                         context.Envelope,
                         context.SequenceId,
                         out dynamicActor))
            {
                actorCode = dynamicActor.ActorCode;
                actorType = dynamicActor.ActorType;
                if (!roomSnapshot.RoomState.InstanceRoom.TryGetActorDeathFact(
                        context.SequenceId,
                        out _)
                    && !_bloodAltar.CanAcceptDynamicActorDeath(
                        run,
                        dynamicActor))
                {
                    FileLogger.Log(
                        $"[DungeonKill] dynamic actor death rejected before " +
                        $"world ledger: cid={context.Session.Player.CharacterId} " +
                        $"seq={context.SequenceId} provider={dynamicActor.Provider} " +
                        $"generation={dynamicActor.ProviderGeneration}");
                    return false;
                }
            }
            else
            {
                return false;
            }

            if (hasStaticActor
                && !_tournament.CanAcceptActorDeath(
                    run,
                    context.Envelope.SourceEventId,
                    context.SequenceId))
            {
                FileLogger.Log(
                    $"[Tournament] actor death rejected before world ledger: " +
                    $"cid={context.Session.Player.CharacterId} " +
                    $"seq={context.SequenceId} " +
                    $"event={context.Envelope.SourceEventId:N}");
                return false;
            }
            worldDeath = roomSnapshot.RoomState.InstanceRoom.TryRecordActorDeath(
                context.Envelope,
                context.SequenceId,
                actorCode,
                actorType,
                context.DeathKind);
            if (!worldDeath.Accepted || worldDeath.Fact == null)
                return false;

            var canonicalEnvelope = worldDeath.Fact.Source.ForAffectedPlayer(
                run.CaptureIdentity(),
                roomSnapshot.RoomIdentity.RoomInstanceId,
                context.Session.Player.CharacterId);
            canonicalContext = new KillContext(
                context.Session,
                canonicalEnvelope,
                context.SequenceId,
                context.SourceUserId,
                context.Origin,
                worldDeath.Fact.DeathKind);
            return true;
        }

        private IReadOnlyList<DungeonParticipantRosterEntry> CaptureEligibleRoster(
            KillContext source,
            DungeonRun sourceRun)
        {
            var result = new List<DungeonParticipantRosterEntry>();
            var seen = new HashSet<DungeonParticipantRunIdentity>();
            var roomIdentity = source.Envelope.RoomIdentity;

            void Add(
                int characterId,
                ushort participantUserId,
                DungeonRun candidateRun,
                long attachmentGeneration)
            {
                if (candidateRun == null
                    || characterId <= 0
                    || participantUserId == 0
                    || !candidateRun.TryCaptureCurrentRoomSnapshot(
                        roomIdentity,
                        out var snapshot)
                    || !seen.Add(snapshot.RunIdentity.ParticipantIdentity))
                {
                    return;
                }

                result.Add(new DungeonParticipantRosterEntry(
                    characterId,
                    participantUserId,
                    candidateRun,
                    snapshot.RunIdentity,
                    snapshot.RoomIdentity,
                    attachmentGeneration));
            }

            foreach (var participant in _services.InstanceRegistry
                         .CaptureParticipantRoster(roomIdentity))
            {
                if (seen.Add(participant.RunIdentity.ParticipantIdentity))
                    result.Add(participant);
            }

            var sourcePlayer = source.Session.Player;
            Add(
                sourcePlayer.CharacterId,
                sourcePlayer.UserId,
                sourceRun,
                attachmentGeneration: 0);

            var partyManager = _services.PartyManager;
            var sessions = _services.Sessions;
            if (partyManager == null || sessions == null)
                return result;

            var party = partyManager.GetPartyByUser(sourcePlayer.UserId);
            if (party == null)
                return result;

            foreach (var member in party.MembersBySlot())
            {
                if (!sessions.TryGet(member.CharacterId, out var memberSession))
                    continue;
                var memberPlayer = memberSession?.Player;
                Add(
                    member.CharacterId,
                    memberPlayer?.UserId ?? 0,
                    memberPlayer?.CurrentRun,
                    attachmentGeneration: 0);
            }

            return result;
        }

        private async Task RelayToFrozenRosterAsync(
            KillContext source,
            DungeonRun sourceRun)
        {
            var sessions = _services.Sessions;
            if (sessions == null || source.Session.Player == null)
                return;

            var sourceCharacterId = source.Session.Player.CharacterId;
            var roster = sourceRun.Instance.ParticipantEffects.GetRoster(
                source.Envelope.SourceEventId,
                DungeonParticipantEffectAudience.Room);
            foreach (var participant in roster)
            {
                if (participant.CharacterId == sourceCharacterId
                    && participant.RunIdentity.Equals(sourceRun.CaptureIdentity()))
                {
                    continue;
                }
                if (!sessions.TryGet(participant.CharacterId, out var memberSession)
                    || memberSession?.Player?.CurrentRun == null
                    || memberSession.TcpClient == null
                    || !memberSession.TcpClient.Connected)
                {
                    continue;
                }

                var memberRun = memberSession.Player.CurrentRun;
                if (!ReferenceEquals(memberRun, participant.Run)
                    || !memberRun.Matches(participant.RunIdentity)
                    || !memberRun.Matches(participant.RoomIdentity))
                {
                    continue;
                }

                var memberEvent = source.Envelope.ForAffectedPlayer(
                    participant.RunIdentity,
                    participant.RoomIdentity.RoomInstanceId,
                    participant.CharacterId);
                try
                {
                    await ProcessAsync(new KillContext(
                        memberSession,
                        memberEvent,
                        source.SequenceId,
                        source.SourceUserId,
                        DungeonKillOrigin.PartyRelay,
                        source.DeathKind));
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DungeonKill] frozen participant relay failed: " +
                        $"source={sourceCharacterId} member={participant.CharacterId} " +
                        $"seq={source.SequenceId} event={source.Envelope.SourceEventId:N} " +
                        $"error={ex.Message}");
                }
            }
        }

        private async Task RelayAndCompleteDeferredClearFanoutAsync(
            KillContext source,
            DungeonRun sourceRun)
        {
            try
            {
                await RelayToFrozenRosterAsync(source, sourceRun);
            }
            finally
            {
                await _settlement.CompleteDeferredClearFanoutAsync(
                    source.Session,
                    sourceRun,
                    source.Envelope.SourceEventId);
            }
        }

        private static DungeonParticipantRosterEntry FindParticipant(
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            int characterId,
            DungeonRunIdentity runIdentity)
        {
            if (roster == null)
                return null;

            foreach (var participant in roster)
            {
                if (participant.CharacterId == characterId
                    && participant.RunIdentity.Equals(runIdentity))
                {
                    return participant;
                }
            }
            return null;
        }

        private static bool TryResolvePendingBossActor(
            DungeonRun run,
            DungeonRoomIdentity roomIdentity,
            int actorCode,
            long? requestedSequenceId,
            out ushort sequenceId,
            out int resolvedActorCode)
        {
            sequenceId = 0;
            resolvedActorCode = 0;
            if (run == null
                || !run.TryCaptureCurrentRoomSnapshot(
                    roomIdentity,
                    out var snapshot)
                || snapshot.RoomState?.InstanceRoom == null)
            {
                return false;
            }

            var hasExpectedActorCode = actorCode > 0;
            if (!hasExpectedActorCode
                && (!requestedSequenceId.HasValue
                    || requestedSequenceId.Value <= 0
                    || requestedSequenceId.Value > ushort.MaxValue))
            {
                return false;
            }

            for (var index = 0; index < snapshot.Monsters.Count; index++)
            {
                var actor = snapshot.Monsters[index];
                if (!IsBossActorType(actor.Type)
                    || (hasExpectedActorCode && actor.Code != actorCode))
                    continue;

                var sequenceValue = (int)snapshot.RoomStartSequence + index;
                if (sequenceValue <= 0 || sequenceValue > ushort.MaxValue)
                    continue;

                var candidate = (ushort)sequenceValue;
                if (!hasExpectedActorCode
                    && candidate != (ushort)requestedSequenceId.Value)
                {
                    continue;
                }
                if (snapshot.RoomState.InstanceRoom.TryGetActorDeathFact(
                        candidate,
                        out _))
                {
                    continue;
                }

                sequenceId = candidate;
                resolvedActorCode = actor.Code;
                return true;
            }
            return false;
        }

        private static Task SendMonsterDieAsync(
            EnhancedClientSession session,
            KillContext context,
            IReadOnlyList<DropInfo> drops)
        {
            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.DIE_MONSTER,
                    DungeonNotificationBuilder.BuildMonsterDie(
                        context.SequenceId,
                        drops,
                        context.SourceUserId)));
        }

        private static bool IsCurrent(
            DungeonRun run,
            DungeonEventEnvelope envelope)
        {
            if (run == null
                || envelope == null
                || !run.Matches(envelope.RunIdentity))
            {
                return false;
            }

            return !envelope.RoomInstanceId.HasValue
                || run.CurrentRoomInstanceId == envelope.RoomInstanceId.Value;
        }

        private static bool TryGetCurrentRoomState(
            DungeonRun run,
            out RoomState roomState)
        {
            if (run == null)
            {
                roomState = null;
                return false;
            }

            lock (run.SyncRoot)
                return run.RoomStates.TryGetValue(run.RoomKey, out roomState);
        }

        private static bool ShouldDeferQuestConnectedStartMapSync(
            DungeonRun run,
            int currentMapId)
        {
            return run != null
                && run.MazeQuestConnected
                && run.MazeStartMapId > 0
                && run.MazeStartMapId == currentMapId
                && run.RoomKey.X == run.MazeStartX
                && run.RoomKey.Y == run.MazeStartY;
        }

        private static bool IsBossActorType(byte monsterType) =>
            monsterType == 3 || monsterType == 8;

        private static int GetRewardMonsterType(byte monsterType) =>
            monsterType == 8 ? 3 : monsterType;

        private static AbyssPartyDropRequest BuildAbyssPartyDropRequest(
            RoomState roomState,
            DungeonData.MonsterSumInfo monster,
            int dungeonMinimumLevel,
            int dungeonBasisLevel)
        {
            var isLastGroupMonster = false;
            if (roomState.HellPartyGroupRemaining != null
                && monster.HellPartyGroupId > 0
                && roomState.HellPartyGroupRemaining.TryGetValue(
                    monster.HellPartyGroupId,
                    out var remaining))
            {
                var after = Math.Max(0, remaining - 1);
                if (after == 0)
                {
                    roomState.HellPartyGroupRemaining.Remove(monster.HellPartyGroupId);
                    isLastGroupMonster = true;
                }
                else
                {
                    roomState.HellPartyGroupRemaining[monster.HellPartyGroupId] = after;
                }
            }

            return new AbyssPartyDropRequest
            {
                MonsterCode = monster.Code,
                DungeonMinimumLevel = dungeonMinimumLevel,
                DungeonBasisLevel = dungeonBasisLevel,
                AbyssPartyDifficulty = monster.HellPartyDifficulty,
                RewardRollCount = monster.HellRewardRollCount,
                IsLastGroupMonster = isLastGroupMonster,
                IsAbyssMonsterScript = monster.IsHellMonsterScript,
            };
        }

        private uint CalculateGrowthContractMonsterBonus(
            EnhancedClientSession session,
            uint baseMonsterExp)
        {
            if (baseMonsterExp == 0)
                return 0;

            var accountId = session.Account?.AccountId ?? 0;
            var connectionString = _services.ConnectionString;
            return Game.Premium.PremiumEffectProvider
                .GetCombinedEffects(connectionString, accountId)
                .ComputeBonusExp(baseMonsterExp);
        }

        private static void WriteUnclearedBossDiagnostic(
            EnhancedClientSession session,
            DungeonRun run,
            ushort sequenceId,
            int monsterCode,
            byte monsterType,
            bool roomCleared,
            int blockingCount,
            int killedBlockingCount)
        {
            TryGetCurrentRoomState(run, out var room);
            var roomX = room?.Maze.X ?? -999;
            var roomY = room?.Maze.Y ?? -999;
            var bossX = run.BossMapPos != null && run.BossMapPos.Length >= 2
                ? run.BossMapPos[0]
                : -1;
            var bossY = run.BossMapPos != null && run.BossMapPos.Length >= 2
                ? run.BossMapPos[1]
                : -1;
            FileLogger.Log(
                $"[DungeonKill] boss not cleared: cid={session.Player.CharacterId} " +
                $"seq={sequenceId} code={monsterCode} type={monsterType} " +
                $"roomCleared={roomCleared} blocking={killedBlockingCount}/{blockingCount} " +
                $"ccNull={run.ClearCondition == null} " +
                $"ccCleared={run.ClearCondition?.IsCleared} " +
                $"room=({roomX},{roomY}) boss=({bossX},{bossY}) " +
                $"phase={run.Phase}");
        }
    }
}
