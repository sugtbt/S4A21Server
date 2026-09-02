using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class AnotherAradConfigCatalog
    {
        private const string ConfigPath = "etc/crackofdimensionlist.etc";
        private static readonly Lazy<ConfigSnapshot> Snapshot =
            new Lazy<ConfigSnapshot>(LoadSnapshot);

        internal static IReadOnlyList<int> ConfiguredQuestIds =>
            Snapshot.Value.QuestIds;

        internal static bool TryGetHistoricalDungeonId(
            int questId,
            out int dungeonId)
            => Snapshot.Value.QuestDungeons.TryGetValue(questId, out dungeonId);

        internal static bool MatchesQuestDungeon(int questId, int dungeonId)
            => TryGetHistoricalDungeonId(questId, out var configuredDungeonId)
                && configuredDungeonId == dungeonId;

        internal static bool TryResolveReward(
            int characterLevel,
            out int itemId,
            out int count)
        {
            itemId = 0;
            count = 0;
            foreach (var definition in Snapshot.Value.Rewards)
            {
                if (characterLevel < definition.MinimumLevel
                    || characterLevel > definition.MaximumLevel)
                {
                    continue;
                }

                itemId = definition.ItemId;
                count = definition.Count;
                return true;
            }

            return false;
        }

        private static ConfigSnapshot LoadSnapshot()
        {
            var content = PvfArchiveAccessor.ReadText(ConfigPath);
            var root = new ScriptParser().Parse(content);
            var questDungeons = new Dictionary<int, int>();
            var crackInfo = ReadIntegers(
                root.GetChild("crack info list"),
                content);
            if (crackInfo.Count == 0 || crackInfo.Count % 2 != 0)
            {
                throw new InvalidOperationException(
                    "Invalid [crack info list] in " + ConfigPath + ".");
            }
            for (var index = 0; index < crackInfo.Count; index += 2)
            {
                var dungeonId = crackInfo[index];
                var questId = crackInfo[index + 1];
                if (dungeonId <= 0
                    || questId <= 0
                    || questId > ushort.MaxValue
                    || questDungeons.ContainsKey(questId))
                {
                    throw new InvalidOperationException(
                        "Invalid or duplicate Crack-of-Dimension quest pair.");
                }
                questDungeons.Add(questId, dungeonId);
            }

            var rewards = new List<RewardDefinition>();
            foreach (var rewardData in root.GetChildren("reward data"))
            {
                var minimumLevel = ReadFirstInt(
                    rewardData.GetChild("min level"),
                    content,
                    fallback: 1);
                var maximumLevel = ReadFirstInt(
                    rewardData.GetChild("max level"),
                    content,
                    fallback: int.MaxValue);
                var values = ReadIntegers(
                    rewardData.GetChild("reward int data"),
                    content);
                if (minimumLevel <= 0
                    || maximumLevel < minimumLevel
                    || values.Count == 0
                    || values.Count % 2 != 0)
                {
                    throw new InvalidOperationException(
                        "Invalid [reward data] in " + ConfigPath + ".");
                }

                for (var index = 0; index < values.Count; index += 2)
                {
                    var itemId = values[index];
                    var count = values[index + 1];
                    if (itemId <= 0
                        || count <= 0
                        || !ItemMetadataResolver.TryLoadStackableFile(
                            itemId,
                            out _))
                    {
                        throw new InvalidOperationException(
                            "Invalid Crack-of-Dimension reward item.");
                    }

                    rewards.Add(new RewardDefinition(
                        minimumLevel,
                        maximumLevel,
                        itemId,
                        count));
                }
            }
            if (rewards.Count == 0)
            {
                throw new InvalidOperationException(
                    "No Crack-of-Dimension rewards were configured.");
            }

            return new ConfigSnapshot(
                questDungeons,
                new List<int>(questDungeons.Keys),
                rewards);
        }

        private static int ReadFirstInt(
            ScriptNode node,
            string content,
            int fallback)
        {
            var values = ReadIntegers(node, content);
            return values.Count > 0 ? values[0] : fallback;
        }

        private static List<int> ReadIntegers(
            ScriptNode node,
            string content)
        {
            var values = new List<int>();
            if (node?.DataItems == null)
                return values;

            foreach (var item in node.DataItems)
            {
                values.AddRange(QuestData.ParseIntList(
                    item.GetContent(content)));
            }
            return values;
        }

        private sealed class ConfigSnapshot
        {
            internal ConfigSnapshot(
                IReadOnlyDictionary<int, int> questDungeons,
                IReadOnlyList<int> questIds,
                IReadOnlyList<RewardDefinition> rewards)
            {
                QuestDungeons = questDungeons;
                QuestIds = questIds;
                Rewards = rewards;
            }

            internal IReadOnlyDictionary<int, int> QuestDungeons { get; }
            internal IReadOnlyList<int> QuestIds { get; }
            internal IReadOnlyList<RewardDefinition> Rewards { get; }
        }

        private readonly struct RewardDefinition
        {
            internal RewardDefinition(
                int minimumLevel,
                int maximumLevel,
                int itemId,
                int count)
            {
                MinimumLevel = minimumLevel;
                MaximumLevel = maximumLevel;
                ItemId = itemId;
                Count = count;
            }

            internal int MinimumLevel { get; }
            internal int MaximumLevel { get; }
            internal int ItemId { get; }
            internal int Count { get; }
        }
    }
}
