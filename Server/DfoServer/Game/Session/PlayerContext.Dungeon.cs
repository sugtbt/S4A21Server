using System;
using System.Threading;

namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        // 副本状态真相: 一局一个 DungeonRun 对象, null = 不在副本中。
        // 进本由 DungeonRunLifecycle.BeginRun/BeginTowerRun 置换新实例,
        // 返城/断线/换角色置 null -- 单局字段随对象消失, 不存在漏重置。
        public Game.Dungeon.DungeonRun CurrentRun { get; internal set; }
        internal object DungeonRunLifecycleSyncRoot { get; } = new object();

        private long _dungeonRunGeneration;
        private long _dungeonSelectionGeneration;
        private int _pendingPartyRetryDungeonId;
        private Game.Dungeon.DungeonSelectionContext _currentDungeonSelection;

        internal long CurrentDungeonRunGeneration =>
            Interlocked.Read(ref _dungeonRunGeneration);

        internal Game.Dungeon.DungeonSelectionContext CurrentDungeonSelection =>
            Volatile.Read(ref _currentDungeonSelection);

        internal long NextDungeonRunGeneration()
        {
            var generation = Interlocked.Increment(ref _dungeonRunGeneration);
            if (generation > 0)
                return generation;

            throw new InvalidOperationException("Dungeon run generation exhausted.");
        }

        internal bool TryAttachResumedDungeonRun(
            Game.Dungeon.DungeonRun run)
        {
            if (run == null
                || !run.CaptureIdentity().IsValid
                || run.RunState == Game.Dungeon.DungeonRunState.Ending
                || run.RunState == Game.Dungeon.DungeonRunState.Ended)
            {
                return false;
            }

            lock (DungeonRunLifecycleSyncRoot)
            {
                if (CurrentRun != null)
                    return ReferenceEquals(CurrentRun, run);

                var currentGeneration = Interlocked.Read(
                    ref _dungeonRunGeneration);
                if (currentGeneration > run.RunGeneration)
                    return false;

                Interlocked.Exchange(
                    ref _dungeonRunGeneration,
                    run.RunGeneration);
                Volatile.Write(ref _currentDungeonSelection, null);
                CurrentRun = run;
                DungeonSceneUniqueId = 0;
                return true;
            }
        }

        internal bool IsCurrentDungeonRun(Game.Dungeon.DungeonRunIdentity identity)
        {
            var run = CurrentRun;
            return run != null && run.Matches(identity);
        }

        internal bool IsCurrentDungeonRoom(Game.Dungeon.DungeonRoomIdentity identity)
        {
            var run = CurrentRun;
            return run != null && run.Matches(identity);
        }

        internal bool IsCurrentDungeonParticipantRoom(
            Game.Dungeon.DungeonParticipantRoomIdentity identity)
        {
            var run = CurrentRun;
            return run != null && run.Matches(identity);
        }

        internal Game.Dungeon.DungeonSelectionContext BeginDungeonSelection(
            Game.Dungeon.DungeonTownReturnAnchor returnAnchor,
            bool isA21TutorialEntry = false)
            => BeginDungeonSelection(
                returnAnchor,
                isA21TutorialEntry,
                partyCohort: null,
                out _);

        internal Game.Dungeon.DungeonSelectionContext BeginDungeonSelection(
            Game.Dungeon.DungeonTownReturnAnchor returnAnchor,
            bool isA21TutorialEntry,
            Game.Dungeon.DungeonPartySelectionCohort partyCohort,
            out bool created)
        {
            created = false;
            lock (DungeonRunLifecycleSyncRoot)
            {
                if (CurrentRun != null)
                    return null;

                var current = CurrentDungeonSelection;
                if (current != null)
                {
                    return current.RunGeneration == CurrentDungeonRunGeneration
                        && !current.IsReturning
                        ? current
                        : null;
                }

                var context = new Game.Dungeon.DungeonSelectionContext(
                    Interlocked.Increment(ref _dungeonSelectionGeneration),
                    CurrentDungeonRunGeneration,
                    returnAnchor,
                    isA21TutorialEntry,
                    partyCohort);
                Volatile.Write(ref _currentDungeonSelection, context);
                created = true;
                return context;
            }
        }

        internal bool TryReplaceDungeonSelectionWithRun(
            Game.Dungeon.DungeonSelectionContext expectedSelection,
            Func<long, Game.Dungeon.DungeonRun> runFactory,
            out Game.Dungeon.DungeonRun run)
        {
            run = null;
            if (expectedSelection == null || runFactory == null)
                return false;

            lock (DungeonRunLifecycleSyncRoot)
            {
                if (CurrentRun != null
                    || !ReferenceEquals(
                        CurrentDungeonSelection,
                        expectedSelection)
                    || CurrentDungeonRunGeneration
                        != expectedSelection.RunGeneration
                    || expectedSelection.IsReturning)
                {
                    return false;
                }

                run = runFactory(NextDungeonRunGeneration())
                    ?? throw new InvalidOperationException(
                        "Dungeon run factory returned null.");
                Volatile.Write(ref _currentDungeonSelection, null);
                CurrentRun = run;
                return true;
            }
        }

        internal bool IsCurrentDungeonSelection(
            Game.Dungeon.DungeonSelectionContext expected)
        {
            return expected != null
                && ReferenceEquals(CurrentDungeonSelection, expected)
                && CurrentRun == null
                && CurrentDungeonRunGeneration == expected.RunGeneration;
        }

        internal void ClearDungeonSelection()
        {
            Volatile.Write(ref _currentDungeonSelection, null);
        }

        internal bool TryInvalidateDungeonSelection(
            Game.Dungeon.DungeonSelectionContext expected)
        {
            return expected != null
                && ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _currentDungeonSelection,
                        null,
                        expected),
                    expected);
        }

        internal void CompleteDungeonSelection(
            Game.Dungeon.DungeonSelectionContext expected)
        {
            if (expected != null && expected.TryCompleteReturn())
            {
                Interlocked.CompareExchange(
                    ref _currentDungeonSelection,
                    null,
                    expected);
            }
        }

        // One-shot linked-dungeon authorization survives the result-screen town
        // transition, but is never persisted and is cleared on character teardown.
        internal object LinkedDungeonEntryAuthorizationSyncRoot { get; } = new object();
        internal Game.Dungeon.LinkedDungeonEntryAuthorization
            PendingLinkedDungeonEntryAuthorization { get; set; }

        // Serialize owner-side ENTER_SELECT capture/create so a duplicate
        // request cannot freeze a second party cohort while the first one is
        // still projecting its selection state.
        internal SemaphoreSlim DungeonRunTransitionGate { get; } =
            new SemaphoreSlim(1, 1);

        internal void MarkPendingPartyRetryEntry(int dungeonId) =>
            Interlocked.Exchange(
                ref _pendingPartyRetryDungeonId,
                Math.Max(0, dungeonId));

        internal bool ConsumePendingPartyRetryEntry(int dungeonId) =>
            dungeonId > 0
            && Interlocked.Exchange(ref _pendingPartyRetryDungeonId, 0)
                == dungeonId;

        internal void ClearPendingPartyRetryEntry() =>
            Interlocked.Exchange(ref _pendingPartyRetryDungeonId, 0);

        // ---- 跨局存活字段(刻意不随 run 重建) ----

        // 深渊华丽挑战 UI 开关: 在选图界面(进本之前)切换。
        public bool HellPartyGorgeousChallengeEnabled { get; set; }

        // Pet satiety runtime anchors. DungeonRun is recreated per run, but town recovery
        // spans the time between runs, so these stay on the player context.
        public DateTime PetCreatureSatietyDungeonStartUtc { get; set; } = DateTime.MinValue;
        public short PetCreatureSatietyDungeonId { get; set; }
        public DateTime PetCreatureSatietyTownStartUtc { get; set; } = DateTime.MinValue;
        public int PetCreatureLastDeathCreatureKey { get; set; }
        public int PetCreatureDeathTimerVersion { get; set; }

        public ushort DungeonSceneUniqueId { get; set; }
    }
}
