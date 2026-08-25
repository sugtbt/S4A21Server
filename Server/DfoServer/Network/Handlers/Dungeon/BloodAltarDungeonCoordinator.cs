using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.BloodAltar;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class BloodAltarDungeonCoordinator
    {
        private const int DifficultyTimeoutSeconds = 10;
        private const int FinalRoundDelayMilliseconds = 1000;
        private const int WaveRetryDelayMilliseconds = 1000;
        private static readonly TimeSpan RankingDisplayDelay =
            TimeSpan.FromSeconds(8);
        private static readonly TimeSpan RewardDisplayDelay =
            TimeSpan.FromSeconds(8);
        private static readonly TimeSpan ExitTimeout =
            TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SettlementRetryDelay =
            TimeSpan.FromSeconds(1);
        private readonly DungeonSharedServices _services;
        private readonly DungeonSettlementHandler _settlement;
        private Func<KillContext, Task> _killProcessor;

        internal BloodAltarDungeonCoordinator(
            DungeonSharedServices services,
            DungeonSettlementHandler settlement)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _settlement = settlement
                ?? throw new ArgumentNullException(nameof(settlement));
        }

        internal void ConfigureKillProcessor(Func<KillContext, Task> processor)
        {
            _killProcessor = processor
                ?? throw new ArgumentNullException(nameof(processor));
        }

        internal async Task HandlePrepareFinishedAsync(
            EnhancedClientSession session,
            BloodAltarPrepareFinishedDungeonCommand command,
            DungeonEventEnvelope source)
        {
            var run = session?.Player?.CurrentRun;
            if (!IsCurrent(session, run, source)
                || !_services.BloodAltars.IsBloodAltar(run))
            {
                LogRejected(session, command, "not_current_blood_altar");
                return;
            }

            var runtime = _services.BloodAltars.GetRuntime(run);
            if (runtime.CurrentMapId <= 0
                || !runtime.CurrentRoomIdentity.Equals(source.RoomIdentity))
            {
                LogRejected(session, command, "altar_map_not_bound");
                return;
            }

            await StartNextRoundAsync(run, "client_prepare_finished");
        }

        internal async Task HandleMonsterDeathsAsync(
            EnhancedClientSession session,
            BloodAltarMonsterDeathsDungeonCommand command,
            DungeonEventEnvelope source)
        {
            var run = session?.Player?.CurrentRun;
            if (!IsCurrent(session, run, source)
                || !_services.BloodAltars.IsBloodAltar(run))
            {
                LogRejected(session, command, "not_current_blood_altar");
                return;
            }
            if (_killProcessor == null)
            {
                LogRejected(session, command, "kill_processor_not_configured");
                return;
            }

            foreach (var sequenceId in command.SequenceIds)
            {
                if (!session.Player.IsCurrentDungeonParticipantRoom(
                        run.CaptureParticipantRoomIdentity()))
                {
                    return;
                }

                var actorEvent = DungeonEventEnvelope.Create(
                    run,
                    session.Player.CharacterId,
                    "blood altar monster death",
                    sourceActorId: sequenceId);
                await _killProcessor(new KillContext(
                    session,
                    actorEvent,
                    sequenceId,
                    session.Player.UserId,
                    DungeonKillOrigin.LocalReport));
            }
        }

        internal async Task HandleSelectDifficultyAsync(
            EnhancedClientSession session,
            BloodAltarSelectDifficultyDungeonCommand command,
            DungeonEventEnvelope source)
        {
            var run = session?.Player?.CurrentRun;
            if (!IsCurrent(session, run, source)
                || !_services.BloodAltars.IsBloodAltar(run))
            {
                LogRejected(session, command, "not_current_blood_altar");
                return;
            }

            var runtime = _services.BloodAltars.GetRuntime(run);
            var promptVersion = runtime.DifficultyPromptVersion;
            if (!_services.BloodAltars.TryResolveUltimateDifficulty(
                    run,
                    command.Difficulty,
                    promptVersion,
                    out _))
            {
                LogRejected(session, command, "difficulty_transition_rejected");
                return;
            }

            runtime.Timers.Cancel(
                DungeonRunTimerKeys.BloodAltarDifficultySelection);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                command.WireType,
                BloodAltarPacketBuilder.BuildUltimateDifficultyConfirmed(
                    command.Difficulty)));
            await StartNextRoundAsync(run, "client_difficulty_selected");
        }

        internal async Task OnDynamicActorDeathAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonDynamicActorDefinition actor,
            DungeonEventEnvelope source)
        {
            if (!IsCurrent(session, run, source)
                || !_services.BloodAltars.CanAcceptActorDeath(run, actor)
                || !_services.BloodAltars.TryApplyActorDeath(
                    run,
                    actor,
                    out var progress,
                    out var releasedSequences))
            {
                return;
            }

            if (releasedSequences.Count > 0)
            {
                FileLogger.Log(
                    $"[BloodAltar] final wave released stale actors: " +
                    $"instance={run.PartyDungeonInstanceId} " +
                    $"room={run.CurrentRoomInstanceId} " +
                    $"count={releasedSequences.Count}");
            }
            await ProcessProgressAsync(session, run, progress, source);
        }

        internal bool BlocksMapMove(DungeonRun run)
            => _services.BloodAltars.BlocksMapMove(run);

        internal bool CanAcceptDynamicActorDeath(
            DungeonRun run,
            DungeonDynamicActorDefinition actor)
            => _services.BloodAltars.CanAcceptActorDeath(run, actor);

        private async Task StartNextRoundAsync(DungeonRun run, string source)
        {
            if (run == null
                || !_services.BloodAltars.TryBeginNextRound(
                    run,
                    DateTime.UtcNow,
                    out var schedule))
            {
                return;
            }

            await SendToCurrentRoomAsync(
                run,
                (ushort)NotiPacketType.BLOOD_ROUND_INTERVAL_TIME,
                BloodAltarPacketBuilder.BuildRoundInterval(
                    schedule.RoundNumber,
                    schedule.InitialIntervalMilliseconds),
                source);
            FileLogger.Log(
                $"[BloodAltar] round started: " +
                $"instance={run.PartyDungeonInstanceId} " +
                $"room={run.CurrentRoomInstanceId} round={schedule.RoundNumber} " +
                $"difficulty={schedule.Difficulty} waves={schedule.Waves.Count}");
            ScheduleNextWave(run, schedule.Generation);
        }

        private void ScheduleNextWave(
            DungeonRun run,
            long scheduleGeneration,
            DateTime retryNotBeforeUtc = default)
        {
            var runtime = _services.BloodAltars.GetRuntime(run);
            if (runtime == null
                || !_services.BloodAltars.TryGetNextWaveDeadline(
                    run,
                    scheduleGeneration,
                    out var waveIndex,
                    out var deadlineUtc))
            {
                return;
            }
            if (retryNotBeforeUtc != default && deadlineUtc < retryNotBeforeUtc)
                deadlineUtc = retryNotBeforeUtc;

            var identity = run.CaptureIdentity();
            var ticket = runtime.Timers.Begin(
                DungeonRunTimerKeys.BloodAltarWaveSchedule,
                deadlineUtc,
                RunTimerDetachPolicy.Cancel);
            var timerName =
                $"blood-altar:wave:{identity.PartyDungeonInstanceId}:" +
                $"{identity.RunId}:{scheduleGeneration}:{waveIndex}:" +
                $"{ticket.Generation}";
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                timerName,
                deadlineUtc,
                async _ => await OnWaveTimerAsync(
                    run,
                    identity,
                    scheduleGeneration,
                    waveIndex,
                    ticket));
            runtime.Timers.Attach(ticket, handle);
        }

        private async Task OnWaveTimerAsync(
            DungeonRun run,
            DungeonRunIdentity identity,
            long scheduleGeneration,
            int waveIndex,
            RunTimerTicket ticket)
        {
            var runtime = _services.BloodAltars.GetRuntime(run);
            if (runtime == null
                || !run.Matches(identity)
                || run.Instance.State == DungeonInstanceState.Ending
                || run.Instance.State == DungeonInstanceState.Ended
                || !runtime.Timers.TryComplete(ticket))
            {
                return;
            }

            if (!_services.BloodAltars.TryMaterializeWave(
                    run,
                    scheduleGeneration,
                    waveIndex,
                    out var wave,
                    out var schedulingComplete,
                    out var failureReason))
            {
                FileLogger.Log(
                    $"[BloodAltar] wave materialization deferred: " +
                    $"instance={identity.PartyDungeonInstanceId} " +
                    $"wave={waveIndex} reason={failureReason}");
                ScheduleNextWave(
                    run,
                    scheduleGeneration,
                    DateTime.UtcNow.AddMilliseconds(WaveRetryDelayMilliseconds));
                return;
            }

            await SendToCurrentRoomAsync(
                run,
                (ushort)NotiPacketType.BLOOD_MONSTER_SPAWN,
                BloodAltarPacketBuilder.BuildMonsterSpawn(wave),
                $"wave_{waveIndex}");
            if (!schedulingComplete)
            {
                ScheduleNextWave(run, scheduleGeneration);
                return;
            }

            if (_services.BloodAltars.TryAdvanceAfterScheduling(
                    run,
                    out var progress))
            {
                var session = FindActiveSession(run);
                if (session != null)
                {
                    await ProcessProgressAsync(
                        session,
                        run,
                        progress,
                        DungeonEventEnvelope.Create(
                            run,
                            session.Player.CharacterId,
                            "blood altar scheduling complete"));
                }
            }
        }

        private async Task ProcessProgressAsync(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarProgress progress,
            DungeonEventEnvelope source)
        {
            switch (progress)
            {
                case BloodAltarProgress.ReadyForNextRound:
                    await StartNextRoundAsync(run, "round_complete");
                    return;

                case BloodAltarProgress.AwaitingUltimateDifficulty:
                    await PromptUltimateDifficultyAsync(run);
                    return;

                case BloodAltarProgress.ReadyForFinalRound:
                    ScheduleFinalRound(run);
                    return;

                case BloodAltarProgress.MapComplete:
                case BloodAltarProgress.DungeonComplete:
                    MarkCurrentRoomCleared(run, source);
                    if (progress != BloodAltarProgress.DungeonComplete
                        || !_services.BloodAltars.TryCreateClearIntent(
                            run,
                            source,
                            out var intent))
                    {
                        return;
                    }
                    await _settlement.SubmitClearIntentAsync(
                        session,
                        intent,
                        deferParticipantFanout: true);
                    return;
            }
        }

        private async Task PromptUltimateDifficultyAsync(DungeonRun run)
        {
            var runtime = _services.BloodAltars.GetRuntime(run);
            if (runtime == null || !runtime.AwaitingUltimateDifficulty)
                return;

            var promptVersion = runtime.DifficultyPromptVersion;
            var round = runtime.CompletedRounds;
            await SendToCurrentRoomAsync(
                run,
                (ushort)NotiPacketType.ULTIMATE_DIFFICULTY_UI,
                BloodAltarPacketBuilder.BuildUltimateDifficultyPrompt(
                    round,
                    DifficultyTimeoutSeconds),
                "difficulty_prompt");

            var identity = run.CaptureIdentity();
            var deadlineUtc = DateTime.UtcNow.AddSeconds(
                DifficultyTimeoutSeconds);
            var ticket = runtime.Timers.Begin(
                DungeonRunTimerKeys.BloodAltarDifficultySelection,
                deadlineUtc,
                RunTimerDetachPolicy.Cancel);
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                $"blood-altar:difficulty:{identity.PartyDungeonInstanceId}:" +
                $"{promptVersion}:{ticket.Generation}",
                deadlineUtc,
                async _ => await OnDifficultyTimeoutAsync(
                    run,
                    identity,
                    promptVersion,
                    ticket));
            runtime.Timers.Attach(ticket, handle);
        }

        private async Task OnDifficultyTimeoutAsync(
            DungeonRun run,
            DungeonRunIdentity identity,
            int promptVersion,
            RunTimerTicket ticket)
        {
            var runtime = _services.BloodAltars.GetRuntime(run);
            if (runtime == null
                || !run.Matches(identity)
                || !runtime.Timers.TryComplete(ticket))
            {
                return;
            }

            var difficulty = (byte)(ServerRandom.Next(2) + 1);
            if (!_services.BloodAltars.TryResolveUltimateDifficulty(
                    run,
                    difficulty,
                    promptVersion,
                    out _))
            {
                return;
            }

            await SendToCurrentRoomAsync(
                run,
                (ushort)NotiPacketType.ULTIMATE_DIFFICULTY_UI,
                BloodAltarPacketBuilder.BuildUltimateDifficultyResolved(
                    difficulty),
                "difficulty_timeout");
            await StartNextRoundAsync(run, "difficulty_timeout");
        }

        private void ScheduleFinalRound(DungeonRun run)
        {
            var runtime = _services.BloodAltars.GetRuntime(run);
            if (runtime == null)
                return;

            var identity = run.CaptureIdentity();
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(
                FinalRoundDelayMilliseconds);
            var ticket = runtime.Timers.Begin(
                DungeonRunTimerKeys.BloodAltarFinalRound,
                deadlineUtc,
                RunTimerDetachPolicy.Cancel);
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                $"blood-altar:final-round:{identity.PartyDungeonInstanceId}:" +
                $"{ticket.Generation}",
                deadlineUtc,
                async _ =>
                {
                    if (run.Matches(identity)
                        && runtime.Timers.TryComplete(ticket))
                    {
                        await StartNextRoundAsync(run, "final_round_delay");
                    }
                });
            runtime.Timers.Attach(ticket, handle);
        }

        internal Task OnParticipantClearedAsync(
            EnhancedClientSession session,
            DungeonRun run)
            => DriveSettlementAsync(session, run, "clear_commit");

        internal async Task<bool> TryHandleEplpCommandAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !_services.BloodAltars.IsBloodAltar(run))
                return false;

            if (!BloodAltarEplpCommandParser.TryParse(
                    body,
                    out var command,
                    out var error))
            {
                FileLogger.Log(
                    $"[BloodAltar] EPLP rejected: " +
                    $"cid={session.Player.CharacterId} reason={error} " +
                    $"body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
                return true;
            }

            var runtime = run.SettlementRuntime?.BloodAltar;
            if (runtime == null)
            {
                FileLogger.Log(
                    $"[BloodAltar] EPLP ignored before settlement prepared: " +
                    $"cid={session.Player.CharacterId} bodyLen={body?.Length ?? 0}");
                return true;
            }

            if (!command.RequestsExit)
            {
                await SendEplpAckAsync(session, header, command);
                return true;
            }

            if (!runtime.TryQueueExitIntent(command))
            {
                FileLogger.Log(
                    $"[BloodAltar] EPLP exit ignored after terminal phase: " +
                    $"cid={session.Player.CharacterId} phase={runtime.Phase}");
                return true;
            }

            await SendEplpAckAsync(session, header, command);
            if (!await TryExecutePendingExitAsync(session, run, runtime))
            {
                FileLogger.Log(
                    $"[BloodAltar] EPLP exit queued: " +
                    $"cid={session.Player.CharacterId} phase={runtime.Phase} " +
                    $"option={command.Option}");
            }
            return true;
        }

        private static Task SendEplpAckAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            BloodAltarEplpCommand command)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                BloodAltarPacketBuilder.BuildEplpCommandAck(
                    command.State,
                    command.Option)));

        private async Task<bool> TryExecutePendingExitAsync(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime)
        {
            if (!runtime.TryBeginPendingExit(out _))
                return false;

            run.Timers.Cancel(DungeonRunTimerKeys.BloodAltarReturnToTown);
            var identity = run.CaptureIdentity();
            try
            {
                await _services.TownReturn.ReturnAsync(session, identity);
                runtime.TryMarkEnded();
                return true;
            }
            catch
            {
                runtime.TryAbortExit();
                EnsureReturnTimer(session, run, runtime);
                throw;
            }
        }

        internal async Task RecoverAsync(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !_services.BloodAltars.IsBloodAltar(run))
                return;

            if (run.SettlementRuntime?.BloodAltar != null)
            {
                await DriveSettlementAsync(session, run, "rejoin");
                return;
            }

            if (run.RunState != DungeonRunState.Active)
                return;
            await ProjectActiveRunRecoveryAsync(session, run);
        }

        private async Task DriveSettlementAsync(
            EnhancedClientSession session,
            DungeonRun run,
            string source)
        {
            var runtime = run?.SettlementRuntime?.BloodAltar;
            if (session?.Player == null
                || runtime == null
                || run.ClearedFact?.PresentationKind
                    != DungeonClearPresentationKind.BloodAltar
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                return;
            }

            await run.Settlement.BloodAltarProjectionGate.WaitAsync();
            try
            {
                if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                    return;
                run.Timers.Cancel(
                    DungeonRunTimerKeys.BloodAltarSettlementRetry);

                switch (runtime.Phase)
                {
                    case BloodAltarSettlementPhase.Prepared:
                        await ProjectRankingAsync(session, run, runtime);
                        break;
                    case BloodAltarSettlementPhase.RankingShown:
                        EnsureRankingTimer(session, run, runtime);
                        break;
                    case BloodAltarSettlementPhase.RewardShown:
                        EnsureRewardTimer(session, run, runtime);
                        break;
                    case BloodAltarSettlementPhase.Committing:
                        runtime.TryAbortCommit();
                        await CommitAndProjectRewardsAsync(
                            session,
                            run,
                            runtime);
                        break;
                    case BloodAltarSettlementPhase.Committed:
                        await ProjectCommittedRewardsAsync(
                            session,
                            run,
                            runtime);
                        break;
                    case BloodAltarSettlementPhase.ExitReady:
                        if (!await TryExecutePendingExitAsync(
                                session,
                                run,
                                runtime))
                        {
                            EnsureReturnTimer(session, run, runtime);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[BloodAltar] settlement drive failed: source={source} " +
                    $"cid={session.Player.CharacterId} phase={runtime.Phase} " +
                    $"error={ex}");
                ScheduleSettlementRetry(session, run, runtime);
            }
            finally
            {
                run.Settlement.BloodAltarProjectionGate.Release();
            }
        }

        private async Task ProjectRankingAsync(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime)
        {
            var plan = runtime.Plan;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.BLOOD_DUNGEON_STATE_RANKING,
                BloodAltarPacketBuilder.BuildRanking(
                    plan.ClearTimeMilliseconds,
                    plan.CompletedRounds,
                    plan.ClearTimeMilliseconds,
                    plan.CompletedRounds,
                    plan.MaxRounds,
                    plan.RewardExperience)));
            if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return;

            var deadlineUtc = DateTime.UtcNow.Add(RankingDisplayDelay);
            if (!runtime.TryMarkRankingShown(deadlineUtc))
                return;
            if (run.SettlementState == DungeonSettlementState.Preparing)
                run.TryMarkResultShown();
            BeginRankingTimer(session, run, runtime, deadlineUtc);
            FileLogger.Log(
                $"[BloodAltar] ranking shown: cid={session.Player.CharacterId} " +
                $"rounds={plan.CompletedRounds}/{plan.MaxRounds} " +
                $"time={plan.ClearTimeMilliseconds} exp={plan.RewardExperience}");
        }

        private void EnsureRankingTimer(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime)
        {
            if (run.Timers.TryGetCurrentTicket(
                    DungeonRunTimerKeys.BloodAltarRankingToReward,
                    out _))
            {
                return;
            }
            if (run.Timers.TryResume(
                    DungeonRunTimerKeys.BloodAltarRankingToReward,
                    out var resumed,
                    out var resumedDeadline))
            {
                ScheduleRankingTimer(
                    session,
                    run,
                    runtime,
                    resumedDeadline,
                    resumed);
                return;
            }

            var deadlineUtc = runtime.RankingDeadlineUtc == DateTime.MinValue
                ? DateTime.UtcNow.Add(SettlementRetryDelay)
                : runtime.RankingDeadlineUtc;
            BeginRankingTimer(session, run, runtime, deadlineUtc);
        }

        private void BeginRankingTimer(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime,
            DateTime deadlineUtc)
        {
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.BloodAltarRankingToReward,
                deadlineUtc,
                RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleRankingTimer(
                session,
                run,
                runtime,
                deadlineUtc,
                ticket);
        }

        private void ScheduleRankingTimer(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime,
            DateTime deadlineUtc,
            RunTimerTicket ticket)
        {
            var identity = run.CaptureIdentity();
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                $"blood-altar:ranking:{session.Player.CharacterId}:" +
                $"{run.RunId}:{ticket.Generation}",
                deadlineUtc,
                async _ => await OnRankingTimerAsync(
                    session,
                    run,
                    identity,
                    runtime,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnRankingTimerAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            BloodAltarParticipantSettlementRuntime runtime,
            RunTimerTicket ticket)
        {
            await run.Settlement.BloodAltarProjectionGate.WaitAsync();
            try
            {
                if (!IsCurrentSettlement(session, run, identity, runtime)
                    || !run.Timers.IsCurrent(ticket))
                {
                    return;
                }

                var plan = runtime.Plan;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.BLOOD_DUNGEON_STATE_REWARD,
                    BloodAltarPacketBuilder.BuildReward(
                        plan.CompletedRounds,
                        plan.MaxRounds,
                        plan.Rewards)));
                if (!IsCurrentSettlement(session, run, identity, runtime))
                    return;

                var deadlineUtc = DateTime.UtcNow.Add(RewardDisplayDelay);
                if (!runtime.TryMarkRewardShown(deadlineUtc))
                    return;
                run.Timers.TryComplete(ticket);
                BeginRewardTimer(session, run, runtime, deadlineUtc);
                FileLogger.Log(
                    $"[BloodAltar] reward shown: " +
                    $"cid={session.Player.CharacterId} " +
                    $"cards={plan.Rewards.Count} gold={plan.TotalGold}");
            }
            catch (Exception ex)
            {
                run.Timers.TryComplete(ticket);
                FileLogger.Log(
                    $"[BloodAltar] reward projection failed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} error={ex}");
                if (IsCurrentSettlement(session, run, identity, runtime))
                {
                    BeginRankingTimer(
                        session,
                        run,
                        runtime,
                        DateTime.UtcNow.Add(SettlementRetryDelay));
                }
            }
            finally
            {
                run.Settlement.BloodAltarProjectionGate.Release();
            }
        }

        private void EnsureRewardTimer(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime)
        {
            if (run.Timers.TryGetCurrentTicket(
                    DungeonRunTimerKeys.BloodAltarRewardToExit,
                    out _))
            {
                return;
            }
            if (run.Timers.TryResume(
                    DungeonRunTimerKeys.BloodAltarRewardToExit,
                    out var resumed,
                    out var resumedDeadline))
            {
                ScheduleRewardTimer(
                    session,
                    run,
                    runtime,
                    resumedDeadline,
                    resumed);
                return;
            }

            var deadlineUtc = runtime.RewardDeadlineUtc == DateTime.MinValue
                ? DateTime.UtcNow.Add(SettlementRetryDelay)
                : runtime.RewardDeadlineUtc;
            BeginRewardTimer(session, run, runtime, deadlineUtc);
        }

        private void BeginRewardTimer(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime,
            DateTime deadlineUtc)
        {
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.BloodAltarRewardToExit,
                deadlineUtc,
                RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleRewardTimer(
                session,
                run,
                runtime,
                deadlineUtc,
                ticket);
        }

        private void ScheduleRewardTimer(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime,
            DateTime deadlineUtc,
            RunTimerTicket ticket)
        {
            var identity = run.CaptureIdentity();
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                $"blood-altar:reward:{session.Player.CharacterId}:" +
                $"{run.RunId}:{ticket.Generation}",
                deadlineUtc,
                async _ => await OnRewardTimerAsync(
                    session,
                    run,
                    identity,
                    runtime,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnRewardTimerAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            BloodAltarParticipantSettlementRuntime runtime,
            RunTimerTicket ticket)
        {
            await run.Settlement.BloodAltarProjectionGate.WaitAsync();
            try
            {
                if (!IsCurrentSettlement(session, run, identity, runtime)
                    || !run.Timers.TryComplete(ticket))
                {
                    return;
                }
                await CommitAndProjectRewardsAsync(session, run, runtime);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[BloodAltar] reward commit/projection failed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} error={ex}");
                if (runtime.Phase == BloodAltarSettlementPhase.Committing)
                    runtime.TryAbortCommit();
                if (IsCurrentSettlement(session, run, identity, runtime))
                    ScheduleSettlementRetry(session, run, runtime);
            }
            finally
            {
                run.Settlement.BloodAltarProjectionGate.Release();
            }
        }

        private async Task CommitAndProjectRewardsAsync(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime)
        {
            if (runtime.Phase == BloodAltarSettlementPhase.RewardShown)
            {
                if (!runtime.TryBeginCommit())
                    return;

                var effectId = new DungeonEffectId(
                    run.GetSettlementSourceEventId(),
                    DungeonPersistentEffectKinds.BloodAltarRewardCommit,
                    DungeonEffectScope.Player,
                    run.RunId);

                try
                {
                    if (!TryGetOwnedInventoryLease(session, out var lease))
                    {
                        throw new InvalidOperationException(
                            "Blood altar settlement has no owned inventory lease.");
                    }
                    if (!_services.PersistentEffects.TryApplyBloodAltarReward(
                        effectId,
                        lease,
                        session.SessionId,
                        runtime.Plan,
                        out var result,
                        out var error))
                    {
                        throw new InvalidOperationException(
                            "Blood altar durable reward commit failed: " +
                            (error ?? "unknown"));
                    }
                    if (!runtime.TryCompleteCommit(result))
                        runtime.ProjectCommitted(result);
                    run.Effects.ProjectCommitted(effectId);
                }
                catch
                {
                    if (runtime.Phase == BloodAltarSettlementPhase.Committing)
                        runtime.TryAbortCommit();
                    throw;
                }
            }

            await ProjectCommittedRewardsAsync(session, run, runtime);
        }

        private async Task ProjectCommittedRewardsAsync(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime)
        {
            if (runtime.Phase != BloodAltarSettlementPhase.Committed
                && runtime.Phase != BloodAltarSettlementPhase.ExitReady)
            {
                return;
            }

            var settlement = run.SettlementRuntime;
            var identity = run.CaptureIdentity();
            if (!runtime.ExperienceProjectionSent)
            {
                await _services.ProgressNotifications
                    .SendExpGrantNotificationAsync(
                        session,
                        settlement.ExperienceGrant,
                        "BLOOD_ALTAR_SETTLEMENT",
                        reloadMissingAccountProgress: true);
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return;
                runtime.TryMarkExperienceProjectionSent();
            }

            if (settlement.ExperienceGrant?.LeveledUp == true
                && !runtime.LevelUpProjectionSent)
            {
                await _services.ProgressNotifications
                    .SendInDungeonLevelUpFollowups(session);
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return;
                runtime.TryMarkLevelUpProjectionSent();
            }

            var commit = runtime.CommitResult;
            if (commit != null && !runtime.InventoryProjectionSent)
            {
                if (_services.InventoryRefresh != null)
                {
                    foreach (var group in commit.Changes.GroupBy(
                                 change => change.ListType))
                    {
                        await _services.InventoryRefresh.SendUpdateItemList(
                            session,
                            group.Key,
                            group.Select(change => change.SlotIndex)
                                .Distinct()
                                .ToList());
                        if (!session.Player.IsCurrentDungeonRun(identity))
                            return;
                    }
                }
                runtime.TryMarkInventoryProjectionSent();
            }

            if (runtime.Phase == BloodAltarSettlementPhase.Committed
                && !runtime.ExitReadyProjectionSent)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0104,
                    BloodAltarPacketBuilder.BuildExitReady()));
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return;

                var returnDeadlineUtc = DateTime.UtcNow.Add(ExitTimeout);
                if (!runtime.TryMarkExitReadyProjectionSent(
                        returnDeadlineUtc))
                {
                    return;
                }
                run.TryCompleteSettlement();
            }

            if (runtime.Phase == BloodAltarSettlementPhase.ExitReady
                && !await TryExecutePendingExitAsync(session, run, runtime))
            {
                EnsureReturnTimer(session, run, runtime);
            }
        }

        private void EnsureReturnTimer(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime)
        {
            if (run.Timers.TryGetCurrentTicket(
                    DungeonRunTimerKeys.BloodAltarReturnToTown,
                    out _))
            {
                return;
            }
            if (run.Timers.TryResume(
                    DungeonRunTimerKeys.BloodAltarReturnToTown,
                    out var resumed,
                    out var resumedDeadline))
            {
                ScheduleReturnTimer(
                    session,
                    run,
                    runtime,
                    resumedDeadline,
                    resumed);
                return;
            }

            var deadlineUtc = runtime.ReturnDeadlineUtc == DateTime.MinValue
                ? DateTime.UtcNow.Add(ExitTimeout)
                : runtime.ReturnDeadlineUtc;
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.BloodAltarReturnToTown,
                deadlineUtc,
                RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleReturnTimer(
                session,
                run,
                runtime,
                deadlineUtc,
                ticket);
        }

        private void ScheduleReturnTimer(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime,
            DateTime deadlineUtc,
            RunTimerTicket ticket)
        {
            var identity = run.CaptureIdentity();
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                $"blood-altar:return:{session.Player.CharacterId}:" +
                $"{run.RunId}:{ticket.Generation}",
                deadlineUtc,
                async _ => await OnReturnTimerAsync(
                    session,
                    run,
                    identity,
                    runtime,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnReturnTimerAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            BloodAltarParticipantSettlementRuntime runtime,
            RunTimerTicket ticket)
        {
            if (!IsCurrentSettlement(session, run, identity, runtime)
                || !run.Timers.TryComplete(ticket)
                || !runtime.TryBeginExit())
            {
                return;
            }

            try
            {
                await _services.TownReturn.ReturnAsync(session, identity);
                runtime.TryMarkEnded();
            }
            catch (Exception ex)
            {
                runtime.TryAbortExit();
                FileLogger.Log(
                    $"[BloodAltar] automatic return failed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} error={ex}");
                if (session?.Player?.IsCurrentDungeonRun(identity) == true)
                    EnsureReturnTimer(session, run, runtime);
            }
        }

        private void ScheduleSettlementRetry(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime)
        {
            switch (runtime.Phase)
            {
                case BloodAltarSettlementPhase.Prepared:
                    ScheduleDriveRetry(
                        session,
                        run,
                        runtime,
                        "prepared_retry");
                    break;
                case BloodAltarSettlementPhase.RankingShown:
                    BeginRankingTimer(
                        session,
                        run,
                        runtime,
                        DateTime.UtcNow.Add(SettlementRetryDelay));
                    break;
                case BloodAltarSettlementPhase.RewardShown:
                case BloodAltarSettlementPhase.Committing:
                    if (runtime.Phase == BloodAltarSettlementPhase.Committing)
                        runtime.TryAbortCommit();
                    BeginRewardTimer(
                        session,
                        run,
                        runtime,
                        DateTime.UtcNow.Add(SettlementRetryDelay));
                    break;
                case BloodAltarSettlementPhase.Committed:
                    ScheduleDriveRetry(
                        session,
                        run,
                        runtime,
                        "committed_retry");
                    break;
                case BloodAltarSettlementPhase.ExitReady:
                    EnsureReturnTimer(session, run, runtime);
                    break;
            }
        }

        private void ScheduleDriveRetry(
            EnhancedClientSession session,
            DungeonRun run,
            BloodAltarParticipantSettlementRuntime runtime,
            string source)
        {
            var identity = run.CaptureIdentity();
            var deadlineUtc = DateTime.UtcNow.Add(SettlementRetryDelay);
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.BloodAltarSettlementRetry,
                deadlineUtc,
                RunTimerDetachPolicy.SuspendUntilResume);
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                $"blood-altar:settlement-retry:" +
                $"{session.Player.CharacterId}:{run.RunId}:" +
                $"{ticket.Generation}",
                deadlineUtc,
                async _ =>
                {
                    if (!IsCurrentSettlement(
                            session,
                            run,
                            identity,
                            runtime)
                        || !run.Timers.TryComplete(ticket))
                    {
                        return;
                    }
                    await DriveSettlementAsync(session, run, source);
                });
            run.Timers.Attach(ticket, handle);
        }

        private async Task ProjectActiveRunRecoveryAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var runtime = _services.BloodAltars.GetRuntime(run);
            if (runtime == null
                || runtime.CurrentMapId <= 0
                || !run.TryCaptureCurrentRoomSnapshot(out var room))
            {
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.START_BLOOD_MAP,
                BloodAltarPacketBuilder.BuildStartMap(
                    (byte)Math.Max(0, Math.Min(byte.MaxValue, room.RoomKey.X)),
                    (byte)Math.Max(0, Math.Min(byte.MaxValue, room.RoomKey.Y)),
                    room.RoomState.Seed,
                    (uint)runtime.CurrentMapId)));
            if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return;

            if (runtime.TryCaptureCurrentSchedule(out var schedule))
            {
                var interval = schedule.InitialIntervalMilliseconds;
                if (_services.BloodAltars.TryGetNextWaveDeadline(
                        run,
                        schedule.Generation,
                        out _,
                        out var waveDeadline))
                {
                    interval = (int)Math.Min(
                        int.MaxValue,
                        Math.Max(0, (waveDeadline - DateTime.UtcNow)
                            .TotalMilliseconds));
                }
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.BLOOD_ROUND_INTERVAL_TIME,
                    BloodAltarPacketBuilder.BuildRoundInterval(
                        schedule.RoundNumber,
                        interval)));
            }

            var active = runtime.CaptureActiveSpawns();
            if (active.Count > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.BLOOD_MONSTER_SPAWN,
                    BloodAltarPacketBuilder.BuildMonsterSpawn(
                        new BloodAltarWave
                        {
                            Monsters = active,
                            TailValue = 0,
                        })));
            }

            if (runtime.AwaitingUltimateDifficulty)
            {
                var seconds = DifficultyTimeoutSeconds;
                if (runtime.Timers.TryGetSnapshot(
                        DungeonRunTimerKeys.BloodAltarDifficultySelection,
                        out var snapshot)
                    && snapshot.HasDeadline)
                {
                    seconds = (int)Math.Max(
                        0,
                        Math.Ceiling(
                            (snapshot.DeadlineUtc - DateTime.UtcNow)
                                .TotalSeconds));
                }
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.ULTIMATE_DIFFICULTY_UI,
                    BloodAltarPacketBuilder.BuildUltimateDifficultyPrompt(
                        runtime.CompletedRounds,
                        seconds)));
            }
        }

        private static bool IsCurrentSettlement(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            BloodAltarParticipantSettlementRuntime runtime)
            => session?.Player != null
               && run?.SettlementRuntime?.BloodAltar == runtime
               && run.Matches(identity)
               && session.Player.IsCurrentDungeonRun(identity);

        private static bool TryGetOwnedInventoryLease(
            EnhancedClientSession session,
            out InventoryLease lease)
        {
            lease = null;
            var characterId = session?.Player?.CharacterId ?? 0;
            return characterId > 0
                && InventoryContext.TryGetLease(characterId, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

        private async Task SendToCurrentRoomAsync(
            DungeonRun run,
            ushort packetType,
            byte[] body,
            string source)
        {
            var sent = new HashSet<int>();
            foreach (var participant in _services.InstanceRegistry
                         .CaptureParticipantRoster(run.CaptureRoomIdentity()))
            {
                if (!sent.Add(participant.CharacterId)
                    || !_services.Sessions.TryGet(
                        participant.CharacterId,
                        out var target)
                    || target?.Player == null
                    || !target.Player.IsCurrentDungeonParticipantRoom(
                        participant.Run.CaptureParticipantRoomIdentity()))
                {
                    continue;
                }

                try
                {
                    await target.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        packetType,
                        body));
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[BloodAltar] projection deferred: source={source} " +
                        $"cid={participant.CharacterId} type=0x{packetType:X4} " +
                        $"error={ex.Message}");
                }
            }

            if (sent.Count == 0)
            {
                FileLogger.Log(
                    $"[BloodAltar] projection checkpoint retained without " +
                    $"active participant: source={source} " +
                    $"instance={run.PartyDungeonInstanceId} " +
                    $"type=0x{packetType:X4}");
            }
        }

        private EnhancedClientSession FindActiveSession(DungeonRun run)
        {
            foreach (var participant in _services.InstanceRegistry
                         .CaptureParticipantRoster(run.CaptureRoomIdentity()))
            {
                if (_services.Sessions.TryGet(
                        participant.CharacterId,
                        out var session)
                    && session?.Player != null
                    && session.Player.IsCurrentDungeonRun(
                        participant.RunIdentity))
                {
                    return session;
                }
            }
            return null;
        }

        private static void MarkCurrentRoomCleared(
            DungeonRun run,
            DungeonEventEnvelope source)
        {
            DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    source,
                    DungeonEncounterDirectiveKind.Succeed,
                    cause: "blood altar map complete"));
            lock (run.SyncRoot)
            {
                if (run.RoomStates.TryGetValue(run.RoomKey, out var roomState))
                    roomState?.TryClear();
            }
        }

        private static bool IsCurrent(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonEventEnvelope source)
            => session?.Player != null
               && run != null
               && source != null
               && run.Matches(source.RunIdentity)
               && run.Matches(source.RoomIdentity)
               && session.Player.IsCurrentDungeonRun(source.RunIdentity);

        private static void LogRejected(
            EnhancedClientSession session,
            DungeonCommand command,
            string reason)
            => FileLogger.Log(
                $"[BloodAltar] command rejected: " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"type=0x{command?.WireType ?? 0:X4} reason={reason}");
    }
}
