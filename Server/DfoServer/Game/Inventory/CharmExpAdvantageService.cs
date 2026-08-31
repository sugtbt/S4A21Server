using DfoServer.Game.ItemUpgrade;
using System;

namespace DfoServer.Game.Inventory
{
    // 快捷栏纹章(charm)的 [exp advantage] 杀怪经验加成。
    // 快捷栏最多放一个纹章(InventoryInsertService/InventoryMoveService 强制),
    // 每次杀怪实时扫描, 进本后换上/换下即时生效。
    // 已知简化: 不做耐久门控 -- 服务端当前没有扣纹章耐久的链路
    // (DECREASE_CHARM_ENERGY 仅有包号常量), 耐久耗尽失效待该链路实现后补。
    internal static class CharmExpAdvantageService
    {
        // 与 InventoryInsertService/InventoryMoveService 的快捷栏区间一致。
        private const short QuickSlotStart = 3;
        private const short QuickSlotEnd = 8;

        internal static int GetQuickSlotCharmExpAdvantagePercent(
            Guid sessionId,
            int characterId)
        {
            if (!InventoryContext.TryGetOwnedLease(
                    sessionId,
                    characterId,
                    out var lease))
            {
                return 0;
            }

            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(lease, sessionId, characterId))
                    return 0;
                return ScanQuickSlot(lease.Inventory);
            }
        }

        internal static int ScanQuickSlot(InventoryService inventory)
            => ScanQuickSlot(
                inventory,
                ResolveEquipmentTypeOrUnknown,
                ItemMetadataResolver.ResolveExpAdvantage);

        // 注入解析器的版本, 供不加载 PVF 的自测使用。
        internal static int ScanQuickSlot(
            InventoryService inventory,
            Func<int, EquipmentType> equipmentTypeOf,
            Func<int, int> expAdvantageOf)
        {
            if (inventory == null)
                return 0;

            for (var slot = QuickSlotStart; slot <= QuickSlotEnd; slot++)
            {
                var item = inventory.GetItem(InventoryListType.Main, slot);
                if (item == null)
                    continue;
                if (equipmentTypeOf(item.ItemId) != EquipmentType.Charm)
                    continue;
                var percent = expAdvantageOf(item.ItemId);
                if (percent > 0)
                    return percent;
            }
            return 0;
        }

        private static EquipmentType ResolveEquipmentTypeOrUnknown(int itemTemplateId)
            => EquipmentTypeInfo.ParseOrUnknown(
                ItemMetadataResolver.ResolveEquipmentType(itemTemplateId));
    }
}
