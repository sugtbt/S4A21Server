using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.Dungeon
{
    internal sealed class AntonNormalSequence
    {
        private readonly List<int> _dungeonIds;

        internal AntonNormalSequence(
            int configKey,
            byte difficulty,
            IEnumerable<int> dungeonIds)
        {
            ConfigKey = configKey;
            Difficulty = difficulty;
            _dungeonIds = dungeonIds.ToList();
        }

        internal int ConfigKey { get; }
        internal byte Difficulty { get; }
        internal IReadOnlyList<int> DungeonIds => _dungeonIds;
        internal int IndexOf(int dungeonId) => _dungeonIds.IndexOf(dungeonId);
    }

    internal sealed class AntonNormalClearPlan
    {
        internal AntonNormalClearPlan(
            AntonNormalSequence sequence,
            int currentIndex,
            int nextDungeonId,
            int previewDungeonId)
        {
            Sequence = sequence;
            CurrentIndex = currentIndex;
            NextDungeonId = nextDungeonId;
            PreviewDungeonId = previewDungeonId;
        }

        internal AntonNormalSequence Sequence { get; }
        internal int CurrentIndex { get; }
        internal int NextDungeonId { get; }
        internal int PreviewDungeonId { get; }
    }

    internal sealed class AntonNormalSyncState
    {
        internal AntonNormalSyncState(
            AntonNormalSequence sequence,
            byte progressIndex,
            List<DungeonPermissionEntrySnapshot> permissionEntries)
        {
            Sequence = sequence;
            ProgressIndex = progressIndex;
            PermissionEntries = permissionEntries;
        }

        internal AntonNormalSequence Sequence { get; }
        internal byte ProgressIndex { get; }
        internal List<DungeonPermissionEntrySnapshot> PermissionEntries { get; }
    }

    internal static class AntonNormalConquest
    {
        private static readonly Lazy<IReadOnlyList<AntonNormalSequence>> Sequences =
            new Lazy<IReadOnlyList<AntonNormalSequence>>(LoadSequences);

        internal static bool TryResolveClearPlan(
            int clearedDungeonId,
            out AntonNormalClearPlan plan)
        {
            plan = null;
            if (!TryGetSequence(clearedDungeonId, out var sequence))
                return false;

            var currentIndex = sequence.IndexOf(clearedDungeonId);
            if (currentIndex < 0)
                return false;

            var nextDungeonId = currentIndex + 1 < sequence.DungeonIds.Count
                ? sequence.DungeonIds[currentIndex + 1]
                : 0;
            var previewDungeonId = currentIndex + 2 < sequence.DungeonIds.Count
                ? sequence.DungeonIds[currentIndex + 2]
                : 0;
            plan = new AntonNormalClearPlan(
                sequence,
                currentIndex,
                nextDungeonId,
                previewDungeonId);
            return true;
        }

        internal static bool TryResolveLinkedNext(
            int dungeonId,
            out int nextDungeonId)
        {
            nextDungeonId = 0;
            if (!TryResolveClearPlan(dungeonId, out var plan)
                || plan.NextDungeonId <= 0)
            {
                return false;
            }

            nextDungeonId = plan.NextDungeonId;
            return true;
        }

        internal static bool TryResolveSyncState(
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> permissions,
            out AntonNormalSyncState state)
        {
            state = null;
            if (permissions == null || permissions.Count == 0)
                return false;

            var clearStates = GroupClearStates(permissions);
            foreach (var sequence in Sequences.Value)
            {
                if (TryResolveSequenceState(sequence, clearStates, out state))
                    return true;
            }

            return false;
        }

        // CMD SEQUENTIAL_DUNGEON_INFO(0x035D) 应答只解析客户端询问的那一条
        // 序列; 不同 area key 可能共享副本(如 key=41 与 key=28), 不能用
        // "第一条匹配序列"代替。
        internal static bool TryResolveSyncState(
            int configKey,
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> permissions,
            out AntonNormalSyncState state)
        {
            state = null;
            if (permissions == null || permissions.Count == 0
                || !TryGetSequenceByKey(configKey, out var sequence))
            {
                return false;
            }

            return TryResolveSequenceState(
                sequence,
                GroupClearStates(permissions),
                out state);
        }

        private static Dictionary<int, byte> GroupClearStates(
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> permissions)
            => permissions
                .GroupBy(entry => (int)entry.DungeonId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Max(entry => entry.ClearState));

        private static bool TryResolveSequenceState(
            AntonNormalSequence sequence,
            IReadOnlyDictionary<int, byte> clearStates,
            out AntonNormalSyncState state)
        {
            state = null;
            if (sequence.DungeonIds.Count < 2
                || !TryResolveCompletedState(
                    sequence.DungeonIds[0],
                    sequence.Difficulty,
                    out var firstCompletedState)
                || !clearStates.TryGetValue(
                    sequence.DungeonIds[0],
                    out var persistedFirstState)
                || persistedFirstState < firstCompletedState)
            {
                return false;
            }

            var highestOpenIndex = 0;
            for (var index = 1; index < sequence.DungeonIds.Count; index++)
            {
                var dungeonId = sequence.DungeonIds[index];
                if (!TryResolveUnlockedState(
                        dungeonId,
                        sequence.Difficulty,
                        out var unlockedState)
                    || !clearStates.TryGetValue(
                        dungeonId,
                        out var persistedState)
                    || persistedState < unlockedState)
                {
                    break;
                }

                highestOpenIndex = index;
            }

            var restoredIndex = highestOpenIndex;
            var finalDungeonId = sequence.DungeonIds[
                sequence.DungeonIds.Count - 1];
            if (highestOpenIndex == sequence.DungeonIds.Count - 1
                && TryResolveCompletedState(
                    finalDungeonId,
                    sequence.Difficulty,
                    out var finalCompletedState)
                && clearStates.TryGetValue(
                    finalDungeonId,
                    out var persistedFinalState)
                && persistedFinalState >= finalCompletedState)
            {
                restoredIndex = sequence.DungeonIds.Count;
            }

            if (restoredIndex > byte.MaxValue)
                return false;

            var progressIndex = (byte)restoredIndex;
            state = new AntonNormalSyncState(
                sequence,
                progressIndex,
                BuildVisiblePermissionEntries(
                    clearStates,
                    sequence,
                    progressIndex));
            return true;
        }

        internal static bool TryGetSequence(
            int dungeonId,
            out AntonNormalSequence sequence)
        {
            sequence = Sequences.Value.FirstOrDefault(
                candidate => candidate.IndexOf(dungeonId) >= 0);
            return sequence != null;
        }

        internal static bool TryGetSequenceByKey(
            int configKey,
            out AntonNormalSequence sequence)
        {
            sequence = Sequences.Value.FirstOrDefault(
                candidate => candidate.ConfigKey == configKey);
            return sequence != null;
        }

        internal static bool TryResolveCompletedState(
            int dungeonId,
            byte difficulty,
            out byte clearState)
            => TryResolvePermissionState(
                dungeonId,
                difficulty + 1,
                out clearState);

        internal static bool TryResolveUnlockedState(
            int dungeonId,
            byte difficulty,
            out byte clearState)
            => TryResolvePermissionState(
                dungeonId,
                difficulty,
                out clearState);

        private static List<DungeonPermissionEntrySnapshot>
            BuildVisiblePermissionEntries(
                IReadOnlyDictionary<int, byte> clearStates,
                AntonNormalSequence sequence,
                byte progressIndex)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();
            var visibleLimit = progressIndex >= sequence.DungeonIds.Count - 1
                ? sequence.DungeonIds.Count - 1
                : progressIndex + 1;

            for (var index = 0;
                index <= visibleLimit && index < sequence.DungeonIds.Count;
                index++)
            {
                var dungeonId = sequence.DungeonIds[index];
                byte clearState;
                if (clearStates.TryGetValue(dungeonId, out var persistedState)
                    && persistedState > 0)
                {
                    clearState = persistedState;
                }
                else
                {
                    if (index <= progressIndex
                        || !TryResolveUnlockedState(
                            dungeonId,
                            sequence.Difficulty,
                            out var unlockedState))
                    {
                        continue;
                    }

                    clearState = (byte)Math.Max(1, unlockedState - 1);
                }

                result.Add(new DungeonPermissionEntrySnapshot
                {
                    DungeonId = (ushort)dungeonId,
                    ClearState = clearState,
                });
            }

            return result;
        }

        private static bool TryResolvePermissionState(
            int dungeonId,
            int requestedState,
            out byte clearState)
        {
            clearState = 0;
            if (dungeonId <= 0 || dungeonId > ushort.MaxValue)
                return false;

            try
            {
                var maxClearState =
                    DungeonData.GetMaxDifficultyCount(dungeonId) - 1;
                if (maxClearState <= 0)
                    return false;

                clearState = (byte)Math.Min(
                    Math.Max(1, requestedState),
                    maxClearState);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IReadOnlyList<AntonNormalSequence> LoadSequences()
        {
            var result = new List<AntonNormalSequence>();
            foreach (var area in GameWorld.WorldMap.Areas)
            {
                if (area == null || area.AreaId <= 0)
                    continue;

                var dungeonIds = area.Dungeons
                    .Where(entry => entry != null
                        && !entry.InProgressOnly
                        && entry.HasExplicitQuestId
                        && entry.QuestId == -1
                        && entry.DungeonId > 0
                        && entry.DungeonId <= ushort.MaxValue)
                    .Select(entry => entry.DungeonId)
                    .Distinct()
                    .ToList();
                if (dungeonIds.Count < 2)
                    continue;

                HashSet<int> commonDifficulties = null;
                var valid = true;
                foreach (var dungeonId in dungeonIds)
                {
                    try
                    {
                        var dungeonFile = DungeonData.GetDungeonFile(dungeonId);
                        if (dungeonFile == null
                            || !dungeonFile.HasTag("anton dungeon"))
                        {
                            valid = false;
                            break;
                        }

                        var difficulties = new HashSet<int>(
                            (dungeonFile.DesignateDungeonDifficulty
                                ?? Array.Empty<int>())
                            .Where(value => value >= 0 && value <= 4));
                        if (difficulties.Count == 0)
                        {
                            valid = false;
                            break;
                        }

                        if (commonDifficulties == null)
                            commonDifficulties = difficulties;
                        else
                            commonDifficulties.IntersectWith(difficulties);
                    }
                    catch
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid
                    || commonDifficulties == null
                    || commonDifficulties.Count != 1)
                {
                    continue;
                }

                var sequence = new AntonNormalSequence(
                    area.AreaId,
                    (byte)commonDifficulties.Single(),
                    dungeonIds);
                result.Add(sequence);
                FileLogger.Log(
                    $"[AntonNormal] sequence loaded: " +
                    $"key={sequence.ConfigKey} " +
                    $"difficulty={sequence.Difficulty} " +
                    $"dungeons={string.Join(",", sequence.DungeonIds)}");
            }

            return result;
        }
    }
}
