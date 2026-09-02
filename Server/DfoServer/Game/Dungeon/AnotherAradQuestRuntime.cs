using System;
using System.Collections.Generic;
using DfoServer.GameWorld;

namespace DfoServer.Game.Dungeon
{
    internal enum AnotherAradQuestClaimDisposition
    {
        Rejected = 0,
        Reserved = 1,
        AlreadyClaimed = 2,
    }

    internal sealed class AnotherAradQuestRuntime
    {
        private readonly object _syncRoot = new object();
        private readonly int[] _huntRemaining;
        private readonly HashSet<Guid> _observedDeaths = new HashSet<Guid>();
        private readonly HashSet<(int X, int Y)> _clearedCells =
            new HashSet<(int X, int Y)>();
        private DateTime _acceptedUtc;
        private bool _accepted;
        private bool _reviveUsed;
        private bool _completed;
        private bool _settlementEvaluated;
        private bool _claimReserved;
        private bool _rewardClaimed;

        internal AnotherAradQuestRuntime(AnotherAradQuestDefinition definition)
        {
            Definition = definition
                ?? throw new ArgumentNullException(nameof(definition));
            _huntRemaining = new int[definition.HuntRequirements.Count];
            for (var index = 0; index < _huntRemaining.Length; index++)
            {
                _huntRemaining[index] =
                    definition.HuntRequirements[index].RequiredCount;
            }
        }

        internal AnotherAradQuestDefinition Definition { get; }

        internal bool Accepted
        {
            get { lock (_syncRoot) return _accepted; }
        }

        internal bool Completed
        {
            get { lock (_syncRoot) return _completed; }
        }

        internal bool SettlementEvaluated
        {
            get { lock (_syncRoot) return _settlementEvaluated; }
        }

        internal bool RewardClaimed
        {
            get { lock (_syncRoot) return _rewardClaimed; }
        }

        internal uint CurrentTrigger
        {
            get { lock (_syncRoot) return BuildCurrentTriggerLocked(); }
        }

        internal bool TryAccept(DateTime acceptedUtc, out uint trigger, out bool duplicate)
        {
            lock (_syncRoot)
            {
                duplicate = _accepted;
                if (!_accepted)
                {
                    _accepted = true;
                    _acceptedUtc = acceptedUtc == DateTime.MinValue
                        ? DateTime.UtcNow
                        : acceptedUtc.ToUniversalTime();
                }
                trigger = BuildCurrentTriggerLocked();
                return true;
            }
        }

        internal bool TryRecordActorDeath(
            Guid sourceEventId,
            int dungeonId,
            int difficulty,
            int actorCode,
            byte actorType,
            int enemyType,
            bool isHostile,
            bool isBlocking,
            out uint trigger)
        {
            lock (_syncRoot)
            {
                trigger = BuildCurrentTriggerLocked();
                if (!_accepted
                    || _settlementEvaluated
                    || Definition.Kind != AnotherAradQuestKind.Hunt
                    || sourceEventId == Guid.Empty
                    || actorCode <= 0
                    || dungeonId != Definition.HistoricalDungeonId
                    || !_observedDeaths.Add(sourceEventId))
                {
                    return false;
                }

                for (var index = 0;
                    index < Definition.HuntRequirements.Count;
                    index++)
                {
                    var requirement = Definition.HuntRequirements[index];
                    if (_huntRemaining[index] <= 0
                        || (requirement.DungeonSelector >= 0
                            && requirement.DungeonSelector != dungeonId)
                        || (requirement.MinimumDifficulty >= 0
                            && difficulty < requirement.MinimumDifficulty)
                        || (requirement.EnemyType > 0
                            && requirement.EnemyType != enemyType)
                        || !MatchesActorSelector(
                            requirement.ActorSelector,
                            actorCode,
                            actorType,
                            isHostile,
                            isBlocking))
                    {
                        continue;
                    }

                    _huntRemaining[index]--;
                    _completed = AreHuntRequirementsCompleteLocked();
                    trigger = BuildCurrentTriggerLocked();
                    return true;
                }

                trigger = BuildCurrentTriggerLocked();
                return false;
            }
        }

        internal bool TryRecordRoomClear(
            int x,
            int y,
            int mapId,
            out uint trigger)
        {
            lock (_syncRoot)
            {
                trigger = BuildCurrentTriggerLocked();
                if (!_accepted
                    || _settlementEvaluated
                    || (Definition.Kind != AnotherAradQuestKind.Locations
                        && Definition.Kind != AnotherAradQuestKind.ClearMap))
                {
                    return false;
                }

                if (Definition.Kind == AnotherAradQuestKind.ClearMap)
                {
                    if (mapId <= 0 || mapId != Definition.ClearTargetId)
                        return false;
                    _completed = true;
                    trigger = 0;
                    return true;
                }

                if (!_clearedCells.Add((x, y)))
                    return false;

                trigger = BuildCurrentTriggerLocked();
                return true;
            }
        }

        internal void MarkReviveUsed()
        {
            lock (_syncRoot)
            {
                if (_accepted && !_settlementEvaluated)
                    _reviveUsed = true;
            }
        }

        internal bool EvaluateSettlement(
            int dungeonId,
            int difficulty,
            DateTime observedUtc,
            out uint trigger)
        {
            lock (_syncRoot)
            {
                if (_settlementEvaluated)
                {
                    trigger = BuildCurrentTriggerLocked();
                    return _completed;
                }

                if (!_accepted)
                {
                    trigger = BuildCurrentTriggerLocked();
                    return false;
                }

                _settlementEvaluated = true;
                var scopeMatches = dungeonId == Definition.HistoricalDungeonId
                    && (Definition.MinimumDifficulty < 0
                        || difficulty >= Definition.MinimumDifficulty);
                switch (Definition.Kind)
                {
                    case AnotherAradQuestKind.Hunt:
                        _completed = scopeMatches
                            && AreHuntRequirementsCompleteLocked();
                        break;

                    case AnotherAradQuestKind.Clear:
                        _completed = scopeMatches
                            && (!Definition.RequireNoRevive || !_reviveUsed);
                        break;

                    case AnotherAradQuestKind.ClearMap:
                        _completed = scopeMatches
                            && (_completed
                                || Definition.ClearTargetId
                                    == Definition.HistoricalDungeonId);
                        break;

                    case AnotherAradQuestKind.TimedClear:
                    {
                        var now = observedUtc == DateTime.MinValue
                            ? DateTime.UtcNow
                            : observedUtc.ToUniversalTime();
                        var elapsed = now - _acceptedUtc;
                        _completed = scopeMatches
                            && elapsed >= TimeSpan.Zero
                            && elapsed <= TimeSpan.FromSeconds(
                                Definition.TimeLimitSeconds);
                        break;
                    }

                    case AnotherAradQuestKind.Locations:
                        _completed = scopeMatches
                            && _clearedCells.Count
                                >= Definition.RequiredLocationCount;
                        break;
                }

                trigger = BuildCurrentTriggerLocked();
                return _completed;
            }
        }

        internal AnotherAradQuestClaimDisposition TryReserveRewardClaim()
        {
            lock (_syncRoot)
            {
                if (_rewardClaimed)
                    return AnotherAradQuestClaimDisposition.AlreadyClaimed;
                if (!_accepted
                    || !_settlementEvaluated
                    || !_completed
                    || _claimReserved)
                {
                    return AnotherAradQuestClaimDisposition.Rejected;
                }

                _claimReserved = true;
                return AnotherAradQuestClaimDisposition.Reserved;
            }
        }

        internal void CommitRewardClaim()
        {
            lock (_syncRoot)
            {
                if (!_claimReserved)
                    throw new InvalidOperationException(
                        "Another Arad reward claim was not reserved.");
                _claimReserved = false;
                _rewardClaimed = true;
            }
        }

        internal void AbortRewardClaim()
        {
            lock (_syncRoot)
                _claimReserved = false;
        }

        private bool AreHuntRequirementsCompleteLocked()
        {
            if (_huntRemaining.Length == 0)
                return false;
            foreach (var remaining in _huntRemaining)
            {
                if (remaining > 0)
                    return false;
            }
            return true;
        }

        private uint BuildCurrentTriggerLocked()
        {
            if (Definition.Kind == AnotherAradQuestKind.Hunt)
            {
                return PackTrigger(
                    _huntRemaining.Length > 0 ? _huntRemaining[0] : 0,
                    _huntRemaining.Length > 1 ? _huntRemaining[1] : 0,
                    _huntRemaining.Length > 2 ? _huntRemaining[2] : 0);
            }

            if (Definition.Kind == AnotherAradQuestKind.Locations)
            {
                return PackTrigger(
                    Math.Max(
                        0,
                        Definition.RequiredLocationCount - _clearedCells.Count),
                    0,
                    0);
            }

            return _completed ? 0u : 1u;
        }

        private static bool MatchesActorSelector(
            int selector,
            int actorCode,
            byte actorType,
            bool isHostile,
            bool isBlocking)
        {
            if (selector > 0)
                return actorCode == selector;
            if (selector == -5)
                return isHostile && isBlocking;
            if (selector == -3)
                return actorType == 3 || actorType == 8;
            if (selector == -11)
            {
                return actorType == 1
                    || actorType == 2
                    || actorType == 3
                    || actorType == 6
                    || actorType == 7
                    || actorType == 8;
            }
            return false;
        }

        private static uint PackTrigger(int first, int second, int third)
            => (uint)(((third & 0x1FF) << 18)
                | ((second & 0x1FF) << 9)
                | (first & 0x1FF));
    }
}
