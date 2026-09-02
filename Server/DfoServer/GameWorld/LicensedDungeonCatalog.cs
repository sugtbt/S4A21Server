using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal sealed class LicensedDungeonDefinition
    {
        internal LicensedDungeonDefinition(
            int groupId,
            int dungeonId,
            int licenseLevel,
            byte difficulty,
            int field4,
            int field5,
            IReadOnlyCollection<int> openDayIndexes,
            LicensedDungeonBossRule bossRule)
        {
            GroupId = groupId;
            DungeonId = dungeonId;
            LicenseLevel = licenseLevel;
            Difficulty = difficulty;
            Field4 = field4;
            Field5 = field5;
            OpenDayIndexes = openDayIndexes
                ?? throw new ArgumentNullException(nameof(openDayIndexes));
            BossRule = bossRule;
        }

        internal int GroupId { get; }
        internal int DungeonId { get; }
        internal int LicenseLevel { get; }
        internal byte Difficulty { get; }
        // Opaque fourth/fifth values from the PVF directory row. They remain
        // catalog metadata and must not be reused as 0x02F9 reward fields.
        internal int Field4 { get; }
        internal int Field5 { get; }
        internal IReadOnlyCollection<int> OpenDayIndexes { get; }
        internal LicensedDungeonBossRule BossRule { get; }
        internal bool IsOpenOn(int dayIndex) => OpenDayIndexes.Contains(dayIndex);
    }

    internal readonly struct LicensedDungeonPermissionRecord
    {
        internal LicensedDungeonPermissionRecord(
            int dungeonId,
            int licenseLevel,
            int field3)
        {
            DungeonId = dungeonId;
            LicenseLevel = licenseLevel;
            Field3 = field3;
        }

        internal int DungeonId { get; }
        internal int LicenseLevel { get; }
        internal int Field3 { get; }
    }

    internal sealed class LicensedDungeonBossRule
    {
        internal LicensedDungeonBossRule(
            int ordinaryMazeIndex,
            IReadOnlyList<int> bossMazeIndices,
            IReadOnlyList<int> bossMapIds)
        {
            OrdinaryMazeIndex = ordinaryMazeIndex;
            BossMazeIndices = bossMazeIndices
                ?? throw new ArgumentNullException(nameof(bossMazeIndices));
            BossMapIds = bossMapIds
                ?? throw new ArgumentNullException(nameof(bossMapIds));
        }

        internal int OrdinaryMazeIndex { get; }
        internal IReadOnlyList<int> BossMazeIndices { get; }
        internal IReadOnlyList<int> BossMapIds { get; }
    }

    internal static class LicensedDungeonCatalog
    {
        internal const string ConfigPath =
            "etc/dungeonetc/licensedungeoninfo.etc";

        private static readonly Lazy<Snapshot> Current =
            new Lazy<Snapshot>(Load);
        private static readonly Lazy<LstFile> MonsterList =
            new Lazy<LstFile>(() => DungeonCatalog.LoadListFile(
                Path.Combine("monster", "monster.lst")));
        private static readonly ConcurrentDictionary<int, bool>
            GroupMonsterByCode = new ConcurrentDictionary<int, bool>();

        internal static int WorldMapAreaId => Current.Value.WorldMapAreaId;
        internal static int DailyEnterCount =>
            Current.Value.Configuration.DailyEnterCount;
        internal static int GroupAppearCountPerMonth =>
            Current.Value.Configuration.GroupAppearCountPerMonth;
        internal static IReadOnlyCollection<LicensedDungeonDefinition>
            Definitions => Current.Value.Definitions;

        internal static IReadOnlyList<LicenseDungeonRewardItem>
            GetDungeonClearRewards(int licenseLevel)
        {
            return Current.Value.Configuration.RewardDefinitions
                .FirstOrDefault(definition =>
                    definition.LicenseLevel == licenseLevel)
                ?.DungeonClearRewards
                ?? Array.Empty<LicenseDungeonRewardItem>();
        }

        internal static IReadOnlyList<LicenseDungeonWeightedDropItem>
            GetGroupDropItems(int licenseLevel)
        {
            return Current.Value.Configuration.RewardDefinitions
                .FirstOrDefault(definition =>
                    definition.LicenseLevel == licenseLevel)
                ?.GroupDropItems
                ?? Array.Empty<LicenseDungeonWeightedDropItem>();
        }

        internal static bool TryGetDailyClearReward(
            int dungeonId,
            out LicenseDungeonDailyClearRewardInfo reward)
        {
            reward = Current.Value.Configuration.DailyClearRewards
                .FirstOrDefault(item => item.DungeonId == dungeonId);
            return reward != null;
        }

        internal static IReadOnlyList<LicensedDungeonPermissionRecord>
            GetInitialLicenseRecords()
        {
            var records = Current.Value.Definitions
                .GroupBy(definition => definition.GroupId)
                .Select(group => group
                    .OrderBy(definition => definition.LicenseLevel)
                    .ThenBy(definition => definition.DungeonId)
                    .First())
                .OrderBy(definition => definition.GroupId)
                .Select(definition => new LicensedDungeonPermissionRecord(
                    definition.DungeonId,
                    definition.LicenseLevel,
                    field3: 0))
                .ToList();
            return new ReadOnlyCollection<LicensedDungeonPermissionRecord>(
                records);
        }

        internal static IReadOnlyList<LicensedDungeonPermissionRecord>
            GetLicenseRecords(IReadOnlyDictionary<int, int> unlockedLevels)
        {
            var records = Current.Value.Definitions
                .GroupBy(definition => definition.GroupId)
                .Select(group =>
                {
                    var initial = group
                        .OrderBy(definition => definition.LicenseLevel)
                        .ThenBy(definition => definition.DungeonId)
                        .First();
                    var unlockedLevel = unlockedLevels != null
                        && unlockedLevels.TryGetValue(
                            group.Key,
                            out var level)
                        ? level
                        : initial.LicenseLevel;
                    return group
                        .Where(definition =>
                            definition.LicenseLevel <= unlockedLevel)
                        .OrderByDescending(definition => definition.LicenseLevel)
                        .ThenByDescending(definition => definition.DungeonId)
                        .FirstOrDefault() ?? initial;
                })
                .OrderBy(definition => definition.GroupId)
                .Select(definition => new LicensedDungeonPermissionRecord(
                    definition.DungeonId,
                    definition.LicenseLevel,
                    field3: 0))
                .ToList();
            return new ReadOnlyCollection<LicensedDungeonPermissionRecord>(
                records);
        }

        internal static bool TryGetDefinition(
            int groupId,
            int licenseLevel,
            out LicensedDungeonDefinition definition)
        {
            definition = Current.Value.Definitions
                .Where(item =>
                    item.GroupId == groupId
                    && item.LicenseLevel == licenseLevel)
                .OrderBy(item => item.DungeonId)
                .FirstOrDefault();
            return definition != null;
        }

        internal static bool TryCreatePermissionRecord(
            int groupId,
            int licenseLevel,
            out LicensedDungeonPermissionRecord record)
        {
            record = default;
            if (!TryGetDefinition(groupId, licenseLevel, out var definition))
                return false;

            record = new LicensedDungeonPermissionRecord(
                definition.DungeonId,
                definition.LicenseLevel,
                field3: 0);
            return true;
        }

        internal static IReadOnlyDictionary<int, int>
            GetInitialLicenseLevels()
            => Current.Value.Definitions
                .GroupBy(definition => definition.GroupId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Min(definition => definition.LicenseLevel));

        internal static int GetInitialLicenseLevel(int groupId)
        {
            if (!TryGetInitialDefinition(groupId, out var definition))
                return 0;
            return definition.LicenseLevel;
        }

        private static bool TryGetInitialDefinition(
            int groupId,
            out LicensedDungeonDefinition definition)
        {
            definition = Current.Value.Definitions
                .Where(item => item.GroupId == groupId)
                .OrderBy(item => item.LicenseLevel)
                .ThenBy(item => item.DungeonId)
                .FirstOrDefault();
            return definition != null;
        }

        internal static bool TryGetNextLicenseLevel(
            int groupId,
            int currentLevel,
            out int nextLevel)
        {
            nextLevel = Current.Value.Definitions
                .Where(item =>
                    item.GroupId == groupId
                    && item.LicenseLevel > currentLevel)
                .Select(item => item.LicenseLevel)
                .DefaultIfEmpty()
                .Min();
            return nextLevel > currentLevel;
        }

        internal static bool TryGetDefinition(
            int dungeonId,
            out LicensedDungeonDefinition definition)
            => Current.Value.ByDungeonId.TryGetValue(dungeonId, out definition);

        internal static int ResolveGroupAppearRate(int monthlyEntryOrdinal)
        {
            if (monthlyEntryOrdinal <= 0)
                return 0;

            var rates = Current.Value.Configuration.GroupAppearRates;
            var selected = 0;
            foreach (var rate in rates)
            {
                if (rate.EntryCount > monthlyEntryOrdinal)
                    break;
                selected = rate.RatePerTenThousand;
            }

            if (selected == 0 && rates.Count > 0)
                selected = rates[0].RatePerTenThousand;
            return selected;
        }

        private static Snapshot Load()
        {
            var config = LicenseDungeonInfoFile.Parse(
                PvfArchiveAccessor.ReadText(ConfigPath));
            if (!config.IsValid)
            {
                throw new InvalidDataException(
                    $"licensed dungeon config is invalid: {config.InvalidReason}");
            }

            var difficultyByLicense = new Dictionary<int, byte>();
            foreach (var rule in config.DifficultyRules)
            {
                difficultyByLicense.Add(
                    rule.LicenseLevel,
                    ParseDifficulty(rule.DifficultyName));
            }

            var openDaysByDungeon = new Dictionary<int, HashSet<int>>();
            foreach (var rule in config.DailyOpenRules)
            {
                foreach (var dungeonId in rule.DungeonIds)
                {
                    if (!openDaysByDungeon.TryGetValue(
                            dungeonId,
                            out var days))
                    {
                        days = new HashSet<int>();
                        openDaysByDungeon.Add(dungeonId, days);
                    }
                    days.Add(rule.DayIndex);
                }
            }

            var groupApplyLicenses = new HashSet<int>(
                config.GroupApplyLicenses);
            var definitions = new List<LicensedDungeonDefinition>();
            var byDungeonId = new Dictionary<int, LicensedDungeonDefinition>();
            int? worldMapAreaId = null;
            foreach (var row in config.LicenseRows)
            {
                if (!difficultyByLicense.TryGetValue(
                        row.LicenseLevel,
                        out var difficulty))
                {
                    throw new InvalidDataException(
                        $"licensed dungeon {row.DungeonId} has no difficulty " +
                        $"mapping for license {row.LicenseLevel}");
                }
                if (!openDaysByDungeon.TryGetValue(
                        row.DungeonId,
                        out var openDays)
                    || openDays.Count == 0)
                {
                    throw new InvalidDataException(
                        $"licensed dungeon {row.DungeonId} has no open day");
                }

                var area = WorldMap.GetAreaByDungeonId(row.DungeonId);
                if (area == null)
                {
                    throw new InvalidDataException(
                        $"licensed dungeon {row.DungeonId} is absent from worldmap");
                }
                if (!worldMapAreaId.HasValue)
                    worldMapAreaId = area.AreaId;
                else if (worldMapAreaId.Value != area.AreaId)
                {
                    throw new InvalidDataException(
                        "licensed dungeons span multiple worldmap areas");
                }

                var bossRule = groupApplyLicenses.Contains(row.LicenseLevel)
                    ? BuildBossRule(row.DungeonId)
                    : null;
                var definition = new LicensedDungeonDefinition(
                    row.GroupId,
                    row.DungeonId,
                    row.LicenseLevel,
                    difficulty,
                    row.Field4,
                    row.Field5,
                    new ReadOnlyCollection<int>(
                        openDays.OrderBy(value => value).ToList()),
                    bossRule);
                definitions.Add(definition);
                byDungeonId.Add(row.DungeonId, definition);
            }

            if (!worldMapAreaId.HasValue || definitions.Count == 0)
                throw new InvalidDataException("licensed dungeon catalog is empty");

            FileLogger.Log(
                $"[LicensedDungeonCatalog] loaded area={worldMapAreaId.Value} " +
                $"dungeons={definitions.Count} bossRules=" +
                $"{definitions.Count(item => item.BossRule != null)} " +
                $"daily={config.DailyEnterCount} " +
                $"monthlyGroop={config.GroupAppearCountPerMonth}");
            return new Snapshot(
                config,
                worldMapAreaId.Value,
                definitions,
                byDungeonId);
        }

        private static LicensedDungeonBossRule BuildBossRule(int dungeonId)
        {
            var dungeon = Dungeon.GetDungeonFile(dungeonId);
            if (dungeon?.Mazes == null || dungeon.Mazes.Count == 0)
            {
                throw new InvalidDataException(
                    $"licensed boss dungeon {dungeonId} has no maze");
            }

            var ordinaryMazeIndices = new List<int>();
            var bossMazeIndices = new List<int>();
            var bossMapIds = new HashSet<int>();
            for (var mazeIndex = 0;
                 mazeIndex < dungeon.Mazes.Count;
                 mazeIndex++)
            {
                var mazeBossMaps = FindGroupBossMaps(dungeon.Mazes[mazeIndex]);
                if (mazeBossMaps.Count == 0)
                {
                    ordinaryMazeIndices.Add(mazeIndex);
                    continue;
                }

                bossMazeIndices.Add(mazeIndex);
                foreach (var mapId in mazeBossMaps)
                    bossMapIds.Add(mapId);
            }

            if (ordinaryMazeIndices.Count != 1
                || bossMazeIndices.Count != 3
                || bossMapIds.Count != 1)
            {
                throw new InvalidDataException(
                    $"licensed boss maze shape changed: dungeon={dungeonId} " +
                    $"ordinary={string.Join(",", ordinaryMazeIndices)} " +
                    $"boss={string.Join(",", bossMazeIndices)} " +
                    $"maps={string.Join(",", bossMapIds)}");
            }

            return new LicensedDungeonBossRule(
                ordinaryMazeIndices[0],
                new ReadOnlyCollection<int>(bossMazeIndices),
                new ReadOnlyCollection<int>(bossMapIds.OrderBy(id => id).ToList()));
        }

        private static HashSet<int> FindGroupBossMaps(MazeInfo maze)
        {
            var result = new HashSet<int>();
            if (maze?.MapSpecifications == null)
                return result;

            foreach (var specification in maze.MapSpecifications)
            {
                if (specification == null)
                    continue;
                var candidates = specification.MapCandidates != null
                    && specification.MapCandidates.Length > 0
                        ? specification.MapCandidates
                        : new[] { specification.Index };
                foreach (var mapId in candidates)
                {
                    if (mapId <= 0)
                        continue;
                    var map = DungeonMapCatalog.GetMapFile(mapId);
                    if (map?.Monsters == null)
                        continue;
                    if (map.Monsters.Any(monster =>
                            monster?.MonsterId > 0
                            && IsGroupBossMonster(
                                monster.MonsterId.Value)))
                    {
                        result.Add(mapId);
                    }
                }
            }

            return result;
        }

        private static bool IsGroupBossMonster(int monsterId)
            => GroupMonsterByCode.GetOrAdd(monsterId, ResolveGroupBossMonster);

        private static bool ResolveGroupBossMonster(int monsterId)
        {
            try
            {
                var entry = MonsterList.Value.GetById(monsterId);
                if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                    return false;
                var monster = MonsterFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("monster", entry.FilePath)));
                return monster.Categories.Any(category =>
                    string.Equals(
                        NormalizeTag(category),
                        "groop",
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static byte ParseDifficulty(string value)
        {
            switch (NormalizeTag(value))
            {
                // The license ETC table is one-based, but A21
                // SELECT_DUNGEON carries the corresponding zero-based wire
                // difficulty: normal=0, expert=1, master=2, king=3.
                case "normal": return 0;
                case "expert": return 1;
                case "master": return 2;
                case "king": return 3;
                case "slayer": return 4;
                default:
                    throw new InvalidDataException(
                        $"unknown licensed dungeon difficulty: {value}");
            }
        }

        private static string NormalizeTag(string value) =>
            (value ?? string.Empty)
                .Trim()
                .Trim('`')
                .Trim()
                .Trim('[', ']')
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

        private sealed class Snapshot
        {
            internal Snapshot(
                LicenseDungeonInfoFile configuration,
                int worldMapAreaId,
                List<LicensedDungeonDefinition> definitions,
                Dictionary<int, LicensedDungeonDefinition> byDungeonId)
            {
                Configuration = configuration;
                WorldMapAreaId = worldMapAreaId;
                Definitions = new ReadOnlyCollection<LicensedDungeonDefinition>(
                    definitions);
                ByDungeonId = new ReadOnlyDictionary<int, LicensedDungeonDefinition>(
                    byDungeonId);
            }

            internal LicenseDungeonInfoFile Configuration { get; }
            internal int WorldMapAreaId { get; }
            internal IReadOnlyCollection<LicensedDungeonDefinition> Definitions
                { get; }
            internal IReadOnlyDictionary<int, LicensedDungeonDefinition>
                ByDungeonId { get; }
        }
    }
}
