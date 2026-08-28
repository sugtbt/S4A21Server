using DfoServer.Game.Inventory;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Pets
{
    internal enum PetCreatureDeathCheckOutcome
    {
        NoDeath,
        Died,
        Retry,
    }

    internal static class PetCreatureRuntimeService
    {
        private const string ProtocolName = "GameProtocol";
        private const string ClockTickName = "pet-creature-runtime";
        private const string DeathTimerNamePrefix = "pet-creature-death:";
        private const double TownSatietyRecoveryIntervalSeconds = 360.0;
        internal static readonly TimeSpan DeathCommitRetryDelay =
            TimeSpan.FromSeconds(5);
        private static readonly ConcurrentDictionary<Guid, EnhancedClientSession> Sessions =
            new ConcurrentDictionary<Guid, EnhancedClientSession>();
        private static int _clockRegistered;
        private static int _tickRunning;

        internal static void EnsureClockRegistered()
        {
            if (Interlocked.Exchange(ref _clockRegistered, 1) != 0)
                return;

            ClockService.Instance.RegisterMinuteTick(ClockTickName, TickOnlineSessions);
        }

        internal static void RegisterSession(EnhancedClientSession session)
        {
            if (session == null)
                return;

            Sessions[session.SessionId] = session;
        }

        internal static void UnregisterSession(EnhancedClientSession session)
        {
            if (session == null)
                return;

            CancelDeathCheck(session);
            Sessions.TryRemove(session.SessionId, out _);
        }

        internal static Task BeginTownAsync(EnhancedClientSession session, string source)
        {
            if (!HasCharacter(session) || session.Player.CurrentRun != null)
                return Task.CompletedTask;

            var townGeneration = session.Player.CurrentDungeonRunGeneration;
            ClearDungeonAnchor(session);
            return BeginTownCoreAsync(
                session,
                source,
                DateTime.UtcNow,
                () => CanApplyTownState(session, townGeneration));
        }

        internal static void BeginDungeon(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity,
            string source)
        {
            if (!HasCharacter(session)
                || !session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            var dungeonId = session.Player.CurrentRun.DungeonId;
            var now = DateTime.UtcNow;
            PersistTownRecovery(session, source, now, continueTiming: false);
            session.Player.PetCreatureSatietyDungeonStartUtc = now;
            session.Player.PetCreatureSatietyDungeonId =
                (short)Math.Max(0, Math.Min(short.MaxValue, (int)dungeonId));
            session.Player.PetCreatureSatietyTownStartUtc = DateTime.MinValue;
            session.Player.PetCreatureLastDeathCreatureKey = 0;

            try
            {
                if (!TryGetInventoryLease(session, out var lease))
                    return;

                PetCreatureSatietyUpdate current;
                lock (lease.SyncRoot)
                    current = PetCreatureSatietyService.LoadEquippedCreatureSatiety(lease.Inventory);
                SetSessionCreatureAliveState(session, current.CreatureKey > 0 && current.Before > 0 ? (byte)1 : (byte)0);
                FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: begin dungeon source={source} cid={session.Player.CharacterId} dungeon={dungeonId} key={current.CreatureKey} satiety={current.Before} foodRate={current.FoodConsumeRatePercent}% multiplier={current.FoodConsumeMultiplier:0.###}");
                ScheduleDungeonDeathCheck(session, $"{source}:begin", now);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: begin dungeon failed source={source} cid={session.Player.CharacterId}: {ex.Message}");
            }
        }

        internal static async Task EndDungeonToTownAsync(
            EnhancedClientSession session,
            DungeonRunIdentity endingRunIdentity,
            string source)
        {
            if (!HasCharacter(session)
                || !CanCompleteEndedRun(session, endingRunIdentity))
                return;

            var now = DateTime.UtcNow;
            await CheckDungeonDeathAsync(session, $"{source}:before-town", now);
            if (!CanCompleteEndedRun(session, endingRunIdentity))
                return;
            PersistDungeonElapsed(session, source, now, continueTiming: false);
            if (!CanCompleteEndedRun(session, endingRunIdentity))
                return;
            CancelDeathCheck(session);
            await BeginTownCoreAsync(
                session,
                source,
                now,
                () => CanCompleteEndedRun(session, endingRunIdentity));
        }

        internal static void EndCharacterSession(EnhancedClientSession session, string source)
        {
            if (!HasCharacter(session))
                return;

            var now = DateTime.UtcNow;
            CancelDeathCheck(session);
            PersistDungeonElapsed(session, source, now, continueTiming: false);
            PersistTownRecovery(session, source, now, continueTiming: false);
            session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.MinValue;
            session.Player.PetCreatureSatietyDungeonId = 0;
            session.Player.PetCreatureLastDeathCreatureKey = 0;
            session.Player.PetCreatureSatietyTownStartUtc = DateTime.MinValue;
        }

        internal static void PersistDungeonElapsedBeforeMutation(
            EnhancedClientSession session,
            string source,
            bool continueTiming = false)
        {
            if (!HasCharacter(session))
                return;

            PersistDungeonElapsed(session, source, DateTime.UtcNow, continueTiming);
        }

        internal static bool TryCommitDungeonElapsedBeforeMutation(
            EnhancedClientSession session,
            InventoryLease lease,
            string source,
            bool continueTiming = false)
        {
            if (!HasCharacter(session) || lease?.Inventory == null)
                return false;

            var startUtc = session.Player.PetCreatureSatietyDungeonStartUtc;
            if (startUtc == DateTime.MinValue)
                return true;

            var dungeonId = session.Player.PetCreatureSatietyDungeonId;
            var now = DateTime.UtcNow;
            var committed = PetCreatureSatietyCommitService.TryCommitDungeonElapsed(
                lease,
                startUtc,
                now,
                out var update);
            if (!committed)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PetCreatureSatiety: dungeon pre-mutation commit failed "
                    + $"source={source} cid={session.Player.CharacterId} dungeon={dungeonId}");
                return false;
            }

            if (!continueTiming)
                session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.MinValue;
            else if (update.StateChanged)
                session.Player.PetCreatureSatietyDungeonStartUtc = now;
            if (!continueTiming)
                session.Player.PetCreatureSatietyDungeonId = 0;

            SetSessionCreatureAliveState(
                session,
                update.CreatureKey > 0 && update.After > 0 ? (byte)1 : (byte)0);
            FileLogger.Log(
                $"[{ProtocolName}] PetCreatureSatiety: dungeon pre-mutation committed "
                + $"source={source} cid={session.Player.CharacterId} dungeon={dungeonId} "
                + $"key={update.CreatureKey} elapsed={update.ElapsedSeconds:0.0}s "
                + $"foodRate={update.FoodConsumeRatePercent}% "
                + $"multiplier={update.FoodConsumeMultiplier:0.###} "
                + $"consumed={update.ConsumedSatiety} "
                + $"satiety={update.Before}->{update.After} changed={update.Changed}");
            return true;
        }

        internal static bool BeginInventoryMoveMutation(
            EnhancedClientSession session,
            InventoryLease lease,
            InventoryMoveRequest request,
            out bool trackedPetRuntimeMove)
        {
            trackedPetRuntimeMove = false;
            if (!HasCharacter(session)
                || session.Player.CurrentRun == null
                || !IsPetRuntimeAffectingMoveRequest(request))
            {
                return true;
            }

            trackedPetRuntimeMove = true;
            return TryCommitDungeonElapsedBeforeMutation(
                session,
                lease,
                "pet_runtime_move_before",
                continueTiming: true);
        }

        internal static Task CompleteInventoryMoveMutationAsync(
            EnhancedClientSession session,
            InventoryMoveResult result,
            bool trackedPetRuntimeMove)
        {
            if (!HasCharacter(session) || result == null)
                return Task.CompletedTask;

            if (result.PetCreatureStateChanged)
                return HandlePetCreatureChangedInDungeonAsync(session, "pet_creature_move_after");

            if (result.PetItemStateChanged && trackedPetRuntimeMove)
                return HandlePetCreatureChangedInDungeonAsync(session, "pet_artifact_move_after");

            return Task.CompletedTask;
        }

        internal static async Task HandlePetCreatureChangedInDungeonAsync(EnhancedClientSession session, string source)
        {
            if (!HasCharacter(session) || session.Player.CurrentRun == null)
                return;

            var now = DateTime.UtcNow;
            session.Player.PetCreatureSatietyDungeonStartUtc = now;
            session.Player.PetCreatureSatietyDungeonId = session.Player.CurrentRun.DungeonId;
            session.Player.PetCreatureLastDeathCreatureKey = 0;

            if (!TryGetInventoryLease(session, out var lease))
                return;

            PetCreatureSatietyUpdate current;
            lock (lease.SyncRoot)
                current = PetCreatureSatietyService.LoadEquippedCreatureSatiety(lease.Inventory);
            if (current.CreatureKey <= 0)
            {
                ClearDungeonAnchor(session);
                SetSessionCreatureAliveState(session, 0);
                FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: pet changed no active creature source={source} cid={session.Player.CharacterId}");
                return;
            }

            SetSessionCreatureAliveState(session, current.Before > 0 ? (byte)1 : (byte)0);
            FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: pet changed source={source} cid={session.Player.CharacterId} dungeon={session.Player.CurrentRun.DungeonId} key={current.CreatureKey} satiety={current.Before} foodRate={current.FoodConsumeRatePercent}% multiplier={current.FoodConsumeMultiplier:0.###}");
            ScheduleDungeonDeathCheck(session, source, now);
        }

        internal static void HandlePetSatietyChangedAfterFeed(EnhancedClientSession session, int creatureKey, int satietyAfter, string source)
        {
            if (!HasCharacter(session))
                return;

            SetSessionCreatureAliveState(session, creatureKey > 0 && satietyAfter > 0 ? (byte)1 : (byte)0);
            if (session.Player.CurrentRun != null && creatureKey > 0 && satietyAfter > 0)
            {
                session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.UtcNow;
                session.Player.PetCreatureSatietyDungeonId = session.Player.CurrentRun.DungeonId;
                session.Player.PetCreatureLastDeathCreatureKey = 0;
                ScheduleDungeonDeathCheck(session, source, session.Player.PetCreatureSatietyDungeonStartUtc);
            }

            FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: feed applied source={source} cid={session.Player.CharacterId} key={creatureKey} satiety={satietyAfter}");
        }

        internal static async Task GrantRoomClearExperienceOnceAsync(
            EnhancedClientSession session,
            Game.Dungeon.RoomState roomState,
            int consumedFatigue)
        {
            if (roomState == null || !roomState.TryBeginPetExperienceGrant())
                return;

            var completed = false;
            try
            {
                var run = session?.Player?.CurrentRun;
                if (run == null || !run.RewardPolicy.AllowsPetExperience)
                {
                    completed = true;
                    return;
                }

                completed = await SendPetCreatureClearExperienceAsync(
                    session,
                    consumedFatigue);
            }
            finally
            {
                if (completed)
                    roomState.CompletePetExperienceGrant();
                else
                    roomState.CancelPetExperienceGrant();
            }
        }

        internal static async Task VerifyCreatureEvolutionQuestAsync(EnhancedClientSession session)
        {
            var questManager = session?.GameSession?.QuestManager;
            if (questManager == null)
            {
                FileLogger.Log($"[{ProtocolName}] VERIFY_CREATURE_QUEST skipped: quest manager unavailable");
                return;
            }

            var characterId = session.Player?.CharacterId ?? 0;
            var allowedCreatureKinds = TryGetInventoryLease(session, out var lease)
                ? PetCreatureEvolutionRuntimeService.LoadEligiblePetCreatureEvolutionQuestKinds(lease.Inventory)
                : new HashSet<int>();
            if (allowedCreatureKinds.Count == 0)
            {
                FileLogger.Log($"[{ProtocolName}] VERIFY_CREATURE_QUEST skipped: equipped creature has no pending evolution quest cid={characterId}");
                return;
            }

            // 旧服 Dispatcher_VerifyCreatureQuest::read() 不消费包体。
            // 只有当前装备宠物存在进化任务时才会发送任务列表；自动进化宠物不回包。
            await questManager.SendAcceptableQuestListAsync();
        }

        internal static async Task<bool> SendPetCreatureClearExperienceAsync(
            EnhancedClientSession session,
            int consumedFatigue)
        {
            if (!HasCharacter(session) || consumedFatigue <= 0)
                return false;

            PetCreatureExperienceUpdate update;
            try
            {
                if (!TryGetInventoryLease(session, out var lease))
                    return false;

                if (!PetCreatureExperienceCommitService.TryCommit(
                        lease,
                        consumedFatigue,
                        out update))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureExp: failed cid={session.Player.CharacterId} fatigue={consumedFatigue}: {ex.Message}");
                return false;
            }

            if (!update.Changed)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureExp: skipped cid={session.Player.CharacterId} key={update.CreatureKey} fatigue={consumedFatigue}");
                return true;
            }

            try
            {
                var writer = new GamePacketWriter();
                writer.WriteByte((byte)Math.Max(1, Math.Min(255, update.AfterLevel)));
                writer.WriteByte(0);
                writer.WriteInt32(update.AfterExperience);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0066, writer.ToArray()));

                if (update.Evolution.Changed)
                {
                    await SendPetCreatureEvolutionAsync(session, update.Evolution);
                }
                else if (update.AfterLevel > update.BeforeLevel
                    && session.GameSession?.QuestManager != null)
                {
                    await session.GameSession.QuestManager.SendAcceptableQuestListAsync();
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PetCreatureExp: notification failed after commit "
                    + $"cid={session.Player.CharacterId} key={update.CreatureKey}: {ex.Message}");
            }

            FileLogger.Log($"[{ProtocolName}] PetCreatureExp: GAIN_EXP_CREATURE cid={session.Player.CharacterId} key={update.CreatureKey} fatigue={consumedFatigue} exp={update.BeforeExperience}->{update.AfterExperience} gained={update.GainedExperience} level={update.BeforeLevel}->{update.AfterLevel}");
            return true;
        }

        private static void TickOnlineSessions(DateTime utcNow)
        {
            if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0)
                return;

            try
            {
                foreach (var pair in Sessions.ToArray())
                {
                    var session = pair.Value;
                    if (session == null)
                    {
                        Sessions.TryRemove(pair.Key, out _);
                        continue;
                    }

                    TickSession(session, utcNow);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureRuntime: tick loop failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        private static void TickSession(EnhancedClientSession session, DateTime utcNow)
        {
            if (!HasCharacter(session))
                return;

            try
            {
                var run = session.Player.CurrentRun;
                if (run != null)
                {
                    PersistDungeonElapsed(session, "clock", utcNow, continueTiming: true);
                    return;
                }

                var townGeneration = session.Player.CurrentDungeonRunGeneration;
                if (!CanApplyTownState(session, townGeneration))
                    return;
                PersistTownRecovery(session, "clock", utcNow, continueTiming: true);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureRuntime: tick failed cid={session.Player.CharacterId}: {ex.Message}");
            }
        }

        private static void ScheduleDungeonDeathCheck(
            EnhancedClientSession session,
            string source,
            DateTime now)
        {
            var run = session?.Player?.CurrentRun;
            if (!HasCharacter(session) || run == null)
            {
                CancelDeathCheck(session);
                return;
            }
            var runIdentity = run.CaptureIdentity();

            PetCreatureSatietyUpdate current;
            try
            {
                if (!TryGetInventoryLease(session, out var lease))
                {
                    CancelDeathCheck(session);
                    return;
                }

                lock (lease.SyncRoot)
                    current = PetCreatureSatietyService.LoadEquippedCreatureSatiety(lease.Inventory);
            }
            catch (Exception ex)
            {
                CancelDeathCheck(session);
                FileLogger.Log($"[{ProtocolName}] PetCreatureDeathTimer: schedule failed source={source} cid={session.Player.CharacterId}: {ex.Message}");
                return;
            }

            if (current.CreatureKey <= 0)
            {
                CancelDeathCheck(session);
                return;
            }

            if (current.Before <= 0)
            {
                var immediateVersion = AdvanceDeathTimerVersion(session);
                ScheduleDeathCheck(
                    session,
                    now,
                    immediateVersion,
                    runIdentity,
                    $"{source}:zero");
                FileLogger.Log($"[{ProtocolName}] PetCreatureDeathTimer: schedule immediate source={source} cid={session.Player.CharacterId} key={current.CreatureKey} satiety={current.Before} version={immediateVersion}");
                return;
            }

            var multiplier = current.FoodConsumeMultiplier;
            var delaySeconds = current.Before <= 1
                ? 0.0
                : (current.Before - 1) * 60.0 / Math.Max(0.01, multiplier);
            var dueUtc = now.AddSeconds(delaySeconds);
            var version = AdvanceDeathTimerVersion(session);
            ScheduleDeathCheck(session, dueUtc, version, runIdentity, source);
            FileLogger.Log($"[{ProtocolName}] PetCreatureDeathTimer: schedule source={source} cid={session.Player.CharacterId} dungeon={run.DungeonId} key={current.CreatureKey} satiety={current.Before} foodRate={current.FoodConsumeRatePercent}% multiplier={multiplier:0.###} dueIn={delaySeconds:0.0}s version={version}");
        }

        private static void ScheduleDeathCheck(
            EnhancedClientSession session,
            DateTime dueUtc,
            int version,
            DungeonRunIdentity runIdentity,
            string source)
        {
            var name = BuildDeathTimerName(session);
            ClockService.Instance.ScheduleOneShotAsync(name, dueUtc, async utcNow =>
            {
                try
                {
                    if (!HasCharacter(session)
                        || !session.Player.IsCurrentDungeonRun(runIdentity))
                    {
                        FileLogger.Log($"[{ProtocolName}] PetCreatureDeathTimer: skip stale run source={source} cid={session?.Player?.CharacterId ?? 0} run={runIdentity.RunId} generation={runIdentity.RunGeneration}");
                        return;
                    }

                    if (session.Player.PetCreatureDeathTimerVersion != version)
                    {
                        FileLogger.Log($"[{ProtocolName}] PetCreatureDeathTimer: skip stale source={source} cid={session.Player.CharacterId} expected={version} actual={session.Player.PetCreatureDeathTimerVersion}");
                        return;
                    }

                    var outcome = await CheckDungeonDeathAsync(
                        session,
                        $"{source}:timer",
                        utcNow);
                    if (!session.Player.IsCurrentDungeonRun(runIdentity))
                        return;

                    if (outcome == PetCreatureDeathCheckOutcome.NoDeath)
                    {
                        ScheduleDungeonDeathCheck(
                            session,
                            $"{source}:timer-reschedule",
                            utcNow);
                    }
                    else if (outcome == PetCreatureDeathCheckOutcome.Retry)
                    {
                        var retryDueUtc = utcNow.Add(DeathCommitRetryDelay);
                        ScheduleDeathCheck(
                            session,
                            retryDueUtc,
                            version,
                            runIdentity,
                            $"{source}:commit-retry");
                        FileLogger.Log(
                            $"[{ProtocolName}] PetCreatureDeathTimer: " +
                            $"commit retry source={source} " +
                            $"cid={session.Player.CharacterId} " +
                            $"dueIn={DeathCommitRetryDelay.TotalSeconds:0.0}s " +
                            $"version={version}");
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] PetCreatureDeathTimer: callback failed source={source}: {ex.Message}");
                }
            });
        }

        private static void CancelDeathCheck(EnhancedClientSession session)
        {
            if (session == null)
                return;

            ClockService.Instance.CancelOneShot(BuildDeathTimerName(session));
            AdvanceDeathTimerVersion(session);
        }

        private static int AdvanceDeathTimerVersion(EnhancedClientSession session)
        {
            if (session?.Player == null)
                return 0;

            unchecked
            {
                session.Player.PetCreatureDeathTimerVersion++;
                if (session.Player.PetCreatureDeathTimerVersion == 0)
                    session.Player.PetCreatureDeathTimerVersion = 1;
            }

            return session.Player.PetCreatureDeathTimerVersion;
        }

        private static string BuildDeathTimerName(EnhancedClientSession session)
            => DeathTimerNamePrefix + session.SessionId.ToString("N");

        private static async Task BeginTownCoreAsync(
            EnhancedClientSession session,
            string source,
            DateTime now,
            Func<bool> continuationIsCurrent = null)
        {
            if (continuationIsCurrent != null && !continuationIsCurrent())
            {
                return;
            }

            await TryRevivePetCreatureOnTownReturnAsync(
                session,
                source,
                continuationIsCurrent);
            if (continuationIsCurrent != null && !continuationIsCurrent())
            {
                return;
            }
            if (session.Player.PetCreatureSatietyTownStartUtc == DateTime.MinValue)
            {
                session.Player.PetCreatureSatietyTownStartUtc = now;
                FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: town begin source={source} cid={session.Player.CharacterId}");
            }
        }

        private static void PersistDungeonElapsed(
            EnhancedClientSession session,
            string source,
            DateTime now,
            bool continueTiming)
        {
            var startUtc = session.Player.PetCreatureSatietyDungeonStartUtc;
            if (startUtc == DateTime.MinValue)
                return;

            var dungeonId = session.Player.PetCreatureSatietyDungeonId;

            try
            {
                if (!TryGetInventoryLease(session, out var lease))
                    return;

                if (!PetCreatureSatietyCommitService.TryCommitDungeonElapsed(
                        lease,
                        startUtc,
                        now,
                        out var update))
                {
                    return;
                }

                if (!continueTiming)
                    session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.MinValue;
                else if (update.StateChanged)
                    session.Player.PetCreatureSatietyDungeonStartUtc = now;
                if (!continueTiming)
                    session.Player.PetCreatureSatietyDungeonId = 0;
                SetSessionCreatureAliveState(session, update.CreatureKey > 0 && update.After > 0 ? (byte)1 : (byte)0);

                FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: dungeon persist source={source} cid={session.Player.CharacterId} dungeon={dungeonId} key={update.CreatureKey} elapsed={update.ElapsedSeconds:0.0}s foodRate={update.FoodConsumeRatePercent}% multiplier={update.FoodConsumeMultiplier:0.###} consumed={update.ConsumedSatiety} satiety={update.Before}->{update.After} changed={update.Changed}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: dungeon persist failed source={source} cid={session.Player.CharacterId} dungeon={dungeonId}: {ex.Message}");
            }
        }

        private static void PersistTownRecovery(
            EnhancedClientSession session,
            string source,
            DateTime now,
            bool continueTiming)
        {
            var startUtc = session.Player.PetCreatureSatietyTownStartUtc;
            if (startUtc == DateTime.MinValue)
                return;

            if (continueTiming
                && Math.Max(0, (now - startUtc).TotalSeconds) < TownSatietyRecoveryIntervalSeconds)
            {
                return;
            }

            try
            {
                if (!TryGetInventoryLease(session, out var lease))
                    return;

                if (!PetCreatureSatietyCommitService.TryCommitTownElapsed(
                        lease,
                        startUtc,
                        now,
                        out var update))
                {
                    return;
                }
                SetSessionCreatureAliveState(session, update.CreatureKey > 0 && update.After > 0 ? (byte)1 : (byte)0);
                session.Player.PetCreatureSatietyTownStartUtc = continueTiming
                    ? CalculateNextTownRecoveryAnchor(startUtc, now, update)
                    : DateTime.MinValue;

                FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: town persist source={source} cid={session.Player.CharacterId} key={update.CreatureKey} elapsed={update.ElapsedSeconds:0.0}s recovered={update.RecoveredSatiety} satiety={update.Before}->{update.After} changed={update.Changed}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureSatiety: town persist failed source={source} cid={session.Player.CharacterId}: {ex.Message}");
            }
        }

        internal static async Task<PetCreatureDeathCheckOutcome> CheckDungeonDeathAsync(
            EnhancedClientSession session,
            string source,
            DateTime now)
        {
            var startUtc = session.Player.PetCreatureSatietyDungeonStartUtc;
            if (startUtc == DateTime.MinValue)
                return PetCreatureDeathCheckOutcome.NoDeath;

            try
            {
                if (!TryGetInventoryLease(session, out var lease))
                    return PetCreatureDeathCheckOutcome.Retry;

                if (!PetCreatureSatietyCommitService.TryCommitDungeonDeath(
                        lease,
                        startUtc,
                        now,
                        out var update))
                {
                    return PetCreatureDeathCheckOutcome.Retry;
                }

                if (update.CreatureKey <= 0)
                {
                    session.Player.PetCreatureSatietyDungeonStartUtc = now;
                    return PetCreatureDeathCheckOutcome.NoDeath;
                }

                if (update.After > 0)
                {
                    if (update.StateChanged)
                        session.Player.PetCreatureSatietyDungeonStartUtc = now;
                    SetSessionCreatureAliveState(session, 1);
                    return PetCreatureDeathCheckOutcome.NoDeath;
                }

                if (session.Player.PetCreatureLastDeathCreatureKey == update.CreatureKey)
                    return PetCreatureDeathCheckOutcome.Died;

                CancelDeathCheck(session);
                session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.MinValue;
                session.Player.PetCreatureSatietyDungeonId = 0;
                session.Player.PetCreatureLastDeathCreatureKey = update.CreatureKey;
                SetSessionCreatureAliveState(session, 0);

                try
                {
                    var writer = new GamePacketWriter();
                    writer.WriteUInt16(session.Player.UserId);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0064, writer.ToArray()));
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] PetCreatureDeath: notification failed after commit source={source} cid={session.Player.CharacterId}: {ex.Message}");
                }

                FileLogger.Log($"[{ProtocolName}] PetCreatureDeath: DIED_CREATURE source={source} uid={session.Player.UserId} cid={session.Player.CharacterId} key={update.CreatureKey} elapsed={update.ElapsedSeconds:0.0}s foodRate={update.FoodConsumeRatePercent}% multiplier={update.FoodConsumeMultiplier:0.###} satiety={update.Before}->0");
                return PetCreatureDeathCheckOutcome.Died;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureDeath: check failed source={source} cid={session.Player.CharacterId}: {ex.Message}");
                return PetCreatureDeathCheckOutcome.Retry;
            }
        }

        private static async Task TryRevivePetCreatureOnTownReturnAsync(
            EnhancedClientSession session,
            string source,
            Func<bool> continuationIsCurrent = null)
        {
            if (continuationIsCurrent != null && !continuationIsCurrent())
            {
                return;
            }

            PetCreatureRevivalUpdate update;
            try
            {
                if (!TryGetInventoryLease(session, out var lease))
                    return;

                if (!PetCreatureSatietyCommitService.TryCommitRevival(
                        lease,
                        out update))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureRevival: failed source={source} cid={session.Player.CharacterId}: {ex.Message}");
                return;
            }

            if (update.CreatureKey <= 0)
                return;

            if (!update.Revived)
            {
                SetSessionCreatureAliveState(session, update.After > 0 ? (byte)1 : (byte)0);
                return;
            }

            SetSessionCreatureAliveState(session, update.After > 0 ? (byte)1 : (byte)0);
            session.Player.PetCreatureLastDeathCreatureKey = 0;

            try
            {
                var revival = new GamePacketWriter();
                revival.WriteUInt16(session.Player.UserId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x006B, revival.ToArray()));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureRevival: notification failed after commit source={source} cid={session.Player.CharacterId}: {ex.Message}");
            }
            if (continuationIsCurrent != null && !continuationIsCurrent())
            {
                return;
            }

            try
            {
                await SendPetCreatureStateAsync(session, update.CreatureKey, update.After);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureRevival: state refresh failed after commit source={source} cid={session.Player.CharacterId}: {ex.Message}");
            }
            FileLogger.Log($"[{ProtocolName}] PetCreatureRevival: REVIVAL_CREATURE source={source} uid={session.Player.UserId} cid={session.Player.CharacterId} key={update.CreatureKey} satiety={update.Before}->{update.After}");
        }

        internal static async Task SendPetCreatureEvolutionAsync(
            EnhancedClientSession session,
            PetCreatureEvolutionResult evolution)
        {
            if (session == null || session.Player == null || !evolution.Changed)
                return;

            var writer = new GamePacketWriter();
            writer.WriteUInt16(NormalizeCreatureEventParam(evolution.EvolvedCreatureParam));
            var eventUniqueId = GetPetCreatureEventUniqueId(session.Player);
            writer.WriteUInt16(eventUniqueId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x006A, writer.ToArray()));
            SetSessionEquippedCreatureItemId(session.Player, evolution.EvolvedItemTemplateId);

            try
            {
                await InventoryRefreshSender.SendOnlineUpdateItemList(
                    session,
                    InventoryListType.Equipment,
                    evolution.EquipmentSlot);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureEvolution: refresh failed cid={session.Player.CharacterId} slot={evolution.EquipmentSlot}: {ex.Message}");
            }

            FileLogger.Log($"[{ProtocolName}] PetCreatureEvolution: EVOLUTE_CREATURE cid={session.Player.CharacterId} uid={eventUniqueId} baseUid={session.Player.UserId} sceneUid={session.Player.DungeonSceneUniqueId} creature={evolution.CurrentCreatureId}->{evolution.EvolvedCreatureId} param={evolution.EvolvedCreatureParam} item=0x{evolution.PreviousItemTemplateId:X8}->0x{evolution.EvolvedItemTemplateId:X8}");
        }

        internal static async Task SendPetCreatureEvolutionAsync(
            ISessionPacketSender sender,
            PetCreatureEvolutionResult evolution)
        {
            if (sender == null || sender.Player == null || !evolution.Changed)
                return;

            var writer = new GamePacketWriter();
            writer.WriteUInt16(NormalizeCreatureEventParam(evolution.EvolvedCreatureParam));
            var eventUniqueId = GetPetCreatureEventUniqueId(sender.Player);
            writer.WriteUInt16(eventUniqueId);
            await sender.SendNotiAsync(0x006A, writer.ToArray());
            SetSessionEquippedCreatureItemId(sender.Player, evolution.EvolvedItemTemplateId);

            try
            {
                await InventoryRefreshSender.SendOnlineUpdateItemList(
                    sender,
                    InventoryListType.Equipment,
                    evolution.EquipmentSlot);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] PetCreatureEvolution: refresh failed cid={sender.CharacterId} slot={evolution.EquipmentSlot}: {ex.Message}");
            }

            FileLogger.Log($"[{ProtocolName}] PetCreatureEvolution: EVOLUTE_CREATURE cid={sender.CharacterId} uid={eventUniqueId} baseUid={sender.Player.UserId} sceneUid={sender.Player.DungeonSceneUniqueId} creature={evolution.CurrentCreatureId}->{evolution.EvolvedCreatureId} param={evolution.EvolvedCreatureParam} item=0x{evolution.PreviousItemTemplateId:X8}->0x{evolution.EvolvedItemTemplateId:X8}");
        }

        private static Task SendPetCreatureStateAsync(
            EnhancedClientSession session,
            int creatureKey,
            int stateValue)
        {
            var state = new GamePacketWriter();
            state.WriteInt32(creatureKey);
            state.WriteInt32(stateValue);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0067, state.ToArray()));
        }

        private static void SetSessionCreatureAliveState(EnhancedClientSession session, byte value)
        {
            if (session?.Player == null)
                return;

            var tail = session.Player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
            tail.EquippedCreatureAliveState = value;
            session.Player.Subtype0Tail = tail;
        }

        private static void SetSessionEquippedCreatureItemId(PlayerContext player, int itemId)
        {
            if (player == null || itemId <= 0)
                return;

            var tail = player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
            tail.EquippedCreatureItemId = unchecked((uint)itemId);
            player.Subtype0Tail = tail;
        }

        private static ushort GetPetCreatureEventUniqueId(PlayerContext player)
        {
            if (player == null)
                return 0;

            return player.CurrentRun != null && player.DungeonSceneUniqueId != 0
                ? player.DungeonSceneUniqueId
                : player.UserId;
        }

        private static ushort NormalizeCreatureEventParam(int value)
            => (ushort)Math.Max(0, Math.Min(ushort.MaxValue, value));

        private static void ClearDungeonAnchor(EnhancedClientSession session)
        {
            if (session?.Player == null)
                return;

            CancelDeathCheck(session);
            session.Player.PetCreatureSatietyDungeonStartUtc = DateTime.MinValue;
            session.Player.PetCreatureSatietyDungeonId = 0;
            session.Player.PetCreatureLastDeathCreatureKey = 0;
        }

        internal static bool CanCompleteEndedRun(
            EnhancedClientSession session,
            DungeonRunIdentity endingRunIdentity)
        {
            var player = session?.Player;
            return endingRunIdentity.IsValid
                && player != null
                && player.CurrentRun == null
                && player.CurrentDungeonRunGeneration
                    == endingRunIdentity.RunGeneration;
        }

        private static bool CanApplyTownState(
            EnhancedClientSession session,
            long expectedGeneration)
        {
            var player = session?.Player;
            return player != null
                && player.CurrentRun == null
                && player.CurrentDungeonRunGeneration == expectedGeneration;
        }

        private static DateTime CalculateNextTownRecoveryAnchor(
            DateTime startUtc,
            DateTime now,
            PetCreatureSatietyUpdate update)
        {
            if (update.Changed && update.RecoveredSatiety > 0 && update.After < 100)
            {
                var next = startUtc.AddSeconds(update.RecoveredSatiety * TownSatietyRecoveryIntervalSeconds);
                return next > now ? now : next;
            }

            return now;
        }

        private static bool HasCharacter(EnhancedClientSession session)
            => session?.Player != null && session.Player.CharacterId > 0;

        private static bool TryGetInventoryLease(EnhancedClientSession session, out InventoryLease lease)
        {
            lease = null;
            if (!HasCharacter(session))
                return false;

            return InventoryContext.TryGetLease(session.Player.CharacterId, out lease);
        }

        private static bool IsPetRuntimeAffectingMoveRequest(InventoryMoveRequest request)
        {
            if (request == null)
                return false;

            return (request.SourceListType == InventoryListType.Pet
                    && request.DestinationListType == InventoryListType.Equipment
                    && IsPetRuntimeEquipmentSlot(request.DestinationSlotIndex))
                || (request.SourceListType == InventoryListType.Equipment
                    && IsPetRuntimeEquipmentSlot(request.SourceSlotIndex)
                    && request.DestinationListType == InventoryListType.Pet);
        }

        private static bool IsPetRuntimeEquipmentSlot(short slot)
            => PetInventoryLayout.IsPetEquipmentSlot(slot);
    }
}
