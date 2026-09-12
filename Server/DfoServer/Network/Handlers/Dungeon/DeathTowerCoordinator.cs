using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Dungeon;
using DfoServer.Infrastructure;
using GameDungeon = DfoServer.Game.Dungeon;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    public sealed class DeathTowerCoordinator
    {
        private readonly DeathTowerSettlementService _settlementService;
        private readonly Func<EnhancedClientSession, DeathTowerSettlementResult, Task> _sendExpGrantNotification;
        private readonly Func<EnhancedClientSession, Task> _sendInDungeonLevelUpFollowups;
        private readonly InventoryRefreshSender _inventoryRefresh;
        private readonly GameDungeon.DungeonInstanceRegistry _instanceRegistry;
        private readonly DungeonTownReturnCoordinator _townReturn;
        private readonly ISessionDirectory _sessionDirectory;
        private readonly DeathTowerTransientInventoryService _transientInventory =
            new DeathTowerTransientInventoryService();

        private static readonly TimeSpan RankingToRewardDelay =
            TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RewardToEplpDelay =
            TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DefaultReturnDelay =
            TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RetryDelay =
            TimeSpan.FromSeconds(1);

        public DeathTowerCoordinator()
            : this(null, null, null, null, null, null, null, null, null)
        {
        }

        internal DeathTowerCoordinator(
            string connectionString = null,
            DeathTowerExperienceGrantInTransaction grantExperienceInTransaction = null,
            Func<EnhancedClientSession, DeathTowerSettlementResult, Task> sendExpGrantNotification = null,
            AccountExperienceProgressService accountExperience = null,
            Func<EnhancedClientSession, Task> sendInDungeonLevelUpFollowups = null,
            InventoryRefreshSender inventoryRefresh = null,
            GameDungeon.DungeonInstanceRegistry instanceRegistry = null,
            DungeonTownReturnCoordinator townReturn = null,
            ISessionDirectory sessionDirectory = null)
        {
            _sendExpGrantNotification = sendExpGrantNotification;
            _sendInDungeonLevelUpFollowups = sendInDungeonLevelUpFollowups;
            _inventoryRefresh = inventoryRefresh;
            _instanceRegistry = instanceRegistry;
            _townReturn = townReturn;
            _sessionDirectory = sessionDirectory;
            if (!string.IsNullOrWhiteSpace(connectionString))
                _settlementService = new DeathTowerSettlementService(
                    connectionString,
                    accountExperience,
                    grantExperienceInTransaction);
        }

        public bool TryCreateSession(int dungeonId, out DeathTowerSession tower)
        {
            tower = null;
            var config = DeathTowerData.GetConfig(dungeonId);
            if (config == null)
                return false;

            tower = new DeathTowerSession(config);
            return true;
        }

        public async Task SendEntryPacketsAsync(EnhancedClientSession session, DeathTowerSession tower, byte difficulty = 0)
        {
            var dungeonId = tower.Config.DungeonId;
            var hasRun = session.Player.CurrentRun != null;
            FileLogger.Log($"[DeathTower] ENTER: cid={session.Player.CharacterId} dungeon={dungeonId} difficulty={difficulty} hasRun={hasRun} stages={tower.Config.TotalStages} basisLv={tower.Config.BasisLevel}");

            // NOTI 142 DEATH_TOWER_INFO (8B)
            var infoBody = DeathTowerPacketBuilder.BuildTowerInfo(dungeonId, (ushort)tower.Config.TotalStages);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x008E, infoBody));
            FileLogger.Log($"[DeathTower] SENT 0x008E TOWER_INFO: bodyLen={infoBody.Length}");

            // NOTI 143 首层
            await SendStageMap(session, tower);

            // NOTI 0x1E FINISH_LOADING
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, new byte[0]));
            FileLogger.Log($"[DeathTower] SENT 0x001E FINISH_LOADING (entry)");
        }

        public async Task<bool> TryHandleMoveMap(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            var tower = run?.Tower;
            if (run == null || tower == null)
                return false;
            var runIdentity = run.CaptureIdentity();

            var prevState = tower.State;
            if (prevState >= 1)
                await SyncCurrentStageClearMapAsync(session, tower, "tower_move_map");

            if (!IsCurrentTowerRun(session, run, runIdentity, tower))
                return true;

            if (!tower.TryAdvanceStage())
            {
                FileLogger.Log($"[DeathTower] MOVE_MAP rejected: cid={session.Player.CharacterId} stage={tower.CurrentStage}/{tower.EndStage} state={tower.State} (need state>=1, not last stage)");
                return true;
            }

            if (prevState == 1)
                FileLogger.Log($"[DeathTower] MOVE_MAP advance from state=1 (0x009F(2) not received, 86JP may skip it)");

            FileLogger.Log($"[DeathTower] ADVANCE: cid={session.Player.CharacterId} stage={tower.CurrentStage}/{tower.EndStage} map={tower.GetCurrentMapId()}");

            var stage = tower.CurrentStage;
            if (!tower.TryDeferStageLoadingRelease(runIdentity, stage))
            {
                FileLogger.Log(
                    $"[DeathTower] MOVE_MAP release defer rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"run={runIdentity.RunId}/{runIdentity.RunGeneration} " +
                    $"stage={stage}");
                return true;
            }

            try
            {
                await SendStageMap(session, tower);
            }
            catch
            {
                tower.TryCancelStageLoadingRelease(runIdentity, stage);
                throw;
            }

            FileLogger.Log(
                $"[DeathTower] DEFER FINISH_LOADING release: " +
                $"cid={session.Player.CharacterId} " +
                $"run={runIdentity.RunId}/{runIdentity.RunGeneration} " +
                $"stage={stage} wait=CMD_FINISH_LOADING");

            return true;
        }

        internal async Task HandleTowerCharacterDeathAsync(
            EnhancedClientSession session,
            GameDungeon.DungeonRunIdentity expectedRun)
        {
            var run = session?.Player?.CurrentRun;
            var tower = run?.Tower;
            if (run == null
                || tower == null
                || !expectedRun.IsValid
                || !IsCurrentTowerRun(session, run, expectedRun, tower))
            {
                FileLogger.Log(
                    $"[DeathTower] PLAYER_DEATH ignored: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"run={expectedRun.RunId}/{expectedRun.RunGeneration}");
                return;
            }

            DungeonRunLifecycle.CancelDeathRespawn(run);
            FileLogger.Log(
                $"[DeathTower] PLAYER_DEATH -> settlement: " +
                $"cid={session.Player.CharacterId} " +
                $"run={expectedRun.RunId}/{expectedRun.RunGeneration} " +
                $"stage={tower.CurrentStage}/{tower.EndStage}");
            await SendSettlement(session, tower, "player-death");
        }

        public async Task HandleStageCommand(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var tower = session.Player.DeathTowerState;
            if (tower == null)
            {
                FileLogger.Log($"[DeathTower] STAGE_CMD ignored: cid={session.Player?.CharacterId} not in tower");
                return;
            }
            if (body == null || body.Length < 1)
            {
                FileLogger.Log($"[DeathTower] STAGE_CMD ignored: body null or empty");
                return;
            }

            var commandType = body[0];
            switch (commandType)
            {
                case 1:
                    tower.SetFighting();
                    FileLogger.Log($"[DeathTower] STAGE_CMD(1) fight start: cid={session.Player.CharacterId} stage={tower.CurrentStage}");
                    break;
                case 2:
                    tower.SetCleared();
                    FileLogger.Log($"[DeathTower] STAGE_CMD(2) stage clear: cid={session.Player.CharacterId} stage={tower.CurrentStage}/{tower.EndStage} isLast={tower.IsLastStage}");
                    await SyncCurrentStageClearMapAsync(session, tower, "tower_stage_cmd");
                    if (tower.IsLastStage)
                    {
                        await SendSettlement(session, tower, "final-stage");
                        return;
                    }
                    break;
                default:
                    FileLogger.Log($"[DeathTower] STAGE_CMD unknown commandType={commandType}: cid={session.Player.CharacterId} bodyHex={BitConverter.ToString(body)}");
                    break;
            }
        }

        private static Task SyncCurrentStageClearMapAsync(EnhancedClientSession session, DeathTowerSession tower, string source)
        {
            var mapId = tower.GetCurrentMapId();
            return DungeonClearMapQuestSync.SyncAsync(session, 0, mapId, source);
        }

        // 返城时清除塔状�?由生命周期统一清理路径调用; run 置换后本方法只负责日志与提前摘除)
        public static void ClearTowerState(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run?.Tower != null)
            {
                FileLogger.Log($"[DeathTower] CLEAR: cid={session.Player.CharacterId} wasStage={run.Tower.CurrentStage}");
                run.Tower = null;
            }
        }

        private async Task SendSettlement(
            EnhancedClientSession session,
            DeathTowerSession tower,
            string source)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || !ReferenceEquals(run.Tower, tower)
                || _settlementService == null)
            {
                FileLogger.Log(
                    $"[DeathTower] SETTLEMENT rejected: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    "reason=missing_run_or_service");
                return;
            }

            var identity = run.CaptureIdentity();
            await run.Settlement.DeathTowerProjectionGate.WaitAsync();
            try
            {
                if (!IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        run.Settlement.DeathTower))
                {
                    return;
                }

                var runtime = run.Settlement.DeathTower;
                if (runtime == null)
                {
                    var accountId = session.Account?.AccountId ?? 0;
                    if (accountId <= 0)
                    {
                        FileLogger.Log(
                            $"[DeathTower] SETTLEMENT rejected: " +
                            $"cid={session.Player.CharacterId} " +
                            "reason=missing_account");
                        return;
                    }

                    try
                    {
                        var context = new DeathTowerSettlementContext(
                            run.GetSettlementSourceEventId(),
                            session.Player.CharacterId,
                            accountId,
                            session.Player.Level,
                            session.Player.Exp,
                            run.Difficulty);
                        runtime = new DeathTowerSettlementRuntime(
                            _settlementService.Prepare(
                                context,
                                tower,
                                run.CalculateElapsedMilliseconds(
                                    DateTime.UtcNow)));
                        run.Settlement.DeathTower = runtime;
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[DeathTower] SETTLEMENT prepare failed: " +
                            $"cid={session.Player.CharacterId} error={ex}");
                        return;
                    }
                }

                if (runtime.Phase != DeathTowerSettlementPhase.Prepared)
                {
                    FileLogger.Log(
                        $"[DeathTower] SETTLEMENT duplicate ignored: " +
                        $"cid={session.Player.CharacterId} " +
                        $"dungeon={tower.Config.DungeonId} " +
                        $"phase={runtime.Phase}");
                    return;
                }

                try
                {
                    await ProjectRankingAndScheduleRewardAsync(
                        session,
                        run,
                        identity,
                        tower,
                        runtime,
                        source);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DeathTower] ranking projection failed: " +
                        $"cid={session.Player.CharacterId} error={ex}");
                    ScheduleRankingRetry(
                        session,
                        run,
                        identity,
                        tower,
                        runtime,
                        "ranking-projection-failed");
                }
            }
            finally
            {
                run.Settlement.DeathTowerProjectionGate.Release();
            }
        }

        private bool IsCurrentSettlement(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime)
        {
            if (session?.Player == null
                || run == null
                || tower == null
                || !identity.IsValid
                || !ReferenceEquals(session.Player.CurrentRun, run)
                || !session.Player.IsCurrentDungeonRun(identity)
                || !run.Matches(identity)
                || !ReferenceEquals(run.Tower, tower))
            {
                return false;
            }

            if (runtime != null
                && (!ReferenceEquals(run.Settlement.DeathTower, runtime)
                    || runtime.Plan.Context.CharacterId
                        != session.Player.CharacterId))
            {
                return false;
            }

            if (_sessionDirectory != null
                && (!_sessionDirectory.TryGet(
                        session.Player.CharacterId,
                        out var currentSession)
                    || !ReferenceEquals(currentSession, session)))
            {
                return false;
            }

            if (_instanceRegistry != null)
            {
                if (!_instanceRegistry.TryGetForRun(
                        session.Player.CharacterId,
                        identity,
                        out var attachment)
                    || attachment.State
                        != GameDungeon.DungeonParticipantAttachmentState.Active
                    || !ReferenceEquals(attachment.Run, run))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsCurrentTowerRun(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower)
        {
            return session?.Player != null
                && run != null
                && tower != null
                && identity.IsValid
                && ReferenceEquals(session.Player.CurrentRun, run)
                && session.Player.IsCurrentDungeonRun(identity)
                && run.Matches(identity)
                && ReferenceEquals(run.Tower, tower);
        }

        private async Task ProjectRankingAndScheduleRewardAsync(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            string source)
        {
            if (!IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime)
                || runtime.Phase != DeathTowerSettlementPhase.Prepared)
            {
                return;
            }

            var rankingBody = DeathTowerPacketBuilder.BuildRanking(
                runtime.Plan.DungeonId,
                runtime.Plan.ClearedFloorCount,
                runtime.Plan.ClearTimeMilliseconds);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0090,
                rankingBody));
            if (!IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime))
            {
                return;
            }

            var deadlineUtc = DateTime.UtcNow.Add(RankingToRewardDelay);
            if (!runtime.TryMarkRankingShown(deadlineUtc))
                return;

            var ticket = run.Timers.Begin(
                GameDungeon.DungeonRunTimerKeys.DeathTowerRankingToReward,
                deadlineUtc,
                GameDungeon.RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleRankingTimer(
                session,
                run,
                identity,
                tower,
                runtime,
                deadlineUtc,
                ticket);
            FileLogger.Log(
                $"[DeathTower] SENT 0x0090 ranking: " +
                $"cid={session.Player.CharacterId} dungeon={runtime.Plan.DungeonId} " +
                $"source={source} rewardDeadline={deadlineUtc:O}");
        }

        private void ScheduleRankingTimer(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            DateTime deadlineUtc,
            GameDungeon.RunTimerTicket ticket)
        {
            if (!IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime)
                || !run.Timers.IsCurrent(ticket))
            {
                return;
            }

            var handle = ClockService.Instance.ScheduleOneShotAsync(
                BuildTimerName("reward", session, run, ticket),
                deadlineUtc,
                async _ => await OnRankingTimerElapsedAsync(
                    session,
                    run,
                    identity,
                    tower,
                    runtime,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnRankingTimerElapsedAsync(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            GameDungeon.RunTimerTicket ticket)
        {
            await run.Settlement.DeathTowerProjectionGate.WaitAsync();
            try
            {
                if (!IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime)
                    || !run.Timers.IsCurrent(ticket))
                {
                    return;
                }

                if (runtime.Phase == DeathTowerSettlementPhase.Prepared)
                {
                    await ProjectRankingAndScheduleRewardAsync(
                        session,
                        run,
                        identity,
                        tower,
                        runtime,
                        "ranking-retry");
                    return;
                }
                if (runtime.Phase != DeathTowerSettlementPhase.RankingShown)
                {
                    run.Timers.TryComplete(ticket);
                    return;
                }

                var rewardBody = DeathTowerPacketBuilder.BuildReward(
                    (int)Math.Min(runtime.Plan.RewardExp, (uint)int.MaxValue),
                    runtime.Plan.Candidates);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0091,
                    rewardBody));
                if (!IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime))
                {
                    return;
                }

                var deadlineUtc = DateTime.UtcNow.Add(RewardToEplpDelay);
                if (!runtime.TryMarkRewardShown(deadlineUtc))
                    return;

                run.Timers.TryComplete(ticket);
                var nextTicket = run.Timers.Begin(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerRewardToEplp,
                    deadlineUtc,
                    GameDungeon.RunTimerDetachPolicy.SuspendUntilResume);
                ScheduleEplpTimer(
                    session,
                    run,
                    identity,
                    tower,
                    runtime,
                    deadlineUtc,
                    nextTicket);
                FileLogger.Log(
                    $"[DeathTower] SENT 0x0091 reward: " +
                    $"cid={session.Player.CharacterId} exp={runtime.Plan.RewardExp} " +
                    $"candidates={runtime.Plan.Candidates.Count} " +
                    $"eplpDeadline={deadlineUtc:O}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DeathTower] reward phase failed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} error={ex}");
                if (IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime))
                {
                    if (runtime.Phase
                        == DeathTowerSettlementPhase.RewardShown)
                    {
                        ScheduleEplpRetry(
                            session,
                            run,
                            identity,
                            tower,
                            runtime,
                            "reward-timer-failed");
                    }
                    else
                    {
                        ScheduleRankingRetry(
                            session,
                            run,
                            identity,
                            tower,
                            runtime,
                            "reward-phase-failed");
                    }
                }
            }
            finally
            {
                run.Settlement.DeathTowerProjectionGate.Release();
            }
        }

        private void ScheduleRankingRetry(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            string source)
        {
            if (!IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime)
                || (runtime.Phase != DeathTowerSettlementPhase.Prepared
                    && runtime.Phase
                        != DeathTowerSettlementPhase.RankingShown))
            {
                return;
            }

            var deadlineUtc = DateTime.UtcNow.Add(RetryDelay);
            var ticket = run.Timers.Begin(
                GameDungeon.DungeonRunTimerKeys.DeathTowerRankingToReward,
                deadlineUtc,
                GameDungeon.RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleRankingTimer(
                session,
                run,
                identity,
                tower,
                runtime,
                deadlineUtc,
                ticket);
            FileLogger.Log(
                $"[DeathTower] ranking/reward retry scheduled: " +
                $"cid={session.Player.CharacterId} source={source} " +
                $"deadline={deadlineUtc:O}");
        }

        private void ScheduleEplpTimer(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            DateTime deadlineUtc,
            GameDungeon.RunTimerTicket ticket)
        {
            if (!IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime)
                || !run.Timers.IsCurrent(ticket))
            {
                return;
            }

            var handle = ClockService.Instance.ScheduleOneShotAsync(
                BuildTimerName("eplp", session, run, ticket),
                deadlineUtc,
                async _ => await OnEplpTimerElapsedAsync(
                    session,
                    run,
                    identity,
                    tower,
                    runtime,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnEplpTimerElapsedAsync(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            GameDungeon.RunTimerTicket ticket)
        {
            await run.Settlement.DeathTowerProjectionGate.WaitAsync();
            try
            {
                if (!IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime)
                    || !run.Timers.IsCurrent(ticket))
                {
                    return;
                }

                if (runtime.Phase == DeathTowerSettlementPhase.RewardShown)
                {
                    var frozenState = runtime.AllMembersHaveEplpItem;
                    if (!frozenState.HasValue)
                    {
                        var allMembersHaveRequiredItem =
                            ResolveAllActiveMembersHaveEplpItem(session, run);
                        if (!runtime.TryFreezeEplpState(
                                allMembersHaveRequiredItem))
                        {
                            return;
                        }
                        frozenState = runtime.AllMembersHaveEplpItem;
                    }

                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x0092,
                        DeathTowerPacketBuilder.BuildEplp(
                            frozenState ?? false)));
                    if (!IsCurrentSettlement(
                            session,
                            run,
                            identity,
                            tower,
                            runtime)
                        || !runtime.TryMarkEplpShown())
                    {
                        return;
                    }

                    FileLogger.Log(
                        $"[DeathTower] SENT 0x0092 EPLP: " +
                        $"cid={session.Player.CharacterId} " +
                        $"allActiveMembersHave4183={frozenState ?? false}");
                }

                run.Timers.TryComplete(ticket);
                await CommitAndProjectAsync(
                    session,
                    run,
                    identity,
                    tower,
                    runtime);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DeathTower] EPLP/commit phase failed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} error={ex}");
                if (IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime))
                {
                    ScheduleEplpRetry(
                        session,
                        run,
                        identity,
                        tower,
                        runtime,
                        "eplp-or-commit-failed");
                }
            }
            finally
            {
                run.Settlement.DeathTowerProjectionGate.Release();
            }
        }

        private void ScheduleEplpRetry(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            string source)
        {
            var phase = runtime?.Phase
                ?? DeathTowerSettlementPhase.Prepared;
            if (!IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime)
                || (phase != DeathTowerSettlementPhase.RewardShown
                    && phase != DeathTowerSettlementPhase.EplpShown
                    && phase != DeathTowerSettlementPhase.Committed
                    && phase != DeathTowerSettlementPhase.Ending))
            {
                return;
            }

            var deadlineUtc = DateTime.UtcNow.Add(RetryDelay);
            var ticket = run.Timers.Begin(
                GameDungeon.DungeonRunTimerKeys.DeathTowerRewardToEplp,
                deadlineUtc,
                GameDungeon.RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleEplpTimer(
                session,
                run,
                identity,
                tower,
                runtime,
                deadlineUtc,
                ticket);
            FileLogger.Log(
                $"[DeathTower] EPLP/commit retry scheduled: " +
                $"cid={session.Player.CharacterId} source={source} " +
                $"phase={phase} deadline={deadlineUtc:O}");
        }

        private async Task CommitAndProjectAsync(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime)
        {
            if (!IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime))
            {
                return;
            }

            if (runtime.Phase == DeathTowerSettlementPhase.Committing)
                runtime.TryAbortCommit();

            if (runtime.Phase == DeathTowerSettlementPhase.EplpShown)
            {
                if (!runtime.TryBeginCommit())
                    return;

                DeathTowerSettlementResult result;
                try
                {
                    if (!TryGetOwnedInventory(session, out var lease))
                    {
                        throw new InvalidOperationException(
                            "Death tower settlement has no current owned inventory lease.");
                    }
                    result = _settlementService.Commit(
                        runtime.Plan,
                        lease,
                        session.SessionId);
                }
                catch
                {
                    runtime.TryAbortCommit();
                    throw;
                }

                if (!runtime.TryCompleteCommit(result))
                {
                    throw new InvalidOperationException(
                        "Death tower settlement commit checkpoint was lost.");
                }
                FileLogger.Log(
                    $"[DeathTower] reward committed: " +
                    $"cid={session.Player.CharacterId} " +
                    $"floors={result.ClearedFloorCount} exp={result.ExpGained} " +
                    $"gold={result.GoldGained} items={result.Items.Count}");
            }

            if (!runtime.IsCommitted)
                return;

            await ProjectCommittedEffectsAsync(
                session,
                run,
                identity,
                tower,
                runtime);
            ScheduleDefaultReturn(
                session,
                run,
                identity,
                tower,
                runtime,
                "reward-committed");
        }

        private async Task ProjectCommittedEffectsAsync(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime)
        {
            var result = runtime.CommitResult;
            if (result == null
                || !IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime))
            {
                return;
            }

            var grant = result.ExperienceGrant;
            if (grant != null)
            {
                session.Player.Level = grant.NewLevel;
                session.Player.Exp = grant.NewExp;
            }

            if (!runtime.ExperienceProjectionSent)
            {
                if (_sendExpGrantNotification != null)
                    await _sendExpGrantNotification(session, result);
                runtime.TryMarkExperienceProjectionSent();
            }

            if (grant?.LeveledUp == true
                && !runtime.LevelUpFollowupsSent)
            {
                if (_sendInDungeonLevelUpFollowups != null)
                    await _sendInDungeonLevelUpFollowups(session);
                runtime.TryMarkLevelUpFollowupsSent();
            }

            if (!runtime.InventoryProjectionSent)
            {
                if (_inventoryRefresh != null
                    && result.ChangedMainSlots != null
                    && result.ChangedMainSlots.Count > 0)
                {
                    await _inventoryRefresh.SendUpdateItemList(
                        session,
                        InventoryListType.Main,
                        result.ChangedMainSlots);
                }
                runtime.TryMarkInventoryProjectionSent();
            }
        }

        private bool ResolveAllActiveMembersHaveEplpItem(
            EnhancedClientSession currentSession,
            GameDungeon.DungeonRun currentRun)
        {
            if (_instanceRegistry == null || _sessionDirectory == null)
                return SessionHasEplpItem(currentSession);

            var roster = _instanceRegistry.CaptureInstanceParticipantRoster(
                currentRun.Instance.Identity);
            var validMemberCount = 0;
            foreach (var member in roster)
            {
                if (!_instanceRegistry.TryGetForRun(
                        member.CharacterId,
                        member.RunIdentity,
                        out var attachment)
                    || attachment.State
                        != GameDungeon.DungeonParticipantAttachmentState.Active
                    || !_sessionDirectory.TryGet(
                        member.CharacterId,
                        out var memberSession)
                    || memberSession?.Player == null
                    || !ReferenceEquals(
                        memberSession.Player.CurrentRun,
                        member.Run)
                    || !memberSession.Player.IsCurrentDungeonRun(
                        member.RunIdentity)
                    || !member.Run.Instance.Identity.Equals(
                        currentRun.Instance.Identity))
                {
                    continue;
                }

                validMemberCount++;
                if (!SessionHasEplpItem(memberSession))
                    return false;
            }

            return validMemberCount > 0;
        }

        private static bool SessionHasEplpItem(
            EnhancedClientSession session)
        {
            if (!TryGetOwnedInventory(session, out var lease))
                return false;
            lock (lease.SyncRoot)
                return lease.Inventory.CountMainItem(4183) > 0;
        }

        private void ScheduleDefaultReturn(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            string source)
        {
            if (!runtime.IsCommitted
                || !IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime))
            {
                return;
            }
            if (run.Timers.TryGetCurrentTicket(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerReturnToTown,
                    out _))
            {
                return;
            }
            if (run.Timers.TryGetSnapshot(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerReturnToTown,
                    out var snapshot)
                && snapshot.IsSuspended
                && snapshot.HasDeadline)
            {
                return;
            }

            var deadlineUtc = runtime.ReturnDeadlineUtc != DateTime.MinValue
                ? runtime.ReturnDeadlineUtc
                : DateTime.UtcNow.Add(DefaultReturnDelay);
            ScheduleReturn(
                session,
                run,
                identity,
                tower,
                runtime,
                deadlineUtc,
                source);
        }

        private void ScheduleReturn(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            DateTime deadlineUtc,
            string source)
        {
            if (!IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime)
                || !runtime.TryScheduleReturn(deadlineUtc))
            {
                return;
            }

            var ticket = run.Timers.Begin(
                GameDungeon.DungeonRunTimerKeys.DeathTowerReturnToTown,
                deadlineUtc,
                GameDungeon.RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleReturnTimer(
                session,
                run,
                identity,
                tower,
                runtime,
                deadlineUtc,
                ticket);
            FileLogger.Log(
                $"[DeathTower] return scheduled: " +
                $"cid={session.Player.CharacterId} source={source} " +
                $"deadline={deadlineUtc:O}");
        }

        private void ScheduleReturnTimer(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            DateTime deadlineUtc,
            GameDungeon.RunTimerTicket ticket)
        {
            if (!IsCurrentSettlement(
                    session,
                    run,
                    identity,
                    tower,
                    runtime)
                || !run.Timers.IsCurrent(ticket))
            {
                return;
            }

            var handle = ClockService.Instance.ScheduleOneShotAsync(
                BuildTimerName("return", session, run, ticket),
                deadlineUtc,
                async _ => await OnReturnTimerElapsedAsync(
                    session,
                    run,
                    identity,
                    tower,
                    runtime,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnReturnTimerElapsedAsync(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime,
            GameDungeon.RunTimerTicket ticket)
        {
            await run.Settlement.DeathTowerProjectionGate.WaitAsync();
            try
            {
                if (!IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime)
                    || !run.Timers.IsCurrent(ticket))
                {
                    return;
                }

                if (!runtime.IsCommitted)
                {
                    ScheduleReturn(
                        session,
                        run,
                        identity,
                        tower,
                        runtime,
                        DateTime.UtcNow.Add(RetryDelay),
                        "awaiting-commit");
                    return;
                }

                await ProjectCommittedEffectsAsync(
                    session,
                    run,
                    identity,
                    tower,
                    runtime);
                runtime.TryScheduleReturn(DateTime.UtcNow);
                run.Timers.TryComplete(ticket);
                if (_townReturn == null)
                {
                    FileLogger.Log(
                        $"[DeathTower] return skipped: " +
                        $"cid={session.Player.CharacterId} reason=no_coordinator");
                    return;
                }

                var returned = await _townReturn.ReturnAsync(
                    session,
                    identity,
                    GameDungeon.DungeonRunEndReason.ReturnToTown);
                if (!returned
                    && IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime))
                {
                    ScheduleReturn(
                        session,
                        run,
                        identity,
                        tower,
                        runtime,
                        DateTime.UtcNow.Add(RetryDelay),
                        "return-rejected");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DeathTower] return failed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} error={ex}");
                if (IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime))
                {
                    ScheduleReturn(
                        session,
                        run,
                        identity,
                        tower,
                        runtime,
                        DateTime.UtcNow.Add(RetryDelay),
                        "return-failed");
                }
            }
            finally
            {
                run.Settlement.DeathTowerProjectionGate.Release();
            }
        }

        internal async Task<bool> TryHandleEplpCommandAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var tower = run?.Tower;
            if (run == null || tower == null)
                return false;

            if (!DeathTowerEplpCommandParser.TryParse(
                    body,
                    out var command,
                    out var error))
            {
                FileLogger.Log(
                    $"[DeathTower] CMD 0x0048 rejected: " +
                    $"cid={session.Player.CharacterId} reason={error} " +
                    $"body={BitConverter.ToString(body ?? Array.Empty<byte>())}");
                return true;
            }

            var identity = run.CaptureIdentity();
            await run.Settlement.DeathTowerProjectionGate.WaitAsync();
            try
            {
                var runtime = run.Settlement.DeathTower;
                if (!IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime)
                    || runtime == null
                    || runtime.Phase < DeathTowerSettlementPhase.EplpShown)
                {
                    FileLogger.Log(
                        $"[DeathTower] CMD 0x0048 ignored before EPLP: " +
                        $"cid={session.Player.CharacterId} " +
                        $"phase={runtime?.Phase.ToString() ?? "none"}");
                    return true;
                }

                DeathTowerEplpCommandRules.TryResolveReturnDelay(
                    command,
                    out var delay,
                    out var keepSelection);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    DeathTowerPacketBuilder.BuildEplpCommandAck(
                        command.State,
                        command.Option)));
                if (!keepSelection)
                {
                    ScheduleReturn(
                        session,
                        run,
                        identity,
                        tower,
                        runtime,
                        DateTime.UtcNow.Add(delay),
                        "client-option-" + command.Option);
                }

                FileLogger.Log(
                    $"[DeathTower] CMD 0x0048 handled: " +
                    $"cid={session.Player.CharacterId} state={command.State} " +
                    $"option={command.Option} keep={keepSelection} " +
                    $"delayMs={delay.TotalMilliseconds:0}");
                return true;
            }
            finally
            {
                run.Settlement.DeathTowerProjectionGate.Release();
            }
        }

        internal async Task<bool> RecoverSettlementAsync(
            EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            var tower = run?.Tower;
            var runtime = run?.Settlement?.DeathTower;
            if (run == null || tower == null || runtime == null)
                return false;

            var identity = run.CaptureIdentity();
            await run.Settlement.DeathTowerProjectionGate.WaitAsync();
            try
            {
                if (!IsCurrentSettlement(
                        session,
                        run,
                        identity,
                        tower,
                        runtime))
                {
                    return false;
                }

                switch (runtime.Phase)
                {
                    case DeathTowerSettlementPhase.Prepared:
                        await ProjectRankingAndScheduleRewardAsync(
                            session,
                            run,
                            identity,
                            tower,
                            runtime,
                            "rejoin-prepared");
                        break;

                    case DeathTowerSettlementPhase.RankingShown:
                        await ReplayRankingAsync(session, runtime);
                        ResumeRankingTimer(
                            session,
                            run,
                            identity,
                            tower,
                            runtime);
                        break;

                    case DeathTowerSettlementPhase.RewardShown:
                        await ReplayRankingAndRewardAsync(session, runtime);
                        ResumeEplpTimer(
                            session,
                            run,
                            identity,
                            tower,
                            runtime);
                        break;

                    case DeathTowerSettlementPhase.EplpShown:
                    case DeathTowerSettlementPhase.Committing:
                        await ReplayFullSettlementAsync(session, runtime);
                        await CommitAndProjectAsync(
                            session,
                            run,
                            identity,
                            tower,
                            runtime);
                        ResumeReturnTimer(
                            session,
                            run,
                            identity,
                            tower,
                            runtime);
                        break;

                    case DeathTowerSettlementPhase.Committed:
                    case DeathTowerSettlementPhase.Ending:
                        await ReplayFullSettlementAsync(session, runtime);
                        await ProjectCommittedEffectsAsync(
                            session,
                            run,
                            identity,
                            tower,
                            runtime);
                        ResumeReturnTimer(
                            session,
                            run,
                            identity,
                            tower,
                            runtime);
                        break;
                }

                FileLogger.Log(
                    $"[DeathTower] settlement recovered: " +
                    $"cid={session.Player.CharacterId} phase={runtime.Phase} " +
                    $"run={run.RunId}/{run.RunGeneration}");
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DeathTower] settlement recovery failed: " +
                    $"cid={session?.Player?.CharacterId ?? 0} error={ex}");
                return true;
            }
            finally
            {
                run.Settlement.DeathTowerProjectionGate.Release();
            }
        }

        private static Task ReplayRankingAsync(
            EnhancedClientSession session,
            DeathTowerSettlementRuntime runtime)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0090,
                DeathTowerPacketBuilder.BuildRanking(
                    runtime.Plan.DungeonId,
                    runtime.Plan.ClearedFloorCount,
                    runtime.Plan.ClearTimeMilliseconds)));

        private static async Task ReplayRankingAndRewardAsync(
            EnhancedClientSession session,
            DeathTowerSettlementRuntime runtime)
        {
            await ReplayRankingAsync(session, runtime);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0091,
                DeathTowerPacketBuilder.BuildReward(
                    (int)Math.Min(
                        runtime.Plan.RewardExp,
                        (uint)int.MaxValue),
                    runtime.Plan.Candidates)));
        }

        private static async Task ReplayFullSettlementAsync(
            EnhancedClientSession session,
            DeathTowerSettlementRuntime runtime)
        {
            await ReplayRankingAndRewardAsync(session, runtime);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0092,
                DeathTowerPacketBuilder.BuildEplp(
                    runtime.AllMembersHaveEplpItem ?? false)));
        }

        private void ResumeRankingTimer(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime)
        {
            if (!run.Timers.TryResume(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerRankingToReward,
                    out var ticket,
                    out var deadlineUtc))
            {
                deadlineUtc = runtime.RewardDeadlineUtc == DateTime.MinValue
                    ? DateTime.UtcNow
                    : runtime.RewardDeadlineUtc;
                ticket = run.Timers.Begin(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerRankingToReward,
                    deadlineUtc,
                    GameDungeon.RunTimerDetachPolicy.SuspendUntilResume);
            }

            ScheduleRankingTimer(
                session,
                run,
                identity,
                tower,
                runtime,
                deadlineUtc,
                ticket);
        }

        private void ResumeEplpTimer(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime)
        {
            if (!run.Timers.TryResume(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerRewardToEplp,
                    out var ticket,
                    out var deadlineUtc))
            {
                deadlineUtc = runtime.EplpDeadlineUtc == DateTime.MinValue
                    ? DateTime.UtcNow
                    : runtime.EplpDeadlineUtc;
                ticket = run.Timers.Begin(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerRewardToEplp,
                    deadlineUtc,
                    GameDungeon.RunTimerDetachPolicy.SuspendUntilResume);
            }

            ScheduleEplpTimer(
                session,
                run,
                identity,
                tower,
                runtime,
                deadlineUtc,
                ticket);
        }

        private void ResumeReturnTimer(
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.DungeonRunIdentity identity,
            DeathTowerSession tower,
            DeathTowerSettlementRuntime runtime)
        {
            if (run.Timers.TryGetCurrentTicket(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerReturnToTown,
                    out _))
            {
                return;
            }

            if (!run.Timers.TryResume(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerReturnToTown,
                    out var ticket,
                    out var deadlineUtc))
            {
                if (runtime.ReturnDeadlineUtc == DateTime.MinValue)
                {
                    ScheduleDefaultReturn(
                        session,
                        run,
                        identity,
                        tower,
                        runtime,
                        "rejoin-default");
                    return;
                }

                deadlineUtc = runtime.ReturnDeadlineUtc;
                ticket = run.Timers.Begin(
                    GameDungeon.DungeonRunTimerKeys.DeathTowerReturnToTown,
                    deadlineUtc,
                    GameDungeon.RunTimerDetachPolicy.SuspendUntilResume);
            }

            runtime.TryScheduleReturn(deadlineUtc);
            ScheduleReturnTimer(
                session,
                run,
                identity,
                tower,
                runtime,
                deadlineUtc,
                ticket);
        }

        private static string BuildTimerName(
            string phase,
            EnhancedClientSession session,
            GameDungeon.DungeonRun run,
            GameDungeon.RunTimerTicket ticket)
            => "death-tower-" + phase
                + ":" + session.SessionId.ToString("N")
                + ":" + run.RunId
                + ":" + ticket.Generation;

        private async Task SendStageMap(EnhancedClientSession session, DeathTowerSession tower)
        {
            var mapId = tower.GetCurrentMapId();
            var monsters = DeathTowerMapLoader.LoadStageMonsters(tower);
            if (monsters.Count > byte.MaxValue)
            {
                FileLogger.Log($"[DeathTower] Stage monster list truncated to {byte.MaxValue}: stage={tower.CurrentStage} map={mapId} count={monsters.Count}");
                monsters.RemoveRange(byte.MaxValue, monsters.Count - byte.MaxValue);
            }
            if (monsters.Count == 0)
                FileLogger.Log($"[DeathTower] WARNING: stage={tower.CurrentStage} map={mapId} loaded 0 monsters (map may have only [apc random point] or PVF read failed)");

            var items = DeathTowerMapLoader.LoadStageItems(tower, monsters);
            var stageSeed = (uint)Infrastructure.ServerRandom.Next();
            tower.BeginStage(stageSeed, items);
            SyncCombatStage(session, tower, monsters);
            var body = DeathTowerPacketBuilder.BuildStageMap(tower, monsters, items, stageSeed);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x008F, body));
            FileLogger.Log($"[DeathTower] SENT 0x008F STAGE_MAP: stage={tower.CurrentStage} map={mapId} monsters={monsters.Count} items={items.Count} seed={stageSeed} bodyLen={body.Length}");
        }

        private static bool TryGetOwnedInventory(
            EnhancedClientSession session,
            out InventoryLease lease)
        {
            lease = null;
            var characterId = session?.Player?.CharacterId ?? 0;
            return characterId > 0
                && InventoryContext.TryGetLease(characterId, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

        internal static void SyncCombatStage(
            EnhancedClientSession session,
            DeathTowerSession tower,
            IReadOnlyList<StageMonster> monsters)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !ReferenceEquals(run.Tower, tower))
                return;

            var combatMonsters = new List<DfoServer.GameWorld.Dungeon.MonsterSumInfo>(monsters.Count);
            foreach (var monster in monsters)
            {
                combatMonsters.Add(new DfoServer.GameWorld.Dungeon.MonsterSumInfo
                {
                    Code = monster.MonsterIndex,
                    Level = monster.MonsterLevel,
                    Type = monster.MonsterType,
                    IsBlocking = monster.IsBoxMonster == 0,
                    TemplateOrder = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, monster.ListIndex)),
                    PacketIndex = monster.MonsterUniqueId,
                });
            }

            var stageNumber = tower.CurrentStage + 1;
            var mapId = tower.GetCurrentMapId();
            var roomKey = new GameDungeon.RoomKey(
                stageNumber,
                0,
                mapId);
            var stageMaze = new DungeonData.MazeSumInfo
            {
                Index = mapId,
                X = stageNumber,
                Y = 0,
                Monsters = combatMonsters,
            };
            var firstSequence = monsters.Count > 0
                ? monsters[0].MonsterUniqueId
                : (ushort)0;
            var instanceRoom = run.Instance.GetOrCreateRoom(
                roomKey,
                roomInstanceId => new GameDungeon.DungeonInstanceRoom(
                    roomInstanceId,
                    roomKey,
                    stageMaze,
                    tower.StageSeed,
                    firstSequence),
                out var roomCreated);

            lock (run.SyncRoot)
            {
                if (!ReferenceEquals(run.Tower, tower))
                    return;

                var killedSequenceIds = new HashSet<ushort>();
                var roomState = new GameDungeon.RoomState
                {
                    InstanceRoom = instanceRoom,
                    Maze = stageMaze,
                    FirstSeqId = firstSequence,
                    MonsterCount = (ushort)Math.Min(
                        ushort.MaxValue,
                        combatMonsters.Count),
                    KilledSeqIds = killedSequenceIds,
                    Seed = tower.StageSeed,
                    Lcg = tower.StageLcg,
                };
                roomState.TryActivate();

                run.RoomKilledSeqIds = killedSequenceIds;
                run.Drops.Clear();
                run.RoomMonsters = combatMonsters;
                run.MonsterCount = roomState.MonsterCount;
                run.RoomStartSequence = firstSequence;
                run.Seed = tower.StageSeed;
                run.RoomLcg = tower.StageLcg;
                run.RoomKey = roomKey;
                run.RoomStates[roomKey] = roomState;
                run.SetCurrentRoom(instanceRoom);
            }

            FileLogger.Log(
                $"[DeathTower] STAGE_ROOM: dungeon={tower.Config.DungeonId} " +
                $"stage={stageNumber} map={mapId} instance={run.PartyDungeonInstanceId} " +
                $"room={instanceRoom.RoomInstanceId} created={roomCreated} " +
                $"firstSeq={firstSequence} actors={combatMonsters.Count}");
        }

        public bool TryGenerateDropsForMonster(
            EnhancedClientSession session,
            ushort monsterUniqueId,
            out IReadOnlyList<GameDungeon.DropInfo> drops)
        {
            var tower = session?.Player?.DeathTowerState;
            if (tower == null)
            {
                drops = Array.Empty<GameDungeon.DropInfo>();
                return false;
            }

            drops = tower.GenerateDropsForMonster(monsterUniqueId);

            FileLogger.Log($"[DeathTower] DIE_MONSTER: cid={session.Player.CharacterId} stage={tower.CurrentStage} monsterUid={monsterUniqueId} drops={drops.Count} ground={tower.GroundItems.Count}");
            return true;
        }

        public async Task<bool> TryHandleGetItem(EnhancedClientSession session, ushort sceneSlot)
        {
            if (!TryCaptureTowerCommandContext(
                    session,
                    out var runIdentity,
                    out var tower))
                return false;

            if (!TryGetOwnedInventoryLease(session, out var lease)
                || !IsCurrentTowerCommandContext(
                    session,
                    runIdentity,
                    tower,
                    lease))
            {
                FileLogger.Log(
                    $"[DeathTower] GET_ITEM rejected: missing inventory lease " +
                    $"cid={session?.Player?.CharacterId ?? 0}");
                return true;
            }

            if (!_transientInventory.TryPickup(
                    tower,
                    lease,
                    sceneSlot,
                    out var pickup))
            {
                FileLogger.Log($"[DeathTower] GET_ITEM rejected: cid={session.Player.CharacterId} sceneSlot={sceneSlot} ground={tower.GroundItems.Count} inventory={tower.InventoryItems.Count}");
                return true;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.GET_ITEM,
                DropItemBuilder.BuildPickupItem(
                    sceneSlot,
                    session.Player.UserId,
                    (ushort)pickup.DestinationSlot,
                    7)));
            await SendInventorySnapshotAsync(session, tower, lease);
            RecalibrateTowerQuestProgress(session, tower, pickup.ItemId);
            FileLogger.Log($"[DeathTower] GET_ITEM: cid={session.Player.CharacterId} sceneSlot={sceneSlot} item={pickup.ItemId} towerSlot={pickup.DestinationSlot}");
            return true;
        }

        public async Task<bool> TryHandleUseStackable(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryCaptureTowerCommandContext(
                    session,
                    out var runIdentity,
                    out var tower))
                return false;
            if (!DeathTowerInventoryCommandParser.TryParseUseStackable(
                    body,
                    out var command))
                return true;

            if (!TryGetOwnedInventoryLease(session, out var lease)
                || !IsCurrentTowerCommandContext(
                    session,
                    runIdentity,
                    tower,
                    lease))
                return true;

            var expectedItemId = command.ExpectedItemId;
            if (expectedItemId <= 0)
            {
                lock (lease.SyncRoot)
                {
                    if (tower.TryGetInventoryItem(
                            new DeathTowerInventoryEndpoint(
                                command.ListType,
                                command.SlotIndex),
                            out var authoritativeItem))
                    {
                        expectedItemId = authoritativeItem.ItemId;
                    }
                }
            }

            var success = _transientInventory.TryUseStackable(
                tower,
                lease,
                command.ListType,
                command.SlotIndex,
                expectedItemId,
                out var handled,
                out var mutation);
            if (!handled)
                return false;

            if (!success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x002C,
                    UseStackableAckBuilder.BuildError(
                        (byte)command.ListType,
                        command.InstanceValue,
                        expectedItemId)));
                await SendInventorySnapshotAsync(session, tower, lease);
                FileLogger.Log($"[DeathTower] USE_STACKABLE rejected: cid={session.Player.CharacterId} list={command.ListType} slot={command.SlotIndex} item={expectedItemId}");
                return true;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x002C,
                UseStackableAckBuilder.BuildSuccess(
                    command.SlotIndex,
                    (byte)command.ListType,
                    command.InstanceValue,
                    expectedItemId)));
            await SendInventorySnapshotAsync(session, tower, lease);
            RecalibrateTowerQuestProgress(session, tower, mutation.ItemId);
            FileLogger.Log($"[DeathTower] USE_STACKABLE: cid={session.Player.CharacterId} slot={command.SlotIndex} item={expectedItemId} remaining={mutation.RemainingCount}");
            return true;
        }

        public async Task<bool> TryHandleMoveItem(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryCaptureTowerCommandContext(
                    session,
                    out var runIdentity,
                    out var tower))
                return false;
            if (!DeathTowerInventoryCommandParser.TryParseMove(
                    body,
                    out var command))
                return true;

            if (!TryGetOwnedInventoryLease(session, out var lease)
                || !IsCurrentTowerCommandContext(
                    session,
                    runIdentity,
                    tower,
                    lease))
                return true;

            var success = _transientInventory.TryMove(
                tower,
                lease,
                command,
                session.Player.Job,
                session.Player.GrowType,
                out var handled,
                out var move);
            if (!handled)
                return false;

            if (!success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0013,
                    MoveItemSpaceAckBuilder.BuildError(
                        move.ErrorCode,
                        (byte)command.SourceListType,
                        (byte)command.DestinationListType)));
                await SendInventorySnapshotAsync(session, tower, lease);
                FileLogger.Log(
                    $"[DeathTower] MOVE_ITEMSPACE rejected: "
                    + $"cid={session.Player.CharacterId} "
                    + $"src={command.SourceListType}:{command.SourceSlotIndex} "
                    + $"dst={command.DestinationListType}:{command.DestinationSlotIndex} "
                    + $"count={command.MoveCount} "
                    + $"identity={move.IdentityResolved}");
                return true;
            }

            var ackResult = new InventoryMoveResult
            {
                SourceListType = command.SourceListType,
                SourceSlotIndex = command.SourceSlotIndex,
                MoveValue32 = move.MoveValue32,
                DestinationListType = command.DestinationListType,
                DestinationSlotIndex = command.DestinationSlotIndex,
                Mutated = move.ChangedSlots.Count > 0,
            };
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0013,
                DeathTowerInventoryProjectionBuilder.BuildMoveAckBody(ackResult)));
            await SendInventorySnapshotAsync(session, tower, lease);
            return true;
        }

        public async Task<bool> TryHandleSortItem(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryCaptureTowerCommandContext(
                    session,
                    out var runIdentity,
                    out var tower))
                return false;

            if (!DeathTowerInventoryCommandParser.TryParseSort(
                    body,
                    out var command))
            {
                FileLogger.Log(
                    $"[DeathTower] SORT rejected: invalid body length "
                    + $"cid={session?.Player?.CharacterId ?? 0}");
                return true;
            }
            if (!TryGetOwnedInventoryLease(session, out var lease)
                || !IsCurrentTowerCommandContext(
                    session,
                    runIdentity,
                    tower,
                    lease))
                return true;

            var success = _transientInventory.TrySort(
                tower,
                lease,
                command.ListType,
                command.Category,
                command.HasCategory,
                out var handled,
                out var result);
            if (!handled)
                return false;
            if (!success || !result.Success)
            {
                FileLogger.Log(
                    $"[DeathTower] SORT_ITEM rejected: " +
                    $"cid={session.Player.CharacterId} list={command.ListType} " +
                    $"category={command.Category} hasCategory={command.HasCategory}");
                return true;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0014,
                SortItemAckBuilder.Build(command.ListType)));
            await SendInventorySnapshotAsync(session, tower, lease);
            return true;
        }

        public async Task<bool> TryHandleDeleteItem(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryCaptureTowerCommandContext(
                    session,
                    out var runIdentity,
                    out var tower))
                return false;
            if (!DeathTowerInventoryCommandParser.TryParseDelete(
                    body,
                    out var command))
            {
                return false;
            }
            if (!TryGetOwnedInventoryLease(session, out var lease)
                || !IsCurrentTowerCommandContext(
                    session,
                    runIdentity,
                    tower,
                    lease))
                return true;

            var success = _transientInventory.TryDeleteSkillMaterials(
                tower,
                lease,
                command,
                out var handled,
                out var result);
            if (!handled)
                return false;
            if (!success || !result.Success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0012,
                    DeleteItemAckBuilder.BuildError((byte)command.ListType)));
                FileLogger.Log(
                    $"[DeathTower] DELETE_ITEM skill material rejected: " +
                    $"cid={session.Player.CharacterId} entries={command.Entries.Count}");
                return true;
            }

            foreach (var mutation in result.Mutations)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0012,
                    DeleteItemAckBuilder.Build(mutation)));
            }
            await SendInventorySnapshotAsync(session, tower, lease);
            foreach (var itemId in result.TransientItemIds)
                RecalibrateTowerQuestProgress(session, tower, itemId);
            return true;
        }

        private static async Task SendInventorySnapshotAsync(
            EnhancedClientSession session,
            DeathTowerSession tower,
            InventoryLease lease)
        {
            if (session == null || tower == null || lease == null)
                return;

            byte[] body;
            lock (lease.SyncRoot)
            {
                body = DeathTowerInventoryProjectionBuilder.BuildFullListBody(
                    tower,
                    lease.Inventory);
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000D,
                body));
        }

        private static bool TryGetOwnedInventoryLease(
            EnhancedClientSession session,
            out InventoryLease lease)
        {
            lease = null;
            var player = session?.Player;
            return player != null
                && InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    player.CharacterId,
                    out lease);
        }

        private static bool TryCaptureTowerCommandContext(
            EnhancedClientSession session,
            out GameDungeon.DungeonRunIdentity runIdentity,
            out DeathTowerSession tower)
        {
            runIdentity = default;
            tower = null;
            var player = session?.Player;
            var run = player?.CurrentRun;
            if (run?.Tower == null)
                return false;

            runIdentity = run.CaptureIdentity();
            if (!player.IsCurrentDungeonRun(runIdentity))
                return false;

            tower = run.Tower;
            return true;
        }

        private static bool IsCurrentTowerCommandContext(
            EnhancedClientSession session,
            GameDungeon.DungeonRunIdentity runIdentity,
            DeathTowerSession tower,
            InventoryLease lease)
        {
            var player = session?.Player;
            return player != null
                && tower != null
                && player.IsCurrentDungeonRun(runIdentity)
                && ReferenceEquals(player.CurrentRun?.Tower, tower)
                && InventoryContext.IsCurrentLease(
                    lease,
                    session.SessionId,
                    player.CharacterId);
        }

        private static void RecalibrateTowerQuestProgress(
            EnhancedClientSession session,
            DeathTowerSession tower,
            int itemId)
        {
            var questManager = session?.GameSession?.QuestManager;
            if (questManager == null || itemId <= 0)
                return;
            questManager.RecalibrateItemSeekingQuestProgressWithoutNotification(
                new[] { itemId },
                tower.GetItemCountsSnapshot());
        }
    }
}
