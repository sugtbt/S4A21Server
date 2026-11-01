namespace DfoServer.Game.Inventory
{
    internal static class InventoryCompoundPlanning
    {
        internal static InventoryService CloneInventory(InventoryService source)
        {
            var inventory = new InventoryService(
                source.CharacterId,
                source.AccountId,
                source.Database);
            CopyListParam(source, inventory, InventoryListType.Main);
            CopyListParam(source, inventory, InventoryListType.Equipment);
            CopyListParam(source, inventory, InventoryListType.Avatar);
            CopyListParam(source, inventory, InventoryListType.Pet);
            CopyListParam(source, inventory, InventoryListType.PersonalCargo);
            CopyListParam(source, inventory, InventoryListType.AccountCargo);

            CopyItems(source, inventory, InventoryListType.Main);
            CopyItems(source, inventory, InventoryListType.Equipment);
            CopyItems(source, inventory, InventoryListType.Avatar);
            CopyItems(source, inventory, InventoryListType.Pet);
            CopyItems(source, inventory, InventoryListType.PersonalCargo);
            CopyItems(source, inventory, InventoryListType.AccountCargo);

            foreach (var item in source.GetMainVirtualCounts())
                inventory.AttachMainVirtualCount(item.SlotIndex, item.ItemId, item.Count);

            inventory.ClearDirtyState();
            if (source.PendingHappyTokenCeraGrant > 0)
                inventory.TryQueueHappyTokenCeraGrant(source.PendingHappyTokenCeraGrant);
            if (source.PendingTokenCeraGrant > 0)
                inventory.TryQueueTokenCeraGrant(source.PendingTokenCeraGrant);
            return inventory;
        }

        private static void CopyListParam(
            InventoryService source,
            InventoryService target,
            InventoryListType listType)
        {
            target.SetListParam16(listType, source.GetListParam16(listType));
        }

        private static void CopyItems(
            InventoryService source,
            InventoryService target,
            InventoryListType listType)
        {
            foreach (var pair in source.GetItems(listType))
                target.AttachItem(listType, pair.Key, pair.Value.Copy());
        }
    }
}
