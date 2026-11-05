using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// Gold consignment (金币寄售) persistence owner.
    ///
    /// Owns <c>gold_consignment_listings</c> and <c>gold_consignment_settlements</c>
    /// (schema v42). All writes run inside the caller's transaction; this type
    /// never opens its own connection so escrow, gold deduction and the token-cera
    /// credit can share one atomic boundary.
    ///
    /// Business rules are PROVISIONAL pending the operator's final numbers.
    /// </summary>
    internal static class GoldConsignmentRepository
    {
        internal const int StateActive = 0;
        internal const int StateSold = 1;
        internal const int StateCancelled = 2;
        internal const int StateExpired = 3;

        internal const int OutcomeSold = 1;
        internal const int OutcomeCancelled = 2;
        internal const int OutcomeExpired = 3;

        internal static long InsertListing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            int goldAmount,
            int unitPriceCera,
            int durationHours,
            long nowUnix,
            int feePercent = GoldConsignmentFeePolicy.NormalFeePercent)
        {
            // The escrow is the GOLD itself; the 28-byte consignment body carries no
            // unit price, so a zero price is a legitimate "no cera price" listing and
            // must not block the insert. Only negative values and overflow are invalid.
            if (goldAmount <= 0 || unitPriceCera < 0) return 0;
            // 一口价 IS the total cera for the whole lot, exactly as the player typed it
            // ("1000 点券" for this lot). Multiplying by gold blocks inflated the price
            // 100x in the listing board (1000 -> 100,000), which is wrong.
            var total = (long)unitPriceCera;
            if (total < 0 || total > int.MaxValue) return 0;
            var expires = nowUnix + durationHours * 3600L;

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
INSERT INTO gold_consignment_listings (
    seller_character_id, seller_account_id, gold_amount, unit_price_cera,
    total_price_cera, fee_percent, duration_hours, state, created_at_unix, expires_at_unix
) VALUES (@cid, @aid, @gold, @unit, @total, @fee, @hours, @state, @now, @exp);
SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@cid", characterId);
            cmd.Parameters.AddWithValue("@aid", accountId);
            cmd.Parameters.AddWithValue("@gold", goldAmount);
            cmd.Parameters.AddWithValue("@unit", unitPriceCera);
            cmd.Parameters.AddWithValue("@total", total);
            cmd.Parameters.AddWithValue("@fee", Math.Clamp(feePercent, 0, 100));
            cmd.Parameters.AddWithValue("@hours", durationHours);
            cmd.Parameters.AddWithValue("@state", StateActive);
            cmd.Parameters.AddWithValue("@now", nowUnix);
            cmd.Parameters.AddWithValue("@exp", expires);
            var result = cmd.ExecuteScalar();
            return result == null ? 0 : Convert.ToInt64(result);
        }

        internal static bool TryLoadActive(
            SqliteConnection connection,
            int characterId,
            out List<GoldConsignmentListing> listings)
        {
            listings = new List<GoldConsignmentListing>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT listing_id, gold_amount, unit_price_cera, total_price_cera,
       duration_hours, created_at_unix, expires_at_unix
FROM gold_consignment_listings
WHERE seller_character_id = @cid AND state = @state
ORDER BY listing_id;";
            cmd.Parameters.AddWithValue("@cid", characterId);
            cmd.Parameters.AddWithValue("@state", StateActive);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                listings.Add(new GoldConsignmentListing(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6)));
            }
            return true;
        }

        internal static int CountActiveListings(SqliteConnection connection, int characterId)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM gold_consignment_listings "
                + "WHERE seller_character_id = @cid AND state = @state;";
            cmd.Parameters.AddWithValue("@cid", characterId);
            cmd.Parameters.AddWithValue("@state", StateActive);
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
        }

        /// <summary>
        /// Marks a listing sold and pays the seller's 代币券 via a system-mail attachment,
        /// all inside the caller's transaction. Returns false when the listing is not
        /// active (an earlier settlement already consumed it), so the caller rolls back.
        ///
        /// <paramref name="database"/> is only used to construct the mailbox service; the
        /// actual writes go through <paramref name="connection"/>/<paramref name="transaction"/>.
        /// </summary>
        internal static bool TrySettleSold(
            Infrastructure.IGameDatabase database,
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int buyerCharacterId,
            int buyerAccountId,
            long nowUnix,
            out int sellerCharacterId,
            out int sellerAccountId,
            out int goldAmount,
            out int tokenCeraPaid)
        {
            sellerCharacterId = 0;
            sellerAccountId = 0;
            goldAmount = 0;
            tokenCeraPaid = 0;
            var soldPriceCera = 0;

            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = @"
SELECT seller_character_id, seller_account_id, gold_amount, total_price_cera,
       COALESCE(fee_percent, 5)
FROM gold_consignment_listings
WHERE listing_id = @id AND state = @state;";
                read.Parameters.AddWithValue("@id", listingId);
                read.Parameters.AddWithValue("@state", StateActive);
                using var reader = read.ExecuteReader();
                if (!reader.Read()) return false;
                sellerCharacterId = reader.GetInt32(0);
                sellerAccountId = reader.GetInt32(1);
                goldAmount = reader.GetInt32(2);
                soldPriceCera = reader.GetInt32(3);
                // The official fee tier was locked onto the listing at listing time,
                // so the payout must use it rather than today's average price.
                tokenCeraPaid = GoldConsignmentFeePolicy.NetProceeds(
                    soldPriceCera, reader.GetInt32(4));
                reader.Close();
            }

            using (var upd = connection.CreateCommand())
            {
                upd.Transaction = transaction;
                upd.CommandText = @"
UPDATE gold_consignment_listings
SET state = @sold, settled_at_unix = @now,
    buyer_character_id = @bcid, buyer_account_id = @baid
WHERE listing_id = @id AND state = @active;";
                upd.Parameters.AddWithValue("@sold", StateSold);
                upd.Parameters.AddWithValue("@now", nowUnix);
                upd.Parameters.AddWithValue("@bcid", buyerCharacterId);
                upd.Parameters.AddWithValue("@baid", buyerAccountId);
                upd.Parameters.AddWithValue("@id", listingId);
                upd.Parameters.AddWithValue("@active", StateActive);
                if (upd.ExecuteNonQuery() != 1) return false;
            }

            // The seller is paid in 代币券, delivered as a CLAIMABLE mail attachment.
            //
            // The voucher item id (2681762, PVF trade_point.stk) is routed by
            // SpecialRewardRouter into the account currency accounts.token_cera, so
            // claiming the attachment converts it instead of leaving a dead bag item.
            // That also matches the client, which special-cases 0x28EBA2 at 0x0117E503
            // and reports the gain from its own currency object (NOT from the item count:
            // attachments of 1/7/12345 all displayed "获得0代币券" before this routing).
            //
            // The currency is credited at CLAIM time, not here, so nothing double-pays.
            var sellerNameBytes = LoadCharacterNameBytes(connection, sellerCharacterId);
            var sellerName = sellerNameBytes.Length == 0
                ? string.Empty
                : System.Text.Encoding.GetEncoding(936).GetString(sellerNameBytes);
            var mail = new Mailbox.MailboxSendRequest
            {
                SenderCharacterId = sellerCharacterId,
                SenderAccountId = sellerAccountId,
                SenderName = "金币寄售",
                ReceiverCharacterId = sellerCharacterId,
                ReceiverAccountId = sellerAccountId,
                ReceiverName = sellerName,
                Title = "金币寄售",
                // 正文严格照官方成交邮件排版。官方实例：
                //   - 已成功拍卖[100万] -
                //   成交价：+ 5111 代币券
                //   手续费：- 4855 代币券
                //   拍卖[100万]后，您获得了256代币券。
                // 客户端据「您获得了N代币券」这一句解析数量，自由发挥会导致弹窗显示 0。
                Text = BuildSoldMailText(
                    goldAmount, soldPriceCera, soldPriceCera - tokenCeraPaid, tokenCeraPaid),
                // 邮件窗口底部「代币券 N」那一行读的是邮件的 gold 字段（实机确认：
                // gold=2432 时底部显示 2432）。这里填税后实得，仅用于显示；
                // 领取时 MailboxRepository 会把带账户货币凭证的邮件的 gold 视为纯显示，
                // 不再重复发放金币，真实入账走附件凭证 → accounts.token_cera。
                Gold = tokenCeraPaid,
                // 官方邮件类型：MailType=0 会被客户端显示成「玩家邮件」，与实际不符。
                MailType = 1,
                Unlimited = true,
                AuditActor = "gold-consignment",
                AuditReason = "sold",
                IdempotencyKey = $"gold-consign-sold:{listingId}",
                Attachments = new[]
                {
                    new Mailbox.MailboxSendAttachmentRequest
                    {
                        ItemType = 0,
                        ItemId = (int)GoldConsignmentWire.CeraItemId,
                        ItemCount = tokenCeraPaid,
                        ItemCoreData = BuildCeraCore(
                            (int)GoldConsignmentWire.CeraItemId, tokenCeraPaid)
                    }
                }
            };
            var send = new Mailbox.MailboxService(new Mailbox.MailboxRepository(database))
                .SendSystemMails(connection, transaction, new[] { mail });
            if (send == null || !send.Success)
            {
                // Roll the whole settlement back: no sale without its payout notice.
                throw new InvalidOperationException(
                    $"consignment sale mail failed for listing {listingId}");
            }

            InsertSettlement(connection, transaction, listingId, OutcomeSold,
                sellerCharacterId, sellerAccountId, goldAmount, tokenCeraPaid, nowUnix);
            return true;
        }

        /// <summary>
        /// 官方成交邮件正文。面板按固定句式排版，客户端据最后一句
        /// 「您获得了N代币券」解析数量，所以字段顺序和空格都不能改。
        /// </summary>
        internal static string BuildSoldMailText(int goldAmount, int priceCera, int feeCera, int netCera)
        {
            var label = GoldLadder.TryGetLabel(goldAmount, out var resolved)
                ? resolved
                : goldAmount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"- 已成功拍卖[{label}] -\n"
                + $"成交价：+ {priceCera} 代币券\n"
                + $"手续费：- {feeCera} 代币券\n"
                + $"拍卖[{label}]后，您获得了{netCera}代币券。";
        }

        /// <summary>
        /// 99-byte item core for the 代币券 voucher attachment.
        ///
        /// The kind MUST agree with the catalog: 2681762 (cash/chn_Trade_Gold/trade_point.stk)
        /// resolves to KindMaterial (3). A mismatched kind made the client refuse the item.
        /// The claim then routes this voucher into accounts.token_cera.
        /// </summary>
        private static byte[] BuildCeraCore(int itemId, int count)
        {
            var kind = ItemMetadataResolver.TryResolveItemKind(itemId, out var resolved)
                ? resolved
                : ItemCore.KindMaterial;
            var core = new ItemCore
            {
                ItemKind = kind,
                ItemId = itemId,
                Value = count
            };
            return core.ToBytes();
        }

        internal static bool TrySettleCancelledOrExpired(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long listingId,
            int outcome,
            long nowUnix,
            out int sellerCharacterId,
            out int goldAmount)
        {
            sellerCharacterId = 0;
            goldAmount = 0;
            int sellerAccountId;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = @"
SELECT seller_character_id, seller_account_id, gold_amount
FROM gold_consignment_listings
WHERE listing_id = @id AND state = @state;";
                read.Parameters.AddWithValue("@id", listingId);
                read.Parameters.AddWithValue("@state", StateActive);
                using var reader = read.ExecuteReader();
                if (!reader.Read()) return false;
                sellerCharacterId = reader.GetInt32(0);
                sellerAccountId = reader.GetInt32(1);
                goldAmount = reader.GetInt32(2);
                reader.Close();
            }

            using (var upd = connection.CreateCommand())
            {
                upd.Transaction = transaction;
                upd.CommandText = @"
UPDATE gold_consignment_listings
SET state = @next, settled_at_unix = @now
WHERE listing_id = @id AND state = @active;";
                upd.Parameters.AddWithValue("@next", outcome == OutcomeCancelled ? StateCancelled : StateExpired);
                upd.Parameters.AddWithValue("@now", nowUnix);
                upd.Parameters.AddWithValue("@id", listingId);
                upd.Parameters.AddWithValue("@active", StateActive);
                if (upd.ExecuteNonQuery() != 1) return false;
            }

            InsertSettlement(connection, transaction, listingId, outcome,
                sellerCharacterId, sellerAccountId, goldAmount, 0, nowUnix);

            // NOTE: the gold refund is NOT written here. Gold lives in the online
            // inventory model (character_inventory_items virtual slot 0), so the
            // service layer credits the lease wallet and lets the shared commit
            // coordinator persist it together with this state change — exactly the
            // mirror of Place(), which deducts from the lease and then commits.
            // Writing gold directly here would drift from the online model and be
            // overwritten by the coordinator's dirty-inventory save.
            return true;
        }

        /// <summary>
        /// Character name as raw bytes. The <c>characters.name</c> column is a BLOB
        /// holding the client's own encoded bytes, so it must NOT be round-tripped
        /// through a string: Convert.ToString on a byte[] yields "System.Byte[]",
        /// which is 13+ characters and breaks the 13-byte seller tag.
        /// </summary>
        internal static byte[] LoadCharacterNameBytes(SqliteConnection connection, int characterId)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM characters WHERE character_id = @cid;";
            cmd.Parameters.AddWithValue("@cid", characterId);
            var v = cmd.ExecuteScalar();
            if (v == null || v == DBNull.Value) return Array.Empty<byte>();
            return v as byte[] ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Every active listing on the board, newest first, for the "金币搜索" tab.
        /// Unlike <see cref="TryLoadActive"/> this is not scoped to one seller: other
        /// players browse these rows to buy.
        ///
        /// <paramref name="ladderItemId"/> is the dropdown step the searcher picked; the
        /// client sends it at body[10..14) and shows only that step. Passing 0 (unknown)
        /// returns the whole board rather than silently nothing.
        /// </summary>
        internal static List<GoldConsignmentBoardRow> LoadBoard(
            SqliteConnection connection, long nowUnix, uint ladderItemId = 0, int limit = 60)
        {
            var rows = new List<GoldConsignmentBoardRow>();
            // gold_amount is what the filter compares against: the ladder maps 1:1 to an
            // amount, so filtering on the amount is equivalent to filtering on the item id
            // and avoids storing the id twice.
            var goldFilter = ladderItemId == 0 ? 0 : GoldLadder.GoldForItem(ladderItemId);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT l.listing_id, l.seller_character_id, l.gold_amount, l.unit_price_cera,
       l.total_price_cera, l.duration_hours, l.created_at_unix, l.expires_at_unix,
       c.name
FROM gold_consignment_listings l
LEFT JOIN characters c ON c.character_id = l.seller_character_id
WHERE l.state = @active AND l.expires_at_unix > @now
  AND (@goldFilter = 0 OR l.gold_amount = @goldFilter)
ORDER BY l.listing_id DESC
LIMIT @limit;";
            cmd.Parameters.AddWithValue("@active", StateActive);
            cmd.Parameters.AddWithValue("@now", nowUnix);
            cmd.Parameters.AddWithValue("@goldFilter", goldFilter);
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new GoldConsignmentBoardRow(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.IsDBNull(8) ? Array.Empty<byte>() : reader.GetFieldValue<byte[]>(8)));
            }
            return rows;
        }

        /// <summary>
        /// Pre-check for a purchase: the active listing's price, seller and gold amount.
        /// Returns false when the row is not an active listing.
        /// </summary>
        internal static bool TryLoadPurchaseTarget(
            SqliteConnection connection,
            long listingId,
            out long priceCera,
            out int sellerCharacterId,
            out int goldAmount)
        {
            priceCera = 0;
            sellerCharacterId = 0;
            goldAmount = 0;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT total_price_cera, seller_character_id, gold_amount
FROM gold_consignment_listings
WHERE listing_id = @id AND state = @active;";
            cmd.Parameters.AddWithValue("@id", listingId);
            cmd.Parameters.AddWithValue("@active", StateActive);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return false;
            priceCera = reader.GetInt32(0);
            sellerCharacterId = reader.GetInt32(1);
            goldAmount = reader.GetInt32(2);
            return true;
        }

        /// <summary>
        /// 近期平均成交价 for one gold amount, the "平均价格" the official fee tiers and
        /// the 0x00B6 price reference are based on.
        ///
        /// Official rule text: 玩家选定金币值后系统显示近期交易价格作为参考. The reference
        /// is therefore per ladder step, not one global number.
        /// Returns 0 when there is no history inside the window.
        /// </summary>
        internal static int LoadAveragePriceCera(
            SqliteConnection connection, int goldAmount, long nowUnix, int windowDays = 7)
        {
            if (goldAmount <= 0) return 0;
            var since = nowUnix - windowDays * 86400L;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT AVG(total_price_cera)
FROM gold_consignment_listings
WHERE gold_amount = @gold AND state = @sold AND settled_at_unix >= @since;";
            cmd.Parameters.AddWithValue("@gold", goldAmount);
            cmd.Parameters.AddWithValue("@sold", StateSold);
            cmd.Parameters.AddWithValue("@since", since);
            var raw = cmd.ExecuteScalar();
            if (raw == null || raw == DBNull.Value) return 0;
            var avg = Convert.ToDouble(raw);
            if (avg <= 0) return 0;
            return avg >= int.MaxValue ? int.MaxValue : (int)Math.Round(avg);
        }

        /// <summary>
        /// Escrowed gold of an active listing that belongs to
        /// <paramref name="characterId"/>, or 0 when it is missing, already settled, or
        /// owned by someone else. The ownership check is what stops a player cancelling
        /// another player's consignment.
        /// </summary>
        internal static long LoadRefundAmount(
            SqliteConnection connection, long listingId, int characterId)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT gold_amount FROM gold_consignment_listings
WHERE listing_id = @id AND state = @active AND seller_character_id = @cid;";
            cmd.Parameters.AddWithValue("@id", listingId);
            cmd.Parameters.AddWithValue("@active", StateActive);
            cmd.Parameters.AddWithValue("@cid", characterId);
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt64(v);
        }

        /// <summary>
        /// Escrowed gold of an active listing, or 0 when it is no longer active.
        /// The caller credits this onto the online wallet before settling, so the
        /// refund and the state change commit together.
        /// </summary>
        internal static long LoadRefundAmount(SqliteConnection connection, long listingId)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT gold_amount FROM gold_consignment_listings
WHERE listing_id = @id AND state = @active;";
            cmd.Parameters.AddWithValue("@id", listingId);
            cmd.Parameters.AddWithValue("@active", StateActive);
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt64(v);
        }

        /// <summary>
        /// Listing ids that are still active but past their expiry. Callers settle
        /// each one through <see cref="TrySettleCancelledOrExpired"/> inside their
        /// own transaction so the gold refund and the state change stay atomic.
        /// </summary>
        /// <summary>
        /// Active listings whose expiry has passed, for ONE seller.
        ///
        /// The seller filter is mandatory: the refund is credited to the caller's
        /// online wallet, so returning every player's expired rows would pay their gold
        /// to whoever happened to be logging in.
        /// </summary>
        internal static List<long> LoadExpiredActiveIds(
            SqliteConnection connection, int sellerCharacterId, long nowUnix, int limit = 200)
        {
            var ids = new List<long>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT listing_id
FROM gold_consignment_listings
WHERE state = @active AND expires_at_unix <= @now AND seller_character_id = @cid
ORDER BY expires_at_unix, listing_id
LIMIT @limit;";
            cmd.Parameters.AddWithValue("@active", StateActive);
            cmd.Parameters.AddWithValue("@now", nowUnix);
            cmd.Parameters.AddWithValue("@cid", sellerCharacterId);
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            return ids;
        }

        private static void InsertSettlement(
            SqliteConnection connection, SqliteTransaction transaction, long listingId,
            int outcome, int sellerCharacterId, int sellerAccountId, int goldAmount,
            int tokenCeraPaid, long nowUnix)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
INSERT OR IGNORE INTO gold_consignment_settlements (
    listing_id, outcome, seller_character_id, seller_account_id,
    gold_amount, token_cera_paid, created_at_unix
) VALUES (@id, @outcome, @cid, @aid, @gold, @paid, @now);";
            cmd.Parameters.AddWithValue("@id", listingId);
            cmd.Parameters.AddWithValue("@outcome", outcome);
            cmd.Parameters.AddWithValue("@cid", sellerCharacterId);
            cmd.Parameters.AddWithValue("@aid", sellerAccountId);
            cmd.Parameters.AddWithValue("@gold", goldAmount);
            cmd.Parameters.AddWithValue("@paid", tokenCeraPaid);
            cmd.Parameters.AddWithValue("@now", nowUnix);
            cmd.ExecuteNonQuery();
        }
    }

    internal sealed record GoldConsignmentListing(long ListingId, int GoldAmount,
        int UnitPriceCera, int TotalPriceCera, int DurationHours,
        long CreatedAtUnix, long ExpiresAtUnix);

    /// <summary>
    /// One row of the public gold board ("金币搜索"): a listing plus its seller's
    /// display name bytes, so other players can browse and buy it.
    /// </summary>
    internal sealed record GoldConsignmentBoardRow(
        long ListingId, int SellerCharacterId, int GoldAmount, int UnitPriceCera,
        int TotalPriceCera, int DurationHours, long CreatedAtUnix, long ExpiresAtUnix,
        byte[] SellerName);
}
