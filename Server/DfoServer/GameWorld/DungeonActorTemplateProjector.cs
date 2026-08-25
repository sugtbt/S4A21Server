using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class DungeonActorTemplateProjector
    {
        private static readonly Lazy<LstFile> AiCharacterList =
            new Lazy<LstFile>(() => DungeonCatalog.LoadListFile(
                Path.Combine("AICharacter", "AICharacter.lst")));

        private static readonly ConcurrentDictionary<int, int> AiCharacterLevels =
            new ConcurrentDictionary<int, int>();

        private static readonly Regex AiCharacterMinimumInfoRegex =
            new Regex(
                @"\[minimum info\]\s*`[^`]*`\s+\d+\s+\d+\s+\d+\s+\d+\s+(\d+)",
                RegexOptions.Compiled);

        internal static List<Dungeon.MonsterSumInfo> Project(
            MapFile mapFile,
            byte dungeonBasicLevel,
            int mapId)
        {
            var result = new List<Dungeon.MonsterSumInfo>();
            for (var monsterIndex = 0;
                 monsterIndex < mapFile.Monsters.Count;
                 monsterIndex++)
            {
                var item = mapFile.Monsters[monsterIndex];
                if (!item.MonsterId.HasValue || item.MonsterId.Value <= 0)
                {
                    FileLogger.Log(
                        $"[DungeonActorTemplateProjector] skip monster with " +
                        $"invalid id in map={mapId}");
                    continue;
                }

                var monsterType = (byte)item.Type;
                if (monsterType > 3)
                {
                    FileLogger.Log(
                        $"[DungeonActorTemplateProjector] clamp monster type " +
                        $"{monsterType} to 0 in map={mapId}");
                    monsterType = 0;
                }

                var rawLevel = item.Lv.GetValueOrDefault() != 0
                    ? dungeonBasicLevel + item.AutoLv.GetValueOrDefault()
                    : item.AutoLv.GetValueOrDefault();
                var monsterLevel = rawLevel > 0
                    ? (byte)Math.Min(rawLevel, byte.MaxValue)
                    : dungeonBasicLevel;
                result.Add(new Dungeon.MonsterSumInfo
                {
                    Code = item.MonsterId.Value,
                    CaptureItems = MonsterCaptureDefinitionCatalog.GetItems(
                        item.MonsterId.Value),
                    Type = monsterType,
                    Level = monsterLevel,
                    IsBlocking = IsBlockingMonster(mapFile, monsterIndex),
                    NoChampionPromotion = MonsterCaptureDefinitionCatalog
                        .IsChampionPromotionDisabled(item.MonsterId.Value),
                    X = item.X.GetValueOrDefault(),
                    Y = item.Y.GetValueOrDefault(),
                    Z = item.Z.GetValueOrDefault(),
                });
            }

            AppendSpecialPassiveObjectTemplates(
                result,
                mapFile,
                dungeonBasicLevel,
                mapId);

            var hasScriptedBossWaves = HasScriptedBossWaveTemplates(mapFile);
            foreach (var apc in mapFile.AICharacters)
            {
                if (apc.Code <= 0
                    || !TryGetAiCharacterLevel(apc.Code, out var apcLevel))
                {
                    FileLogger.Log(
                        $"[DungeonActorTemplateProjector] skip APC code={apc.Code} " +
                        $"not found in map={mapId}");
                    continue;
                }

                var apcType = (byte)apc.AIType;
                if (apcType < 5 || apcType > 8)
                {
                    FileLogger.Log(
                        $"[DungeonActorTemplateProjector] clamp APC type " +
                        $"{apcType} to 5 in map={mapId}");
                    apcType = 5;
                }

                result.Add(new Dungeon.MonsterSumInfo
                {
                    Code = apc.Code,
                    Type = apcType,
                    Level = apcLevel,
                    Faction = apc.Faction,
                    X = apc.X,
                    Y = apc.Y,
                    IsBlocking = hasScriptedBossWaves
                        && apc.Faction == ApcFaction.Monster
                        && apc.AIType == ApcAIType.Boss,
                });
            }

            return result;
        }

        private static bool IsBlockingMonster(MapFile mapFile, int monsterIndex)
        {
            var teams = mapFile?.MonsterTeam;
            if (teams == null
                || monsterIndex < 0
                || monsterIndex >= teams.Length)
            {
                return true;
            }

            return teams[monsterIndex] == (int)ApcFaction.Monster;
        }

        internal static List<Dungeon.MonsterSumInfo> ProjectConditional(
            IReadOnlyList<MonsterInfo> monsters,
            byte dungeonBasicLevel,
            ICollection<int> monsterCodes,
            bool conditionalSummon)
        {
            var result = new List<Dungeon.MonsterSumInfo>();
            if (monsters == null || monsterCodes == null)
                return result;

            for (var index = 0; index < monsters.Count; index++)
            {
                var item = monsters[index];
                if (!item.MonsterId.HasValue
                    || item.MonsterId.Value <= 0
                    || !monsterCodes.Contains(item.MonsterId.Value))
                {
                    continue;
                }

                var monsterType = (byte)item.Type;
                if (monsterType > 3)
                    monsterType = 0;

                var rawLevel = item.Lv.GetValueOrDefault() != 0
                    ? dungeonBasicLevel + item.AutoLv.GetValueOrDefault()
                    : item.AutoLv.GetValueOrDefault();
                var level = rawLevel > 0
                    ? (byte)Math.Min(rawLevel, byte.MaxValue)
                    : dungeonBasicLevel;
                var conditionalOrder = item.ConditionalParam0.GetValueOrDefault();

                result.Add(new Dungeon.MonsterSumInfo
                {
                    Code = item.MonsterId.Value,
                    CaptureItems = MonsterCaptureDefinitionCatalog.GetItems(
                        item.MonsterId.Value),
                    Type = monsterType,
                    Level = level,
                    X = item.X.GetValueOrDefault(),
                    Y = item.Y.GetValueOrDefault(),
                    Z = item.Z.GetValueOrDefault(),
                    IsBlocking = !conditionalSummon,
                    NoChampionPromotion = MonsterCaptureDefinitionCatalog
                        .IsChampionPromotionDisabled(item.MonsterId.Value),
                    TemplateOrder = conditionalSummon && conditionalOrder > 0
                        ? (ushort)Math.Min(conditionalOrder, ushort.MaxValue)
                        : (ushort)0,
                    PacketIndex = conditionalSummon
                        && item.ConditionalParam0.HasValue
                            ? item.ConditionalParam0.Value
                            : index,
                    Flag0 = conditionalSummon ? (byte)1 : (byte)0,
                });
            }

            return result;
        }

        internal static bool TryGetAiCharacterLevel(
            int aiCharacterCode,
            out byte level)
        {
            level = 0;
            if (aiCharacterCode <= 0)
                return false;

            var parsedLevel = AiCharacterLevels.GetOrAdd(
                aiCharacterCode,
                ResolveAiCharacterLevel);
            if (parsedLevel <= 0 || parsedLevel > byte.MaxValue)
                return false;

            level = (byte)parsedLevel;
            return true;
        }

        private static bool HasScriptedBossWaveTemplates(MapFile mapFile)
        {
            if (mapFile == null
                || !string.Equals(
                    mapFile.Type,
                    "[boss]",
                    StringComparison.OrdinalIgnoreCase)
                || mapFile.SpecialPassiveObjects == null)
            {
                return false;
            }

            foreach (var obj in mapFile.SpecialPassiveObjects)
            {
                if (obj?.Spawns == null)
                    continue;

                foreach (var spawn in obj.Spawns)
                {
                    if (spawn.Code > 0
                        && string.Equals(
                            spawn.Kind,
                            "[monster]",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void AppendSpecialPassiveObjectTemplates(
            List<Dungeon.MonsterSumInfo> actors,
            MapFile mapFile,
            byte dungeonBasicLevel,
            int mapId)
        {
            if (actors == null
                || mapFile?.SpecialPassiveObjects == null
                || mapFile.SpecialPassiveObjects.Count == 0)
            {
                return;
            }

            // The client creates parent objects from the MAP. START_MAP only
            // supplies the inline templates those objects can activate.
            var templateRows = 0;
            for (var objectIndex = 0;
                 objectIndex < mapFile.SpecialPassiveObjects.Count;
                 objectIndex++)
            {
                var obj = mapFile.SpecialPassiveObjects[objectIndex];
                if (obj?.Spawns == null || obj.Spawns.Count == 0)
                    continue;

                for (var spawnIndex = 0;
                     spawnIndex < obj.Spawns.Count;
                     spawnIndex++)
                {
                    var spawn = obj.Spawns[spawnIndex];
                    if (spawn.Code <= 0
                        || !string.Equals(
                            spawn.Kind,
                            "[monster]",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    actors.Add(new Dungeon.MonsterSumInfo
                    {
                        Code = spawn.Code,
                        CaptureItems = MonsterCaptureDefinitionCatalog.GetItems(
                            spawn.Code),
                        Type = 0,
                        Level = spawn.Level > 0
                            ? (byte)Math.Min(spawn.Level, byte.MaxValue)
                            : dungeonBasicLevel,
                        IsBlocking = false,
                        NoChampionPromotion = MonsterCaptureDefinitionCatalog
                            .IsChampionPromotionDisabled(spawn.Code),
                        TemplateOrder = (ushort)Math.Min(
                            objectIndex,
                            ushort.MaxValue),
                        PacketIndex = spawnIndex,
                        Flag0 = 1,
                        Flag1 = (byte)Math.Min(objectIndex, byte.MaxValue),
                        SourceSpecialPassiveObjectIndex = objectIndex,
                    });
                    templateRows++;
                }
            }

            if (templateRows > 0)
            {
                FileLogger.Log(
                    $"[DungeonActorTemplateProjector] special passive object " +
                    $"templates: map={mapId} templates={templateRows}");
            }
        }

        private static int ResolveAiCharacterLevel(int aiCharacterCode)
        {
            var entry = AiCharacterList.Value.GetById(aiCharacterCode);
            if (entry == null)
                return -1;

            var content = PvfArchiveAccessor.ReadText(
                Path.Combine("AICharacter", entry.FilePath));
            var match = AiCharacterMinimumInfoRegex.Match(content);
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, out var level)
                || level <= 0
                || level > byte.MaxValue)
            {
                return -1;
            }

            return level;
        }
    }
}
