using DfoServer.Game.Inventory;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public static class HellMonsterDropConfig
    {
        private const int BaseDropRate = 100;
        private const int DropChanceDenominator = 1001;
        private const int EpicRarity = 4;

        private struct DropProb
        {
            public int LevelMin;
            public int LevelMax;
            public int[] DifficultyRates;
        }

        private static DropProb[] _dropProbs;
        private static int[][] _rarityThresholds;
        private static (int Down, int Up)[] _gradeRanges;
        private static Dictionary<long, List<(int Id, int Weight)>> _equipPool;

        private static readonly object _lock = new object();
        private static bool _loaded;

        public static List<DropInfo> GenerateSpecificEquipmentDrops(
            DnfLcg lcg,
            int dungeonMinimumLevel,
            int itemBasisLevel,
            int dungeonDifficulty,
            byte hellDifficulty,
            int rewardRollCount,
            bool epicBuffPotionActive,
            ref ushort slotCounter)
        {
            EnsureLoaded();
            var result = new List<DropInfo>();
            if (lcg == null || rewardRollCount <= 0)
                return result;

            var prob = FindDropProb(itemBasisLevel);
            if (prob.DifficultyRates == null)
                return result;

            var difficultyIndex = Math.Max(0, Math.Min(dungeonDifficulty, prob.DifficultyRates.Length - 1));
            var rate = prob.DifficultyRates[difficultyIndex];
            if (rate <= 0)
                return result;

            // 70 服务端以基础倍率乘深渊概率表；当前模拟器没有独立基础倍率来源，先固定为 100。
            var chance = BaseDropRate * rate / 100;
            for (var i = 0; i < rewardRollCount; i++)
            {
                var chanceRoll = lcg.Next(DropChanceDenominator);
                if (chanceRoll > chance)
                {
                    FileLogger.Log($"[HellMonsterDropConfig] roll#{i + 1}: miss itemBasisLevel={itemBasisLevel} dungeonDifficulty={dungeonDifficulty} hellDifficulty={hellDifficulty} rate={rate} chance={chance} roll={chanceRoll}");
                    continue;
                }

                var initialRarity = RollHellRarity(lcg, hellDifficulty);
                var rarity = ApplyEpicBuffPotionRarityReroll(
                    initialRarity,
                    epicBuffPotionActive,
                    () => RollHellRarity(lcg, hellDifficulty),
                    out var rerollRarity);
                var itemId = ChooseEquipment(
                    lcg,
                    dungeonMinimumLevel,
                    itemBasisLevel,
                    rarity,
                    allowFallback: true,
                    predicate: null,
                    out var candidateCount,
                    out var fallbackUsed,
                    out var gradeMin,
                    out var gradeMax);
                if (itemId <= 0)
                {
                    FileLogger.Log($"[HellMonsterDropConfig] roll#{i + 1}: no equipment dungeonLevelRange={dungeonMinimumLevel}-{itemBasisLevel} gradeRange={gradeMin}-{gradeMax} rarity={rarity} epicBuff={FormatEpicBuffReroll(epicBuffPotionActive, initialRarity, rerollRarity)} candidates={candidateCount} fallback={fallbackUsed}");
                    continue;
                }

                var meta = ItemMetadataResolver.Resolve(itemId);
                slotCounter++;
                result.Add(DropInfo.CreateItem(slotCounter, itemId, 1));
                FileLogger.Log($"[HellMonsterDropConfig] roll#{i + 1}: hit dungeonLevelRange={dungeonMinimumLevel}-{itemBasisLevel} gradeRange={gradeMin}-{gradeMax} dungeonDifficulty={dungeonDifficulty} hellDifficulty={hellDifficulty} rate={rate} chance={chance} roll={chanceRoll} rarity={rarity} epicBuff={FormatEpicBuffReroll(epicBuffPotionActive, initialRarity, rerollRarity)} candidates={candidateCount} fallback={fallbackUsed} item={itemId} itemGrade={meta.Grade} itemMinLv={meta.MinimumLevel}");
            }

            return result;
        }

        public static void WarmUp()
        {
            EnsureLoaded();
        }

        internal static bool TryChooseSpecificEquipment(
            DnfLcg lcg,
            int dungeonMinimumLevel,
            int itemBasisLevel,
            int rarity,
            bool allowFallback,
            Func<int, bool> predicate,
            out int itemId,
            out int candidateCount,
            out bool fallbackUsed,
            out int gradeMin,
            out int gradeMax)
        {
            itemId = ChooseEquipment(
                lcg,
                dungeonMinimumLevel,
                itemBasisLevel,
                rarity,
                allowFallback,
                predicate,
                out candidateCount,
                out fallbackUsed,
                out gradeMin,
                out gradeMax);
            return itemId > 0;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    LoadDropInfo();
                    _equipPool = EquipmentDropPoolProvider.GetPool();
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[HellMonsterDropConfig] INIT FAILED: {ex}");
                }
                _loaded = true;
            }
        }

        private static void LoadDropInfo()
        {
            var text = ReadDropScript();
            if (string.IsNullOrWhiteSpace(text))
            {
                FileLogger.Log("[HellMonsterDropConfig] itemdropinfo_monster_hell.etc empty/not found");
                return;
            }

            ParseDropProb(text);
            ParseRarityThresholds(text);
            ParseGradeRanges(text);
        }

        private static string ReadDropScript()
        {
            try { return GameWorld.PvfArchiveAccessor.ReadText("etc/itemdropinfo_monster_hell.etc"); }
            catch
            {
                try { return GameWorld.PvfArchiveAccessor.ReadText("Etc/itemdropinfo_monster_hell.etc"); }
                catch { return string.Empty; }
            }
        }

        private static void ParseDropProb(string text)
        {
            var vals = ParseInts(ExtractSection(text, "dungeon difficulty drop prob"));
            if (vals.Count < 7)
                return;

            var rows = vals.Count / 7;
            _dropProbs = new DropProb[rows];
            for (var row = 0; row < rows; row++)
            {
                var baseIndex = row * 7;
                var rates = new int[5];
                for (var i = 0; i < rates.Length; i++)
                    rates[i] = vals[baseIndex + 2 + i];

                _dropProbs[row] = new DropProb
                {
                    LevelMin = vals[baseIndex],
                    LevelMax = vals[baseIndex + 1],
                    DifficultyRates = rates,
                };
            }
            FileLogger.Log($"[HellMonsterDropConfig] [dungeon difficulty drop prob]: rows={rows}");
        }

        private static void ParseRarityThresholds(string text)
        {
            var vals = ParseInts(ExtractSection(text, "basis of rarity dicision"));
            if (vals.Count < 2)
                return;

            var rowCount = Math.Max(0, vals[0]);
            if (rowCount <= 0)
                return;

            _rarityThresholds = new int[rowCount][];
            var index = 1;
            for (var row = 0; row < rowCount && index < vals.Count; row++)
            {
                var thresholds = new int[7];
                for (var col = 0; col < thresholds.Length && index < vals.Count; col++)
                    thresholds[col] = vals[index++];
                _rarityThresholds[row] = thresholds;
            }
            FileLogger.Log($"[HellMonsterDropConfig] [basis of rarity dicision]: rows={rowCount}");
        }

        private static void ParseGradeRanges(string text)
        {
            var vals = ParseInts(ExtractSection(text, "item drop ref table"));
            if (vals.Count < 3)
                return;

            _gradeRanges = new (int Down, int Up)[200];
            for (var i = 0; i + 2 < vals.Count; i += 3)
            {
                var level = vals[i];
                if (level >= 1 && level <= _gradeRanges.Length)
                    _gradeRanges[level - 1] = (vals[i + 1], vals[i + 2]);
            }
            FileLogger.Log($"[HellMonsterDropConfig] [item drop ref table]: rows={vals.Count / 3}");
        }

        private static DropProb FindDropProb(int level)
        {
            if (_dropProbs != null)
            {
                for (var i = 0; i < _dropProbs.Length; i++)
                {
                    var prob = _dropProbs[i];
                    if (level >= prob.LevelMin && level <= prob.LevelMax)
                        return prob;
                }
            }
            return default;
        }

        private static int RollHellRarity(DnfLcg lcg, byte hellDifficulty)
        {
            EnsureLoaded();
            if (_rarityThresholds == null || _rarityThresholds.Length == 0)
                return 0;

            // hellDifficulty: 1=A/非常困难，2=B/困难；稀有度表数组从 0 开始。
            var row = Math.Max(0, Math.Min((int)hellDifficulty - 1, _rarityThresholds.Length - 1));
            var thresholds = _rarityThresholds[row];
            if (thresholds == null || thresholds.Length == 0)
                return 0;

            var roll = lcg.Next(1000000) + 1;
            var limit = Math.Min(6, thresholds.Length);
            for (var rarity = 0; rarity < limit; rarity++)
            {
                if (roll <= thresholds[rarity])
                    return rarity;
            }
            return 0;
        }

        internal static int ApplyEpicBuffPotionRarityRerollForSelfTest(
            int initialRarity,
            bool epicBuffPotionActive,
            Func<int> rollRarity)
        {
            return ApplyEpicBuffPotionRarityReroll(
                initialRarity,
                epicBuffPotionActive,
                rollRarity,
                out _);
        }

        private static int ApplyEpicBuffPotionRarityReroll(
            int initialRarity,
            bool epicBuffPotionActive,
            Func<int> rollRarity,
            out int? rerollRarity)
        {
            rerollRarity = null;
            if (!epicBuffPotionActive
                || initialRarity == EpicRarity
                || rollRarity == null)
            {
                return initialRarity;
            }

            var secondRarity = rollRarity();
            rerollRarity = secondRarity;
            return secondRarity == EpicRarity
                ? EpicRarity
                : initialRarity;
        }

        private static string FormatEpicBuffReroll(
            bool epicBuffPotionActive,
            int initialRarity,
            int? rerollRarity)
        {
            if (!epicBuffPotionActive)
                return "off";
            if (!rerollRarity.HasValue)
                return initialRarity.ToString();
            return $"{initialRarity}->{rerollRarity.Value}";
        }

        private static int ChooseEquipment(
            DnfLcg lcg,
            int dungeonMinimumLevel,
            int itemBasisLevel,
            int rarity,
            bool allowFallback,
            Func<int, bool> predicate,
            out int candidateCount,
            out bool fallbackUsed,
            out int gradeMin,
            out int gradeMax)
        {
            candidateCount = 0;
            fallbackUsed = false;
            gradeMin = 0;
            gradeMax = 0;
            EnsureLoaded();
            if (_equipPool == null)
                return -1;

            if (!EquipmentDropLevelRule.TryGetAtlasGradeRange(dungeonMinimumLevel, itemBasisLevel, out gradeMin, out gradeMax))
                return -1;
            var candidates = CollectWeightedFromPool(
                gradeMin,
                gradeMax,
                rarity,
                predicate);
            if (candidates.Count == 0 && allowFallback && rarity > 0)
            {
                candidates = CollectWeightedFromPool(
                    gradeMin,
                    gradeMax,
                    0,
                    predicate);
                fallbackUsed = true;
            }
            candidateCount = candidates.Count;
            if (candidates.Count == 0)
                return -1;

            var totalWeight = 0;
            for (var i = 0; i < candidates.Count; i++)
                totalWeight += candidates[i].Weight;
            if (totalWeight <= 0)
                return -1;

            var roll = lcg.Next(totalWeight);
            var cumulative = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                cumulative += candidates[i].Weight;
                if (roll < cumulative)
                    return candidates[i].Id;
            }
            return candidates[candidates.Count - 1].Id;
        }

        private static bool TryGetGradeRangeFromItemDropRefTable(int itemBasisLevel, out int gradeMin, out int gradeMax)
        {
            gradeMin = 0;
            gradeMax = 0;
            if (_gradeRanges == null || itemBasisLevel < 1 || itemBasisLevel > _gradeRanges.Length)
                return false;

            var range = _gradeRanges[itemBasisLevel - 1];
            if (range.Down <= 0 && range.Up <= 0)
                return false;

            // [item drop ref table] 按副本掉落基准等级查表，返回可抽取装备 .equ [grade] 范围。
            // 表内 down/up 包含基准等级本身，例如 83 7 4 => 83-6 到 83+3，即 77..86。
            var downOffset = Math.Max(0, range.Down - 1);
            var upOffset = Math.Max(0, range.Up - 1);
            gradeMin = Math.Max(1, itemBasisLevel - downOffset);
            gradeMax = Math.Min(200, itemBasisLevel + upOffset);
            return gradeMax >= gradeMin;
        }

        private static List<(int Id, int Weight)> CollectWeightedFromPool(
            int gradeMin,
            int gradeMax,
            int rarity,
            Func<int, bool> predicate)
        {
            var result = new List<(int Id, int Weight)>();
            if (gradeMax < gradeMin)
                return result;

            for (var grade = gradeMin; grade <= gradeMax; grade++)
            {
                var key = (long)grade * 10 + rarity;
                if (_equipPool.TryGetValue(key, out var items))
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        if (predicate == null || predicate(item.Id))
                            result.Add(item);
                    }
                }
            }
            return result;
        }

        private static string ExtractSection(string text, string name)
        {
            var tag = "[" + name + "]";
            var start = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return null;

            start += tag.Length;
            var end = text.IndexOf("[", start, StringComparison.Ordinal);
            if (end < 0)
                end = text.Length;
            return text.Substring(start, end - start);
        }

        private static List<int> ParseInts(string section)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(section))
                return result;

            var tokens = section.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                if (int.TryParse(tokens[i], out var value))
                    result.Add(value);
            }
            return result;
        }
    }
}
