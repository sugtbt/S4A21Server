using DfoServer.Game.ItemUpgrade;
using System;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryItemUpgradeCommitService
    {
        internal static bool TryCommit(
            InventoryLease lease,
            ItemUpgradeCommand command,
            out ItemUpgradeResult result,
            out bool persistenceFailed,
            ItemCore expectedTarget = null)
        {
            result = null;
            persistenceFailed = false;
            if (lease?.Inventory == null || command == null)
                return false;

            var upgradeApplied = false;
            ItemUpgradeResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "upgrade-item",
                (connection, transaction) =>
                {
                    if (expectedTarget != null)
                    {
                        var current = lease.Inventory.GetItem(command.TargetListType, command.TargetSlotIndex);
                        if (current == null || !current.ToBytes().AsSpan().SequenceEqual(expectedTarget.ToBytes()))
                            return false;
                    }
                    upgradeApplied = InventoryItemUpgradeService.TryUpgradeItem(
                        lease.Inventory,
                        command,
                        out committedResult);
                    return upgradeApplied;
                });

            result = committedResult;
            persistenceFailed = upgradeApplied && !committed;
            return upgradeApplied && committed;
        }
    }
}
