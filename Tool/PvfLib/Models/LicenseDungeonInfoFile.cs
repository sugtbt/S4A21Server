using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace PvfLib
{
    public sealed class LicenseDungeonInfoRow
    {
        public int GroupId { get; set; }
        public int DungeonId { get; set; }
        public int LicenseLevel { get; set; }
        public int Field4 { get; set; }
        public int Field5 { get; set; }
    }

    public sealed class LicenseDungeonRewardItem
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
    }

    public sealed class LicenseDungeonWeightedDropItem
    {
        public int ItemId { get; set; }
        public int Weight { get; set; }
    }

    public sealed class LicenseDungeonRewardDefinition
    {
        public int LicenseLevel { get; set; }
        public IReadOnlyList<LicenseDungeonRewardItem> DungeonClearRewards
            { get; set; } = Array.Empty<LicenseDungeonRewardItem>();
        public IReadOnlyList<LicenseDungeonWeightedDropItem> GroupDropItems
            { get; set; } = Array.Empty<LicenseDungeonWeightedDropItem>();
    }

    public sealed class LicenseDungeonDailyClearRewardInfo
    {
        public int DungeonId { get; set; }
        public int ItemId { get; set; }
        public int Count { get; set; }
    }

    public sealed class LicenseDungeonDailyOpenInfo
    {
        public int DayIndex { get; set; }
        public IReadOnlyList<int> DungeonIds { get; set; }
            = Array.Empty<int>();
    }

    public sealed class LicenseDungeonDifficultyInfo
    {
        public int LicenseLevel { get; set; }
        public string DifficultyName { get; set; }
    }

    public sealed class LicenseDungeonAppearRateInfo
    {
        public int EntryCount { get; set; }
        public int RatePerTenThousand { get; set; }
    }

    public sealed class LicenseDungeonInfoFile : PvfModelBase
    {
        private static readonly Regex IntegerTokenRegex =
            new Regex(@"-?\d+", RegexOptions.Compiled);
        private static readonly Regex TokenRegex =
            new Regex(@"`[^`]*`|\S+", RegexOptions.Compiled);

        public IReadOnlyList<LicenseDungeonInfoRow> LicenseRows
            { get; private set; } = Array.Empty<LicenseDungeonInfoRow>();
        public IReadOnlyList<LicenseDungeonDailyOpenInfo> DailyOpenRules
            { get; private set; } = Array.Empty<LicenseDungeonDailyOpenInfo>();
        public IReadOnlyList<LicenseDungeonDifficultyInfo> DifficultyRules
            { get; private set; } = Array.Empty<LicenseDungeonDifficultyInfo>();
        public IReadOnlyList<LicenseDungeonAppearRateInfo> GroupAppearRates
            { get; private set; } = Array.Empty<LicenseDungeonAppearRateInfo>();
        public IReadOnlyList<int> GroupApplyLicenses
            { get; private set; } = Array.Empty<int>();
        public IReadOnlyList<LicenseDungeonRewardDefinition> RewardDefinitions
            { get; private set; } = Array.Empty<LicenseDungeonRewardDefinition>();
        public IReadOnlyList<LicenseDungeonDailyClearRewardInfo> DailyClearRewards
            { get; private set; } = Array.Empty<LicenseDungeonDailyClearRewardInfo>();
        public int DailyEnterCount { get; private set; }
        public int GroupAppearCountPerMonth { get; private set; }
        public bool IsValid { get; private set; }
        public string InvalidReason { get; private set; } = string.Empty;

        public static LicenseDungeonInfoFile Parse(string content)
        {
            var file = new LicenseDungeonInfoFile
            {
                Content = content ?? string.Empty,
            };
            if (string.IsNullOrWhiteSpace(content))
            {
                file.Root = new ScriptNode { Tag = "ROOT" };
                file.InvalidReason = "license dungeon info is empty";
                return file;
            }

            try
            {
                file.Root = new ScriptParser().Parse(content);
                file.ParseCore();
                file.IsValid = true;
            }
            catch (Exception ex)
            {
                file.InvalidReason = ex.Message;
            }

            return file;
        }

        private void ParseCore()
        {
            var licenseValues = ReadIntegers(
                Root.GetChild("dungeon license info"));
            if (licenseValues.Count == 0 || licenseValues.Count % 5 != 0)
            {
                throw new InvalidOperationException(
                    "dungeon license info must be a non-empty five-field table");
            }

            var licenseRows = new List<LicenseDungeonInfoRow>();
            var seenDungeons = new HashSet<int>();
            for (var offset = 0; offset < licenseValues.Count; offset += 5)
            {
                var row = new LicenseDungeonInfoRow
                {
                    GroupId = licenseValues[offset],
                    DungeonId = licenseValues[offset + 1],
                    LicenseLevel = licenseValues[offset + 2],
                    Field4 = licenseValues[offset + 3],
                    Field5 = licenseValues[offset + 4],
                };
                if (row.GroupId <= 0
                    || row.DungeonId <= 0
                    || row.LicenseLevel <= 0
                    || !seenDungeons.Add(row.DungeonId))
                {
                    throw new InvalidOperationException(
                        $"invalid or duplicate license dungeon row at offset {offset}");
                }
                licenseRows.Add(row);
            }

            var dailyRules = new List<LicenseDungeonDailyOpenInfo>();
            var seenDays = new HashSet<int>();
            foreach (var node in Root.GetChildren("daily open dungeon"))
            {
                var dayValues = ReadIntegers(node.GetChild("day"));
                var dungeonIds = ReadIntegers(node.GetChild("dungeon"));
                if (dayValues.Count != 1
                    || dayValues[0] < 0
                    || dayValues[0] > 6
                    || !seenDays.Add(dayValues[0])
                    || dungeonIds.Count == 0
                    || dungeonIds.Any(id => id <= 0))
                {
                    throw new InvalidOperationException(
                        "daily open dungeon contains an invalid day or dungeon list");
                }
                dailyRules.Add(new LicenseDungeonDailyOpenInfo
                {
                    DayIndex = dayValues[0],
                    DungeonIds = new ReadOnlyCollection<int>(dungeonIds),
                });
            }
            if (dailyRules.Count != 7)
            {
                throw new InvalidOperationException(
                    "daily open dungeon must define day indexes 0 through 6");
            }

            DailyEnterCount = ReadSinglePositive(
                "daily enter count",
                Root.GetChild("daily enter count"));
            GroupAppearCountPerMonth = ReadSinglePositive(
                "groop appear count on month",
                Root.GetChild("groop appear count on month"));

            var difficultyTokens = ReadTokens(
                Root.GetChild("dungeon difficulty by license"));
            if (difficultyTokens.Count == 0 || difficultyTokens.Count % 2 != 0)
            {
                throw new InvalidOperationException(
                    "dungeon difficulty by license must contain level/name pairs");
            }
            var difficultyRules = new List<LicenseDungeonDifficultyInfo>();
            var difficultyLevels = new HashSet<int>();
            for (var offset = 0; offset < difficultyTokens.Count; offset += 2)
            {
                if (!int.TryParse(
                        difficultyTokens[offset],
                        out var licenseLevel)
                    || licenseLevel <= 0
                    || !difficultyLevels.Add(licenseLevel))
                {
                    throw new InvalidOperationException(
                        "dungeon difficulty by license contains an invalid level");
                }
                var difficultyName = StripBacktick(
                    difficultyTokens[offset + 1]).Trim();
                if (string.IsNullOrWhiteSpace(difficultyName))
                {
                    throw new InvalidOperationException(
                        "dungeon difficulty by license contains an empty name");
                }
                difficultyRules.Add(new LicenseDungeonDifficultyInfo
                {
                    LicenseLevel = licenseLevel,
                    DifficultyName = difficultyName,
                });
            }

            var appearValues = ReadIntegers(
                Root.GetChild("groop appear rate by enter count"));
            if (appearValues.Count == 0 || appearValues.Count % 2 != 0)
            {
                throw new InvalidOperationException(
                    "groop appear rate must contain entry/rate pairs");
            }
            var appearRates = new List<LicenseDungeonAppearRateInfo>();
            var lastEntryCount = 0;
            for (var offset = 0; offset < appearValues.Count; offset += 2)
            {
                var entryCount = appearValues[offset];
                var rate = appearValues[offset + 1];
                if (entryCount <= lastEntryCount || rate < 0 || rate > 10000)
                {
                    throw new InvalidOperationException(
                        "groop appear rate is not strictly ordered or exceeds 10000");
                }
                appearRates.Add(new LicenseDungeonAppearRateInfo
                {
                    EntryCount = entryCount,
                    RatePerTenThousand = rate,
                });
                lastEntryCount = entryCount;
            }

            var applyLicenses = ReadIntegers(
                Root.GetChild("groop apply licence"));
            if (applyLicenses.Count == 0
                || applyLicenses.Any(value => value <= 0)
                || applyLicenses.Distinct().Count() != applyLicenses.Count)
            {
                throw new InvalidOperationException(
                    "groop apply licence is empty or invalid");
            }

            var rewardInfoNode = Root.GetChild("license dungeon reward info");
            if (rewardInfoNode == null)
            {
                throw new InvalidOperationException(
                    "license dungeon reward info is missing");
            }

            var rewardDefinitions = new List<LicenseDungeonRewardDefinition>();
            var rewardLevels = new HashSet<int>();
            foreach (var licenseNode in rewardInfoNode.GetChildren("license"))
            {
                var levelValues = ReadIntegers(licenseNode);
                if (levelValues.Count != 1
                    || levelValues[0] <= 0
                    || !rewardLevels.Add(levelValues[0]))
                {
                    throw new InvalidOperationException(
                        "license dungeon reward contains an invalid or duplicate license");
                }

                var clearValues = ReadIntegers(
                    licenseNode.GetChild("dungeon clear reward"));
                if (clearValues.Count == 0 || clearValues.Count % 2 != 0)
                {
                    throw new InvalidOperationException(
                        "license dungeon clear reward must contain item/count pairs");
                }
                var clearRewards = new List<LicenseDungeonRewardItem>();
                for (var offset = 0; offset < clearValues.Count; offset += 2)
                {
                    if (clearValues[offset] <= 0 || clearValues[offset + 1] <= 0)
                    {
                        throw new InvalidOperationException(
                            "license dungeon clear reward contains invalid item/count");
                    }
                    clearRewards.Add(new LicenseDungeonRewardItem
                    {
                        ItemId = clearValues[offset],
                        Count = clearValues[offset + 1],
                    });
                }

                var groupValues = ReadIntegers(
                    licenseNode.GetChild("groop drop item info"));
                if (groupValues.Count % 2 != 0)
                {
                    throw new InvalidOperationException(
                        "license dungeon group drop must contain item/weight pairs");
                }
                var groupDrops = new List<LicenseDungeonWeightedDropItem>();
                for (var offset = 0; offset < groupValues.Count; offset += 2)
                {
                    if (groupValues[offset] <= 0 || groupValues[offset + 1] <= 0)
                    {
                        throw new InvalidOperationException(
                            "license dungeon group drop contains invalid item/weight");
                    }
                    groupDrops.Add(new LicenseDungeonWeightedDropItem
                    {
                        ItemId = groupValues[offset],
                        Weight = groupValues[offset + 1],
                    });
                }

                rewardDefinitions.Add(new LicenseDungeonRewardDefinition
                {
                    LicenseLevel = levelValues[0],
                    DungeonClearRewards = new ReadOnlyCollection<LicenseDungeonRewardItem>(
                        clearRewards),
                    GroupDropItems = new ReadOnlyCollection<LicenseDungeonWeightedDropItem>(
                        groupDrops),
                });
            }
            if (rewardDefinitions.Count == 0)
            {
                throw new InvalidOperationException(
                    "license dungeon reward info has no license definitions");
            }

            var dailyValues = ReadIntegers(
                rewardInfoNode.GetChild("daily clear reward"));
            if (dailyValues.Count == 0 || dailyValues.Count % 3 != 0)
            {
                throw new InvalidOperationException(
                    "daily clear reward must contain dungeon/item/count triples");
            }
            var dailyRewards = new List<LicenseDungeonDailyClearRewardInfo>();
            for (var offset = 0; offset < dailyValues.Count; offset += 3)
            {
                if (dailyValues[offset] <= 0
                    || dailyValues[offset + 1] <= 0
                    || dailyValues[offset + 2] <= 0)
                {
                    throw new InvalidOperationException(
                        "daily clear reward contains invalid dungeon/item/count");
                }
                dailyRewards.Add(new LicenseDungeonDailyClearRewardInfo
                {
                    DungeonId = dailyValues[offset],
                    ItemId = dailyValues[offset + 1],
                    Count = dailyValues[offset + 2],
                });
            }

            LicenseRows = new ReadOnlyCollection<LicenseDungeonInfoRow>(
                licenseRows);
            DailyOpenRules = new ReadOnlyCollection<LicenseDungeonDailyOpenInfo>(
                dailyRules);
            DifficultyRules =
                new ReadOnlyCollection<LicenseDungeonDifficultyInfo>(
                    difficultyRules);
            GroupAppearRates =
                new ReadOnlyCollection<LicenseDungeonAppearRateInfo>(
                    appearRates);
            GroupApplyLicenses = new ReadOnlyCollection<int>(applyLicenses);
            RewardDefinitions = new ReadOnlyCollection<LicenseDungeonRewardDefinition>(
                rewardDefinitions);
            DailyClearRewards = new ReadOnlyCollection<LicenseDungeonDailyClearRewardInfo>(
                dailyRewards);
        }

        private int ReadSinglePositive(string tag, ScriptNode node)
        {
            var values = ReadIntegers(node);
            if (values.Count != 1 || values[0] <= 0)
                throw new InvalidOperationException(tag + " must be one positive integer");
            return values[0];
        }

        private List<int> ReadIntegers(ScriptNode node)
        {
            var result = new List<int>();
            if (node == null)
                return result;

            foreach (var item in node.DataItems)
            {
                foreach (Match match in IntegerTokenRegex.Matches(
                             item.GetContent(Content)))
                {
                    if (int.TryParse(match.Value, out var value))
                        result.Add(value);
                }
            }
            return result;
        }

        private List<string> ReadTokens(ScriptNode node)
        {
            var result = new List<string>();
            if (node == null)
                return result;

            foreach (var item in node.DataItems)
            {
                foreach (Match match in TokenRegex.Matches(
                             item.GetContent(Content)))
                {
                    result.Add(match.Value);
                }
            }
            return result;
        }
    }
}
