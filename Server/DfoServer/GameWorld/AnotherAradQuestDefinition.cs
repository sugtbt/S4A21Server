using System;
using System.Collections.Generic;
using DfoServer.Game.Quests;

namespace DfoServer.GameWorld
{
    internal enum AnotherAradQuestKind
    {
        Hunt = 0,
        Clear = 1,
        ClearMap = 2,
        TimedClear = 3,
        Locations = 4,
    }

    internal sealed class AnotherAradHuntRequirement
    {
        internal AnotherAradHuntRequirement(
            int dungeonSelector,
            int minimumDifficulty,
            int actorSelector,
            int enemyType,
            int requiredCount,
            int channelIndex)
        {
            DungeonSelector = dungeonSelector;
            MinimumDifficulty = minimumDifficulty;
            ActorSelector = actorSelector;
            EnemyType = enemyType;
            RequiredCount = requiredCount;
            ChannelIndex = channelIndex;
        }

        internal int DungeonSelector { get; }
        internal int MinimumDifficulty { get; }
        internal int ActorSelector { get; }
        internal int EnemyType { get; }
        internal int RequiredCount { get; }
        internal int ChannelIndex { get; }
    }

    internal sealed class AnotherAradQuestDefinition
    {
        private AnotherAradQuestDefinition(
            ushort questId,
            int historicalDungeonId,
            string name,
            AnotherAradQuestKind kind,
            IReadOnlyList<AnotherAradHuntRequirement> huntRequirements,
            int minimumDifficulty,
            int clearTargetId,
            int requiredLocationCount,
            int timeLimitSeconds,
            bool requireNoRevive)
        {
            QuestId = questId;
            HistoricalDungeonId = historicalDungeonId;
            Name = name ?? string.Empty;
            Kind = kind;
            HuntRequirements = huntRequirements
                ?? Array.Empty<AnotherAradHuntRequirement>();
            MinimumDifficulty = minimumDifficulty;
            ClearTargetId = clearTargetId;
            RequiredLocationCount = requiredLocationCount;
            TimeLimitSeconds = timeLimitSeconds;
            RequireNoRevive = requireNoRevive;
        }

        internal ushort QuestId { get; }
        internal int HistoricalDungeonId { get; }
        internal string Name { get; }
        internal AnotherAradQuestKind Kind { get; }
        internal IReadOnlyList<AnotherAradHuntRequirement> HuntRequirements
        {
            get;
        }
        internal int MinimumDifficulty { get; }
        internal int ClearTargetId { get; }
        internal int RequiredLocationCount { get; }
        internal int TimeLimitSeconds { get; }
        internal bool RequireNoRevive { get; }

        internal QuestFinishType FinishType => Kind == AnotherAradQuestKind.Hunt
            ? QuestData.NormalizeQuestTag(
                    QuestData.GetQuestFile(QuestId)?.Type) == "hunt enemy"
                ? QuestFinishType.HuntEnemy
                : QuestFinishType.HuntMonster
            : QuestFinishType.ConditionUnderClear;

        internal uint InitialTrigger
        {
            get
            {
                if (Kind == AnotherAradQuestKind.Hunt)
                {
                    var first = HuntRequirements.Count > 0
                        ? HuntRequirements[0].RequiredCount
                        : 0;
                    var second = HuntRequirements.Count > 1
                        ? HuntRequirements[1].RequiredCount
                        : 0;
                    var third = HuntRequirements.Count > 2
                        ? HuntRequirements[2].RequiredCount
                        : 0;
                    return PackTrigger(first, second, third);
                }

                if (Kind == AnotherAradQuestKind.Locations)
                    return PackTrigger(RequiredLocationCount, 0, 0);

                return 1;
            }
        }

        internal static bool TryCreate(
            int questId,
            int historicalDungeonId,
            out AnotherAradQuestDefinition definition,
            out string reason)
        {
            definition = null;
            reason = string.Empty;
            if (questId <= 0
                || questId > ushort.MaxValue
                || historicalDungeonId <= 0)
            {
                reason = "invalid_identity";
                return false;
            }

            var quest = QuestData.GetQuestFile(questId);
            if (quest == null)
            {
                reason = "quest_missing";
                return false;
            }
            if (QuestData.NormalizeQuestTag(quest.RewardType)
                != "crack of dimension")
            {
                reason = "reward_type_mismatch";
                return false;
            }
            if (!QuestCatalog.TryGetPath(questId, out var path)
                || !IsCrackQuestPath(path))
            {
                reason = "quest_path_mismatch";
                return false;
            }

            var type = QuestData.NormalizeQuestTag(quest.Type);
            var values = QuestData.ParseIntList(quest.IntData);
            if (type == "hunt monster" || type == "hunt enemy")
            {
                var stride = type == "hunt enemy" ? 5 : 4;
                if (values.Count == 0
                    || values.Count % stride != 0
                    || values.Count / stride > 3)
                {
                    reason = "hunt_shape_invalid";
                    return false;
                }

                var requirements = new List<AnotherAradHuntRequirement>();
                for (var offset = 0; offset < values.Count; offset += stride)
                {
                    var dungeonSelector = values[offset];
                    var minimumDifficulty = values[offset + 1];
                    var actorSelector = values[offset + 2];
                    var enemyType = type == "hunt enemy"
                        ? values[offset + 3]
                        : 0;
                    var requiredCount = type == "hunt enemy"
                        ? values[offset + 4]
                        : values[offset + 3];
                    if ((dungeonSelector != -1
                            && dungeonSelector != historicalDungeonId)
                        || minimumDifficulty < -1
                        || !IsSupportedActorSelector(actorSelector)
                        || enemyType < 0
                        || requiredCount <= 0
                        || requiredCount > 0x1FF)
                    {
                        reason = "hunt_requirement_invalid";
                        return false;
                    }

                    requirements.Add(new AnotherAradHuntRequirement(
                        dungeonSelector,
                        minimumDifficulty,
                        actorSelector,
                        enemyType,
                        requiredCount,
                        offset / stride));
                }

                definition = new AnotherAradQuestDefinition(
                    (ushort)questId,
                    historicalDungeonId,
                    quest.Name,
                    AnotherAradQuestKind.Hunt,
                    requirements,
                    -1,
                    0,
                    0,
                    0,
                    false);
                reason = "ok_hunt";
                return true;
            }

            if (type == "clear map")
            {
                if (values.Count < 1
                    || values.Count > 2
                    || values[0] <= 0
                    || (values.Count == 2 && values[1] < 0))
                {
                    reason = "clear_map_shape_invalid";
                    return false;
                }

                definition = new AnotherAradQuestDefinition(
                    (ushort)questId,
                    historicalDungeonId,
                    quest.Name,
                    AnotherAradQuestKind.ClearMap,
                    null,
                    -1,
                    values[0],
                    0,
                    0,
                    false);
                reason = "ok_clear_map";
                return true;
            }

            if (type != "condition under clear")
            {
                reason = "unsupported_type:" + type;
                return false;
            }

            if (quest.SubType == 4 || quest.SubType == 6)
            {
                if (values.Count != 2
                    || values[0] != historicalDungeonId
                    || values[1] < -1)
                {
                    reason = "clear_shape_invalid";
                    return false;
                }

                definition = new AnotherAradQuestDefinition(
                    (ushort)questId,
                    historicalDungeonId,
                    quest.Name,
                    AnotherAradQuestKind.Clear,
                    null,
                    values[1],
                    0,
                    0,
                    0,
                    quest.SubType == 4);
                reason = "ok_clear";
                return true;
            }

            if (quest.SubType == 0 || quest.SubType == 11)
            {
                var locations = quest.SubType == 11;
                if (values.Count != 3
                    || values[0] != historicalDungeonId
                    || values[1] < -1
                    || values[2] <= 0
                    || (locations && values[2] > 0x1FF))
                {
                    reason = "conditional_shape_invalid";
                    return false;
                }

                definition = new AnotherAradQuestDefinition(
                    (ushort)questId,
                    historicalDungeonId,
                    quest.Name,
                    locations
                        ? AnotherAradQuestKind.Locations
                        : AnotherAradQuestKind.TimedClear,
                    null,
                    values[1],
                    0,
                    locations ? values[2] : 0,
                    locations ? 0 : values[2],
                    false);
                reason = locations ? "ok_locations" : "ok_timed_clear";
                return true;
            }

            reason = "unsupported_clear_subtype:" + quest.SubType;
            return false;
        }

        private static bool IsSupportedActorSelector(int selector)
            => selector > 0 || selector == -3 || selector == -5 || selector == -11;

        private static uint PackTrigger(int first, int second, int third)
            => (uint)(((third & 0x1FF) << 18)
                | ((second & 0x1FF) << 9)
                | (first & 0x1FF));

        private static bool IsCrackQuestPath(string path)
        {
            var normalized = (path ?? string.Empty)
                .Replace('\\', '/')
                .TrimStart('/')
                .ToLowerInvariant();
            return normalized.StartsWith(
                    "n_quest/crackofdimension/",
                    StringComparison.Ordinal)
                || normalized.StartsWith(
                    "n_quest/crack_of_dimension/",
                    StringComparison.Ordinal);
        }
    }
}
