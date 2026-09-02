using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Game.KnightShield
{
    public static class KnightShieldEquipmentSnapshotSynchronizer
    {
        public const byte SupportWeaponSlot = (byte)EquipmentType.SupportWeapon;

        public static void Apply(
            byte job,
            int growType,
            UserInfoAdditionSnapshot addition,
            KnightShieldDeckSnapshot deck)
        {
            if (addition == null || !KnightShieldDataProvider.IsEligibleCharacter(job))
                return;

            var existing = addition.EquippedEntries.Find(
                entry => entry != null && entry.Slot == SupportWeaponSlot);
            var shieldItemId = deck != null ? deck.MainShieldItemId : 0;
            var hasValidDeckShield = shieldItemId > 0
                && KnightShieldDataProvider.IsCatalogShield(growType, shieldItemId);

            // 空 deck 或非图鉴主盾不能清掉穿戴栏里的真实副武器。
            // 穿戴栏槽 24 是战斗/装备 ItemCore 真源，已有条目不能被虚拟
            // deck 的默认 Core 覆盖（否则会丢失强化/附魔等状态）。
            if (!hasValidDeckShield)
                return;
            if (existing?.Core != null)
                return;

            addition.EquippedEntries.RemoveAll(entry => entry.Slot == SupportWeaponSlot);

            addition.EquippedEntries.Add(new EquippedEntrySnapshot
            {
                Slot = SupportWeaponSlot,
                Core = ItemCore.Create(ItemCore.KindEquipment, shieldItemId),
            });
            addition.EquippedEntries.Sort((left, right) => left.Slot.CompareTo(right.Slot));
        }
    }
}
