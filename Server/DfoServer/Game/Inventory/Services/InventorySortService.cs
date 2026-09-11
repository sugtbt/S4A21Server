using System;
using System.Collections.Generic;
using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Game.Inventory
{
    internal static class InventorySortCondition
    {
        public const byte Slot = 0;
        public const byte Rarity = 1;
        public const byte Level = 2;
        public const byte TypeOrPart = 3;
        public const byte ExpireTime = 4;
        public const byte Default = Slot;
    }

    internal static class InventorySortAlgorithm
    {
        public const byte Slot = 0;
        public const byte Rarity = 1;
        public const byte Level = 2;
        public const byte Part = 3;
        public const byte StackableType = 4;
        public const byte ExpireTime = 5;
    }

    internal readonly struct InventorySortKeys
    {
        public InventorySortKeys(
            int minimumLevel,
            int rarity,
            int partRank,
            string stackableType,
            int expireTime)
        {
            MinimumLevel = minimumLevel;
            Rarity = rarity;
            PartRank = partRank;
            StackableType = stackableType ?? string.Empty;
            ExpireTime = expireTime;
        }

        public int MinimumLevel { get; }

        public int Rarity { get; }

        public int PartRank { get; }

        public string StackableType { get; }

        public int ExpireTime { get; }
    }

    internal sealed class InventorySortServiceResult
    {
        public bool Success { get; set; }

        public bool Mutated { get; set; }

        public InventoryListType ListType { get; set; }

        public byte Category { get; set; }

        public byte Condition { get; set; }

        public byte Algorithm { get; set; }

        public int AffectedSlotCount { get; set; }

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal static class InventorySortService
    {
        internal static bool TrySort(
            InventoryService inventory,
            InventoryListType listType,
            byte category,
            out InventorySortServiceResult result)
            => TrySort(inventory, listType, category, InventorySortCondition.Default, out result);

        internal static bool TrySort(
            InventoryService inventory,
            InventoryListType listType,
            byte category,
            byte condition,
            out InventorySortServiceResult result)
        {
            var algorithm = ResolveSortAlgorithm(listType, category, condition);
            result = new InventorySortServiceResult
            {
                ListType = listType,
                Category = category,
                Condition = condition,
                Algorithm = algorithm,
            };

            if (inventory == null || !IsSupportedSortListType(listType))
                return false;

            if (!TryGetSortRange(inventory, listType, category, out var range))
                return SucceedNoOp(result);

            var lockedSlots = new HashSet<short>();
            var original = new Dictionary<short, ItemCore>();
            var movableItems = new List<SortableItem>();

            for (var slot = range.Start; slot <= range.End; slot++)
            {
                var item = inventory.GetItem(listType, slot);
                if (item == null)
                    continue;

                var snapshot = item.Copy();
                original[(short)slot] = snapshot;

                if (IsSortLocked(listType, (short)slot, snapshot))
                {
                    lockedSlots.Add((short)slot);
                    continue;
                }

                movableItems.Add(new SortableItem((short)slot, snapshot, GetSortKeys(snapshot)));
            }

            movableItems.Sort((left, right) => CompareSortableItems(left, right, algorithm));

            var targetSlots = new List<short>();
            for (var slot = range.Start; slot <= range.End; slot++)
            {
                if (!lockedSlots.Contains((short)slot))
                    targetSlots.Add((short)slot);
            }

            if (movableItems.Count > targetSlots.Count)
                return false;

            var assigned = new Dictionary<short, ItemCore>();
            for (var index = 0; index < movableItems.Count; index++)
                assigned[targetSlots[index]] = movableItems[index].Item.Copy();

            foreach (var slot in targetSlots)
            {
                assigned.TryGetValue(slot, out var next);
                original.TryGetValue(slot, out var previous);

                if (ItemsEqual(previous, next))
                    continue;

                if (next == null)
                {
                    if (previous != null && !inventory.RemoveItem(listType, slot))
                        return false;
                }
                else if (!inventory.SetItem(listType, slot, next))
                {
                    return false;
                }

                result.Changes.AddSlot(listType, slot);
            }

            var remainingMovable = 0;
            for (var i = 0; i < targetSlots.Count; i++)
            {
                if (inventory.GetItem(listType, targetSlots[i]) != null)
                    remainingMovable++;
            }

            if (remainingMovable != movableItems.Count)
                return false;

            result.Success = true;
            result.Mutated = result.Changes.HasChanges;
            result.AffectedSlotCount = result.Changes.Slots.Count;
            return true;
        }

        internal static bool TryGetSortRange(
            InventoryService inventory,
            InventoryListType listType,
            byte category,
            out ItemSlotRange range)
        {
            range = default;
            switch (listType)
            {
                case InventoryListType.Main:
                    return TryGetMainSortRange(inventory, category, out range);
                case InventoryListType.Avatar:
                    if (category != ItemCore.KindAvatar)
                        return false;
                    range = ItemSlotBoundService.GetAvatarOpenRange(inventory.GetListParam16(InventoryListType.Avatar));
                    return range.Count > 0;
                case InventoryListType.Pet:
                    return TryGetPetSortRange(category, out range);
                case InventoryListType.PersonalCargo:
                    if (category != 11)
                        return false;
                    range = ItemSlotBoundService.GetPersonalCargoOpenRange(inventory.GetListParam16(InventoryListType.PersonalCargo));
                    return range.Count > 0;
                case InventoryListType.AccountCargo:
                    range = ItemSlotBoundService.GetAccountCargoOpenRange(inventory.GetListParam16(InventoryListType.AccountCargo));
                    return range.Count > 0;
                case InventoryListType.GuildMedal:
                    return TryGetGuildMedalSortRange(category, out range);
                default:
                    return false;
            }
        }

        private static bool TryGetMainSortRange(InventoryService inventory, byte category, out ItemSlotRange range)
        {
            range = default;
            byte itemKind;
            switch (category)
            {
                case ItemCore.KindEquipment:
                case ItemCore.KindConsumable:
                case ItemCore.KindMaterial:
                case ItemCore.KindQuest:
                case ItemCore.KindAvatarEmblem:
                case ItemCore.KindExpertJobMaterial:
                    itemKind = category;
                    break;
                default:
                    return false;
            }

            return ItemSlotBoundService.TryGetSlotRange(
                    itemKind,
                    inventory.GetListParam16(InventoryListType.Main),
                    out var listType,
                    out range)
                && listType == InventoryListType.Main
                && range.Count > 0;
        }

        private static bool TryGetPetSortRange(byte category, out ItemSlotRange range)
        {
            range = default;
            byte itemKind;
            switch (category)
            {
                case ItemCore.KindCreature:
                case ItemCore.KindCreatureEquipment:
                case ItemCore.KindCreatureConsumable:
                    itemKind = category;
                    break;
                default:
                    return false;
            }

            return ItemSlotBoundService.TryGetSlotRange(
                    itemKind,
                    ItemSlotBoundService.MainExpandStageFull,
                    out var listType,
                    out range)
                && listType == InventoryListType.Pet
                && range.Count > 0;
        }

        private static bool TryGetGuildMedalSortRange(byte category, out ItemSlotRange range)
        {
            range = default;
            byte itemKind;
            switch (category)
            {
                case ItemCore.KindGuildMedal:
                case ItemCore.KindGuardianGem:
                    itemKind = category;
                    break;
                default:
                    return false;
            }

            return ItemSlotBoundService.TryGetSlotRange(
                    itemKind,
                    ItemSlotBoundService.MainExpandStageFull,
                    out var listType,
                    out range)
                && listType == InventoryListType.GuildMedal
                && range.Count > 0;
        }

        internal static bool IsSortLocked(InventoryListType listType, short slotIndex, ItemCore item)
        {
            return item != null
                && item.SortLockFlag == 1
                && InventoryLockService.CanApplySortItemLock(listType, slotIndex);
        }

        private static int CompareSortableItems(
            SortableItem left,
            SortableItem right,
            byte condition)
        {
            return CompareItems(
                left.Item,
                left.OriginalSlot,
                left.Keys,
                right.Item,
                right.OriginalSlot,
                right.Keys,
                condition);
        }

        internal static byte ResolveSortAlgorithm(
            InventoryListType listType,
            byte category,
            byte condition)
        {
            if (!IsConditionAllowed(listType, category, condition))
                return DefaultAlgorithm(listType, category);

            if (listType == InventoryListType.Avatar)
            {
                if (condition == InventorySortCondition.TypeOrPart)
                    return InventorySortAlgorithm.Part;
                return InventorySortAlgorithm.Rarity;
            }

            if (listType == InventoryListType.Main)
            {
                switch (category)
                {
                    case ItemCore.KindEquipment:
                        switch (condition)
                        {
                            case InventorySortCondition.Rarity:
                                return InventorySortAlgorithm.Rarity;
                            case InventorySortCondition.Level:
                                return InventorySortAlgorithm.Level;
                            case InventorySortCondition.TypeOrPart:
                                return InventorySortAlgorithm.Part;
                            default:
                                return InventorySortAlgorithm.Slot;
                        }
                    case ItemCore.KindConsumable:
                        if (condition == InventorySortCondition.TypeOrPart)
                            return InventorySortAlgorithm.StackableType;
                        if (condition == InventorySortCondition.ExpireTime)
                            return InventorySortAlgorithm.ExpireTime;
                        return InventorySortAlgorithm.Slot;
                    case ItemCore.KindMaterial:
                    case ItemCore.KindExpertJobMaterial:
                        if (condition == InventorySortCondition.TypeOrPart)
                            return InventorySortAlgorithm.StackableType;
                        return InventorySortAlgorithm.Slot;
                }
            }

            return InventorySortAlgorithm.Slot;
        }

        internal static bool IsConditionAllowed(
            InventoryListType listType,
            byte category,
            byte condition)
        {
            if (listType == InventoryListType.Main)
            {
                switch (category)
                {
                    case ItemCore.KindEquipment:
                        return condition == InventorySortCondition.Slot
                            || condition == InventorySortCondition.Rarity
                            || condition == InventorySortCondition.Level
                            || condition == InventorySortCondition.TypeOrPart;
                    case ItemCore.KindConsumable:
                        return condition == InventorySortCondition.Slot
                            || condition == InventorySortCondition.TypeOrPart
                            || condition == InventorySortCondition.ExpireTime;
                    case ItemCore.KindMaterial:
                    case ItemCore.KindExpertJobMaterial:
                        return condition == InventorySortCondition.Slot
                            || condition == InventorySortCondition.TypeOrPart;
                    default:
                        return condition == InventorySortCondition.Slot;
                }
            }

            if (listType == InventoryListType.Avatar && category == ItemCore.KindAvatar)
            {
                return condition == InventorySortCondition.Slot
                    || condition == InventorySortCondition.Rarity
                    || condition == InventorySortCondition.TypeOrPart;
            }

            if (listType == InventoryListType.Pet)
            {
                return (category == ItemCore.KindCreatureEquipment
                        || category == ItemCore.KindCreatureConsumable)
                    && condition == InventorySortCondition.Slot;
            }

            return condition == InventorySortCondition.Slot;
        }

        internal static InventorySortKeys GetSortKeys(ItemCore item)
        {
            if (item == null)
                return default;

            var metadata = ItemMetadataResolver.Resolve(item.ItemId);
            var part = EquipmentTypeInfo.ParseOrUnknown(metadata?.EquipmentType);
            var partRank = part == EquipmentType.Unknown ? int.MaxValue : (int)part;
            var stackableType = (metadata?.StackableType ?? string.Empty)
                .Replace("`", string.Empty)
                .Trim();
            return new InventorySortKeys(
                metadata?.MinimumLevel ?? 0,
                ToRaritySortRank(metadata?.Rarity ?? 0),
                partRank,
                stackableType,
                item.ExpireTime);
        }

        internal static int CompareItems(
            ItemCore left,
            short leftOriginalSlot,
            ItemCore right,
            short rightOriginalSlot)
        {
            return CompareItems(
                left,
                leftOriginalSlot,
                GetSortKeys(left),
                right,
                rightOriginalSlot,
                GetSortKeys(right),
                InventorySortAlgorithm.Slot);
        }

        internal static int CompareItems(
            ItemCore left,
            short leftOriginalSlot,
            InventorySortKeys leftKeys,
            ItemCore right,
            short rightOriginalSlot,
            InventorySortKeys rightKeys,
            byte algorithm)
        {
            if (left == null || right == null)
            {
                if (left == null && right == null)
                    return leftOriginalSlot.CompareTo(rightOriginalSlot);
                return left == null ? 1 : -1;
            }

            int result;
            switch (algorithm)
            {
                case InventorySortAlgorithm.Rarity:
                    result = rightKeys.Rarity.CompareTo(leftKeys.Rarity);
                    if (result != 0)
                        return result;
                    break;
                case InventorySortAlgorithm.Level:
                    result = rightKeys.MinimumLevel.CompareTo(leftKeys.MinimumLevel);
                    if (result != 0)
                        return result;
                    break;
                case InventorySortAlgorithm.Part:
                    result = leftKeys.PartRank.CompareTo(rightKeys.PartRank);
                    if (result != 0)
                        return result;
                    break;
                case InventorySortAlgorithm.StackableType:
                    result = string.Compare(
                        leftKeys.StackableType ?? string.Empty,
                        rightKeys.StackableType ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase);
                    if (result != 0)
                        return result;
                    break;
                case InventorySortAlgorithm.ExpireTime:
                    result = NormalizeExpireSortKey(leftKeys.ExpireTime)
                        .CompareTo(NormalizeExpireSortKey(rightKeys.ExpireTime));
                    if (result != 0)
                        return result;
                    break;
            }

            result = left.ItemKind.CompareTo(right.ItemKind);
            if (result != 0)
                return result;

            result = left.ItemId.CompareTo(right.ItemId);
            if (result != 0)
                return result;

            return leftOriginalSlot.CompareTo(rightOriginalSlot);
        }

        private static byte DefaultAlgorithm(InventoryListType listType, byte category)
        {
            if (listType == InventoryListType.Avatar && category == ItemCore.KindAvatar)
                return InventorySortAlgorithm.Rarity;
            return InventorySortAlgorithm.Slot;
        }

        private static int NormalizeExpireSortKey(int expireTime)
            => expireTime > 0 ? expireTime : int.MaxValue;

        // A21 PVF: 0 common, 1 uncommon, 2 rare, 3 unique, 4 epic, 5 chronicle, 6 legendary.
        // Player 按品级 order is epic > legendary > unique > chronicle > rare.
        internal static int ToRaritySortRank(int rarity)
        {
            switch (rarity)
            {
                case 4:
                    return 6;
                case 6:
                    return 5;
                case 3:
                    return 4;
                case 5:
                    return 3;
                default:
                    return rarity < 0 ? 0 : rarity;
            }
        }

        private static bool ItemsEqual(ItemCore left, ItemCore right)
        {
            if (left == null || right == null)
                return left == null && right == null;

            var leftBytes = left.ToBytes();
            var rightBytes = right.ToBytes();
            if (leftBytes.Length != rightBytes.Length)
                return false;

            for (var index = 0; index < leftBytes.Length; index++)
            {
                if (leftBytes[index] != rightBytes[index])
                    return false;
            }

            return true;
        }

        private static bool IsSupportedSortListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet
                || listType == InventoryListType.AccountCargo
                || listType == InventoryListType.GuildMedal;
        }

        private static bool SucceedNoOp(InventorySortServiceResult result)
        {
            result.Success = true;
            result.Mutated = false;
            return true;
        }

        private readonly struct SortableItem
        {
            public SortableItem(short originalSlot, ItemCore item, InventorySortKeys keys)
            {
                OriginalSlot = originalSlot;
                Item = item;
                Keys = keys;
            }

            public short OriginalSlot { get; }

            public ItemCore Item { get; }

            public InventorySortKeys Keys { get; }
        }
    }
}
