using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class ItemPurchaseLimitDefinition
    {
        public int ItemId { get; set; }

        public int LimitType { get; set; }

        public int LimitCount { get; set; }

        public int ResetType { get; set; }
    }

    internal static class ItemPurchaseLimitService
    {
        private const string TableName = "item_purchase_limits";
        private const int LimitTypeAccount = 0;
        private const int LimitTypeCharacter = 1;
        private const int ResetTypeNone = 0;
        private const int ResetTypeDaily = 1;

        private static readonly Lazy<ItemShopCatalog> ShopCatalog =
            new Lazy<ItemShopCatalog>(LoadShopCatalog);

        private static string CreateTableSql => $@"
CREATE TABLE IF NOT EXISTS {TableName} (
    account_id INTEGER NOT NULL,
    character_id INTEGER NOT NULL DEFAULT 0,
    npc_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    buy_count INTEGER NOT NULL DEFAULT 0,
    limit_type INTEGER NOT NULL DEFAULT 0,
    reset_type INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, character_id, npc_id, item_id, limit_type, reset_type),
    CHECK(limit_type IN (0, 1)),
    CHECK(reset_type IN (0, 1))
);";

        private static string CreateAccountResetIndexSql => $@"
CREATE INDEX IF NOT EXISTS idx_item_purchase_limits_account_reset
    ON {TableName}(account_id, reset_type);";

        internal static bool TryResolveDefinition(
            int itemTemplateId,
            out ItemPurchaseLimitDefinition definition)
        {
            definition = null;
            if (itemTemplateId <= 0)
                return false;

            var stackable = StackableItemProvider.Load(itemTemplateId);
            if (stackable == null
                || stackable.DailyPurchaseLimitCount <= 0)
            {
                return false;
            }

            var scope = NormalizeScope(stackable.DailyPurchaseLimitScope);
            var limitType = ResolveLimitType(scope);
            if (limitType < 0)
                return false;

            definition = new ItemPurchaseLimitDefinition
            {
                ItemId = itemTemplateId,
                LimitType = limitType,
                LimitCount = stackable.DailyPurchaseLimitCount,
                ResetType = stackable.ResetDailyPurchaseItem
                    ? ResetTypeDaily
                    : ResetTypeNone,
            };
            return true;
        }

        internal static List<ItemValueEntrySnapshot> LoadNpcPurchaseCounts(
            InventoryService inventory,
            int npcId)
        {
            var result = new List<ItemValueEntrySnapshot>();
            if (inventory == null
                || inventory.Database == null
                || inventory.AccountId <= 0
                || inventory.CharacterId <= 0
                || npcId <= 0)
            {
                return result;
            }

            var catalog = ShopCatalog.Value;
            if (catalog == null)
                return result;

            var definitions = GetNpcLimitedItemDefinitions(catalog, npcId);
            if (definitions.Count == 0)
                return result;

            try
            {
                using (var connection = inventory.Database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    if (!EnsureSchema(connection, transaction))
                        return result;

                    foreach (var definition in definitions)
                    {
                        if (!TryLoadCurrentCount(
                                connection,
                                transaction,
                                inventory.AccountId,
                                inventory.CharacterId,
                                npcId,
                                definition,
                                out var buyCount)
                            || buyCount <= 0)
                        {
                            continue;
                        }

                        result.Add(new ItemValueEntrySnapshot
                        {
                            ItemId = definition.ItemId,
                            Value = buyCount,
                        });
                    }

                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ItemPurchaseLimit] load npc counts failed npcId={npcId} "
                    + $"cid={inventory.CharacterId} aid={inventory.AccountId}: {ex.Message}");
            }

            return result;
        }

        internal static bool TryRecordPurchase(
            InventoryService inventory,
            int npcId,
            int itemTemplateId,
            int purchaseCount,
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (inventory == null || purchaseCount <= 0)
                return false;

            if (!TryResolveDefinition(itemTemplateId, out var definition))
                return true;

            if (connection == null
                || transaction == null
                || npcId <= 0
                || inventory.AccountId <= 0
                || (definition.LimitType == LimitTypeCharacter && inventory.CharacterId <= 0))
            {
                return false;
            }

            if (!EnsureSchema(connection, transaction))
                return false;

            var characterId = definition.LimitType == LimitTypeAccount
                ? 0
                : inventory.CharacterId;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
INSERT INTO {TableName} (
    account_id, character_id, npc_id, item_id, buy_count, limit_type, reset_type, updated_at
)
SELECT
    @aid, @cid, @npcId, @itemId, @buyCount, @limitType, @resetType, CURRENT_TIMESTAMP
WHERE @buyCount <= @limitCount
ON CONFLICT(account_id, character_id, npc_id, item_id, limit_type, reset_type) DO UPDATE SET
    buy_count = {TableName}.buy_count + excluded.buy_count,
    updated_at = CURRENT_TIMESTAMP
WHERE {TableName}.buy_count <= @limitCount - excluded.buy_count;";
                command.Parameters.AddWithValue("@aid", inventory.AccountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@npcId", npcId);
                command.Parameters.AddWithValue("@itemId", itemTemplateId);
                command.Parameters.AddWithValue("@buyCount", purchaseCount);
                command.Parameters.AddWithValue("@limitType", definition.LimitType);
                command.Parameters.AddWithValue("@resetType", definition.ResetType);
                command.Parameters.AddWithValue("@limitCount", definition.LimitCount);
                return command.ExecuteNonQuery() > 0;
            }
        }

        internal static bool ResetPurchasesForAccount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId)
        {
            if (connection == null || accountId <= 0)
                return false;

            if (!EnsureSchema(connection, transaction))
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
UPDATE {TableName}
SET buy_count = 0,
    updated_at = CURRENT_TIMESTAMP
WHERE account_id = @aid
  AND reset_type = {ResetTypeDaily}
  AND buy_count > 0;";
                command.Parameters.AddWithValue("@aid", accountId);
                command.ExecuteNonQuery();
                return true;
            }
        }

        private static List<ItemPurchaseLimitDefinition> GetNpcLimitedItemDefinitions(
            ItemShopCatalog catalog,
            int npcId)
        {
            var definitions = new List<ItemPurchaseLimitDefinition>();
            if (catalog == null || npcId <= 0)
                return definitions;

            var seen = new HashSet<int>();
            var shopIds = catalog.GetShopIdsByNpcId(npcId);
            for (var i = 0; i < shopIds.Count; i++)
            {
                var itemIds = catalog.GetItemIdsByShopId(shopIds[i]);
                for (var j = 0; j < itemIds.Count; j++)
                {
                    var itemId = itemIds[j];
                    if (itemId <= 0 || !seen.Add(itemId))
                        continue;

                    if (TryResolveDefinition(itemId, out var definition))
                        definitions.Add(definition);
                }
            }

            return definitions;
        }

        private static bool TryLoadCurrentCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            int npcId,
            ItemPurchaseLimitDefinition definition,
            out int buyCount)
        {
            buyCount = 0;
            if (connection == null
                || definition == null
                || accountId <= 0
                || characterId <= 0
                || npcId <= 0)
            {
                return false;
            }

            var rowCharacterId = definition.LimitType == LimitTypeAccount
                ? 0
                : characterId;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
SELECT buy_count
FROM {TableName}
WHERE account_id = @aid
  AND character_id = @cid
  AND npc_id = @npcId
  AND item_id = @itemId
  AND limit_type = @limitType
  AND reset_type = @resetType;";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", rowCharacterId);
                command.Parameters.AddWithValue("@npcId", npcId);
                command.Parameters.AddWithValue("@itemId", definition.ItemId);
                command.Parameters.AddWithValue("@limitType", definition.LimitType);
                command.Parameters.AddWithValue("@resetType", definition.ResetType);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return false;

                buyCount = Convert.ToInt32(value);
                return true;
            }
        }

        private static bool EnsureSchema(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (connection == null)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = CreateTableSql;
                command.ExecuteNonQuery();
                command.CommandText = CreateAccountResetIndexSql;
                command.ExecuteNonQuery();
                return true;
            }
        }

        private static ItemShopCatalog LoadShopCatalog()
        {
            try
            {
                using (var archive = PvfArchive.Open(GameWorldConfig.PvfArchivePath))
                {
                    return ItemShopCatalog.Load(archive);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[ItemPurchaseLimit] itemshop catalog load failed: {ex.Message}");
                return null;
            }
        }

        private static string NormalizeScope(string scope)
        {
            return string.IsNullOrWhiteSpace(scope)
                ? string.Empty
                : scope.Trim();
        }

        private static int ResolveLimitType(string scope)
        {
            if (scope.Equals("account", StringComparison.OrdinalIgnoreCase))
                return LimitTypeAccount;
            if (scope.Equals("charac", StringComparison.OrdinalIgnoreCase)
                || scope.Equals("character", StringComparison.OrdinalIgnoreCase))
                return LimitTypeCharacter;
            return -1;
        }
    }
}
