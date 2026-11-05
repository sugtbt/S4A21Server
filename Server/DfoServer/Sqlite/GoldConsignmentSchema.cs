using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    /// <summary>
    /// Gold consignment (金币寄售) storage.
    ///
    /// Independent feature from the ordinary item auction: players consign a
    /// quantity of gold at a cera-point unit price, and the sale proceeds are
    /// credited as token cera (accounts.token_cera).
    ///
    /// Wire evidence (captured 2026-10-30, current A21 client):
    ///   0x00B7 register body = 28B, identical to the 37B item-auction register
    ///   minus the trailing 3 filter tuples, with space=0 and slot=0 meaning
    ///   "Main inventory virtual slot 0" (gold). See
    ///   Docs/金币寄售业务接入规范.md.
    /// </summary>
    internal static class GoldConsignmentSchema
    {
        internal const string Sql = @"
CREATE TABLE IF NOT EXISTS gold_consignment_listings (
    listing_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    seller_character_id INTEGER NOT NULL,
    seller_account_id   INTEGER NOT NULL,
    gold_amount     INTEGER NOT NULL,          -- gold consigned (escrowed)
    unit_price_cera INTEGER NOT NULL,          -- cera per 10000 gold
    total_price_cera INTEGER NOT NULL,         -- locked price at listing time
    fee_percent     INTEGER NOT NULL DEFAULT 5, -- 手续费%锁存于上架时（官方分档规则）
    duration_hours  INTEGER NOT NULL,
    state           INTEGER NOT NULL,          -- 0 active, 1 sold, 2 cancelled, 3 expired
    created_at_unix INTEGER NOT NULL,
    expires_at_unix INTEGER NOT NULL,
    settled_at_unix INTEGER,                   -- NULL while active
    buyer_character_id INTEGER,                 -- set when sold
    buyer_account_id   INTEGER
);
CREATE INDEX IF NOT EXISTS idx_gold_consignment_active
    ON gold_consignment_listings(state, expires_at_unix);
CREATE INDEX IF NOT EXISTS idx_gold_consignment_seller
    ON gold_consignment_listings(seller_character_id, state);

-- Outcome ledger: one row per terminal transition, used to make settlement
-- idempotent and to audit escrow release.
CREATE TABLE IF NOT EXISTS gold_consignment_settlements (
    settlement_id   INTEGER PRIMARY KEY AUTOINCREMENT,
    listing_id      INTEGER NOT NULL,
    outcome         INTEGER NOT NULL,          -- 1 sold, 2 cancelled, 3 expired
    seller_character_id INTEGER NOT NULL,
    seller_account_id   INTEGER NOT NULL,
    gold_amount     INTEGER NOT NULL,
    token_cera_paid INTEGER NOT NULL,          -- 0 for cancel/expire
    created_at_unix INTEGER NOT NULL,
    UNIQUE(listing_id, outcome)
);";

        internal static void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = Sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// v43: lock the official fee tier onto each listing. The tier depends on
        /// 「一口价 ÷ 平均价」at listing time, so it cannot be recomputed later without
        /// changing the seller's payout after the fact.
        /// </summary>
        internal static void ApplyFeePercentColumn(
            SqliteConnection connection, SqliteTransaction transaction)
        {
            // Some bootstrap paths build a database from a schema file that predates the
            // consignment tables and then only run the migration chain, so the table can
            // legitimately be absent here. Nothing to migrate in that case: the CREATE
            // TABLE (migration 42 / item_schema.sql) already carries the column.
            if (!TableExists(connection, transaction, "gold_consignment_listings"))
                return;
            if (HasColumn(connection, transaction, "gold_consignment_listings", "fee_percent"))
                return;

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                "ALTER TABLE gold_consignment_listings "
                + "ADD COLUMN fee_percent INTEGER NOT NULL DEFAULT 5;";
            cmd.ExecuteNonQuery();
        }

        private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction,
            string table)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
            cmd.Parameters.AddWithValue("@name", table);
            return System.Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction,
            string table, string column)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
