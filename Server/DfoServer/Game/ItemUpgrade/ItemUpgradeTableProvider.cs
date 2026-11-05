using System;
using DfoServer.Infrastructure;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.ItemUpgrade
{
    public static class ItemUpgradeTableProvider
    {
        private static readonly Lazy<UpgradeTableFile> ReinforcementTableFile =
            new Lazy<UpgradeTableFile>(() => UpgradeTableFile.Parse(PvfArchiveAccessor.ReadText("etc/upgrade.etc")));

        private static readonly Lazy<UpgradeTableFile> AmplifyTableFile =
            new Lazy<UpgradeTableFile>(() => UpgradeTableFile.Parse(PvfArchiveAccessor.ReadText("etc/amplifyupgrade.etc")));

        private static readonly Lazy<UpgradeTableFile> AdvancedReinforcementTableFile =
            new Lazy<UpgradeTableFile>(() => UpgradeTableFile.Parse(PvfArchiveAccessor.ReadText("etc/chn_high_ranking_upgrade.etc")));

        private static readonly Lazy<AmplifyItemFile> AmplifyItemConfig =
            new Lazy<AmplifyItemFile>(() => AmplifyItemFile.Parse(PvfArchiveAccessor.ReadText("etc/amplifyitem.etc")));

        public static UpgradeTableFile GetFile(ItemUpgradeTableKind kind)
        {
            switch (kind)
            {
                case ItemUpgradeTableKind.Amplify:
                    return AmplifyTableFile.Value;
                case ItemUpgradeTableKind.Advanced:
                    return AdvancedReinforcementTableFile.Value;
                default:
                    return ReinforcementTableFile.Value;
            }
        }

        public static UpgradeTableDefinition GetTable(ItemUpgradeTableKind kind, string tableType = null)
        {
            var file = GetFile(kind);
            var resolvedTableType = tableType;
            if (string.IsNullOrWhiteSpace(resolvedTableType))
            {
                resolvedTableType = kind == ItemUpgradeTableKind.Amplify
                    ? "amplify"
                    : kind == ItemUpgradeTableKind.Advanced
                        ? "chn_high_ranking"
                        : "normal";
            }

            return file.GetTable(resolvedTableType);
        }

        public static bool TryGetRow(ItemUpgradeTableKind kind, int targetLevel, out UpgradeTableRow row, string tableType = null)
        {
            row = null;
            var table = GetTable(kind, tableType);
            if (table == null)
                return false;

            row = table.GetRowByTargetLevel(targetLevel);
            return row != null;
        }

        public static ItemUpgradeCost BuildCost(ItemUpgradeTableKind kind, UpgradeTableRow row, EquipmentUpgradeCostInput input)
        {
            if (row == null || input == null)
                return new ItemUpgradeCost();

            var file = GetFile(kind);
            var materialCount = row.MaterialCount;
            if (input.EquipmentType == EquipmentType.GuildMedal)
            {
                if (!string.Equals(row.TableType, "flag_upgrade_table", StringComparison.Ordinal)
                    || input.Rarity < 0 || input.Rarity >= file.CostWeightsByGuildFlagRarity.Count)
                    throw new InvalidOperationException("PVF guild-medal upgrade cost is incomplete.");
                // 029CAD38-029CAE79: multiply the material count by the rarity
                // weight using float, then truncate toward zero (x87 RC=truncate).
                var weight = (float)file.CostWeightsByGuildFlagRarity[input.Rarity];
                var count = row.MaterialCount * (double)weight;
                if (!float.IsFinite(weight) || weight <= 0 || count < 1 || count > int.MaxValue)
                    throw new InvalidOperationException("PVF guild-medal upgrade cost is invalid.");
                materialCount = (int)count;
            }
            return new ItemUpgradeCost
            {
                MaterialItemId = row.MaterialItemId,
                MaterialCount = materialCount,
                Gold = CalculateGoldCost(file, input),
            };
        }

        public static int GetPenaltyType(ItemUpgradeTableKind kind, UpgradeTableRow row, int currentUpgradeLevel, int rarity)
        {
            if (row == null)
                return 0;

            var destroyLevel = GetDestroyLevel(GetFile(kind), rarity);
            if (destroyLevel < 0 || currentUpgradeLevel < destroyLevel)
                return row.PenaltyType;

            return 3;
        }

        public static ItemUpgradeDestroyBonus CalculateDestroyBonus(
            ItemUpgradeTableKind kind,
            int currentUpgradeLevel,
            int equipmentGrade,
            int rarity)
        {
            return CalculateDestroyBonus(GetFile(kind), currentUpgradeLevel, equipmentGrade, rarity);
        }

        public static ItemUpgradeDestroyBonus[] CalculateDestroyBonuses(
            ItemUpgradeTableKind kind,
            int currentUpgradeLevel,
            int equipmentGrade,
            int rarity)
        {
            var primary = CalculateDestroyBonus(kind, currentUpgradeLevel, equipmentGrade, rarity);
            if (kind != ItemUpgradeTableKind.Amplify)
                return primary.HasValue ? new[] { primary } : Array.Empty<ItemUpgradeDestroyBonus>();

            var normal = CalculateDestroyBonus(ItemUpgradeTableKind.Normal, currentUpgradeLevel, equipmentGrade, rarity);
            if (primary.HasValue && normal.HasValue)
                return new[] { primary, normal };
            if (primary.HasValue)
                return new[] { primary };
            if (normal.HasValue)
                return new[] { normal };

            return Array.Empty<ItemUpgradeDestroyBonus>();
        }

        public static bool IsNoticeLevel(ItemUpgradeTableKind kind, int upgradeLevel)
        {
            var noticeLevel = GetFile(kind)?.NoticeLevel ?? -1;
            return noticeLevel >= 0 && upgradeLevel >= noticeLevel;
        }

        public static ItemUpgradeDestroyBonus CalculateDestroyBonus(
            UpgradeTableFile file,
            int currentUpgradeLevel,
            int equipmentGrade,
            int rarity)
        {
            if (file == null || currentUpgradeLevel <= 0)
                return ItemUpgradeDestroyBonus.Empty;

            var disjoint = file.Disjoint;
            if (disjoint == null || disjoint.DisjointBonusItemId <= 0)
                return ItemUpgradeDestroyBonus.Empty;

            var rarityName = GetRarityName(rarity);
            var correctionGrade = GetNamedInt(disjoint.CorrectionGradeByRarity, rarityName, 0);
            var correctedGrade = Math.Max(0, equipmentGrade - correctionGrade);
            if (disjoint.EquipLevelConst > 0 && correctedGrade < disjoint.EquipLevelConst)
                return ItemUpgradeDestroyBonus.Empty;

            var levelBase = disjoint.UpgradeConstForBonusItemCount;
            if (currentUpgradeLevel <= levelBase)
                return ItemUpgradeDestroyBonus.Empty;

            var rarityWeight = GetNamedDouble(disjoint.UpgradeFailedBonusWeightByRarity, rarityName, 0);
            if (rarityWeight <= 0)
                return ItemUpgradeDestroyBonus.Empty;

            var count = (int)(Math.Pow(currentUpgradeLevel - levelBase, 2) * rarityWeight);
            if (count <= 0)
                return ItemUpgradeDestroyBonus.Empty;

            return new ItemUpgradeDestroyBonus(disjoint.DisjointBonusItemId, count);
        }

        public static int CalculateGoldCost(UpgradeTableFile file, EquipmentUpgradeCostInput input)
        {
            if (file == null || input == null)
                return 0;

            var costLevelIndex = ResolveCostLevelIndex(input.EquipmentLevel, input.Rarity);
            var baseCost = GetIndexedInt(file.Costs, costLevelIndex, 0);
            if (baseCost <= 0)
                return 0;

            var rarityWeight = GetIndexedDouble(file.CostWeightsByRarity, input.Rarity, 1);
            var typeWeight = GetEquipmentTypeWeight(file, input.EquipmentType);
            var upgradeLevelWeight = GetUpgradeLevelWeight(file, input.CurrentUpgradeLevel);

            var cost = baseCost * rarityWeight * typeWeight * upgradeLevelWeight;
            if (cost <= 0)
                return 0;

            return Math.Max(0, Convert.ToInt32(Math.Round(cost, MidpointRounding.AwayFromZero)));
        }

        public static int CalculateInitialAmplifyValue(int rarity, double baseValue = 0)
        {
            if (baseValue <= 0)
                baseValue = AmplifyItemConfig.Value.GetBaseValue(AmplifyOptionType.PhysicalAttack);

            var rarityWeight = GetAmplifyRarityWeight(rarity);
            return Math.Max(0, (int)(rarityWeight * baseValue));
        }

        public static int CalculateInitialAmplifyValue(int rarity, AmplifyOptionType optionType)
        {
            var baseValue = AmplifyItemConfig.Value.GetBaseValue(optionType);
            return CalculateInitialAmplifyValue(rarity, baseValue);
        }

        public static int GetAmplificationRateByRarity(int rarity)
        {
            return GetIndexedInt(AmplifyItemConfig.Value.AmplificationRatesByRarity, rarity, 0);
        }

        public static bool TryRollInitialAmplifyOption(out AmplifyOptionType optionType)
        {
            optionType = AmplifyOptionType.None;
            var options = AmplifyItemConfig.Value.OptionData;
            if (options == null || options.Count == 0)
                return false;

            var totalWeight = 0d;
            foreach (var option in options)
            {
                if (option != null && option.CumulativeWeight > totalWeight)
                    totalWeight = option.CumulativeWeight;
            }

            if (totalWeight <= 0)
                return false;

            var roll = ServerRandom.Next(1_000_000) * totalWeight / 1_000_000d;
            foreach (var option in options)
            {
                if (option == null)
                    continue;

                if (roll < option.CumulativeWeight)
                {
                    optionType = option.OptionType;
                    return optionType != AmplifyOptionType.None;
                }
            }

            return false;
        }

        public static int GetAmplifyEquipLevelConst()
        {
            return Math.Max(0, AmplifyItemConfig.Value.EquipLevelConst);
        }

        public static bool IsPurifyMaterial(int itemId)
        {
            return HasConfiguredItem(AmplifyItemConfig.Value.PurifyMaterials, itemId);
        }

        public static bool TryGetPurifyMaterialCount(int itemId, out int count)
        {
            return TryGetConfiguredItemCount(AmplifyItemConfig.Value.PurifyMaterials, itemId, out count);
        }

        public static bool IsOutworldVigorClearMaterial(int itemId)
        {
            var config = AmplifyItemConfig.Value;
            return HasConfiguredItem(config.PurifyOnlyMaterials, itemId)
                || HasConfiguredItem(config.PurifyOnlyCeraMaterials, itemId);
        }

        public static bool TryGetOutworldVigorClearMaterialCount(int itemId, out int count)
        {
            var config = AmplifyItemConfig.Value;
            return TryGetConfiguredItemCount(config.PurifyOnlyMaterials, itemId, out count)
                || TryGetConfiguredItemCount(config.PurifyOnlyCeraMaterials, itemId, out count);
        }

        public static bool IsInvestAmplifyOptionMaterial(int itemId)
        {
            return HasConfiguredOption(AmplifyItemConfig.Value.InvestOptions, itemId);
        }

        public static bool TryGetInvestAmplifyOption(int itemId, out AmplifyOptionType optionType, out int count)
        {
            return TryGetConfiguredOption(AmplifyItemConfig.Value.InvestOptions, itemId, out optionType, out count);
        }

        public static bool TryGetInvestAmplifyOptionType(int itemId, out AmplifyOptionType optionType)
        {
            return TryGetConfiguredOptionType(AmplifyItemConfig.Value.InvestOptions, itemId, out optionType);
        }

        public static bool IsReinvestAmplifyOptionMaterial(int itemId)
        {
            return HasConfiguredOption(AmplifyItemConfig.Value.ReinvestOptions, itemId);
        }

        public static bool TryGetReinvestAmplifyOption(int itemId, out AmplifyOptionType optionType, out int count)
        {
            return TryGetConfiguredOption(AmplifyItemConfig.Value.ReinvestOptions, itemId, out optionType, out count);
        }

        public static bool IsRandomInvestUpgradeOptionMaterial(int itemId)
        {
            return HasConfiguredOption(AmplifyItemConfig.Value.RandomInvestUpgradeOptions, itemId);
        }

        public static bool TryGetRandomInvestUpgradeOption(int itemId, out AmplifyOptionType optionType, out int count)
        {
            return TryGetConfiguredOption(AmplifyItemConfig.Value.RandomInvestUpgradeOptions, itemId, out optionType, out count);
        }

        public static int CalculateAmplifyConstValue(int currentUpgradeLevel)
        {
            var file = GetFile(ItemUpgradeTableKind.Amplify);
            if (file.AmplificationConsts == null || currentUpgradeLevel < 0 || currentUpgradeLevel >= file.AmplificationConsts.Count)
                return 0;

            var row = file.AmplificationConsts[currentUpgradeLevel];
            return row != null && row.Length > 0 ? Math.Max(0, row[0]) : 0;
        }

        public static int CalculateAmplifyBonusConst(int currentUpgradeLevel)
        {
            return CalculateAmplifyConstValue(currentUpgradeLevel);
        }

        private static double GetEquipmentTypeWeight(UpgradeTableFile file, EquipmentType equipmentType)
        {
            if (file.TypeWeights == null || file.TypeWeights.Count == 0)
                return 1;

            // PVF [type] 从装备枚举10开始，对应武器到魔法石这一段。
            var index = (int)equipmentType - (int)EquipmentType.Weapon;
            return GetIndexedDouble(file.TypeWeights, index, 1);
        }

        private static int ResolveCostLevelIndex(int equipmentLevel, int rarity)
        {
            // PVF 金币表按品级对装备等级做补正: uncommon 以上每提高一档品级，金币表索引后移2级。
            return Math.Max(0, equipmentLevel) + Math.Max(0, rarity - 1) * 2;
        }

        private static double GetUpgradeLevelWeight(UpgradeTableFile file, int currentUpgradeLevel)
        {
            if (file.CostWeightByUpgradeLevel != null && file.CostWeightByUpgradeLevel.TryGetValue(currentUpgradeLevel, out var weight))
                return weight;

            return 1;
        }

        private static int GetDestroyLevel(UpgradeTableFile file, int rarity)
        {
            if (file.DestroyLevelByRarity == null)
                return -1;

            return GetNamedInt(file.DestroyLevelByRarity, GetRarityName(rarity), -1);
        }

        private static double GetAmplifyRarityWeight(int rarity)
        {
            var rarityName = GetRarityName(rarity);
            return GetNamedDouble(AmplifyItemConfig.Value.RarityWeights, rarityName, 1);
        }

        private static bool HasConfiguredItem(System.Collections.Generic.Dictionary<int, int> values, int itemId)
        {
            return values != null && values.TryGetValue(itemId, out var count) && count > 0;
        }

        private static bool TryGetConfiguredItemCount(System.Collections.Generic.Dictionary<int, int> values, int itemId, out int count)
        {
            count = 0;
            if (values == null || !values.TryGetValue(itemId, out count) || count <= 0)
            {
                count = 0;
                return false;
            }

            return true;
        }

        private static bool HasConfiguredOption(System.Collections.Generic.List<AmplifyMaterialOption> values, int itemId)
        {
            if (values == null)
                return false;

            foreach (var value in values)
            {
                if (value != null && value.ItemId == itemId && value.Count > 0)
                    return true;
            }

            return false;
        }

        private static bool TryGetConfiguredOptionType(System.Collections.Generic.List<AmplifyMaterialOption> values, int itemId, out AmplifyOptionType optionType)
        {
            optionType = AmplifyOptionType.None;
            return TryGetConfiguredOption(values, itemId, out optionType, out _);
        }

        private static bool TryGetConfiguredOption(System.Collections.Generic.List<AmplifyMaterialOption> values, int itemId, out AmplifyOptionType optionType, out int count)
        {
            optionType = AmplifyOptionType.None;
            count = 0;
            if (values == null)
                return false;

            foreach (var value in values)
            {
                if (value != null && value.ItemId == itemId && value.Count > 0)
                {
                    optionType = value.OptionType;
                    count = value.Count;
                    return true;
                }
            }

            return false;
        }

        private static string GetRarityName(int rarity)
        {
            switch (rarity)
            {
                case 0:
                    return "common";
                case 1:
                    return "uncommon";
                case 2:
                    return "rare";
                case 3:
                    return "unique";
                case 4:
                    return "epic";
                case 5:
                    return "chronicle";
                case 6:
                    return "legendary";
                default:
                    return string.Empty;
            }
        }

        private static int GetNamedInt(System.Collections.Generic.Dictionary<string, int> values, string key, int fallback)
        {
            return values != null && key != null && values.TryGetValue(key, out var value) ? value : fallback;
        }

        private static double GetNamedDouble(System.Collections.Generic.Dictionary<string, double> values, string key, double fallback)
        {
            return values != null && key != null && values.TryGetValue(key, out var value) ? value : fallback;
        }

        private static int GetIndexedInt(System.Collections.Generic.List<int> values, int index, int fallback)
        {
            return values != null && index >= 0 && index < values.Count ? values[index] : fallback;
        }

        private static double GetIndexedDouble(System.Collections.Generic.List<double> values, int index, double fallback)
        {
            return values != null && index >= 0 && index < values.Count ? values[index] : fallback;
        }
    }

    public sealed class EquipmentUpgradeCostInput
    {
        public int EquipmentLevel { get; set; }
        public int Rarity { get; set; }
        public EquipmentType EquipmentType { get; set; } = EquipmentType.Unknown;
        public int CurrentUpgradeLevel { get; set; }
    }

    public readonly struct ItemUpgradeDestroyBonus
    {
        public static readonly ItemUpgradeDestroyBonus Empty = new ItemUpgradeDestroyBonus(0, 0);

        public ItemUpgradeDestroyBonus(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }
        public int Count { get; }
        public bool HasValue => ItemId > 0 && Count > 0;
    }
}
