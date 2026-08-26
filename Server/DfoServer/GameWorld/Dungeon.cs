using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld
{
    public class Dungeon
    {
        internal static LstFile LoadLstFile(string relativePath)
            => DungeonCatalog.LoadListFile(relativePath);

        public static LstFile LoadDungeonLstFile()
            => DungeonCatalog.LoadDungeonList();

        public static DungeonFile GetDungeonFile(int dungeonId)
            => DungeonCatalog.GetDungeonFile(dungeonId);

        internal static bool IsDimensionDungeon(int dungeonId)
            => DungeonCatalog.IsDimensionDungeon(dungeonId);

        internal static string ResolveFilePath(LstFile lstFile, int id, string description)
            => DungeonCatalog.ResolveFilePath(lstFile, id, description);

        public struct MonsterSumInfo
        {
            public int Code { get; set; }

            public IReadOnlyList<MonsterCaptureItemDefinition> CaptureItems
            { get; set; }

            public byte Level { get; set; }

            // START_MAP 对象类型。0..3 为怪物，5..8 为 APC/AICharacter，9 为特殊被动对象路径。
            public byte Type { get; set; }

            // 仅 APC/AICharacter 有值；保留 PVF 阵营供副本清房策略判断，不改变通用解析语义。
            public ApcFaction? Faction { get; set; }

            public bool IsHostileApcBoss =>
                Type == (byte)ApcAIType.Boss
                && Faction == ApcFaction.Monster;

            public bool IsBlocking { get; set; }

            // MOB [no champion] blocks only runtime normal -> champion promotion.
            public bool NoChampionPromotion { get; set; }

            // START_MAP 模板/波次字段。深渊隐藏行使用 map [hellparty] 的 order。
            public ushort TemplateOrder { get; set; }

            // START_MAP 运行序号。为空时按普通 monster/APC 计数自动生成。
            public int? PacketIndex { get; set; }

            // START_MAP 隐藏标记。0 为可见房间对象，1 为深渊隐藏模板行。
            public byte Flag0 { get; set; }

            // 深渊柱子挂接选择器。86 官方柱子路径消费 Flag1 == 0xFF 的 hidden row。
            public byte Flag1 { get; set; }

            // START_MAP 附加状态。当前深渊隐藏行保持 0。
            public int ExtraState { get; set; }

            // 配置来源：MAP [special passive object] 的父对象序号。
            // 与协议 Flag1 分开保存，避免业务规则依赖 byte 截断后的封包字段。
            public int? SourceSpecialPassiveObjectIndex { get; set; }

            // 是否为深渊柱子流程挂接的隐藏小队成员。为 true 时死亡走深渊专用掉落分支。
            public bool IsHellPartyActor { get; set; }

            // 深渊小队编号，对应 etc/hellparty.etc 的 [group index]。
            public int HellPartyGroupId { get; set; }

            // 深渊难度：1=A/非常困难，2=B/困难。
            public byte HellPartyDifficulty { get; set; }

            // [difficulty] 第 1 项，最终深渊装备奖励计算次数。
            public int HellRewardRollCount { get; set; }

            // monster/APC 脚本中的 [hell monster] 标记。为 true 时不触发最终装备奖励。
            public bool IsHellMonsterScript { get; set; }

            // Source coordinates retained for conditional runtime spawns.
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
        }

        public struct MazeSumInfo
        {
            public int Index { get; set; }

            public int X { get; set; }

            public int Y { get; set; }

            public List<MonsterSumInfo> Monsters { get; set; }

            public IReadOnlyList<EventMonsterPositionInfo> EventMonsterPositions { get; set; }

            public IReadOnlyList<SpecialPassiveObjectInfo> SpecialPassiveObjects { get; set; }

            // MAP-owned ordinary passive objects do not receive START_MAP
            // actor sequences, but can satisfy type-0 destroy-object clear
            // conditions after a story/NPC interaction.
            public IReadOnlyList<int> PassiveObjectCodes { get; set; }
        }

        public struct DungeonRoomCoordinate
        {
            public int X { get; set; }

            public int Y { get; set; }

            public int MapId { get; set; }

            public string FilePath { get; set; }
        }

        public sealed class LinkedDungeonEntry
        {
            public int DungeonId { get; set; }
            public int Rate { get; set; }
            public int Condition { get; set; }
        }

        public sealed class LinkedDungeonClearPassiveObject
        {
            public int ObjectCode { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
        }

        public sealed class HellPartyWaveInfo
        {
            public int GroupId { get; set; }
            public int Order { get; set; }
            public List<MonsterSumInfo> Monsters { get; set; } = new List<MonsterSumInfo>();
        }

        public sealed class HellPartyRoomInfo
        {
            public int MapId { get; set; } = -1;
            public int NormalMapId { get; set; } = -1;
            public int X { get; set; }
            public int Y { get; set; }
            public int PillarObjectCode { get; set; }
            public int SpawnX { get; set; }
            public int SpawnY { get; set; }
            public HellPartyDifficultyRule DifficultyRule { get; set; }
            public List<HellPartyWaveInfo> Waves { get; set; } = new List<HellPartyWaveInfo>();

            public bool Found => MapId > 0;
        }

        public static byte GetDungeonBasicLv(int dungeonId)
            => DungeonCatalog.GetBasicLevel(dungeonId);

        public static int GetDungeonMinimumRequiredLevel(int dungeonId)
            => DungeonCatalog.GetMinimumRequiredLevel(dungeonId);

        public static bool MeetsMinimumRequiredLevel(
            int dungeonId,
            int characterLevel,
            out int minimumRequiredLevel)
        {
            minimumRequiredLevel = GetDungeonMinimumRequiredLevel(dungeonId);
            return minimumRequiredLevel <= 0
                || characterLevel >= minimumRequiredLevel;
        }

        public static bool IsSuitableLevelDungeon(int dungeonId, int characterLevel)
        {
            return characterLevel > 0
                && TryGetSuitableLevelRange(dungeonId, out var minLevel, out var maxLevel)
                && characterLevel >= minLevel
                && characterLevel <= maxLevel;
        }

        public static bool TryGetSuitableLevelRange(int dungeonId, out int minLevel, out int maxLevel)
            => DungeonCatalog.TryGetSuitableLevelRange(
                dungeonId,
                out minLevel,
                out maxLevel);

        public static int GetMaxDifficultyCount(int dungeonId)
            => DungeonCatalog.GetMaximumDifficultyCount(dungeonId);

        public static int GetChampionCount(int dungeonId, int difficulty, int mazeIndex, out int[] namedMonsterCodes)
        {
            namedMonsterCodes = null;
            try
            {
                var dngFile = GetDungeonFile(dungeonId);
                namedMonsterCodes = dngFile.NamedMonster;

                if (dngFile.Champion == null || dngFile.Champion.Length == 0)
                    return 0;

                int diffIdx = difficulty;
                if (diffIdx < 0) diffIdx = 0;
                if (diffIdx >= dngFile.Champion.Length) diffIdx = dngFile.Champion.Length - 1;
                int probBase = dngFile.Champion[diffIdx];

                int adjusted = probBase;
                switch (difficulty)
                {
                    case 1: adjusted = probBase * 150 / 100; break;
                    case 2: adjusted = probBase * 250 / 100; break;
                    case 3: adjusted = probBase * 500 / 100; break;
                }

                int mazeW = 4, mazeH = 5;
                if (dngFile.Mazes != null && mazeIndex >= 0 && mazeIndex < dngFile.Mazes.Count)
                {
                    var m = dngFile.Mazes[mazeIndex];
                    if (m.Width > 0) mazeW = m.Width;
                    if (m.Height > 0) mazeH = m.Height;
                }

                int area = mazeW * mazeH;
                return 100 * adjusted / area > Infrastructure.ServerRandom.Next(100) ? 1 : 0;
            }
            catch { return 0; }
        }

        public static void PromoteChampions(
            List<MonsterSumInfo> monsters,
            int count,
            int[] namedMonsterCodes = null,
            int dungeonId = 0)
        {
            if (count <= 0) return;

            var namedSet = namedMonsterCodes != null && namedMonsterCodes.Length > 0
                ? new HashSet<int>(namedMonsterCodes) : null;
            var visibleNormalCounts = new Dictionary<int, int>();
            foreach (var monster in monsters)
            {
                if (monster.Type != 0
                    || !monster.IsBlocking
                    || monster.Flag0 != 0
                    || monster.Code <= 0)
                {
                    continue;
                }

                visibleNormalCounts.TryGetValue(monster.Code, out var existing);
                visibleNormalCounts[monster.Code] = existing + 1;
            }

            var specialDungeon = false;
            if (dungeonId > 0)
            {
                try
                {
                    specialDungeon = GetDungeonFile(dungeonId)?.SpecialDungeon == true;
                }
                catch
                {
                    specialDungeon = false;
                }
            }

            var normalIndices = new List<int>();
            for (int i = 0; i < monsters.Count; i++)
            {
                var monster = monsters[i];
                visibleNormalCounts.TryGetValue(
                    monster.Code,
                    out var sameCodeCount);
                if (monster.Type == 0
                    && monster.IsBlocking
                    && monster.Flag0 == 0
                    && !monster.NoChampionPromotion
                    && (namedSet == null || !namedSet.Contains(monster.Code))
                    && !SequentialDungeonMonsterCatalog.Contains(
                        dungeonId,
                        monster.Code)
                    && !(specialDungeon
                        && sameCodeCount == 1
                        && IndependentDropDefinitionCatalog
                            .HasMonsterDefinition(monster.Code)))
                    normalIndices.Add(i);
            }

            for (int i = 0; i < count && normalIndices.Count > 0; i++)
            {
                int pick = Infrastructure.ServerRandom.Next(normalIndices.Count);
                int idx = normalIndices[pick];
                normalIndices.RemoveAt(pick);

                var m = monsters[idx];
                m.Type = 1;
                monsters[idx] = m;
            }
        }

        public static float GetExperienceWeight(int dungeonId)
        {
            try
            {
                var dngFile = GetDungeonFile(dungeonId);
                return dngFile.ExperienceIncreasingPoint >= 0 ? dngFile.ExperienceIncreasingPoint : 1.0f;
            }
            catch
            {
                return 1.0f;
            }
        }

        public static MazeInfo GetDungeonDefaultMaze(int dungeonId)
            => DungeonCatalog.GetDefaultMaze(dungeonId);

        public static List<LinkedDungeonEntry> GetLinkedDungeonNextEntries(int dungeonId)
        {
            try
            {
                return ParseLinkedDungeonNextEntries(
                    GetDungeonFile(dungeonId)?.LinkedDungeon);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Dungeon] linked dungeon parse failed: " +
                    $"dungeon={dungeonId} error={ex.Message}");
                return new List<LinkedDungeonEntry>();
            }
        }

        public static List<int> GetLinkedDungeonPreviousIds(int dungeonId)
        {
            try
            {
                return ParseLinkedDungeonPreviousIds(
                    GetDungeonFile(dungeonId)?.LinkedDungeon);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Dungeon] linked dungeon prev parse failed: " +
                    $"dungeon={dungeonId} error={ex.Message}");
                return new List<int>();
            }
        }

        public static bool CanEnterLinkedDungeonFrom(
            int dungeonId,
            int previousDungeonId)
        {
            if (previousDungeonId <= 0)
                return false;

            return GetLinkedDungeonPreviousIds(dungeonId)
                .Contains(previousDungeonId);
        }

        public static bool SupportsLinkedDungeonContinue(int dungeonId)
        {
            try
            {
                var dungeonFile = GetDungeonFile(dungeonId);
                if (dungeonFile == null
                    || ParseLinkedDungeonNextEntries(
                        dungeonFile.LinkedDungeon).Count == 0)
                {
                    return false;
                }

                return dungeonFile.SpecialDungeon
                    || TryParseLinkedDungeonClearPassiveObject(
                        dungeonFile.LinkedDungeon,
                        out _);
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetLinkedDungeonClearPassiveObject(
            int dungeonId,
            out LinkedDungeonClearPassiveObject passiveObject)
        {
            passiveObject = null;
            try
            {
                return TryParseLinkedDungeonClearPassiveObject(
                    GetDungeonFile(dungeonId)?.LinkedDungeon,
                    out passiveObject);
            }
            catch
            {
                return false;
            }
        }

        public static LinkedDungeonEntry PickLinkedDungeonNext(int dungeonId)
        {
            var entries = GetLinkedDungeonNextEntries(dungeonId);
            if (entries.Count == 0)
                return null;

            var noSelectionRate = 0;
            try
            {
                noSelectionRate = ParseLinkedDungeonNoSelectionRate(
                    GetDungeonFile(dungeonId)?.LinkedDungeon);
            }
            catch
            {
                noSelectionRate = 0;
            }

            var totalRate = noSelectionRate > 0 ? noSelectionRate : 0;
            foreach (var entry in entries)
            {
                if (entry.Rate > 0
                    && totalRate <= int.MaxValue - entry.Rate)
                {
                    totalRate += entry.Rate;
                }
            }

            if (totalRate <= 0)
                return entries[0];

            var roll = Infrastructure.ServerRandom.Next(totalRate);
            return SelectLinkedDungeonByRoll(entries, noSelectionRate, roll);
        }

        internal static LinkedDungeonEntry SelectLinkedDungeonByRoll(
            IReadOnlyList<LinkedDungeonEntry> entries,
            int noSelectionRate,
            int roll)
        {
            if (entries == null || entries.Count == 0 || roll < 0)
                return null;

            foreach (var entry in entries)
            {
                if (entry.Rate <= 0)
                    continue;
                if (roll < entry.Rate)
                    return entry;
                roll -= entry.Rate;
            }

            return noSelectionRate > 0 && roll < noSelectionRate
                ? null
                : entries[0];
        }

        internal static List<LinkedDungeonEntry> ParseLinkedDungeonNextEntries(
            string linkedDungeon)
        {
            var result = new List<LinkedDungeonEntry>();
            if (string.IsNullOrWhiteSpace(linkedDungeon))
                return result;

            var blocks = new List<string>();
            var matches = Regex.Matches(
                linkedDungeon,
                @"\[next\](?<body>.*?)(?:\[/next\]|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
                blocks.Add(match.Groups["body"].Value);
            if (blocks.Count == 0)
                blocks.Add(linkedDungeon);

            foreach (var block in blocks)
            {
                var numbers = Regex.Matches(
                    block ?? string.Empty,
                    @"[+-]?\d+");
                for (var i = 0; i + 2 < numbers.Count; i += 3)
                {
                    if (!int.TryParse(
                            numbers[i].Value,
                            out var nextDungeonId)
                        || !int.TryParse(
                            numbers[i + 1].Value,
                            out var rate)
                        || !int.TryParse(
                            numbers[i + 2].Value,
                            out var condition)
                        || nextDungeonId <= 0)
                    {
                        continue;
                    }

                    result.Add(new LinkedDungeonEntry
                    {
                        DungeonId = nextDungeonId,
                        Rate = rate,
                        Condition = condition,
                    });
                }
            }

            return result;
        }

        internal static List<int> ParseLinkedDungeonPreviousIds(
            string linkedDungeon)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(linkedDungeon))
                return result;

            var seen = new HashSet<int>();
            var matches = Regex.Matches(
                linkedDungeon,
                @"\[prev\](?<body>.*?)(?=\[/prev\]|\[[^\]\r\n]+\]|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                var numbers = Regex.Matches(
                    match.Groups["body"].Value,
                    @"[+-]?\d+");
                foreach (Match number in numbers)
                {
                    if (!int.TryParse(number.Value, out var previousDungeonId)
                        || previousDungeonId <= 0
                        || !seen.Add(previousDungeonId))
                    {
                        continue;
                    }

                    result.Add(previousDungeonId);
                }
            }

            return result;
        }

        internal static int ParseLinkedDungeonNoSelectionRate(
            string linkedDungeon)
        {
            if (string.IsNullOrWhiteSpace(linkedDungeon))
                return 0;

            var total = 0;
            var matches = Regex.Matches(
                linkedDungeon,
                @"\[next\](?<body>.*?)(?:\[/next\]|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                var numbers = Regex.Matches(
                    match.Groups["body"].Value,
                    @"[+-]?\d+");
                for (var i = 0; i + 2 < numbers.Count; i += 3)
                {
                    if (!int.TryParse(numbers[i].Value, out var dungeonId)
                        || !int.TryParse(numbers[i + 1].Value, out var rate)
                        || dungeonId >= 0
                        || rate <= 0)
                    {
                        continue;
                    }

                    if (total <= int.MaxValue - rate)
                        total += rate;
                }
            }

            return total;
        }

        internal static bool TryParseLinkedDungeonClearPassiveObject(
            string linkedDungeon,
            out LinkedDungeonClearPassiveObject passiveObject)
        {
            passiveObject = null;
            if (string.IsNullOrWhiteSpace(linkedDungeon))
                return false;

            var match = Regex.Match(
                linkedDungeon,
                @"\[on\s+clear\s+add\s+passive\s+object\]\s*" +
                @"(?<code>[+-]?\d+)\s+(?<x>[+-]?\d+)\s+(?<y>[+-]?\d+)",
                RegexOptions.IgnoreCase);
            if (!match.Success
                || !int.TryParse(match.Groups["code"].Value, out var objectCode)
                || !int.TryParse(match.Groups["x"].Value, out var x)
                || !int.TryParse(match.Groups["y"].Value, out var y)
                || objectCode <= 0)
            {
                return false;
            }

            passiveObject = new LinkedDungeonClearPassiveObject
            {
                ObjectCode = objectCode,
                X = x,
                Y = y,
            };
            return true;
        }

        private static readonly Lazy<Dictionary<int, bool>> _monsterHellFlags =
            new Lazy<Dictionary<int, bool>>(() => LoadHellMonsterFlags("monster/monster.lst", "monster"));
        private static readonly Lazy<Dictionary<int, bool>> _aiCharacterHellFlags =
            new Lazy<Dictionary<int, bool>>(() => LoadHellMonsterFlags("AICharacter/AICharacter.lst", "AICharacter"));
        private static readonly object _namedMonsterCacheLock = new object();
        private static readonly Dictionary<int, HashSet<int>> _namedMonsterCache = new Dictionary<int, HashSet<int>>();

        public static bool IsNamedMonster(int dungeonId, int monsterCode)
        {
            if (dungeonId <= 0 || monsterCode <= 0)
                return false;

            HashSet<int> namedSet;
            lock (_namedMonsterCacheLock)
            {
                if (!_namedMonsterCache.TryGetValue(dungeonId, out namedSet))
                {
                    namedSet = new HashSet<int>();
                    try
                    {
                        var loaded = LoadDungeonFileWithPath(dungeonId);
                        if (loaded.File.NamedMonster != null)
                        {
                            foreach (var code in loaded.File.NamedMonster)
                                if (code > 0) namedSet.Add(code);
                        }
                    }
                    catch { }

                    _namedMonsterCache[dungeonId] = namedSet;
                }
            }

            return namedSet.Contains(monsterCode);
        }

        public static int[] RandomizeStartPosition(int[] startMap)
        {
            return RandomizeMapPosition(startMap);
        }

        public static int[] RandomizeBossPosition(int[] bossMap)
        {
            return RandomizeMapPosition(bossMap);
        }

        private static int[] RandomizeMapPosition(int[] positions)
        {
            if (positions == null || positions.Length < 2) return null;
            int pairCount = positions.Length / 2;
            if (pairCount <= 1) return new[] { positions[0], positions[1] };
            int pick = Infrastructure.ServerRandom.Next(pairCount);
            return new[] { positions[pick * 2], positions[pick * 2 + 1] };
        }

        // df_game_r CBattle_Field::GetAppropriateMaze — two-pass quest connection matching.
        // Pass 1 (questType=0): match mazes where the quest is currently active (IsDoingQuest).
        // Pass 2 (questType=1): match mazes where the quest is already cleared (isClearQuest).
        // qc[0]=questType, qc[1]=questId, qc[2]=minDifficulty (-1 = no restriction).
        public static (MazeInfo Maze, int Index) SelectDungeonMaze(
            int dungeonId,
            int difficulty = 0,
            ICollection<int> activeQuestIds = null,
            ICollection<int> clearedQuestIds = null,
            Action<string> diagnosticSink = null)
            => DungeonSelectionPlanner.SelectMaze(
                dungeonId,
                difficulty,
                activeQuestIds,
                clearedQuestIds,
                diagnosticSink);

        public static bool IsQuestConnectedSelection(
            int dungeonId,
            MazeInfo maze,
            ICollection<int> activeQuestIds,
            int difficulty)
            => DungeonSelectionPlanner.IsQuestConnected(
                dungeonId,
                maze,
                activeQuestIds,
                difficulty);

        public static int ResolveActiveQuestMazeQuestId(
            int dungeonId,
            MazeInfo maze,
            ICollection<int> activeQuestIds,
            int difficulty)
            => DungeonSelectionPlanner.ResolveActiveQuestConnectionId(
                dungeonId,
                maze,
                activeQuestIds,
                difficulty);

        public static bool TrySelectActiveQuestMaze(
            int dungeonId,
            int difficulty,
            int activeQuestId,
            out (MazeInfo Maze, int Index) selection,
            Action<string> diagnosticSink = null)
            => DungeonSelectionPlanner.TrySelectActiveQuestMaze(
                dungeonId,
                difficulty,
                activeQuestId,
                out selection,
                diagnosticSink);

        public static MazeInfo GetDungeonMaze(int dungeonId, int mazeIndex)
            => DungeonCatalog.GetMaze(dungeonId, mazeIndex);

        public static int[] GetLayeredMapIds(int dungeonId, int x, int y, int mazeIndex)
        {
            var dngFile = GetDungeonFile(dungeonId);
            if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                return null;
            var maze = (mazeIndex >= 0 && mazeIndex < dngFile.Mazes.Count) ? dngFile.Mazes[mazeIndex] : dngFile.Mazes[0];
            if (maze.MapSpecifications == null) return null;
            foreach (var spec in maze.MapSpecifications)
            {
                if (spec.Type == "layered" && spec.X == x && spec.Y == y && spec.LayeredMapIds != null)
                    return spec.LayeredMapIds;
            }
            return null;
        }

        internal static bool TryGetWarpMapOverride(
            int dungeonId,
            int mazeIndex,
            int targetX,
            int targetY,
            out int sourceX,
            out int sourceY,
            out int destX,
            out int destY,
            out int overrideMapId)
        {
            sourceX = sourceY = destX = destY = overrideMapId = -1;

            try
            {
                var dngFile = GetDungeonFile(dungeonId);
                if (dngFile.Mazes == null || dngFile.Mazes.Count == 0)
                    return false;

                var maze = mazeIndex >= 0 && mazeIndex < dngFile.Mazes.Count
                    ? dngFile.Mazes[mazeIndex]
                    : dngFile.Mazes[0];
                if (!TryGetWarpMapConditionRules(
                    dungeonId,
                    mazeIndex,
                    out var rules))
                {
                    return false;
                }

                var matchingRules = rules.FindAll(rule =>
                    rule.SourceX == targetX && rule.SourceY == targetY);
                if (matchingRules.Count != 1)
                {
                    if (matchingRules.Count > 1)
                    {
                        FileLogger.Log(
                            $"[Dungeon] warp map condition has ambiguous source: " +
                            $"dungeon={dungeonId} maze={mazeIndex} target=({targetX},{targetY}) " +
                            $"destinations={matchingRules.Count}");
                    }
                    return false;
                }

                var rule = matchingRules[0];
                sourceX = rule.SourceX;
                sourceY = rule.SourceY;
                destX = rule.DestinationX;
                destY = rule.DestinationY;

                if (maze?.MapSpecifications == null)
                    return false;

                foreach (var spec in maze.MapSpecifications)
                {
                    if (spec.X != destX || spec.Y != destY || spec.Index <= 0)
                        continue;

                    overrideMapId = spec.Index;
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] warp map condition parse failed: dungeon={dungeonId} maze={mazeIndex} target=({targetX},{targetY}) error={ex.Message}");
            }

            return false;
        }

        internal static bool TryGetWarpMapConditionRules(
            int dungeonId,
            int mazeIndex,
            out List<WarpMapConditionEntry> rules)
        {
            rules = new List<WarpMapConditionEntry>();

            try
            {
                var dngFile = GetDungeonFile(dungeonId);
                if (dngFile.Mazes == null || dngFile.Mazes.Count == 0
                    || dngFile.WarpMapConditions == null
                    || dngFile.WarpMapConditions.Count == 0)
                {
                    return false;
                }

                rules.AddRange(dngFile.WarpMapConditions);
                return true;
            }
            catch (Exception ex)
            {
                rules.Clear();
                FileLogger.Log(
                    $"[Dungeon] warp map condition load failed: " +
                    $"dungeon={dungeonId} maze={mazeIndex} error={ex.Message}");
                return false;
            }
        }

        public static List<MonsterSumInfo> GetMapMonsterConditionSummaryInformation(
            int mapId,
            int dungeonId,
            int x,
            int y,
            ICollection<int> monsterCodes)
            => ResolvedRoomTemplateProvider.GetMonsterConditionActors(
                mapId,
                dungeonId,
                x,
                y,
                monsterCodes);

        public static bool IsHellDungeon(int dungeonId)
        {
            try
            {
                var area = WorldMap.GetAreaByDungeonId(dungeonId);
                if (area != null)
                    return area.HellDungeon;

                var loaded = LoadDungeonFileWithPath(dungeonId);
                return loaded.File.GetIntValue("hell dungeon", 0) == 1;
            }
            catch
            {
                return false;
            }
        }

        public static int FindHellMapIdForRoom(int dungeonId, int x, int y, int mazeIndex)
        {
            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                var dungeonFile = loaded.File;
                if (dungeonFile.Mazes == null || dungeonFile.Mazes.Count == 0)
                    return -1;

                var maze = (mazeIndex >= 0 && mazeIndex < dungeonFile.Mazes.Count)
                    ? dungeonFile.Mazes[mazeIndex]
                    : dungeonFile.Mazes[0];

                var maplst = DungeonMapCatalog.LoadMapList();
                var mapDirCandidates = BuildMapDirCandidates(maplst, maze, loaded.FilePath);

                foreach (var entry in maplst.Entries)
                {
                    if (!IsInCandidateDir(entry.FilePath, mapDirCandidates))
                        continue;

                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (string.IsNullOrEmpty(fileName)
                        || !fileName.StartsWith("hell_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (fileName.IndexOf($"({x},{y})", StringComparison.OrdinalIgnoreCase) >= 0
                        || fileName.IndexOf($"({x}.{y})", StringComparison.OrdinalIgnoreCase) >= 0)
                        return entry.Id;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] FindHellMapIdForRoom ERROR: dungeon={dungeonId} room=({x},{y}) {ex.Message}");
            }

            return -1;
        }

        public static IReadOnlyList<DungeonRoomCoordinate> GetDungeonRoomCoordinates(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze)
        {
            var result = new List<DungeonRoomCoordinate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                if (maze == null)
                {
                    var dungeonFile = loaded.File;
                    if (dungeonFile.Mazes == null || dungeonFile.Mazes.Count == 0)
                        return result;

                    maze = mazeIndex >= 0 && mazeIndex < dungeonFile.Mazes.Count
                        ? dungeonFile.Mazes[mazeIndex]
                        : dungeonFile.Mazes[0];
                }

                var maplst = DungeonMapCatalog.LoadMapList();
                var mapDirCandidates = BuildMapDirCandidates(maplst, maze, loaded.FilePath);

                foreach (var entry in maplst.Entries)
                {
                    if (!IsInCandidateDir(entry.FilePath, mapDirCandidates))
                        continue;

                    var fileName = Path.GetFileName(entry.FilePath);
                    if (!DungeonMapResolver.TryParseMapFileCoordinate(fileName, out var x, out var y))
                        continue;

                    var key = x + "," + y;
                    if (!seen.Add(key))
                        continue;

                    result.Add(new DungeonRoomCoordinate
                    {
                        X = x,
                        Y = y,
                        MapId = entry.Id,
                        FilePath = entry.FilePath,
                    });
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] GetDungeonRoomCoordinates ERROR: dungeon={dungeonId} maze={mazeIndex} {ex.Message}");
            }

            return result;
        }

        public static HellPartyRoomInfo FindHellMapRoom(
            int dungeonId,
            MazeInfo maze,
            int mazeIndex,
            byte difficulty,
            bool preferSeasonSealDoor = false)
        {
            if (maze?.MapSpecifications == null)
                return new HellPartyRoomInfo();

            // Difficulty A/B selects the HellParty wave/reward rule.  It does
            // not select the seasonal map route.  The seasonal seal door is
            // an explicit activity route (currently the gorgeous challenge);
            // ordinary and difficult manual entries must share the PVF
            // ordinary seal-door coordinate.
            var preferSeason = preferSeasonSealDoor
                && HellPartyData.IsSeasonHellPartyEnabled();
            if (preferSeason)
            {
                if (TryBuildSealDoorHellPartyRoom(
                        dungeonId,
                        maze,
                        difficulty,
                        "season",
                        maze.SeasonSealDoorMapIndex,
                        maze.SeasonSealDoorPos,
                        out var seasonRoom))
                    return seasonRoom;

                if (TryBuildSealDoorHellPartyRoom(
                        dungeonId,
                        maze,
                        difficulty,
                        "ordinary-fallback",
                        maze.SealDoorMapIndex,
                        maze.SealDoorPos,
                        out var ordinaryFallbackRoom))
                    return ordinaryFallbackRoom;
            }
            else
            {
                if (TryBuildSealDoorHellPartyRoom(
                        dungeonId,
                        maze,
                        difficulty,
                        "ordinary",
                        maze.SealDoorMapIndex,
                        maze.SealDoorPos,
                        out var ordinaryRoom))
                    return ordinaryRoom;

                if (HellPartyData.IsSeasonHellPartyEnabled()
                    && TryBuildSealDoorHellPartyRoom(
                        dungeonId,
                        maze,
                        difficulty,
                        "season-fallback",
                        maze.SeasonSealDoorMapIndex,
                        maze.SeasonSealDoorPos,
                        out var seasonFallbackRoom))
                    return seasonFallbackRoom;
            }

            foreach (var spec in maze.MapSpecifications)
            {
                var hellMapId = FindHellMapIdForRoom(dungeonId, spec.X, spec.Y, mazeIndex);
                if (hellMapId <= 0)
                    continue;

                return BuildHellPartyRoomInfo(hellMapId, spec.Index, spec.X, spec.Y, dungeonId, difficulty);
            }

            return new HellPartyRoomInfo();
        }

        private static bool TryBuildSealDoorHellPartyRoom(
            int dungeonId,
            MazeInfo maze,
            byte difficulty,
            string owner,
            int hellMapId,
            int[] position,
            out HellPartyRoomInfo room)
        {
            room = new HellPartyRoomInfo();
            if (hellMapId <= 0 || position == null || position.Length < 2)
                return false;

            var x = position[0];
            var y = position[1];
            var normalMapId = FindNormalMapIdForRoom(maze, x, y);
            var hasHellParty = IsHellPartyMap(hellMapId);
            if (normalMapId <= 0 || !hasHellParty)
            {
                FileLogger.Log(
                    $"[Dungeon] HellParty seal door ignored: dungeon={dungeonId} "
                    + $"mode={difficulty} owner={owner} room=({x},{y}) "
                    + $"hellMap={hellMapId} normalMap={normalMapId} "
                    + $"hellPartyMap={hasHellParty}");
                return false;
            }

            var candidate = BuildHellPartyRoomInfo(
                hellMapId,
                normalMapId,
                x,
                y,
                dungeonId,
                difficulty);
            if (!candidate.Found || candidate.Waves.Count == 0)
            {
                FileLogger.Log(
                    $"[Dungeon] HellParty seal door build rejected: dungeon={dungeonId} "
                    + $"mode={difficulty} owner={owner} room=({x},{y}) "
                    + $"hellMap={hellMapId} waves={candidate.Waves.Count}");
                return false;
            }

            FileLogger.Log(
                $"[Dungeon] HellParty seal door selected: dungeon={dungeonId} "
                + $"mode={difficulty} seasonEnabled={HellPartyData.IsSeasonHellPartyEnabled()} "
                + $"owner={owner} room=({x},{y}) hellMap={hellMapId} "
                + $"normalMap={normalMapId}");
            room = candidate;
            return true;
        }

        private static bool IsHellPartyMap(int mapId)
        {
            if (mapId <= 0)
                return false;

            try
            {
                var mapFile = LoadMapFile(mapId);
                if (mapFile?.SpecialPassiveObjects == null)
                    return false;

                foreach (var obj in mapFile.SpecialPassiveObjects)
                    if (obj?.HellPartyEntries != null && obj.HellPartyEntries.Count > 0)
                        return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] HellParty map validation failed: map={mapId} {ex.Message}");
            }

            return false;
        }

        private static int FindNormalMapIdForRoom(MazeInfo maze, int x, int y)
        {
            if (maze?.MapSpecifications == null)
                return -1;

            foreach (var spec in maze.MapSpecifications)
                if (spec.X == x && spec.Y == y && spec.Index > 0)
                    return spec.Index;

            return -1;
        }

        private static HellPartyRoomInfo BuildHellPartyRoomInfo(int mapId, int normalMapId, int x, int y, int dungeonId, byte difficulty)
        {
            try
            {
                var mapFile = LoadMapFile(mapId);
                SpecialPassiveObjectInfo pillar = null;
                foreach (var obj in mapFile.SpecialPassiveObjects)
                {
                    if (pillar == null)
                        pillar = obj;
                    if (obj.HellPartyEntries.Count > 0)
                    {
                        pillar = obj;
                        break;
                    }
                }

                return new HellPartyRoomInfo
                {
                    MapId = mapId,
                    NormalMapId = normalMapId,
                    X = x,
                    Y = y,
                    PillarObjectCode = pillar?.ObjectCode ?? 0,
                    SpawnX = pillar?.X ?? 0,
                    SpawnY = pillar?.Y ?? 0,
                    DifficultyRule = HellPartyData.GetDifficultyRule(difficulty),
                    Waves = BuildHellPartyWaves(mapFile, dungeonId, difficulty),
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] BuildHellPartyRoomInfo ERROR: map={mapId} {ex.Message}");
                return new HellPartyRoomInfo();
            }
        }

        private static List<HellPartyWaveInfo> BuildHellPartyWaves(MapFile mapFile, int dungeonId, byte difficulty)
        {
            var result = new List<HellPartyWaveInfo>();
            var entriesByOrder = new SortedDictionary<int, List<HellPartyMapEntry>>();
            foreach (var obj in mapFile.SpecialPassiveObjects)
            {
                foreach (var entry in obj.HellPartyEntries)
                {
                    if (!entriesByOrder.TryGetValue(entry.Order, out var list))
                    {
                        list = new List<HellPartyMapEntry>();
                        entriesByOrder[entry.Order] = list;
                    }
                    list.Add(entry);
                }
            }

            foreach (var pair in entriesByOrder)
            {
                var candidates = new List<HellPartyMapEntry>();
                foreach (var entry in pair.Value)
                    if (HellPartyData.HasEntries(entry.GroupId, difficulty))
                        candidates.Add(entry);

                var selected = PickHellPartyEntry(candidates);
                if (selected == null)
                    continue;

                var monsters = BuildHellPartyMonsterInfos(selected.GroupId, dungeonId, difficulty);
                if (monsters.Count == 0)
                    continue;

                result.Add(new HellPartyWaveInfo
                {
                    GroupId = selected.GroupId,
                    Order = pair.Key,
                    Monsters = monsters,
                });

                FileLogger.Log($"[Dungeon] HellParty wave: order={pair.Key} group={selected.GroupId} mode={difficulty} monsters={monsters.Count}");
            }

            return result;
        }

        private static List<MonsterSumInfo> BuildHellPartyMonsterInfos(int groupId, int dungeonId, byte difficulty)
        {
            var result = new List<MonsterSumInfo>();
            var groupEntries = HellPartyData.GetEntries(groupId, difficulty);
            var difficultyRule = HellPartyData.GetDifficultyRule(difficulty);
            var rewardRollCount = Math.Max(0, difficultyRule?.RewardRollCount ?? 0);
            foreach (var groupEntry in groupEntries)
            {
                byte type;
                byte level;
                bool isHellMonsterScript;
                if (groupEntry.EntityType == 1)
                {
                    type = 5;
                    if (!TryGetAICharacterLevel(groupEntry.Code, out level))
                    {
                        FileLogger.Log($"[Dungeon] HellParty APC code={groupEntry.Code} not found in AICharacter.lst; fallback to dungeon level");
                        level = GetDungeonBasicLv(dungeonId);
                    }
                    isHellMonsterScript = IsAICharacterHellMonster(groupEntry.Code);
                }
                else
                {
                    type = 0;
                    level = GetDungeonBasicLv(dungeonId);
                    isHellMonsterScript = IsMonsterHellMonster(groupEntry.Code);
                }

                result.Add(new MonsterSumInfo
                {
                    Code = groupEntry.Code,
                    Level = level,
                    Type = type,
                    IsBlocking = true,
                    IsHellPartyActor = true,
                    HellPartyGroupId = groupId,
                    HellPartyDifficulty = difficulty,
                    HellRewardRollCount = rewardRollCount,
                    IsHellMonsterScript = isHellMonsterScript,
                });
            }

            return result;
        }

        internal static (DungeonFile File, string FilePath) LoadDungeonFileWithPath(int dungeonId)
            => DungeonCatalog.GetDungeonFileWithPath(dungeonId);

        internal static bool TryGetTowerOfDespairFloor(int dungeonId, out int floor)
        {
            floor = 0;
            try
            {
                var loaded = LoadDungeonFileWithPath(dungeonId);
                if (loaded.File.TowerOfDespair <= 0)
                    return false;

                var dungeonFileName = Path.GetFileNameWithoutExtension(loaded.FilePath) ?? string.Empty;
                var match = Regex.Match(
                    dungeonFileName,
                    @"TowerOfDespair(?<floor>\d{3})$",
                    RegexOptions.IgnoreCase);
                return match.Success
                    && int.TryParse(match.Groups["floor"].Value, out floor)
                    && floor > 0;
            }
            catch
            {
                floor = 0;
                return false;
            }
        }

        internal static bool TryGetTowerOfDespairDungeonId(int floor, out int dungeonId)
        {
            dungeonId = 0;
            if (floor < 1 || floor > 100)
                return false;

            try
            {
                var expectedFileName = $"TowerOfDespair{floor:000}";
                foreach (var entry in LoadDungeonLstFile().Entries)
                {
                    var fileName = Path.GetFileNameWithoutExtension(entry.FilePath) ?? string.Empty;
                    if (!fileName.EndsWith(expectedFileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    dungeonId = entry.Id;
                    return dungeonId > 0;
                }
            }
            catch
            {
                dungeonId = 0;
            }

            return false;
        }

        private static MapFile LoadMapFile(int mapId)
            => DungeonMapCatalog.GetMapFile(mapId);

        internal static List<string> BuildMapDirCandidates(LstFile maplst, MazeInfo maze, string dungeonFilePath)
        {
            var result = new List<string>();

            void AddDirCandidate(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                dir = dir.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(dir)) return;
                foreach (var existing in result)
                    if (string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase)) return;
                result.Add(dir);
            }

            void AddMapId(int mapId)
            {
                var entry = maplst.GetById(mapId);
                if (entry != null && !string.IsNullOrEmpty(entry.FilePath))
                    AddDirCandidate(System.IO.Path.GetDirectoryName(entry.FilePath));
            }

            if (maze.MapSpecifications != null && maplst != null)
            {
                foreach (var spec in maze.MapSpecifications)
                {
                    AddMapId(spec.Index);
                    if (spec.MapCandidates != null)
                        foreach (var id in spec.MapCandidates)
                            AddMapId(id);
                    if (spec.LayeredMapIds != null)
                        foreach (var id in spec.LayeredMapIds)
                            AddMapId(id);
                }
            }

            var dgnDir = System.IO.Path.GetFileNameWithoutExtension(dungeonFilePath);
            AddDirCandidate(dgnDir);
            if (dgnDir != null && dgnDir.StartsWith("tutorial_", StringComparison.OrdinalIgnoreCase))
                AddDirCandidate(dgnDir.Substring("tutorial_".Length));

            if (maplst != null && !string.IsNullOrEmpty(dgnDir))
            {
                foreach (var entry in maplst.Entries)
                {
                    if (entry.FilePath == null) continue;
                    var fileName = System.IO.Path.GetFileName(entry.FilePath);
                    if (fileName != null && fileName.StartsWith(dgnDir, StringComparison.OrdinalIgnoreCase))
                        AddDirCandidate(System.IO.Path.GetDirectoryName(entry.FilePath));
                }
            }

            return result;
        }

        private static bool IsInCandidateDir(string filePath, List<string> candidates)
        {
            if (filePath == null) return false;
            foreach (var dir in candidates)
            {
                if (filePath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)
                    || filePath.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static HellPartyMapEntry PickHellPartyEntry(List<HellPartyMapEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return null;

            var total = 0;
            foreach (var entry in entries)
                if (entry.Rate > 0)
                    total += entry.Rate;

            if (total <= 0)
                return entries[0];

            var roll = Infrastructure.ServerRandom.Next(total);
            foreach (var entry in entries)
            {
                if (entry.Rate <= 0)
                    continue;
                if (roll < entry.Rate)
                    return entry;
                roll -= entry.Rate;
            }

            return entries[0];
        }

        private static bool IsMonsterHellMonster(int monsterCode)
        {
            return _monsterHellFlags.Value.TryGetValue(monsterCode, out var value) && value;
        }

        private static bool IsAICharacterHellMonster(int aiCharacterCode)
        {
            return _aiCharacterHellFlags.Value.TryGetValue(aiCharacterCode, out var value) && value;
        }

        private static Dictionary<int, bool> LoadHellMonsterFlags(string lstPath, string baseDir)
        {
            var result = new Dictionary<int, bool>();
            try
            {
                var lst = LstFile.Parse(PvfArchiveAccessor.ReadText(lstPath));
                foreach (var entry in lst.Entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                        continue;

                    string content;
                    try { content = PvfArchiveAccessor.ReadText(Path.Combine(baseDir, entry.FilePath)); }
                    catch { continue; }

                    result[entry.Id] = ParseHellMonsterFlag(content);
                }
                FileLogger.Log($"[Dungeon] HellMonster flags loaded: {baseDir} count={result.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Dungeon] HellMonster flags load failed: {lstPath} {ex.Message}");
            }
            return result;
        }

        private static bool ParseHellMonsterFlag(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            var match = Regex.Match(content, @"\[hell monster\]\s*([+-]?\d+)", RegexOptions.IgnoreCase);
            return match.Success
                && int.TryParse(match.Groups[1].Value, out var value)
                && value == 1;
        }

        public static List<MonsterSumInfo> GetMapConditionalSummonSummaryInformation(
            int mapId,
            int dungeonId,
            int x,
            int y,
            ICollection<int> monsterCodes)
            => ResolvedRoomTemplateProvider.GetConditionalSummonActors(
                mapId,
                dungeonId,
                x,
                y,
                monsterCodes);

        public static MazeSumInfo GetDungeonMapMonsterSummaryInformation(int dungeonId, int x, int y, int mazeIndex = -1, int overrideMapId = -1, int[] bossPos = null)
        {
            if (dungeonId == 5000)
            {
                return new MazeSumInfo
                {
                    X = 0,
                    Y = 0,
                    Index = 36250,
                    Monsters = new List<MonsterSumInfo>(),
                };
            }
            return ResolvedRoomTemplateProvider.Resolve(
                dungeonId,
                x,
                y,
                mazeIndex,
                overrideMapId,
                bossPos);
        }

        private static bool TryGetAICharacterLevel(int apcCode, out byte level)
            => DungeonActorTemplateProjector.TryGetAiCharacterLevel(
                apcCode,
                out level);
    }
}
