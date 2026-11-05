using DfoServer.Game.Auction;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Network.Parsers.Auction;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    /// <summary>
    /// Gold consignment (金币寄售) self-test.
    ///
    /// Covers, in order:
    ///   * parser recognises the captured 28-byte 0x00B7 body and the 6-byte 0x00B6 body
    ///   * the ordinary 37-byte item register still parses as an item auction request
    ///   * provisional policy validation (min/block/price/overflow)
    ///   * listing insert + active-row accounting
    ///   * duplicate settlement is rejected (idempotency)
    ///   * sold settlement credits the seller's token cera exactly once
    ///   * cancel/expire release the escrow row and pay no token cera
    ///   * migration v42 creates both tables on a fresh database
    /// </summary>
    public static class GoldConsignmentSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== GOLD_CONSIGN selftest ===");
            var failures = 0;
            var tempDbPath = Path.Combine(
                Path.GetTempPath(), $"dfo_gold_consign_{Guid.NewGuid():N}.db");

            try
            {
                var database = new Infrastructure.GameDatabase(
                    tempDbPath, Infrastructure.ServerPaths.SchemaFilePath);

                Check("migration v42 creates gold consignment tables",
                    TableExists(database, "gold_consignment_listings")
                    && TableExists(database, "gold_consignment_settlements")
                    && CurrentVersion(database) >= 42,
                    ref failures);

                CheckParser(ref failures);

                Check("validate rejects unset gold",
                    GoldConsignment.ValidateOrder(0, 1000) == "GoldAmountUnset", ref failures);
                // Amounts outside the PVF ladder are rejected: the ladder is the only
                // legal source of a gold amount.
                Check("validate rejects below the ladder minimum",
                    GoldConsignment.ValidateOrder(999_999, 1000) == "GoldBelowMinimum",
                    ref failures);
                Check("validate rejects above the ladder maximum",
                    GoldConsignment.ValidateOrder(200_000_001, 1000) == "GoldAboveMaximum",
                    ref failures);
                Check("validate accepts the ladder minimum (1百万)",
                    GoldConsignment.ValidateOrder(1_000_000, 1000) == null, ref failures);
                Check("validate accepts the ladder maximum (2亿)",
                    GoldConsignment.ValidateOrder(200_000_000, 1000) == null, ref failures);
                Check("validate accepts a mid ladder step (1千万)",
                    GoldConsignment.ValidateOrder(10_000_000, 1000) == null, ref failures);
                Check("validate rejects a negative price",
                    GoldConsignment.ValidateOrder(1_000_000, -1) == "PriceNegative",
                    ref failures);

                // The gold step is the LADDER ITEM ID from PVF
                // stackable/cash/chn_trade_gold, sent at body[4..8). No exchange rate.
                Check("ladder 1百万 item maps to 1000000 gold",
                    GoldLadder.GoldForItem(2681725) == 1_000_000, ref failures);
                Check("ladder 2百万 item maps to 2000000 gold",
                    GoldLadder.GoldForItem(2681726) == 2_000_000, ref failures);
                Check("ladder 1千万 item maps to 10000000 gold",
                    GoldLadder.GoldForItem(2681734) == 10_000_000, ref failures);
                Check("ladder 2亿 item maps to 200000000 gold",
                    GoldLadder.GoldForItem(2683073) == 200_000_000, ref failures);
                Check("ladder has exactly 17 steps", GoldLadder.ItemIds.Count == 17, ref failures);
                Check("unknown item id yields no gold",
                    GoldLadder.GoldForItem(1) == 0 && !GoldLadder.IsLadderItem(999), ref failures);
                Check("cera item id matches PVF (2681762)",
                    GoldConsignmentWire.CeraItemId == 2681762, ref failures);
                // Round-trip: each ladder amount must map back to its item id, which is
                // what the listing/search snapshots now carry instead of itemId 0.
                Check("1百万 maps back to item 2681725",
                    GoldLadder.ItemForGold(1_000_000) == 2681725, ref failures);
                Check("2亿 maps back to item 2683073",
                    GoldLadder.ItemForGold(200_000_000) == 2683073, ref failures);
                Check("non-ladder amount has no item id",
                    GoldLadder.ItemForGold(12_345) == 0, ref failures);
                Check("net proceeds apply the 5% normal tier",
                    GoldConsignment.NetProceeds(1000) == 950, ref failures);

                // ---- official fee tiers (dnf.qq.com 金币寄售系统) ----------------
                // ratio = 一口价 / 平均价; fee by band, coupon only in the normal band.
                const int avg = 1000;
                Check("below 40% of average cannot be listed",
                    !GoldConsignmentFeePolicy.TryResolveFeePercent(399, avg, false, out _),
                    ref failures);
                Check("exactly 40% is listable at 5%",
                    GoldConsignmentFeePolicy.TryResolveFeePercent(400, avg, false, out var f400)
                    && f400 == 5, ref failures);
                Check("40%-150% band uses 5%",
                    GoldConsignmentFeePolicy.TryResolveFeePercent(1500, avg, false, out var f150)
                    && f150 == 5, ref failures);
                Check("coupon lowers the normal band to 1%",
                    GoldConsignmentFeePolicy.TryResolveFeePercent(1000, avg, true, out var fCoupon)
                    && fCoupon == 1, ref failures);
                Check("150%-160% band uses 30% and ignores the coupon",
                    GoldConsignmentFeePolicy.TryResolveFeePercent(1550, avg, true, out var f155)
                    && f155 == 30, ref failures);
                Check("160%-170% band uses 40%",
                    GoldConsignmentFeePolicy.TryResolveFeePercent(1650, avg, false, out var f165)
                    && f165 == 40, ref failures);
                Check("170%-180% band uses 50%",
                    GoldConsignmentFeePolicy.TryResolveFeePercent(1750, avg, false, out var f175)
                    && f175 == 50, ref failures);
                Check("above 180% uses 95%",
                    GoldConsignmentFeePolicy.TryResolveFeePercent(1801, avg, false, out var f181)
                    && f181 == 95, ref failures);
                Check("net proceeds follow the locked tier",
                    GoldConsignmentFeePolicy.NetProceeds(1000, 5) == 950
                    && GoldConsignmentFeePolicy.NetProceeds(1000, 95) == 50
                    && GoldConsignmentFeePolicy.NetProceeds(1000, 1) == 990, ref failures);

                // ---- 代币券 voucher must become account currency on claim ----------
                Check("代币券 voucher routes to the account currency",
                    SpecialRewardRouter.TryResolveAccountCurrencyReward(
                        2681762, 950, out var ceraOutcome)
                    && ceraOutcome.Kind == SpecialRewardKind.TokenCera
                    && ceraOutcome.Count == 950, ref failures);
                Check("欢乐代币券 still routes to happy token cera",
                    SpecialRewardRouter.TryResolveAccountCurrencyReward(
                        2681917, 5, out var happyOutcome)
                    && happyOutcome.Kind == SpecialRewardKind.HappyTokenCera, ref failures);
                Check("an ordinary item is not routed as account currency",
                    !SpecialRewardRouter.TryResolveAccountCurrencyReward(2683479, 5, out _),
                    ref failures);

                // The 0x00BC consignment listing reply must match what native ACK
                // 0x0111FC90 consumes: flag | selector | count | count * 151 bytes.
                var tag = new byte[13];
                tag[2] = (byte)'A';
                var twoRows = new List<GoldConsignmentListing>
                {
                    new(1, 1_000_000, 1000, 100000, 24, 0, 0),
                    new(2, 2_000_000, 2000, 200000, 48, 0, 0)
                };
                var listBody = Network.Builders.Auction.GoldConsignmentListBuilder.TryBuild(
                    twoRows, tag, _ =>
                    {
                        var n = new byte[83];
                        return (n, new byte[30], new byte[4]);
                    });
                Check("consignment listing body is non-null", listBody != null, ref failures);
                Check("listing body length = 3 + count*151",
                    listBody != null && listBody.Length == 3 + 2 * 151, ref failures);
                Check("listing body flag and selector",
                    listBody != null && listBody[0] == 1 && listBody[1] == 1, ref failures);
                Check("listing body carries the row count",
                    listBody != null && listBody[2] == 2, ref failures);
                // Buyout-only: record[8..12) is the current/minimum bid and must be 0,
                // otherwise the client renders "当前竞拍" next to the buyout price.
                Check("listing record carries the no-bid sentinel (buyout-only)",
                    listBody != null
                    && BitConverter.ToUInt32(listBody, 3 + 8)
                       == Network.Builders.Auction.GoldConsignmentSearchBuilder.NoBidSentinel,
                    ref failures);
                Check("listing record carries the buyout total",
                    listBody != null
                    && BitConverter.ToUInt32(listBody, 3 + 12) == 100000, ref failures);

                // Result codes must match the in-game verified prompt table
                // (Docs/拍卖行与金币寄售结果码对照表.md).
                Check("insufficient gold uses the verified prompt code 0x90",
                    Network.Builders.Auction.AuctionMutationReplyBuilder
                        .InsufficientGoldFailure == 0x90, ref failures);
                Check("invalid price uses 0x98",
                    Network.Builders.Auction.AuctionMutationReplyBuilder
                        .InvalidPriceFailure == 0x98, ref failures);
                Check("listing cap uses 0x97",
                    Network.Builders.Auction.AuctionMutationReplyBuilder
                        .ListingCapFailure == 0x97, ref failures);
                Check("throughput uses 0x9E",
                    Network.Builders.Auction.AuctionMutationReplyBuilder
                        .ThroughputFailure == 0x9E, ref failures);
                // A prompt code must be emitted for an insufficient-gold rejection.
                var rej = Network.Builders.Auction.AuctionMutationReplyBuilder
                    .ConsignmentRejection(
                        Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                        Network.Builders.Auction.AuctionMutationReplyBuilder
                            .InsufficientGoldFailure);
                Check("consignment rejection carries the prompt code",
                    rej.Length == 3 && rej[0] == 0 && rej[1] == 0x90, ref failures);

                // 0x00BA 金币搜索: other players must see consigned gold.
                // body = 8-byte header + N*168 records.
                var boardRows = new List<GoldConsignmentBoardRow>
                {
                    new(1, 77102, 1_000_000, 1000, 1000, 24, 0, 0,
                        System.Text.Encoding.ASCII.GetBytes("Test01")),
                    new(2, 77102, 2_000_000, 2000, 2000, 48, 0, 0,
                        System.Text.Encoding.ASCII.GetBytes("Test01"))
                };
                var searchBody = Network.Builders.Auction.GoldConsignmentSearchBuilder.TryBuild(
                    boardRows, _ => (new byte[83], new byte[30], new byte[4]));
                Check("gold search body is non-null", searchBody != null, ref failures);
                Check("gold search body = 8 + count*168",
                    searchBody != null && searchBody.Length == 8 + 2 * 168, ref failures);
                Check("gold search flag/total/count",
                    searchBody != null && searchBody[0] == 1
                    && BitConverter.ToUInt32(searchBody, 2) == 2
                    && BitConverter.ToUInt16(searchBody, 6) == 2, ref failures);
                // body[1] must be 1 so the ACK routes rows to the consignment window;
                // 0 sends them to the (closed) item-auction window and shows nothing.
                Check("gold search selects the consignment window",
                    searchBody != null && searchBody[1] == 1, ref failures);
                Check("gold search record buyout at [13..17)",
                    searchBody != null
                    && BitConverter.ToUInt32(searchBody, 8 + 13) == 1000, ref failures);
                Check("gold search record carries the no-bid sentinel",
                    searchBody != null
                    && BitConverter.ToUInt32(searchBody, 8 + 9)
                       == Network.Builders.Auction.GoldConsignmentSearchBuilder.NoBidSentinel,
                    ref failures);
                // The seller tag must start at record offset 21 and decode: name at
                // byte 0, NUL terminator at index >= 1, zero tail.
                Check("gold search seller tag decodes",
                    searchBody != null
                    && Network.Builders.Auction.AuctionSellerNameTag13.TryDecode(
                        searchBody.AsSpan(8 + 21, 13), out var decodedName)
                    && decodedName == "Test01", ref failures);
                Check("seller tag puts the name at offset 0",
                    Network.Builders.Auction.GoldConsignmentSearchBuilder
                        .SellerTag(System.Text.Encoding.ASCII.GetBytes("Test01"))[0] == (byte)'T',
                    ref failures);
                Check("seller tag rejects an over-long name",
                    Network.Builders.Auction.GoldConsignmentSearchBuilder
                        .SellerTag(System.Text.Encoding.ASCII.GetBytes("Abcdefghijklm")).Length == 0,
                    ref failures);
                // The 117-byte snapshot must place Attr at [5] and the count at [6..10),
                // matching AuctionNativeDisplayProjection. The client drops a row whose
                // count reads 0, so a wrong offset here silently empties the board.
                var snap = Network.Builders.Auction.GoldConsignmentSearchBuilder
                    .TryBuild(
                        new List<GoldConsignmentBoardRow>
                        {
                            new(1, 1, 1_000_000, 1000, 1000, 24, 0, 0,
                                System.Text.Encoding.ASCII.GetBytes("Test01"))
                        },
                        _ =>
                        {
                            var n = new byte[83];
                            var g = 1_000_000;
                            BitConverter.GetBytes(0).CopyTo(n, 1);
                            n[5] = 0;
                            BitConverter.GetBytes(g).CopyTo(n, 6);
                            return (n, new byte[30], new byte[4]);
                        });
                // record offset: 8 (header) + 43 (item block) + 6 = count position
                Check("snapshot writes the count at native[6..10)",
                    snap != null
                    && BitConverter.ToInt32(snap, 8 + 43 + 6) == 1_000_000, ref failures);
                Check("snapshot keeps Attr at native[5]",
                    snap != null && snap[8 + 43 + 5] == 0, ref failures);
                Check("empty board yields no search body",
                    Network.Builders.Auction.GoldConsignmentSearchBuilder.TryBuild(
                        new List<GoldConsignmentBoardRow>(),
                        _ => (new byte[83], new byte[30], new byte[4])) == null, ref failures);
                Check("empty listing set yields no body",
                    Network.Builders.Auction.GoldConsignmentListBuilder.TryBuild(
                        new List<GoldConsignmentListing>(), tag, _ =>
                            (new byte[83], new byte[30], new byte[4])) == null, ref failures);
                Check("wrong-width seller tag yields no body",
                    Network.Builders.Auction.GoldConsignmentListBuilder.TryBuild(
                        twoRows, new byte[5], _ =>
                            (new byte[83], new byte[30], new byte[4])) == null, ref failures);

                const int accountId = 77101;
                const int sellerId = 77102;
                SeedAccount(database, accountId);
                SeedCharacter(database, sellerId, accountId, "gold-consign-seller");

                const long now = 1_800_000_000L;
                long listingId;
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    listingId = GoldConsignmentRepository.InsertListing(
                        connection, transaction, sellerId, accountId,
                        goldAmount: 100_000, unitPriceCera: 1000,
                        durationHours: 24, nowUnix: now);
                    transaction.Commit();
                }
                Check("listing insert returns an id", listingId > 0, ref failures);

                Check("active listing counted for seller",
                    CountActive(database, sellerId) == 1, ref failures);

                // --- sold settlement pays 代币券 into the ACCOUNT currency -----------
                var sold = SettleSold(database, listingId, buyerCharacter: 77103, buyerAccount: 77104, now + 10);
                Check("sold settlement succeeds", sold, ref failures);
                // 代币券 is accounts.token_cera, the wallet NOTIPACKET_CERA (0x0035) reports.
                // 一口价 is the total cera for the whole lot, so a 1000-cera listing
                // pays exactly 1000 (no per-block multiplication).
                // The voucher is credited only when the seller CLAIMS the mail, so the
                // wallet must still be untouched right after settlement.
                Check("sold settlement does not credit the wallet before claim",
                    ReadTokenCera(database, sellerId) == 0, ref failures);
                // The sale mail is the visible record only; the value is already credited,
                // so it must carry no attachment (an attachment would double-pay and the
                // client shows a bogus "获得0代币券" prompt for voucher items).
                var (mailCount, mailItemId, mailAttachCount) = ReadSoldMail(database, sellerId);
                Check("sold settlement sends the seller one mail", mailCount == 1, ref failures);
                Check("sale mail carries the 代币券 voucher for the net proceeds",
                    mailItemId == (int)GoldConsignmentWire.CeraItemId
                    && mailAttachCount == 950, ref failures);
                // 邮件窗口底部「代币券 N」读的是 mails.gold，实机确认填它才显示正确。
                // 该值仅用于显示：带账户货币凭证的邮件领取时不会再发金币。
                Check("sale mail gold mirrors the net proceeds for display",
                    ReadSoldMailGold(database, sellerId) == 950, ref failures);

                var soldAgain = SettleSold(database, listingId, 77105, 77106, now + 20);
                Check("duplicate sold settlement rejected", !soldAgain, ref failures);
                Check("duplicate settlement does not send a second mail",
                    ReadSoldMail(database, sellerId).mailCount == 1, ref failures);
                Check("settled listing no longer active",
                    CountActive(database, sellerId) == 0, ref failures);
                Check("sold settlement row recorded", SettlementCount(database, listingId) == 1, ref failures);

                // Snapshot the wallet so later cancel/expiry paths can prove they do NOT
                // pay token cera.
                var tokenAfterFirst = ReadTokenCera(database, sellerId);


                // --- cancel pays nothing
                long cancelId;
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    cancelId = GoldConsignmentRepository.InsertListing(
                        connection, transaction, sellerId, accountId,
                        50_000, 800, 24, now);
                    transaction.Commit();
                }
                var cancelled = SettleCancelOrExpire(database, cancelId,
                    GoldConsignmentRepository.OutcomeCancelled, now + 30);
                Check("cancel settlement succeeds", cancelled, ref failures);
                Check("cancel pays no token cera",
                    ReadTokenCera(database, sellerId) == tokenAfterFirst, ref failures);
                Check("cancel settlement row recorded",
                    SettlementCount(database, cancelId) == 1, ref failures);
                // Repository-level expiry: state + ledger only. The gold refund is the
                // service layer's job (it must credit the online wallet and commit it
                // with this state change); that path is asserted in CheckServiceLayer.
                long cancelId2;
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    cancelId2 = GoldConsignmentRepository.InsertListing(
                        connection, transaction, sellerId, accountId,
                        70_000, 0, 24, now);
                    transaction.Commit();
                }
                Check("refund amount readable while active",
                    ReadRefundAmount(database, cancelId2) == 70_000, ref failures);
                var refunded = SettleCancelOrExpire(database, cancelId2,
                    GoldConsignmentRepository.OutcomeExpired, now + 60);
                Check("expiry settlement succeeds", refunded, ref failures);
                Check("expiry pays no token cera",
                    ReadTokenCera(database, sellerId) == tokenAfterFirst, ref failures);
                Check("expiry settlement row recorded",
                    SettlementCount(database, cancelId2) == 1, ref failures);
                Check("refund amount is zero once settled",
                    ReadRefundAmount(database, cancelId2) == 0, ref failures);
                Check("duplicate expiry does not settle twice",
                    !SettleCancelOrExpire(database, cancelId2,
                        GoldConsignmentRepository.OutcomeExpired, now + 70)
                    && SettlementCount(database, cancelId2) == 1, ref failures);

                CheckServiceLayer(database, ref failures);
                CheckDurationAndCap(database, ref failures);

                Console.WriteLine(failures == 0
                    ? "GOLD_CONSIGN selftest passed."
                    : $"GOLD_CONSIGN selftest FAILED ({failures}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] unexpected exception: {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
            finally
            {
                TryDelete(tempDbPath);
            }

            return failures == 0 ? 0 : 1;
        }

        private static void CheckParser(ref int failures)
        {
            // Captured gold register body (28B, C->S, header already stripped).
            var goldRegister = Convert.FromHexString(
                "010000007DEB280001000000FFFFFFFFFFFFFFFF0000000018000000");
            var okRegister = AuctionRequestParser.TryParse(
                Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM, goldRegister, out var parsedRegister);
            Check("28B gold body parses as consignment register", okRegister
                && parsedRegister is GoldConsignRegisterRequest, ref failures);
            if (parsedRegister is GoldConsignRegisterRequest r)
            {
                Check("consignment carries the gold marker",
                    r.Marker == GoldConsignmentWire.GoldMarker, ref failures);
                Check("consignment duration is 24 hours",
                    r.DurationHours == 24, ref failures);
                Check("consignment constant slot is -1",
                    r.ConstantNeg1 == uint.MaxValue, ref failures);
                Check("captured amount field is -1 (input was empty)",
                    r.AmountWire == uint.MaxValue && !r.HasAmount, ref failures);
                Check("consignment quantity is 1",
                    r.Quantity == 1, ref failures);
            }

            // A real listing carries a positive amount at [16..20).
            var withAmount = Convert.FromHexString(
                "010000007DEB280001000000FFFFFFFFC0E1E4000000000018000000");
            Check("15000000 gold body parses with amount",
                AuctionRequestParser.TryParse(
                    Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    withAmount, out var amountParsed)
                && amountParsed is GoldConsignRegisterRequest amt
                && amt.HasAmount && amt.GoldAmount == 15_000_000, ref failures);

            // Captured gold price-reference body (6B).
            var goldPrice = Convert.FromHexString("017DEB280000");
            Check("6B gold body parses as consignment price reference",
                AuctionRequestParser.TryParse(
                    Network.CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE,
                    goldPrice, out var parsedPrice)
                && parsedPrice is GoldConsignAveragePriceRequest, ref failures);

            // Captured cancel body (9B): selector 1 + u64 listing id. The parser stays
            // strict (selector 0 belongs to the ordinary cancel), so the consignment
            // cancel is recognised in the handler by body[0]==1. Verify the captured
            // shape and that the id decodes as expected.
            var goldCancel = Convert.FromHexString("010B00000000000000");
            Check("captured cancel body is 9B with selector 1",
                goldCancel.Length == GoldConsignmentWire.CancelBodyLength
                && goldCancel[0] == 0x01, ref failures);
            Check("captured cancel body carries listing id 11",
                BitConverter.ToUInt64(goldCancel, 1) == 11UL, ref failures);
            // The ordinary auction invariant must remain: selector 1 is NOT accepted as
            // an ordinary cancel by the parser.
            Check("parser still rejects selector 1 for the ordinary cancel",
                !AuctionRequestParser.TryParse(
                    Network.CmdPacketTypeA21.AUCTION_REGIST_CANCEL,
                    goldCancel, out _), ref failures);

            // Captured purchase body (17B): selector | price | listingId | 0 | itemId.
            var goldBuy = Convert.FromHexString("01E803000010000000000000007DEB2800");
            Check("17B gold purchase parses",
                AuctionRequestParser.TryParse(
                    Network.CmdPacketTypeA21.AUCTION_BIDDING,
                    goldBuy, out var parsedBuy)
                && parsedBuy is GoldConsignPurchaseRequest buy, ref failures);
            Check("purchase carries price 1000, listing 16, ladder 2681725",
                AuctionRequestParser.TryParse(
                    Network.CmdPacketTypeA21.AUCTION_BIDDING,
                    goldBuy, out var parsedBuy2)
                && parsedBuy2 is GoldConsignPurchaseRequest buy2
                && buy2.PriceCera == 1000
                && buy2.ListingId == 16
                && buy2.LadderItemId == 2681725, ref failures);

            // Price-reference reply must match the client's 58-byte consumption:
            //   [1] ctrlId selector, [2..6) suggested price, [6..10) unused,
            //   [10..34) six unit prices, [34..58) six quantities.
            var priceBody = Network.Builders.Auction.AuctionOrdinaryBodyBuilder
                .GoldConsignPriceReference(1000);
            Check("price reference body is 58 bytes", priceBody.Length == 58, ref failures);
            Check("price reference carries suggested price at [2..6)",
                BitConverter.ToUInt32(priceBody, 2) == 1000, ref failures);
            Check("price reference selector byte is 0 or 1",
                priceBody[1] <= 1, ref failures);
            Check("price reference first level mirrors the suggestion",
                BitConverter.ToUInt32(priceBody, 10) == 1000, ref failures);

            // Ordinary 37B item register must still parse as the item auction request.
            var itemRegister = new byte[37];
            itemRegister[1] = 0;      // space
            itemRegister[4] = 201;    // itemKey
            itemRegister[8] = 1;      // quantity
            Check("37B item register still parses as item auction",
                AuctionRequestParser.TryParse(
                    Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    itemRegister, out var itemParsed)
                && itemParsed is AuctionRegisterRequest, ref failures);

            // Amount presence semantics: -1 means "input was empty", a positive
            // value is the gold the player typed.
            var amountless = new GoldConsignRegisterRequest(
                GoldConsignmentWire.GoldMarker, 1, uint.MaxValue, uint.MaxValue, 0, 24);
            Check("empty amount (-1) is reported as absent",
                !amountless.HasAmount && amountless.GoldAmount == 0, ref failures);
            var withGold = new GoldConsignRegisterRequest(
                GoldConsignmentWire.GoldMarker, 1, uint.MaxValue, 20_000, 0, 24);
            Check("positive amount is reported as present",
                withGold.HasAmount && withGold.GoldAmount == 20_000, ref failures);
        }

        /// <summary>
        /// Exercises the service layer with a real owned InventoryLease, which the
        /// repository-only checks above cannot cover: gold must actually leave the
        /// wallet and a listing row must appear in the same transaction.
        /// </summary>
        private static void CheckServiceLayer(Infrastructure.GameDatabase database, ref int failures)
        {
            const int accountId = 77201;
            const int characterId = 77202;
            SeedAccount(database, accountId);
            SeedCharacter(database, characterId, accountId, "gold-consign-service");

            var session = Guid.NewGuid();
            var lease = InventoryContext.Register(session,
                new InventoryService(characterId, accountId, database));
            try
            {
                // The ladder minimum is 1百万, so the wallet must hold at least that.
                lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 5_000_000);
                Check("lease wallet starts at 5000000",
                    ReadOnline(lease) == 5_000_000, ref failures);

                var nowUnix = 1_800_000_000L;
                var ok = GoldConsignment.TryPlaceConfirmed(
                    lease, Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    goldAmount: 1_000_000, unitPriceCera: 1000, hours: 24, nowUnix);
                Check("place returns a success ack", ok.Length == 2 && ok[0] == 1,
                    ref failures);
                Check("place deducts exactly the listed gold",
                    ReadOnline(lease) == 4_000_000, ref failures);
                Check("place wrote a listing row for the seller",
                    CountActive(database, characterId) == 1, ref failures);

                // Insufficient gold must not move anything: ask for more than held.
                var before = ReadOnline(lease);
                GoldConsignment.TryPlaceConfirmed(
                    lease, Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    goldAmount: 200_000_000, unitPriceCera: 1000, hours: 24, nowUnix + 1);
                Check("insufficient gold leaves the wallet untouched",
                    ReadOnline(lease) == before, ref failures);
                Check("insufficient gold adds no listing",
                    CountActive(database, characterId) == 1, ref failures);

                // A ladder item id must drive Place; a non-ladder id is refused.
                var emptyBody = new GoldConsignRegisterRequest(
                    GoldConsignmentWire.GoldMarker, 1, uint.MaxValue, uint.MaxValue, 0, 24);
                var emptyReply = GoldConsignment.Place(
                    lease, Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM, emptyBody);
                Check("empty buyout is refused by Place",
                    emptyReply.Length >= 2 && emptyReply[0] == 0, ref failures);
                var bogusLadder = new GoldConsignRegisterRequest(
                    0xDEADBEEF, 1, uint.MaxValue, 5000, 0, 24);
                var bogusReply = GoldConsignment.Place(
                    lease, Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM, bogusLadder);
                Check("unknown ladder item is refused by Place",
                    bogusReply.Length >= 2 && bogusReply[0] == 0, ref failures);
                Check("unknown ladder item does not touch gold",
                    ReadOnline(lease) == before, ref failures);
                Check("unknown ladder item adds no listing",
                    CountActive(database, characterId) == 1, ref failures);

                // ---- cancel (0x00B8) -------------------------------------------
                // The active listing must be cancellable by its owner, refund the gold
                // exactly once, and refuse ids that are not this character's.
                var activeId = FirstActiveListingId(database, characterId);
                Check("cancel refuses a foreign listing id",
                    GoldConsignment.TryCancel(lease,
                        Network.CmdPacketTypeA21.AUCTION_REGIST_CANCEL,
                        activeId + 9999) == null, ref failures);
                var beforeCancel = ReadOnline(lease);
                var cancelBody = GoldConsignment.TryCancel(lease,
                    Network.CmdPacketTypeA21.AUCTION_REGIST_CANCEL, activeId);
                Check("cancel returns a success ack",
                    cancelBody != null && cancelBody.Length == 2
                    && cancelBody[0] == 1 && cancelBody[1] == 1, ref failures);
                Check("cancel refunds the escrowed gold",
                    ReadOnline(lease) == beforeCancel + 1_000_000, ref failures);
                Check("cancel removes the listing",
                    CountActive(database, characterId) == 0, ref failures);
                var afterCancel = ReadOnline(lease);
                Check("second cancel is refused and pays nothing",
                    GoldConsignment.TryCancel(lease,
                        Network.CmdPacketTypeA21.AUCTION_REGIST_CANCEL, activeId) == null
                    && ReadOnline(lease) == afterCancel, ref failures);

                // Re-list so the expiry test below has a live row to settle.
                GoldConsignment.TryPlaceConfirmed(lease,
                    Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    goldAmount: 1_000_000, unitPriceCera: 1000, hours: 24, nowUnix);

                // ---- purchase (0x00B9): buyer pays 点券, seller receives 代币券 -----
                // Uses a separate seller character: self-purchase is refused (0x9B).
                const int otherAccountId = 77211;
                const int otherSellerId = 77212;
                SeedAccount(database, otherAccountId);
                SeedCharacter(database, otherSellerId, otherAccountId, "gold-consign-other");
                // The buyer needs 点券; SeedAccount defaults it to 0.
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    using var fund = connection.CreateCommand();
                    fund.Transaction = transaction;
                    fund.CommandText =
                        "UPDATE accounts SET cera = 5000 WHERE account_id = @a;";
                    fund.Parameters.AddWithValue("@a", accountId);
                    fund.ExecuteNonQuery();
                    transaction.Commit();
                }
                long buyId;
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    GoldConsignmentRepository.InsertListing(connection, transaction,
                        otherSellerId, otherAccountId, goldAmount: 2_000_000,
                        unitPriceCera: 1500, durationHours: 24, nowUnix: nowUnix);
                    transaction.Commit();
                    buyId = ReadNewestListingId(database, otherSellerId);
                }
                Check("purchase fixture listing created", buyId > 0, ref failures);

                // Own-listing guard (0x9B): this character's own active row must be
                // refused, and nothing may move.
                var ownId = FirstActiveListingId(database, characterId);
                var ceraBeforeOwn = ReadCera(database, characterId);
                var ownAttempt = GoldConsignment.TryPurchase(lease,
                    Network.CmdPacketTypeA21.AUCTION_BIDDING,
                    new GoldConsignPurchaseRequest(0, (uint)ownId, 0, 2681725));
                Check("cannot purchase own listing",
                    ownAttempt != null && ownAttempt[0] == 0, ref failures);
                Check("own-listing attempt does not move 点券",
                    ReadCera(database, characterId) == ceraBeforeOwn, ref failures);

                var ceraBefore = ReadCera(database, characterId);
                var tokenBefore = ReadTokenCera(database, otherSellerId);
                var buyerGoldBefore = ReadOnline(lease);
                Check("buyer starts with enough cera", ceraBefore >= 1500, ref failures);

                var purchase = new GoldConsignPurchaseRequest(
                    PriceCera: 1500, ListingId: (uint)buyId, Reserved: 0, LadderItemId: 2681726);
                var buyBody = GoldConsignment.TryPurchase(
                    lease, Network.CmdPacketTypeA21.AUCTION_BIDDING, purchase);
                Check("purchase returns a success ack",
                    buyBody != null && buyBody.Length == 2 && buyBody[0] == 1, ref failures);
                Check("purchase debits the buyer's 点券 by the listing price",
                    ReadCera(database, characterId) == ceraBefore - 1500, ref failures);
                // The seller is paid into the account 代币券 wallet.
                Check("purchase does not credit the wallet before the voucher is claimed",
                    ReadTokenCera(database, otherSellerId) == tokenBefore, ref failures);
                var (soldMailCount, soldMailItem, soldMailAttach) =
                    ReadSoldMail(database, otherSellerId);
                Check("purchase mails the 代币券 voucher at the net amount",
                    soldMailCount == 1
                    && soldMailItem == (int)GoldConsignmentWire.CeraItemId
                    && soldMailAttach == 1425, ref failures);
                Check("purchase credits the buyer's gold",
                    ReadOnline(lease) == buyerGoldBefore + 2_000_000, ref failures);
                Check("purchased listing leaves the board",
                    CountActive(database, otherSellerId) == 0, ref failures);

                var ceraAfter = ReadCera(database, characterId);
                var replay = GoldConsignment.TryPurchase(
                    lease, Network.CmdPacketTypeA21.AUCTION_BIDDING, purchase);
                Check("replayed purchase is refused",
                    replay == null || replay[0] != 1, ref failures);
                Check("replayed purchase does not debit again",
                    ReadCera(database, characterId) == ceraAfter, ref failures);

                // Tampered price must be rejected before any money moves.
                long checkId;
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    GoldConsignmentRepository.InsertListing(connection, transaction,
                        otherSellerId, otherAccountId, goldAmount: 1_000_000,
                        unitPriceCera: 2000, durationHours: 24, nowUnix: nowUnix);
                    transaction.Commit();
                    checkId = ReadNewestListingId(database, otherSellerId);
                }
                var ceraBeforeTamper = ReadCera(database, characterId);
                var tampered = GoldConsignment.TryPurchase(
                    lease, Network.CmdPacketTypeA21.AUCTION_BIDDING,
                    new GoldConsignPurchaseRequest(1, (uint)checkId, 0, 2681725));
                Check("tampered price is refused",
                    tampered != null && tampered[0] == 0, ref failures);
                Check("tampered price does not move 点券",
                    ReadCera(database, characterId) == ceraBeforeTamper, ref failures);
                Check("tampered listing stays active",
                    CountActive(database, otherSellerId) == 1, ref failures);

                // ---- expiry: scan by persisted expires_at_unix and refund ----
                // The listing placed above expires at nowUnix + 24h. Before that
                // moment nothing may settle; after it, the gold must come back.
                var beforeExpiry = ReadOnline(lease);
                Check("no expiry settlement before the deadline",
                    GoldConsignment.SettleExpired(lease, nowUnix + 60) == 0, ref failures);
                Check("wallet unchanged before the deadline",
                    ReadOnline(lease) == beforeExpiry, ref failures);

                var settledCount = GoldConsignment.SettleExpired(
                    lease, nowUnix + 24 * 3600 + 1);
                Check("expiry scan settles the due listing", settledCount == 1, ref failures);
                Check("expiry returns the escrowed gold",
                    ReadOnline(lease) == beforeExpiry + 1_000_000, ref failures);
                Check("expired listing is no longer active",
                    CountActive(database, characterId) == 0, ref failures);
                Check("second expiry scan finds nothing",
                    GoldConsignment.SettleExpired(lease, nowUnix + 24 * 3600 + 2) == 0, ref failures);
                // Other sellers' expired rows must never be refunded into this wallet.
                // They stay active for their own owner to settle on login.
                Check("other seller's listing is untouched by this lease",
                    CountActive(database, otherSellerId) >= 1, ref failures);
            }
            finally
            {
                InventoryContext.Unregister(session);
            }
        }

        /// <summary>
        /// 官方时长与笔数规则：无券 24 小时、持券 48 小时；单角色在挂笔数**恒为 3**，
        /// 拍卖行优惠券只放宽时长与正常档手续费，不放宽笔数。
        /// </summary>
        private static void CheckDurationAndCap(
            Infrastructure.GameDatabase database, ref int failures)
        {
            const int accountId = 77301;
            const int characterId = 77302;
            SeedAccount(database, accountId);
            SeedCharacter(database, characterId, accountId, "gold-consign-duration");
            var session = Guid.NewGuid();
            var lease = InventoryContext.Register(session,
                new InventoryService(characterId, accountId, database));
            try
            {
                lease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart, 20_000_000);
                var now = 1_800_200_000L;

                // 无券即使请求 48 小时，也必须被服务端压回 24（客户端勾选框不可信）。
                GoldConsignment.TryPlaceConfirmed(lease,
                    Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    1_000_000, 1000, hours: 48, now);
                Check("no coupon clamps a requested 48h listing to 24h",
                    LatestDurationHours(database, characterId) == 24, ref failures);

                GoldConsignment.TryPlaceConfirmed(lease,
                    Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    1_000_000, 1000, hours: 24, now + 1);
                GoldConsignment.TryPlaceConfirmed(lease,
                    Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    1_000_000, 1000, hours: 24, now + 2);
                Check("three active listings are allowed",
                    CountActive(database, characterId) == 3, ref failures);

                var goldBefore = ReadOnline(lease);
                GoldConsignment.TryPlaceConfirmed(lease,
                    Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    1_000_000, 1000, hours: 24, now + 3);
                Check("a fourth listing is refused by the cap of 3",
                    CountActive(database, characterId) == 3, ref failures);
                Check("the refused fourth listing leaves gold untouched",
                    ReadOnline(lease) == goldBefore, ref failures);

                // 持券也不放宽笔数：仍然只能挂 3 笔。
                GrantAuctionCoupon(database, accountId, now + 86_400);
                GoldConsignment.TryPlaceConfirmed(lease,
                    Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    1_000_000, 1000, hours: 48, now + 4);
                Check("the auction coupon does not raise the listing cap",
                    CountActive(database, characterId) == 3, ref failures);
            }
            finally
            {
                InventoryContext.Unregister(session);
            }

            // 持券角色：同样的 48 小时请求必须被接受，并锁定到挂单上。
            const int couponAccountId = 77311;
            const int couponCharacterId = 77312;
            SeedAccount(database, couponAccountId);
            SeedCharacter(database, couponCharacterId, couponAccountId, "gold-consign-coupon");
            var couponSession = Guid.NewGuid();
            var couponLease = InventoryContext.Register(couponSession,
                new InventoryService(couponCharacterId, couponAccountId, database));
            try
            {
                couponLease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart, 10_000_000);
                var now = 1_800_300_000L;
                GrantAuctionCoupon(database, couponAccountId, now + 86_400);
                GoldConsignment.TryPlaceConfirmed(couponLease,
                    Network.CmdPacketTypeA21.AUCTION_REGIST_ITEM,
                    1_000_000, 1000, hours: 48, now);
                Check("the auction coupon allows a 48h listing",
                    LatestDurationHours(database, couponCharacterId) == 48, ref failures);
                Check("the coupon listing locks the 1% fee tier",
                    LatestFeePercent(database, couponCharacterId) == 1, ref failures);
            }
            finally
            {
                InventoryContext.Unregister(couponSession);
            }
        }

        /// <summary>在挂单表里读该角色最近一笔的时长。</summary>
        private static int LatestDurationHours(
            Infrastructure.GameDatabase database, int characterId)
            => ReadListingInt(database, characterId, "duration_hours");

        /// <summary>在挂单表里读该角色最近一笔锁存的手续费百分比。</summary>
        private static int LatestFeePercent(
            Infrastructure.GameDatabase database, int characterId)
            => ReadListingInt(database, characterId, "fee_percent");

        private static int ReadListingInt(
            Infrastructure.GameDatabase database, int characterId, string column)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {column} FROM gold_consignment_listings "
                + "WHERE seller_character_id = @cid ORDER BY listing_id DESC LIMIT 1;";
            command.Parameters.AddWithValue("@cid", characterId);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        /// <summary>给账号挂一张生效中的拍卖行优惠券（premium_type = 29）。</summary>
        private static void GrantAuctionCoupon(
            Infrastructure.GameDatabase database, int accountId, long expiresAtUnix)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO account_premiums(account_id, premium_type, end_time)
VALUES(@aid, 29, @expiry)
ON CONFLICT(account_id, premium_type) DO UPDATE SET end_time = excluded.end_time;";
            command.Parameters.AddWithValue("@aid", accountId);
            command.Parameters.AddWithValue("@expiry", expiresAtUnix);
            command.ExecuteNonQuery();
        }

        private static int ReadOnline(InventoryLease lease)
            => lease.Inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

        private static bool SettleSold(Infrastructure.GameDatabase database, long listingId,
            int buyerCharacter, int buyerAccount, long now)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            var ok = GoldConsignmentRepository.TrySettleSold(
                database, connection, transaction, listingId, buyerCharacter, buyerAccount, now,
                out _, out _, out _, out _);
            if (ok) transaction.Commit();
            else transaction.Rollback();
            return ok;
        }

        private static bool SettleCancelOrExpire(Infrastructure.GameDatabase database,
            long listingId, int outcome, long now)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            var ok = GoldConsignmentRepository.TrySettleCancelledOrExpired(
                connection, transaction, listingId, outcome, now, out _, out _);
            if (ok) transaction.Commit();
            else transaction.Rollback();
            return ok;
        }

        /// <summary>Newest active listing id for a character, or 0 when none.</summary>
        private static long FirstActiveListingId(
            Infrastructure.GameDatabase database, int characterId)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT listing_id FROM gold_consignment_listings "
                    + "WHERE seller_character_id = @cid AND state = 0 "
                    + "ORDER BY listing_id LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0L : Convert.ToInt64(v);
            });

        private static int CountActive(Infrastructure.GameDatabase database, int characterId)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM gold_consignment_listings "
                    + "WHERE seller_character_id = @cid AND state = 0;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            });

        private static int SettlementCount(Infrastructure.GameDatabase database, long listingId)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM gold_consignment_settlements WHERE listing_id = @id;";
                cmd.Parameters.AddWithValue("@id", listingId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            });

        /// <summary>
        /// Escrow amount the service layer must credit back, or 0 once settled.
        /// </summary>
        private static long ReadRefundAmount(Infrastructure.GameDatabase database, long listingId)
            => database.Read(connection =>
                GoldConsignmentRepository.LoadRefundAmount(connection, listingId));

        private static int ReadTokenCera(Infrastructure.GameDatabase database, int characterId)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT token_cera FROM accounts WHERE account_id = "
                    + "(SELECT account_id FROM characters WHERE character_id = @cid);";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
            });

        /// <summary>点券 (cash cera) of the account owning this character.</summary>
        private static int ReadCera(Infrastructure.GameDatabase database, int characterId)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT cera FROM accounts WHERE account_id = "
                    + "(SELECT account_id FROM characters WHERE character_id = @cid);";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
            });

        /// <summary>Highest listing id for a seller, used right after an insert.</summary>
        private static long ReadNewestListingId(
            Infrastructure.GameDatabase database, int characterId)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT MAX(listing_id) FROM gold_consignment_listings "
                    + "WHERE seller_character_id = @cid;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0L : Convert.ToInt64(v);
            });

        /// <summary>
        /// Reads the seller's consignment-sale mail: message count, and the first
        /// attachment's item id / count. Returns (0,0,0) when no mail exists.
        /// </summary>
        private static (int mailCount, int itemId, int itemCount) ReadSoldMail(
            Infrastructure.GameDatabase database, int characterId)
            => database.Read(connection =>
            {
                using var count = connection.CreateCommand();
                count.CommandText =
                    "SELECT COUNT(*) FROM mailbox_messages "
                    + "WHERE receiver_character_id = @cid AND title = '金币寄售';";
                count.Parameters.AddWithValue("@cid", characterId);
                var n = Convert.ToInt32(count.ExecuteScalar());
                if (n == 0) return (0, 0, 0);

                using var att = connection.CreateCommand();
                att.CommandText =
                    "SELECT a.item_template_id, a.item_count FROM mailbox_attachments a "
                    + "JOIN mailbox_messages m ON m.message_id = a.message_id "
                    + "WHERE m.receiver_character_id = @cid AND m.title = '金币寄售' "
                    + "ORDER BY a.attachment_id LIMIT 1;";
                att.Parameters.AddWithValue("@cid", characterId);
                using var r = att.ExecuteReader();
                return r.Read() ? (n, r.GetInt32(0), r.GetInt32(1)) : (n, 0, 0);
            });

        /// <summary>ItemKind byte stored in the consignment mail attachment's item_core.</summary>
        /// <summary>mailbox_messages.gold of the seller's consignment sale mail.</summary>
        private static int ReadSoldMailGold(
            Infrastructure.GameDatabase database, int characterId)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT gold FROM mailbox_messages "
                    + "WHERE receiver_character_id = @cid AND title = '金币寄售' "
                    + "ORDER BY message_id LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
            });

        private static byte ReadAttachmentKind(
            Infrastructure.GameDatabase database, int characterId)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT item_core FROM mailbox_attachments a "
                    + "JOIN mailbox_messages m ON m.message_id = a.message_id "
                    + "WHERE m.receiver_character_id = @cid AND m.title = '金币寄售' "
                    + "ORDER BY a.attachment_id LIMIT 1;";
                cmd.Parameters.AddWithValue("@cid", characterId);
                var v = cmd.ExecuteScalar();
                if (v is not byte[] blob || blob.Length < 1) return (byte)255;
                return blob[0];
            });

        private static bool TableExists(Infrastructure.GameDatabase database, string table)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n;";
                cmd.Parameters.AddWithValue("@n", table);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            });

        private static int CurrentVersion(Infrastructure.GameDatabase database)
            => database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA user_version;";
                return Convert.ToInt32(cmd.ExecuteScalar());
            });

        private static void SeedAccount(Infrastructure.GameDatabase database, int accountId)
            => SeedAccount(database, accountId, cera: 0);

        /// <summary>
        /// Seeds an account. <paramref name="cera"/> is 点券, the wallet the BUYER spends
        /// on a consignment purchase; it must be funded for purchase tests.
        /// </summary>
        private static void SeedAccount(
            Infrastructure.GameDatabase database, int accountId, int cera)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash, cera)
VALUES(@accountId, @mid, '', @cera);";
            command.Parameters.AddWithValue("@accountId", accountId);
            command.Parameters.AddWithValue("@mid", $"gold-consign-account-{accountId}");
            command.Parameters.AddWithValue("@cera", cera);
            command.ExecuteNonQuery();
        }

        private static void SeedCharacter(Infrastructure.GameDatabase database,
            int characterId, int accountId, string name)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@characterId, @accountId, @name, 100);";
            command.Parameters.AddWithValue("@characterId", characterId);
            command.Parameters.AddWithValue("@accountId", accountId);
            command.Parameters.AddWithValue("@name", name);
            command.ExecuteNonQuery();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                foreach (var suffix in new[] { "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            catch { }
        }

        private static void Check(string name, bool passed, ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed) failures++;
        }
    }
}
