using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Game.Premium
{
    public static class PremiumService
    {
        public const ushort DefaultServiceType = 1;

        private const int PremiumServiceEntryExpireBase = 6;
        private const int PremiumServiceEntryUsedCountBase = 10;
        private const int PremiumServiceEntryStride = 9;

        public static bool IsContractItem(int itemTemplateId)
        {
            return TryResolveContractItem(itemTemplateId, out _, out _);
        }

        internal static bool TryResolveContractItem(
            int itemTemplateId,
            out int premiumType,
            out int durationDays)
        {
            premiumType = 0;
            durationDays = 0;
            if (itemTemplateId <= 0)
                return false;

            if (PremiumCatalog.Load().TryGetValue(itemTemplateId, out premiumType, out durationDays)
                && premiumType > 0
                && durationDays > 0)
                return true;

            if (DevilContractCatalog.Load().TryResolveServiceItem(
                    itemTemplateId,
                    out var slotIndex,
                    out durationDays))
            {
                premiumType = DevilContractCatalog.SlotToPremiumType(slotIndex);
                return true;
            }

            premiumType = 0;
            durationDays = 0;
            return false;
        }

        public static async Task ActivateAndNotify(
            EnhancedClientSession session,
            IReadOnlyList<(int itemTemplateId, int count)> items,
            ISelectCharacterDataSource dataSource = null)
        {
            await ActivateAndNotify(
                session,
                items,
                dataSource,
                GameDatabase.CreateDefault());
        }

        public static async Task ActivateAndNotify(
            EnhancedClientSession session,
            IReadOnlyList<(int itemTemplateId, int count)> items,
            ISelectCharacterDataSource dataSource,
            IGameDatabase database)
        {
            if (session == null)
                return;
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            var accountId = session.Account?.AccountId ?? 0;
            if (accountId <= 0)
                return;

            var notifications = new List<(int premiumType, long remaining)>();
            if (items != null && items.Count > 0)
            {
                using (var conn = database.OpenConnection())
                {
                    using (var tx = conn.BeginTransaction())
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        foreach (var (itemTemplateId, count) in items)
                        {
                            if (!TryResolveContractItem(itemTemplateId, out var premiumType, out var durationDays))
                                continue;

                            var effectiveCount = Math.Max(1, count);
                            var totalDuration = (long)durationDays * 86400 * effectiveCount;
                            var newExpire = UpsertPremiumExpire(conn, tx, accountId, premiumType, now, totalDuration);
                            var remaining = newExpire - now;
                            notifications.Add((premiumType, remaining));
                            FileLogger.Log($"[PremiumService] Contract activated: account={accountId} type={premiumType} days={durationDays}x{effectiveCount} remaining={remaining} item=0x{itemTemplateId:X8}");
                        }

                        tx.Commit();
                    }
                }
            }

            foreach (var (premiumType, remaining) in notifications)
            {
                var body = BuildCeraSpecialItemNotification(premiumType, remaining);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.CERA_SPECIALITEM,
                    body));
            }

            await SendPremiumServiceRefresh(session, accountId, database);
        }

        public static byte[] BuildPremiumServiceStateBody(
            ushort serviceType,
            byte[] serviceData,
            byte status = 1)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(status);
            writer.WriteUInt16(serviceType);
            writer.WriteBytes(serviceData ?? Array.Empty<byte>());
            return writer.ToArray();
        }

        private static byte[] BuildCeraSpecialItemNotification(int premiumType, long remaining)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(2);
            writer.WriteByte((byte)premiumType);
            writer.WriteBytes(BitConverter.GetBytes(remaining));
            return writer.ToArray();
        }

        internal static async Task SendPremiumServiceRefresh(
            EnhancedClientSession session,
            int accountId,
            IGameDatabase database)
        {
            try
            {
                int cid = session.Player?.CharacterId ?? 0;
                if (cid <= 0) return;

                var dailyResetService = new Game.DailyReset.DailyResetService(database);
                var usage = new DevilContractUsagePolicy(
                    database,
                    dailyResetService).BuildPremiumServiceUsage(cid);
                var serviceData = BuildPremiumServiceData(
                    database.ConnectionString,
                    accountId,
                    usage);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.PREMIUM_SERVICE,
                    BuildPremiumServiceStateBody(DefaultServiceType, serviceData)));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[PremiumService] premium service refresh failed: {ex.Message}");
            }
        }

        public static async Task<(bool success, InventoryMutationResult result)> TryBuyDevilContract(
            EnhancedClientSession session,
            int commodityNo,
            int paymentMode,
            bool couponSelected,
            ISelectCharacterDataSource dataSource = null)
        {
            return await TryBuyDevilContract(
                session,
                commodityNo,
                paymentMode,
                couponSelected,
                dataSource,
                GameDatabase.CreateDefault());
        }

        public static async Task<(bool success, InventoryMutationResult result)> TryBuyDevilContract(
            EnhancedClientSession session,
            int commodityNo,
            int paymentMode,
            bool couponSelected,
            ISelectCharacterDataSource dataSource,
            IGameDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            var accountId = session?.Account?.AccountId ?? 0;
            var characterId = session?.Player?.CharacterId ?? 0;
            if (accountId <= 0 || characterId <= 0 || paymentMode != 0 || couponSelected)
                return (false, null);

            var catalog = DevilContractCatalog.Load();
            if (!catalog.TryGetPurchase(commodityNo, out var purchase)
                || !catalog.TryResolveServiceGrants(purchase, out var grants))
                return (false, null);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            DevilContractPurchaseApplication application = null;
            if (!InventoryCeraShopRuntimeService.TrySpendCeraPaymentAndApplyDbAction(
                    database.ConnectionString,
                    characterId,
                    purchase.ItemTemplateId,
                    purchase.CeraPrice,
                    (connection, transaction) => TryActivateDevilContractServices(
                        connection,
                        transaction,
                        accountId,
                        grants,
                        now,
                        out application),
                    out var payment))
                return (false, null);

            FileLogger.Log($"[PremiumService] Devil Contract service package activated: account={accountId} item=0x{purchase.ItemTemplateId:X8} services={application.Activations.Count}");

            if (session != null)
            {
                foreach (var activation in application.Activations)
                {
                    var body = BuildCeraSpecialItemNotification(activation.PremiumType, activation.RemainingSeconds);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.CERA_SPECIALITEM,
                        body));
                }
                await SendPremiumServiceRefresh(session, accountId, database);
            }

            var result = new InventoryMutationResult
            {
                ConsumedOnPurchase = true,
                UpdatedCoin = payment.NewCera,
                UpdatedTokenCera = payment.NewTokenCera,
                UpdatedHappyTokenCera = payment.NewHappyTokenCera,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            return (true, result);
        }

        internal static bool TryActivateDevilContractServices(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            IReadOnlyList<DevilContractServiceGrant> grants,
            long now,
            out DevilContractPurchaseApplication application)
        {
            application = null;
            if (connection == null
                || transaction == null
                || accountId <= 0
                || grants == null
                || grants.Count == 0)
                return false;

            application = new DevilContractPurchaseApplication();
            foreach (var grant in grants)
            {
                if (grant == null
                    || grant.SlotIndex < 0
                    || grant.SlotIndex >= DevilContractCatalog.SlotCount
                    || grant.DurationDays <= 0)
                    return false;

                var duration = (long)grant.DurationDays * 86400;
                var premiumType = DevilContractCatalog.SlotToPremiumType(grant.SlotIndex);
                var newExpire = UpsertPremiumExpire(connection, transaction, accountId, premiumType, now, duration);
                application.Activations.Add(new DevilContractActivation
                {
                    PremiumType = premiumType,
                    RemainingSeconds = newExpire - now,
                });
            }

            return true;
        }

        public static byte[] BuildPremiumServiceData(
            string connStr,
            int accountId,
            IReadOnlyDictionary<int, int> usedCountBySlot = null)
        {
            var data = new byte[74];
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT premium_type, end_time FROM account_premiums WHERE account_id=@aid AND premium_type>=@lo AND premium_type<@hi;";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@lo", DevilContractCatalog.SlotPremiumTypeBase);
                    cmd.Parameters.AddWithValue("@hi", DevilContractCatalog.SlotPremiumTypeBase + DevilContractCatalog.SlotCount);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var slot = DevilContractCatalog.PremiumTypeToSlot(reader.GetInt32(0));
                            if (slot < 0 || slot >= DevilContractCatalog.SlotCount) continue;
                            var expire = reader.GetInt64(1);
                            var expireOffset = PremiumServiceEntryExpireBase + slot * PremiumServiceEntryStride;
                            Buffer.BlockCopy(BitConverter.GetBytes((int)Math.Min(expire, int.MaxValue)), 0, data, expireOffset, 4);

                            if (usedCountBySlot != null
                                && usedCountBySlot.TryGetValue(slot, out var usedCount))
                            {
                                WriteInt32(
                                    data,
                                    PremiumServiceEntryUsedCountBase + slot * PremiumServiceEntryStride,
                                    Math.Max(0, usedCount));
                            }
                        }
                    }
                }
            }
            return data;
        }

        // 账号是否有生效中的"自动修理"契约 — 自行解析连接串(供 handler 直接调用)。
        public static bool HasActiveAutoRepairForAccount(int accountId)
        {
            return HasActiveAutoRepairForAccount(
                accountId,
                GameDatabase.CreateDefault());
        }

        public static bool HasActiveAutoRepairForAccount(
            int accountId,
            IGameDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            return HasActiveAutoRepair(database.ConnectionString, accountId);
        }

        // 账号是否有生效中的"自动修理"契约(魔王契约 slot 6, premium_type=586)。
        // 生效时装备修理免费。
        public static bool HasActiveAutoRepair(string connStr, int accountId)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                return HasActiveDevilContract(
                    conn,
                    null,
                    accountId,
                    DevilContractCatalog.AutoRepairSlotIndex);
            }
        }

        public static bool HasActiveDevilContract(
            string connectionString,
            int accountId,
            int slotIndex)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return false;

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                return HasActiveDevilContract(
                    connection,
                    null,
                    accountId,
                    slotIndex);
            }
        }

        internal static bool HasActiveDevilContract(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int slotIndex)
        {
            if (connection == null
                || accountId <= 0
                || slotIndex < 0
                || slotIndex >= DevilContractCatalog.SlotCount)
            {
                return false;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM account_premiums
WHERE account_id=@aid
  AND premium_type=@type
  AND end_time>@now
LIMIT 1;";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue(
                    "@type",
                    DevilContractCatalog.SlotToPremiumType(slotIndex));
                command.Parameters.AddWithValue(
                    "@now",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                return command.ExecuteScalar() != null;
            }
        }

        public static bool HasActivePremium(string connStr, int accountId, params int[] premiumTypes)
        {
            if (accountId <= 0 || premiumTypes == null || premiumTypes.Length == 0)
                return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    var typeParams = new string[premiumTypes.Length];
                    for (var i = 0; i < premiumTypes.Length; i++)
                    {
                        var name = "@type" + i;
                        typeParams[i] = name;
                        cmd.Parameters.AddWithValue(name, premiumTypes[i]);
                    }

                    cmd.CommandText = $@"
SELECT 1
FROM account_premiums
WHERE account_id=@aid
  AND end_time>@now
  AND premium_type IN ({string.Join(",", typeParams)})
LIMIT 1;";
                    cmd.Parameters.AddWithValue("@aid", accountId);
                    cmd.Parameters.AddWithValue("@now", now);
                    return cmd.ExecuteScalar() != null;
                }
            }
        }

        private static long UpsertPremiumExpire(string connStr, int accountId, int premiumType, long now, long duration)
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                return UpsertPremiumExpire(conn, null, accountId, premiumType, now, duration);
            }
        }

        private static long UpsertPremiumExpire(SqliteConnection conn, SqliteTransaction tx, int accountId, int premiumType, long now, long duration)
        {
            long oldExpire = 0;
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT end_time FROM account_premiums WHERE account_id=@aid AND premium_type=@type;";
                q.Parameters.AddWithValue("@aid", accountId);
                q.Parameters.AddWithValue("@type", premiumType);
                var val = q.ExecuteScalar();
                if (val != null && val != DBNull.Value)
                    oldExpire = Convert.ToInt64(val);
            }

            var newExpire = Math.Max(now, oldExpire) + duration;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO account_premiums (account_id, premium_type, end_time, updated_at)
VALUES (@aid, @type, @expire, CURRENT_TIMESTAMP)
ON CONFLICT(account_id, premium_type)
DO UPDATE SET end_time = @expire, updated_at = CURRENT_TIMESTAMP;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                cmd.Parameters.AddWithValue("@type", premiumType);
                cmd.Parameters.AddWithValue("@expire", newExpire);
                cmd.ExecuteNonQuery();
            }
            return newExpire;
        }

        private static void WriteInt32(byte[] data, int offset, int value)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length)
                return;

            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, offset, 4);
        }
    }

    internal sealed class DevilContractPurchaseApplication
    {
        public List<DevilContractActivation> Activations { get; } = new List<DevilContractActivation>();
    }

    internal sealed class DevilContractActivation
    {
        public int PremiumType { get; set; }

        public long RemainingSeconds { get; set; }
    }
}
