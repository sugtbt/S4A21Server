using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.DailyReset
{
    internal sealed class DailyRefillItemGrant
    {
        internal int ItemId { get; init; }
        internal int Count { get; init; }
        internal InventoryListType ListType { get; init; }
        internal short SlotIndex { get; init; }
    }

    internal static class DailyRefillItemService
    {
        private const string CounterKeyPrefix = "pvf_daily_refill_item_";

        internal static bool TryApply(
            InventoryLease lease,
            IGameDatabase database,
            out IReadOnlyList<DailyRefillItemGrant> grantedItems)
        {
            grantedItems = Array.Empty<DailyRefillItemGrant>();
            if (lease?.Inventory == null)
                return false;
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                var applied = new List<DailyRefillItemGrant>();
                var dailyReset = new DailyResetService(database);

                try
                {
                    using (var connection = database.OpenConnection())
                    {
                        using (var transaction = connection.BeginTransaction())
                        {
                            foreach (var rule in PvfDailyRefillItemProvider.Current)
                            {
                                var key = CounterKeyPrefix + rule.ItemId;
                                if (dailyReset.GetCounter(connection, transaction, lease.CharacterId, key) > 0)
                                {
                                    if (rule.ItemId == 4183)
                                    {
                                        FileLogger.Log(
                                            $"[DailyRefillItem] skip fixed ticket cid={lease.CharacterId} " +
                                            "reason=already-applied-today");
                                    }
                                    continue;
                                }

                                var metadata = ItemMetadataResolver.Resolve(rule.ItemId);
                                var stackLimit = metadata?.StackLimit ?? 0;
                                var currentCount = inventory.CountMainItem(rule.ItemId);
                                var grantCount = DailyRefillItemPolicy.CalculateGrant(rule, currentCount, stackLimit);

                                InventoryRewardGrantResult grant = null;
                                if (grantCount > 0
                                    && (!InventoryRewardGrantService.TryCreateAndInsert(
                                            inventory,
                                            rule.ItemId,
                                            ItemCreateReason.DailyRefill,
                                            grantCount,
                                            out grant)
                                        || !grant.Success
                                        || grant.GrantedCount != grantCount))
                                {
                                    transaction.Rollback();
                                    FileLogger.Log(
                                        $"[DailyRefillItem] grant deferred cid={lease.CharacterId} " +
                                        $"item={rule.ItemId} count={grantCount} reason=inventory grant failed");
                                    return false;
                                }

                                if (grantCount > 0)
                                    applied.Add(new DailyRefillItemGrant
                                    {
                                        ItemId = rule.ItemId,
                                        Count = grantCount,
                                        ListType = grant.ListType,
                                        SlotIndex = grant.SlotIndex,
                                    });
                                if (!dailyReset.TryClaimFlag(connection, transaction, lease.CharacterId, key))
                                {
                                    transaction.Rollback();
                                    return false;
                                }

                                FileLogger.Log(
                                    $"[DailyRefillItem] applied cid={lease.CharacterId} item={rule.ItemId} " +
                                    $"mode={(int)rule.Mode} current={currentCount} grant={grantCount} limit={stackLimit}");
                            }

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, lease))
                            {
                                transaction.Rollback();
                                return false;
                            }

                            transaction.Commit();
                            inventory.ClearDirtyState();
                            grantedItems = applied;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[DailyRefillItem] apply failed cid={lease.CharacterId}: {ex}");
                    return false;
                }
            }
        }
    }
}
