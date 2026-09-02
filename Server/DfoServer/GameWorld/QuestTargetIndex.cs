using System.Collections.Generic;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal enum HuntMonsterTargetKind
    {
        ExactMonsterCode = 0,
        AnyOrdinaryMonster = 1,
        AnyEliteMonster = 2,
        AnyBossMonster = 3,
    }

    internal sealed class HuntMonsterQuestTarget
    {
        internal const int AnyOrdinaryMonsterCode = -1;
        internal const int AnyEliteMonsterCode = -2;
        internal const int AnyBossMonsterCode = -3;

        public int QuestId;
        public int DungeonId;
        public int MinimumDifficulty;
        public int MonsterCode;
        public int RequiredCount;
        public int ChannelIndex;

        internal HuntMonsterTargetKind Kind => MonsterCode == AnyOrdinaryMonsterCode
            ? HuntMonsterTargetKind.AnyOrdinaryMonster
            : MonsterCode == AnyEliteMonsterCode
                ? HuntMonsterTargetKind.AnyEliteMonster
                : MonsterCode == AnyBossMonsterCode
                    ? HuntMonsterTargetKind.AnyBossMonster
                    : HuntMonsterTargetKind.ExactMonsterCode;

        internal static bool IsSupportedMonsterCode(int monsterCode)
        {
            return monsterCode > 0
                || monsterCode == AnyOrdinaryMonsterCode
                || monsterCode == AnyEliteMonsterCode
                || monsterCode == AnyBossMonsterCode;
        }
    }

    internal sealed class WorldMapHuntMonsterQuestTarget
    {
        internal WorldMapHuntMonsterQuestTarget(
            int worldMapId,
            int minimumDifficulty,
            int selector,
            int requiredCount,
            int channelIndex)
        {
            WorldMapId = worldMapId;
            MinimumDifficulty = minimumDifficulty;
            Selector = selector;
            RequiredCount = requiredCount;
            ChannelIndex = channelIndex;
        }

        internal int WorldMapId { get; }
        internal int MinimumDifficulty { get; }
        internal int Selector { get; }
        internal int RequiredCount { get; }
        internal int ChannelIndex { get; }
    }

    internal enum HuntEnemyProgressSource
    {
        Invalid = 0,
        Server = 1,
        Client = 2,
    }

    internal sealed class HuntEnemyQuestTarget
    {
        internal HuntEnemyQuestTarget(
            int questId,
            int dungeonId,
            int minimumDifficulty,
            int enemyCode,
            int enemyType,
            int requiredCount,
            int channelIndex,
            HuntEnemyProgressSource progressSource)
        {
            QuestId = questId;
            DungeonId = dungeonId;
            MinimumDifficulty = minimumDifficulty;
            EnemyCode = enemyCode;
            EnemyType = enemyType;
            RequiredCount = requiredCount;
            ChannelIndex = channelIndex;
            ProgressSource = progressSource;
        }

        internal int QuestId { get; }
        internal int DungeonId { get; }
        internal int MinimumDifficulty { get; }
        internal int EnemyCode { get; }
        internal int EnemyType { get; }
        internal int RequiredCount { get; }
        internal int ChannelIndex { get; }
        internal HuntEnemyProgressSource ProgressSource { get; }
    }

    internal sealed class DungeonQuestActorTarget
    {
        public int QuestId;
        public int DungeonId;
        public int MapId;
        public int ActorCode;
        public string Source;
    }

    internal sealed class DungeonNpcItemDropQuestTarget
    {
        public int QuestId;
        public int DungeonId;
        public int Difficulty;
        public List<int> ItemIds = new List<int>();
    }

    internal sealed class ClearMapQuestDefinition
    {
        internal ClearMapQuestDefinition(int targetId, int companionApcId)
        {
            TargetId = targetId;
            CompanionApcId = companionApcId;
        }

        internal int TargetId { get; }
        internal int CompanionApcId { get; }
        internal bool HasCompanion => CompanionApcId > 0;
    }

    internal static class QuestTargetIndex
    {
        internal static List<HuntMonsterQuestTarget> GetHuntMonsterTargets(
            int questId)
        {
            var result = new List<HuntMonsterQuestTarget>();
            var quest = QuestData.GetQuestFile(questId);
            if (quest == null
                || QuestData.NormalizeQuestTag(quest.Type) != "hunt monster")
            {
                return result;
            }

            if (TryGetWorldMapHuntMonsterTargets(quest, out _))
                return result;

            var values = QuestData.ParseIntList(quest.IntData);
            const int stride = 4;
            for (var offset = 0;
                offset + stride <= values.Count;
                offset += stride)
            {
                var dungeonId = values[offset];
                var minimumDifficulty = values[offset + 1];
                var monsterCode = values[offset + 2];
                var requiredCount = values[offset + 3];
                if ((dungeonId <= 0 && dungeonId != -1)
                    || !HuntMonsterQuestTarget.IsSupportedMonsterCode(monsterCode)
                    || requiredCount <= 0)
                {
                    continue;
                }

                result.Add(new HuntMonsterQuestTarget
                {
                    QuestId = questId,
                    DungeonId = dungeonId,
                    MinimumDifficulty = minimumDifficulty,
                    MonsterCode = monsterCode,
                    RequiredCount = requiredCount,
                    ChannelIndex = offset / stride,
                });
            }

            return result;
        }

        internal static bool TryGetWorldMapHuntMonsterTargets(
            int questId,
            out List<WorldMapHuntMonsterQuestTarget> targets)
            => TryGetWorldMapHuntMonsterTargets(
                QuestData.GetQuestFile(questId),
                out targets);

        internal static bool TryGetWorldMapHuntMonsterTargets(
            QuestFile quest,
            out List<WorldMapHuntMonsterQuestTarget> targets)
        {
            targets = new List<WorldMapHuntMonsterQuestTarget>();
            if (quest == null
                || QuestData.NormalizeQuestTag(quest.Type) != "hunt monster")
            {
                return false;
            }

            var values = QuestData.ParseIntList(quest.IntData);
            const int stride = 5;
            if (values.Count == 0
                || values.Count % stride != 0
                || values.Count / stride > 3)
            {
                return false;
            }

            for (var offset = 0; offset < values.Count; offset += stride)
            {
                var worldMapSelector = values[offset];
                var worldMapId = values[offset + 1];
                var minimumDifficulty = values[offset + 2];
                var selector = values[offset + 3];
                var requiredCount = values[offset + 4];
                if (worldMapSelector != -2
                    || worldMapId <= 0
                    || minimumDifficulty < -1
                    || requiredCount <= 0)
                {
                    targets.Clear();
                    return false;
                }

                targets.Add(new WorldMapHuntMonsterQuestTarget(
                    worldMapId,
                    minimumDifficulty,
                    selector,
                    requiredCount,
                    offset / stride));
            }

            return targets.Count > 0;
        }

        internal static bool IsClientWorldMapHuntMonsterTriggerAuthorized(
            int questId,
            byte triggerType,
            bool increment)
        {
            if (increment
                || triggerType == 1
                || !TryGetWorldMapHuntMonsterTargets(
                    questId,
                    out var targets))
            {
                return false;
            }

            if (triggerType == 0)
                return targets.Count > 0;

            if ((triggerType & ~0x70) != 0 || (triggerType & 0x70) == 0)
                return false;

            for (var channelIndex = 0; channelIndex < 3; channelIndex++)
            {
                var channelMask = 0x10 << channelIndex;
                if ((triggerType & channelMask) != 0
                    && channelIndex >= targets.Count)
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool TryRepairWorldMapHuntMonsterTrigger(
            int questId,
            uint trigger,
            out uint repaired)
        {
            repaired = trigger;
            if (!TryGetWorldMapHuntMonsterTargets(
                    questId,
                    out var targets))
            {
                return false;
            }

            foreach (var target in targets)
            {
                var actual = QuestData.GetTriggerChannel(
                    repaired,
                    target.ChannelIndex);
                if (actual > target.RequiredCount)
                {
                    repaired = QuestData.ReplaceTriggerChannel(
                        repaired,
                        target.ChannelIndex,
                        target.RequiredCount);
                }
            }

            return true;
        }

        internal static List<HuntEnemyQuestTarget> GetHuntEnemyTargets(
            int questId)
        {
            return TryParseHuntEnemyTargets(
                questId,
                out var targets,
                out _)
                    ? targets
                    : new List<HuntEnemyQuestTarget>();
        }

        internal static HuntEnemyProgressSource GetHuntEnemyProgressSource(
            int questId)
        {
            return TryParseHuntEnemyTargets(
                questId,
                out _,
                out var source)
                    ? source
                    : HuntEnemyProgressSource.Invalid;
        }

        internal static bool IsServerDrivenHuntEnemyActorType(int enemyType)
            => enemyType == QuestDropProvider.EnemyTypeMonster
                || enemyType == QuestDropProvider.EnemyTypeAiCharacter;

        internal static bool IsClientHuntEnemyTriggerAuthorized(
            int questId,
            byte triggerType)
        {
            if (!TryParseHuntEnemyTargets(
                    questId,
                    out var targets,
                    out var source)
                || source != HuntEnemyProgressSource.Client)
            {
                return false;
            }

            if (triggerType == 0)
            {
                foreach (var target in targets)
                {
                    if (target.EnemyCode > 0
                        && target.EnemyType != 10)
                    {
                        return false;
                    }
                }
                return targets.Count > 0;
            }

            if ((triggerType & ~0x70) != 0 || (triggerType & 0x70) == 0)
                return false;

            for (var channelIndex = 0; channelIndex < 3; channelIndex++)
            {
                var channelMask = 0x10 << channelIndex;
                if ((triggerType & channelMask) == 0)
                    continue;

                var matched = false;
                foreach (var target in targets)
                {
                    if (target.ChannelIndex == channelIndex
                        && target.ProgressSource
                            == HuntEnemyProgressSource.Client)
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                    return false;
            }

            return true;
        }

        internal static List<DungeonQuestActorTarget>
            GetUnfinishedDungeonActorTargets(
                int questId,
                uint trigger,
                int dungeonId,
                int difficulty)
        {
            var result = new List<DungeonQuestActorTarget>();
            if (questId <= 0 || trigger == 0 || dungeonId <= 0)
                return result;

            var seen = new HashSet<(int MapId, int ActorCode)>();
            foreach (var target in GetHuntMonsterTargets(questId))
            {
                if (target.Kind != HuntMonsterTargetKind.ExactMonsterCode
                    || !MatchesHuntMonsterTarget(
                        target,
                        dungeonId,
                        difficulty,
                        target.MonsterCode,
                        monsterType: 0)
                    || QuestData.GetTriggerChannel(trigger, target.ChannelIndex) <= 0
                    || target.MonsterCode <= 0
                    || !seen.Add((-1, target.MonsterCode)))
                {
                    continue;
                }

                result.Add(new DungeonQuestActorTarget
                {
                    QuestId = questId,
                    DungeonId = dungeonId,
                    MapId = -1,
                    ActorCode = target.MonsterCode,
                    Source = "hunt monster",
                });
            }

            var quest = QuestData.GetQuestFile(questId);
            if (quest == null)
                return result;

            foreach (var entry in quest.MonsterRewardItems)
            {
                if (entry.MonsterCode <= 0
                    || !MatchesDungeonScope(
                        entry.DungeonId,
                        entry.Difficulty,
                        dungeonId,
                        difficulty)
                    || !seen.Add((-1, entry.MonsterCode)))
                {
                    continue;
                }

                result.Add(new DungeonQuestActorTarget
                {
                    QuestId = questId,
                    DungeonId = dungeonId,
                    MapId = -1,
                    ActorCode = entry.MonsterCode,
                    Source = "monster reward item",
                });
            }

            foreach (var entry in quest.EnemyRewardItems)
            {
                if (entry.EnemyCode <= 0
                    || !MatchesDungeonScope(
                        entry.DungeonId,
                        entry.Difficulty,
                        dungeonId,
                        difficulty)
                    || !seen.Add((-1, entry.EnemyCode)))
                {
                    continue;
                }

                result.Add(new DungeonQuestActorTarget
                {
                    QuestId = questId,
                    DungeonId = dungeonId,
                    MapId = -1,
                    ActorCode = entry.EnemyCode,
                    Source = "enemy reward item",
                });
            }

            return result;
        }

        internal static bool TryGetNpcItemDropQuestTarget(
            int questId,
            int dungeonId,
            int difficulty,
            out DungeonNpcItemDropQuestTarget target)
        {
            target = null;
            if (questId <= 0 || dungeonId <= 0)
                return false;

            var quest = QuestData.GetQuestFile(questId);
            if (quest == null
                || QuestData.NormalizeQuestTag(quest.Type)
                    != "get item check index")
            {
                return false;
            }

            var dungeonValues = QuestData.ParseIntList(quest.DungeonInfo);
            var matched = false;
            var matchedDungeon = -1;
            var matchedDifficulty = -1;
            for (var offset = 0; offset + 1 < dungeonValues.Count; offset += 2)
            {
                var configuredDungeon = dungeonValues[offset];
                var configuredDifficulty = dungeonValues[offset + 1];
                if (configuredDungeon != -1 && configuredDungeon != dungeonId)
                    continue;
                if (configuredDifficulty != -1
                    && configuredDifficulty != difficulty)
                {
                    continue;
                }

                matched = true;
                matchedDungeon = configuredDungeon;
                matchedDifficulty = configuredDifficulty;
                break;
            }

            if (!matched)
                return false;

            target = new DungeonNpcItemDropQuestTarget
            {
                QuestId = questId,
                DungeonId = matchedDungeon,
                Difficulty = matchedDifficulty,
            };

            var uniqueItemIds = new HashSet<int>();
            foreach (var itemId in QuestData.ParseIntList(quest.IntData))
            {
                if (itemId > 0 && uniqueItemIds.Add(itemId))
                    target.ItemIds.Add(itemId);
            }

            return target.ItemIds.Count > 0;
        }

        internal static bool MatchesHuntMonsterTarget(
            HuntMonsterQuestTarget target,
            int dungeonId,
            int difficulty,
            int monsterCode,
            byte monsterType)
        {
            if (target == null || monsterCode <= 0)
            {
                return false;
            }

            switch (target.Kind)
            {
                case HuntMonsterTargetKind.ExactMonsterCode:
                    if (target.MonsterCode != monsterCode)
                        return false;
                    break;

                case HuntMonsterTargetKind.AnyOrdinaryMonster:
                    if (monsterType != 0)
                        return false;
                    break;

                case HuntMonsterTargetKind.AnyEliteMonster:
                    if (monsterType != 1)
                        return false;
                    break;

                case HuntMonsterTargetKind.AnyBossMonster:
                    if (monsterType != 3)
                        return false;
                    break;

                default:
                    return false;
            }

            if (target.DungeonId != -1
                && target.DungeonId != dungeonId)
            {
                return false;
            }

            return target.MinimumDifficulty < 0
                || difficulty < 0
                || difficulty >= target.MinimumDifficulty;
        }

        internal static bool MatchesHuntEnemyTarget(
            HuntEnemyQuestTarget target,
            int dungeonId,
            int difficulty,
            int enemyCode,
            int enemyType)
        {
            if (target == null
                || enemyCode <= 0
                || enemyType < QuestDropProvider.EnemyTypeMonster
                || enemyType > QuestDropProvider.EnemyTypePassiveObject
                || target.EnemyCode != enemyCode
                || target.EnemyType != enemyType)
            {
                return false;
            }

            if (target.DungeonId != -1
                && target.DungeonId != dungeonId)
            {
                return false;
            }

            return target.MinimumDifficulty < 0
                || difficulty < 0
                || difficulty >= target.MinimumDifficulty;
        }

        internal static bool ReferencesDungeon(int questId, int dungeonId)
        {
            if (questId <= 0 || dungeonId <= 0)
                return false;

            var quest = QuestData.GetQuestFile(questId);
            if (quest == null)
                return false;

            var values = QuestData.ParseIntList(quest.DungeonInfo);
            for (var offset = 0; offset + 1 < values.Count; offset += 2)
            {
                if (values[offset] == dungeonId)
                    return true;
            }

            return false;
        }

        internal static bool IsClearMapQuest(int questId)
            => IsClearMapQuest(QuestData.GetQuestFile(questId));

        internal static bool MatchesClearMapTarget(
            int questId,
            int dungeonId,
            int mapId)
            => MatchesClearMapTarget(
                QuestData.GetQuestFile(questId),
                dungeonId,
                mapId);

        internal static bool MatchesClearMapTarget(
            QuestFile quest,
            int dungeonId,
            int mapId)
            => IsClearMapQuest(quest)
                && MatchesClearMapTargetData(quest.IntData, dungeonId, mapId);

        internal static bool TryGetClearMapDefinition(
            int questId,
            out ClearMapQuestDefinition definition)
        {
            var quest = QuestData.GetQuestFile(questId);
            if (!IsClearMapQuest(quest))
            {
                definition = null;
                return false;
            }

            return TryParseClearMapDefinition(quest.IntData, out definition);
        }

        internal static bool MatchesClearMapTargetData(
            string intData,
            int dungeonId,
            int mapId)
        {
            return TryParseClearMapDefinition(intData, out var definition)
                && ((dungeonId > 0 && definition.TargetId == dungeonId)
                    || (mapId > 0 && definition.TargetId == mapId));
        }

        private static bool TryParseClearMapDefinition(
            string intData,
            out ClearMapQuestDefinition definition)
        {
            definition = null;
            var values = QuestData.ParseIntList(intData);
            if (values.Count < 1 || values.Count > 2 || values[0] <= 0)
                return false;

            var companionApcId = values.Count == 2 ? values[1] : 0;
            if (companionApcId < 0)
                return false;

            definition = new ClearMapQuestDefinition(
                values[0],
                companionApcId);
            return true;
        }

        internal static List<QuestRewardItem> GetSeekingConsumeItems(int questId)
        {
            var quest = QuestData.GetQuestFile(questId);
            if (quest == null || QuestData.IsQuestClearQuest(questId))
                return new List<QuestRewardItem>();

            if (QuestData.IsSeekAndMeetNpcQuest(questId))
                return QuestData.ParseSeekAndMeetNpcItems(quest.IntData);

            if (QuestData.NormalizeQuestTag(quest.Type) != "seeking")
                return new List<QuestRewardItem>();

            var items = QuestData.ParseItemPairs(
                quest.IntData,
                preserveGoldMarker: true);
            items.RemoveAll(item => item.ItemId < 0 || item.Count <= 0);
            return items;
        }

        private static bool MatchesDungeonScope(
            int configuredDungeonId,
            int configuredDifficulty,
            int dungeonId,
            int difficulty)
        {
            if (configuredDungeonId != -1
                && configuredDungeonId != dungeonId)
            {
                return false;
            }

            return configuredDifficulty < 0
                || difficulty < 0
                || configuredDifficulty == difficulty;
        }

        private static bool TryParseHuntEnemyTargets(
            int questId,
            out List<HuntEnemyQuestTarget> targets,
            out HuntEnemyProgressSource source)
        {
            targets = new List<HuntEnemyQuestTarget>();
            source = HuntEnemyProgressSource.Invalid;
            var quest = QuestData.GetQuestFile(questId);
            if (quest == null
                || QuestData.NormalizeQuestTag(quest.Type) != "hunt enemy")
            {
                return false;
            }

            var values = QuestData.ParseIntList(quest.IntData);
            const int stride = 5;
            if (values.Count == 0 || values.Count % stride != 0)
                return false;

            var hasServerTargets = false;
            var hasClientTargets = false;
            for (var offset = 0; offset < values.Count; offset += stride)
            {
                var dungeonId = values[offset];
                var minimumDifficulty = values[offset + 1];
                var enemyCode = values[offset + 2];
                var enemyType = values[offset + 3];
                var requiredCount = values[offset + 4];
                if ((dungeonId <= 0 && dungeonId != -1)
                    || minimumDifficulty < -1
                    || requiredCount <= 0)
                {
                    return false;
                }

                if (!TryResolveHuntEnemyProgressSource(
                        enemyCode,
                        enemyType,
                        out var targetSource))
                    return false;

                hasServerTargets |= targetSource
                    == HuntEnemyProgressSource.Server;
                hasClientTargets |= targetSource
                    == HuntEnemyProgressSource.Client;
                targets.Add(new HuntEnemyQuestTarget(
                    questId,
                    dungeonId,
                    minimumDifficulty,
                    enemyCode,
                    enemyType,
                    requiredCount,
                    offset / stride,
                    targetSource));
            }

            if (hasServerTargets == hasClientTargets)
                return false;

            source = hasServerTargets
                ? HuntEnemyProgressSource.Server
                : HuntEnemyProgressSource.Client;
            return true;
        }

        private static bool TryResolveHuntEnemyProgressSource(
            int enemyCode,
            int enemyType,
            out HuntEnemyProgressSource source)
        {
            source = HuntEnemyProgressSource.Invalid;
            if (enemyType == QuestDropProvider.EnemyTypeMonster
                || enemyType == QuestDropProvider.EnemyTypeAiCharacter)
            {
                if (enemyCode > 0)
                {
                    source = HuntEnemyProgressSource.Server;
                    return true;
                }
                if (IsClientReportedHuntEnemyWildcard(enemyCode, enemyType))
                {
                    source = HuntEnemyProgressSource.Client;
                    return true;
                }
                return false;
            }

            if (enemyType == QuestDropProvider.EnemyTypePassiveObject)
            {
                if (enemyCode <= 0)
                    return false;
                source = HuntEnemyProgressSource.Client;
                return true;
            }

            if (enemyType == 10 && enemyCode == -11)
            {
                source = HuntEnemyProgressSource.Client;
                return true;
            }

            return false;
        }

        private static bool IsClientReportedHuntEnemyWildcard(
            int enemyCode,
            int enemyType)
        {
            if (enemyCode == -1)
            {
                return enemyType == QuestDropProvider.EnemyTypeMonster
                    || enemyType == QuestDropProvider.EnemyTypeAiCharacter;
            }

            return enemyCode == -3
                && enemyType == QuestDropProvider.EnemyTypeMonster;
        }

        private static bool IsClearMapQuest(QuestFile quest)
            => quest != null
                && QuestData.NormalizeQuestTag(quest.Type) == "clear map";
    }
}
