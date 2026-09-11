namespace DfoServer.Game.Inventory
{
    internal static class InventorySortCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            InventoryListType listType,
            byte category,
            out InventorySortServiceResult result,
            out bool persistenceFailed)
            => TryCommit(
                lease,
                listType,
                category,
                InventorySortCondition.Default,
                out result,
                out persistenceFailed);

        internal static bool TryCommit(
            InventoryLease lease,
            InventoryListType listType,
            byte category,
            byte condition,
            out InventorySortServiceResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null)
                return false;

            var sortApplied = false;
            InventorySortServiceResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "sort-item-space",
                (connection, transaction) =>
                {
                    var expiredChanges = new InventoryMutationSet();
                    if (InventorySortService.TryGetSortRange(
                            lease.Inventory,
                            listType,
                            category,
                            out var range))
                    {
                        InventoryItemLifecycleService.RemoveExpiredItemsInRange(
                            lease.Inventory,
                            listType,
                            range,
                            InventoryItemLifecycleService.UtcNowUnixSeconds(),
                            expiredChanges);
                    }

                    sortApplied = InventorySortService.TrySort(
                        lease.Inventory,
                        listType,
                        category,
                        condition,
                        out committedResult);
                    if (committedResult != null && expiredChanges.HasChanges)
                    {
                        committedResult.Changes.AddRange(expiredChanges);
                        committedResult.Mutated = committedResult.Changes.HasChanges;
                        committedResult.AffectedSlotCount = committedResult.Changes.Slots.Count;
                    }

                    return sortApplied;
                });

            result = committedResult;
            persistenceFailed = sortApplied && !committed;
            return sortApplied && committed;
        }
    }
}
