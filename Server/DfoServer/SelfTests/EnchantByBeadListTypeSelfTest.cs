using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Parsers.Inventory;
using System;

namespace DfoServer.SelfTests
{
    public static class EnchantByBeadListTypeSelfTest
    {
        private const int LotusBeadItemId = 2600295;
        private const int LotusCardItemId = 3601;
        private const int WeaponItemId = 27850;

        public static int Run()
        {
            Console.WriteLine("=== ENCHANT_BY_BEAD_LISTTYPE selftest ===");
            var failures = 0;

            Check(
                "ENCHANT_BY_BEAD parses bead and target list types",
                EnchantByBeadRequest.TryParse(
                    new byte[] { 0x00, 0x03, 0x00, 0x03, 0x0F, 0x00 },
                    out var request)
                && request.BeadListType == InventoryListType.Main
                && request.BeadSlotIndex == 3
                && request.TargetListType == InventoryListType.Equipment
                && request.TargetSlotIndex == 15,
                ref failures);

            Check(
                "bead enchant can target equipped equipment list",
                VerifyEquippedEquipmentEnchant(),
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "ENCHANT_BY_BEAD_LISTTYPE selftest passed."
                    : $"ENCHANT_BY_BEAD_LISTTYPE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool VerifyEquippedEquipmentEnchant()
        {
            var inventory = new InventoryService(characterId: 91001, accountId: 91000);
            var bead = ItemCore.Create(ItemCore.KindConsumable, LotusBeadItemId);
            bead.Count = 2;
            inventory.SetItem(InventoryListType.Main, 3, bead);

            var weapon = ItemCore.Create(ItemCore.KindEquipment, WeaponItemId);
            inventory.SetItem(
                InventoryListType.Equipment,
                (short)EquipmentType.Weapon,
                weapon);

            var command = new EnchantByBeadCommand
            {
                BeadListType = InventoryListType.Main,
                BeadSlotIndex = 3,
                TargetListType = InventoryListType.Equipment,
                TargetSlotIndex = (short)EquipmentType.Weapon,
            };

            return InventoryEquipmentMutationService.TryEnchantByBead(
                    inventory,
                    command,
                    out var result)
                && result != null
                && result.Success
                && result.TargetListType == InventoryListType.Equipment
                && result.TargetSlotIndex == (short)EquipmentType.Weapon
                && result.BeadRemainingStackCount == 1
                && inventory.GetItem(InventoryListType.Main, 3)?.Count == 1
                && inventory.GetItem(
                    InventoryListType.Equipment,
                    (short)EquipmentType.Weapon)?.EnchantCardId == LotusCardItemId;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
