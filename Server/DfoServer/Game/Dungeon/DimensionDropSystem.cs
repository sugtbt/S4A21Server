using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public static class DimensionDropSystem
    {
        internal const int FragmentItemId = 3311;

        private static readonly HashSet<int> EliteMonsterCodes =
            new HashSet<int>
            {
                61340,
                56108,
                62940,
                56507,
                107000106,
                61412,
                61347,
                107000107,
                56506,
                61353,
                56140,
                56715,
                56146,
                59009,
                56721,
                56448,
                61238,
                56151,
                56453,
                59388,
                61391,
            };
        private static readonly HashSet<int> BossMonsterCodes =
            new HashSet<int>
            {
                61809,
                61128,
                61135,
                62141,
            };

        internal static void WarmUp()
        {
            DimensionGateDropDefinitionCatalog.WarmUp();
        }

        public static List<DropInfo> GenerateMonsterDrops(
            int dungeonId,
            int monsterCode,
            int characterJob,
            int growType,
            DnfLcg lcg,
            ref ushort slotCounter)
        {
            if (!DfoServer.GameWorld.Dungeon.IsDimensionDungeon(dungeonId))
                return new List<DropInfo>();

            switch (ResolveMonsterKind(monsterCode))
            {
                case DimensionMonsterKind.Elite:
                    return GenerateEliteDrops(
                        characterJob,
                        growType,
                        lcg,
                        ref slotCounter);
                case DimensionMonsterKind.Boss:
                    return GenerateBossDrops(
                        characterJob,
                        growType,
                        lcg,
                        ref slotCounter);
                default:
                    return new List<DropInfo>();
            }
        }

        internal static bool TryCreateFreeCard(
            int characterJob,
            int growType,
            DnfLcg lcg,
            out ClearRewardGenerator.CardReward reward)
        {
            reward = default;
            return TrySelectItem(
                    characterJob,
                    growType,
                    DimensionChroniclePoolKind.Normal,
                    lcg,
                    out var itemId)
                && TryCreateEquipmentCard(itemId, out reward);
        }

        internal static bool TryCreatePaidCard(
            int characterJob,
            int growType,
            DnfLcg lcg,
            out ClearRewardGenerator.CardReward reward)
        {
            reward = default;
            return TrySelectItem(
                    characterJob,
                    growType,
                    DimensionChroniclePoolKind.Set,
                    lcg,
                    out var itemId)
                && TryCreateEquipmentCard(itemId, out reward);
        }

        internal static List<DropInfo> GenerateEliteDrops(
            int characterJob,
            int growType,
            DnfLcg lcg,
            ref ushort slotCounter)
        {
            var result = new List<DropInfo>();
            if (!TrySelectItem(
                characterJob,
                growType,
                DimensionChroniclePoolKind.Combined,
                lcg,
                out var itemId))
            {
                return result;
            }

            AddDrop(result, itemId, 1, ref slotCounter);
            AddDrop(result, FragmentItemId, 1, ref slotCounter);
            return result;
        }

        internal static List<DropInfo> GenerateBossDrops(
            int characterJob,
            int growType,
            DnfLcg lcg,
            ref ushort slotCounter)
        {
            var result = new List<DropInfo>();
            if (!TrySelectItem(
                characterJob,
                growType,
                DimensionChroniclePoolKind.Normal,
                lcg,
                out var normalItemId)
                || !TrySelectItem(
                    characterJob,
                    growType,
                    DimensionChroniclePoolKind.Set,
                    lcg,
                    out var setItemId))
            {
                return result;
            }

            AddDrop(result, normalItemId, 1, ref slotCounter);
            AddDrop(result, setItemId, 1, ref slotCounter);
            AddDrop(result, FragmentItemId, 1, ref slotCounter);
            AddDrop(result, FragmentItemId, 1, ref slotCounter);
            return result;
        }

        internal static DimensionMonsterKind ResolveMonsterKind(
            int monsterCode)
        {
            if (monsterCode <= 0)
                return DimensionMonsterKind.None;
            if (BossMonsterCodes.Contains(monsterCode))
                return DimensionMonsterKind.Boss;
            if (EliteMonsterCodes.Contains(monsterCode))
                return DimensionMonsterKind.Elite;
            return DimensionMonsterKind.None;
        }

        private static bool TrySelectItem(
            int characterJob,
            int growType,
            DimensionChroniclePoolKind poolKind,
            DnfLcg lcg,
            out int itemId)
        {
            itemId = 0;
            if (lcg == null
                || !DimensionGateDropDefinitionCatalog.TryResolve(
                    characterJob,
                    growType,
                    out var definition))
            {
                return false;
            }

            IReadOnlyList<int> pool;
            switch (poolKind)
            {
                case DimensionChroniclePoolKind.Normal:
                    pool = definition.NormalItems;
                    break;
                case DimensionChroniclePoolKind.Set:
                    pool = definition.SetItems;
                    break;
                case DimensionChroniclePoolKind.Combined:
                    pool = definition.CombinedItems;
                    break;
                default:
                    pool = Array.Empty<int>();
                    break;
            }

            if (pool.Count == 0)
                return false;

            itemId = pool[lcg.Next(pool.Count)];
            return itemId > 0;
        }

        private static bool TryCreateEquipmentCard(
            int itemId,
            out ClearRewardGenerator.CardReward reward)
        {
            reward = default;
            if (itemId <= 0)
                return false;

            ushort durability = 0;
            try
            {
                var metadata = ItemMetadataResolver.Resolve(itemId);
                durability = metadata != null ? metadata.Durability : (ushort)0;
            }
            catch
            {
                return false;
            }

            reward = new ClearRewardGenerator.CardReward
            {
                IsGold = false,
                ItemId = itemId,
                StackCount = 1,
                IsEquipment = true,
                Durability = durability,
            };
            return true;
        }

        private static void AddDrop(
            List<DropInfo> drops,
            int itemId,
            int count,
            ref ushort slotCounter)
        {
            if (itemId <= 0 || count <= 0)
                return;

            slotCounter++;
            drops.Add(DropInfo.CreateItem(slotCounter, itemId, count));
        }
    }

    internal enum DimensionMonsterKind
    {
        None = 0,
        Elite = 1,
        Boss = 2,
    }

    internal enum DimensionChroniclePoolKind
    {
        Normal = 0,
        Set = 1,
        Combined = 2,
    }
}
