using System;
using DfoServer.Game.Inventory;

namespace DfoServer.SelfTests
{
    public static class InventorySortConditionSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== INVENTORY_SORT_CONDITION selftest ===");
            var failures = 0;

            VerifySlotSortsByKindThenId(ref failures);
            VerifyExpireTimeSortsSoonestFirst(ref failures);
            VerifyRarityLevelPartAndTypeAlgorithms(ref failures);
            VerifyEpicRanksAboveLegendaryAndUnique(ref failures);
            VerifyPerTabConditionAllowList(ref failures);
            VerifyQuestCargoPetAndMedalHaveNoSortType(ref failures);
            VerifyAvatarDefaultsToRarity(ref failures);
            VerifyUnsupportedConditionFallsBack(ref failures);
            VerifySortPreservesItemsAcrossGaps(ref failures);

            Console.WriteLine(failures == 0
                ? "INVENTORY_SORT_CONDITION selftest passed"
                : $"INVENTORY_SORT_CONDITION selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifySlotSortsByKindThenId(ref int failures)
        {
            var inventory = CreateMainTab(
                Eq(9, 300),
                Eq(10, 100),
                Eq(11, 200));

            Check(
                "equipment 按栏位 sorts by item id",
                InventorySortService.TrySort(
                    inventory,
                    InventoryListType.Main,
                    ItemCore.KindEquipment,
                    InventorySortCondition.Slot,
                    out var result)
                && result.Success
                && result.Mutated
                && result.Algorithm == InventorySortAlgorithm.Slot
                && ItemId(inventory, 9) == 100
                && ItemId(inventory, 10) == 200
                && ItemId(inventory, 11) == 300,
                ref failures);
        }

        private static void VerifyExpireTimeSortsSoonestFirst(ref int failures)
        {
            var inventory = CreateMainTab(
                Consume(65, 100, expireTime: 0),
                Consume(67, 300, expireTime: 50),
                Consume(69, 200, expireTime: 10));

            Check(
                "consume 按时间 puts sooner expiry first and non-expiry last",
                InventorySortService.TrySort(
                    inventory,
                    InventoryListType.Main,
                    ItemCore.KindConsumable,
                    InventorySortCondition.ExpireTime,
                    out var result)
                && result.Success
                && result.Mutated
                && result.Algorithm == InventorySortAlgorithm.ExpireTime
                && ItemId(inventory, 65) == 200
                && ItemId(inventory, 66) == 300
                && ItemId(inventory, 67) == 100,
                ref failures);
        }

        private static void VerifyRarityLevelPartAndTypeAlgorithms(ref int failures)
        {
            var low = Core(100, ItemCore.KindEquipment);
            var high = Core(200, ItemCore.KindEquipment);
            var lowKeys = new InventorySortKeys(10, 1, partRank: 16, stackableType: "[b]", expireTime: 0);
            var highKeys = new InventorySortKeys(85, 4, partRank: 12, stackableType: "[a]", expireTime: 0);

            Check(
                "equipment wire 1 is 按品级",
                InventorySortService.ResolveSortAlgorithm(
                    InventoryListType.Main, ItemCore.KindEquipment, InventorySortCondition.Rarity)
                    == InventorySortAlgorithm.Rarity
                && InventorySortService.CompareItems(
                    low, 9, lowKeys, high, 10, highKeys, InventorySortAlgorithm.Rarity) > 0,
                ref failures);

            Check(
                "equipment wire 2 is 按Lv",
                InventorySortService.ResolveSortAlgorithm(
                    InventoryListType.Main, ItemCore.KindEquipment, InventorySortCondition.Level)
                    == InventorySortAlgorithm.Level
                && InventorySortService.CompareItems(
                    low, 9, lowKeys, high, 10, highKeys, InventorySortAlgorithm.Level) > 0,
                ref failures);

            Check(
                "equipment wire 3 is 按部位",
                InventorySortService.ResolveSortAlgorithm(
                    InventoryListType.Main, ItemCore.KindEquipment, InventorySortCondition.TypeOrPart)
                    == InventorySortAlgorithm.Part
                && InventorySortService.CompareItems(
                    low, 9, lowKeys, high, 10, highKeys, InventorySortAlgorithm.Part) > 0,
                ref failures);

            Check(
                "consume wire 3 is 按类型",
                InventorySortService.ResolveSortAlgorithm(
                    InventoryListType.Main, ItemCore.KindConsumable, InventorySortCondition.TypeOrPart)
                    == InventorySortAlgorithm.StackableType
                && InventorySortService.CompareItems(
                    low, 9, lowKeys, high, 10, highKeys, InventorySortAlgorithm.StackableType) > 0,
                ref failures);
        }

        private static void VerifyEpicRanksAboveLegendaryAndUnique(ref int failures)
        {
            Check(
                "epic rarity ranks above legendary and unique",
                InventorySortService.ToRaritySortRank(4) > InventorySortService.ToRaritySortRank(6)
                && InventorySortService.ToRaritySortRank(6) > InventorySortService.ToRaritySortRank(3)
                && InventorySortService.ToRaritySortRank(3) > InventorySortService.ToRaritySortRank(5)
                && InventorySortService.ToRaritySortRank(5) > InventorySortService.ToRaritySortRank(2),
                ref failures);

            var unique = Core(100, ItemCore.KindEquipment);
            var legendary = Core(200, ItemCore.KindEquipment);
            var epic = Core(300, ItemCore.KindEquipment);
            var uniqueKeys = new InventorySortKeys(1, InventorySortService.ToRaritySortRank(3), 12, string.Empty, 0);
            var legendaryKeys = new InventorySortKeys(1, InventorySortService.ToRaritySortRank(6), 12, string.Empty, 0);
            var epicKeys = new InventorySortKeys(1, InventorySortService.ToRaritySortRank(4), 12, string.Empty, 0);

            Check(
                "按品级 places epic before legendary and unique",
                InventorySortService.CompareItems(
                    epic, 11, epicKeys, legendary, 9, legendaryKeys, InventorySortAlgorithm.Rarity) < 0
                && InventorySortService.CompareItems(
                    epic, 11, epicKeys, unique, 10, uniqueKeys, InventorySortAlgorithm.Rarity) < 0
                && InventorySortService.CompareItems(
                    legendary, 9, legendaryKeys, unique, 10, uniqueKeys, InventorySortAlgorithm.Rarity) < 0,
                ref failures);
        }

        private static void VerifyPerTabConditionAllowList(ref int failures)
        {
            Check(
                "equipment allows 栏位/品级/Lv/部位",
                InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindEquipment, 0)
                && InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindEquipment, 1)
                && InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindEquipment, 2)
                && InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindEquipment, 3)
                && !InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindEquipment, 4),
                ref failures);

            Check(
                "consume allows 栏位/类型/时间",
                InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindConsumable, 0)
                && InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindConsumable, 3)
                && InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindConsumable, 4)
                && !InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindConsumable, 1)
                && !InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindConsumable, 2),
                ref failures);

            Check(
                "material/expert allow 栏位/类型 only",
                InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindMaterial, 0)
                && InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindMaterial, 3)
                && !InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindMaterial, 4)
                && InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindExpertJobMaterial, 3)
                && !InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindExpertJobMaterial, 4)
                && InventorySortService.ResolveSortAlgorithm(
                    InventoryListType.Main, ItemCore.KindMaterial, 4) == InventorySortAlgorithm.Slot,
                ref failures);
        }

        private static void VerifyQuestCargoPetAndMedalHaveNoSortType(ref int failures)
        {
            var quest = CreateMainTab(
                Item(177, 300, ItemCore.KindQuest),
                Item(178, 100, ItemCore.KindQuest),
                Item(179, 200, ItemCore.KindQuest));

            Check(
                "quest tab has lock only and ignores extra conditions",
                !InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindQuest, 1)
                && InventorySortService.TrySort(
                    quest,
                    InventoryListType.Main,
                    ItemCore.KindQuest,
                    3,
                    out var questResult)
                && questResult.Algorithm == InventorySortAlgorithm.Slot
                && ItemId(quest, 177) == 100
                && ItemId(quest, 178) == 200
                && ItemId(quest, 179) == 300,
                ref failures);

            Check(
                "cargo/emblem/medal/pet-page have no sort-type radios",
                !InventorySortService.IsConditionAllowed(InventoryListType.PersonalCargo, 11, 1)
                && !InventorySortService.IsConditionAllowed(InventoryListType.Main, ItemCore.KindAvatarEmblem, 1)
                && !InventorySortService.IsConditionAllowed(InventoryListType.GuildMedal, ItemCore.KindGuildMedal, 1)
                && !InventorySortService.IsConditionAllowed(InventoryListType.Pet, ItemCore.KindCreature, 0)
                && InventorySortService.IsConditionAllowed(
                    InventoryListType.Pet, ItemCore.KindCreatureEquipment, 0)
                && !InventorySortService.IsConditionAllowed(
                    InventoryListType.Pet, ItemCore.KindCreatureEquipment, 1)
                && InventorySortService.IsConditionAllowed(
                    InventoryListType.Pet, ItemCore.KindCreatureConsumable, 0),
                ref failures);
        }

        private static void VerifyAvatarDefaultsToRarity(ref int failures)
        {
            Check(
                "avatar default is 按品级 and 3 is 按部位",
                InventorySortService.ResolveSortAlgorithm(
                    InventoryListType.Avatar, ItemCore.KindAvatar, 0) == InventorySortAlgorithm.Rarity
                && InventorySortService.ResolveSortAlgorithm(
                    InventoryListType.Avatar, ItemCore.KindAvatar, 1) == InventorySortAlgorithm.Rarity
                && InventorySortService.ResolveSortAlgorithm(
                    InventoryListType.Avatar, ItemCore.KindAvatar, 3) == InventorySortAlgorithm.Part
                && InventorySortService.IsConditionAllowed(
                    InventoryListType.Avatar, ItemCore.KindAvatar, 0)
                && InventorySortService.IsConditionAllowed(
                    InventoryListType.Avatar, ItemCore.KindAvatar, 3)
                && !InventorySortService.IsConditionAllowed(
                    InventoryListType.Avatar, ItemCore.KindAvatar, 2),
                ref failures);
        }

        private static void VerifyUnsupportedConditionFallsBack(ref int failures)
        {
            var left = Core(100, ItemCore.KindEquipment);
            var right = Core(200, ItemCore.KindEquipment);
            var keys = new InventorySortKeys(1, 1, 12, "[stackable]", 0);
            Check(
                "unknown algorithm falls back to item id",
                InventorySortService.CompareItems(left, 11, keys, right, 9, keys, 99) < 0,
                ref failures);

            var inventory = CreateMainTab(
                Consume(65, 300, expireTime: 9),
                Consume(66, 100, expireTime: 1),
                Consume(67, 200, expireTime: 5));

            Check(
                "consume unsupported level condition uses 按栏位",
                InventorySortService.TrySort(
                    inventory,
                    InventoryListType.Main,
                    ItemCore.KindConsumable,
                    InventorySortCondition.Level,
                    out var result)
                && result.Algorithm == InventorySortAlgorithm.Slot
                && ItemId(inventory, 65) == 100
                && ItemId(inventory, 66) == 200
                && ItemId(inventory, 67) == 300,
                ref failures);
        }

        private static void VerifySortPreservesItemsAcrossGaps(ref int failures)
        {
            var inventory = CreateMainTab(
                Eq(9, 300),
                Eq(11, 100),
                Eq(13, 200));

            Check(
                "equipment sort keeps one copy of each item and fills gaps",
                InventorySortService.TrySort(
                    inventory,
                    InventoryListType.Main,
                    ItemCore.KindEquipment,
                    InventorySortCondition.Slot,
                    out var result)
                && result.Success
                && ItemId(inventory, 9) == 100
                && ItemId(inventory, 10) == 200
                && ItemId(inventory, 11) == 300
                && inventory.GetItem(InventoryListType.Main, 13) == null,
                ref failures);
        }

        private static InventoryService CreateMainTab
(params (short Slot, ItemCore Item)[] items)
        {
            var inventory = new InventoryService(91021, 91020);
            foreach (var entry in items)
                inventory.SetItem(InventoryListType.Main, entry.Slot, entry.Item);
            return inventory;
        }

        private static (short Slot, ItemCore Item) Eq(short slot, int itemId)
            => (slot, Core(itemId, ItemCore.KindEquipment));

        private static (short Slot, ItemCore Item) Consume(short slot, int itemId, int expireTime)
        {
            var item = Core(itemId, ItemCore.KindConsumable);
            item.ExpireTime = expireTime;
            return (slot, item);
        }

        private static (short Slot, ItemCore Item) Item(short slot, int itemId, byte kind)
            => (slot, Core(itemId, kind));

        private static ItemCore Core(int itemId, byte kind)
        {
            return new ItemCore
            {
                ItemKind = kind,
                ItemId = itemId,
            };
        }

        private static int ItemId(InventoryService inventory, short slot)
            => inventory.GetItem(InventoryListType.Main, slot)?.ItemId ?? -1;

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
