using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Parsers.Auction;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// Gold consignment (金币寄售) — independent feature, not part of the ordinary
    /// item auction. It reuses opcodes 0x00B6 / 0x00B7 with a shorter body.
    ///
    /// Register layout (0x00B7, 28 bytes) recovered statically from the current
    /// client sender 0x01456F50. The sender emits
    ///     B(1) B(0) C(0) D(ebx) D(1) D(-1) D(edi) D(0) D([esi+0x1dc])
    /// and each writer's width is proven from its own body
    /// (`add [ecx+0x2bcc2c],N` followed by `push N`):
    ///     writer 0x274D2E0 = 1 byte, 0x274D310 = 2 bytes, 0x274D340 = 4 bytes
    /// 1 + 1 + 2 + 6*4 = 28, matching the captured body exactly:
    ///   body[0]      u8   = 1        marker the sender hardcodes
    ///   body[1]      u8   = 0        space  (0 = Main)
    ///   body[2..4)   u16  = 0        slot   (0 = Main virtual slot 0 = GOLD)
    ///   body[4..8)   u32  = 0x0028EB7D gold marker
    ///   body[8..12)  u32  = 1        quantity
    ///   body[12..16) u32  = -1       price  (unset by the client)
    ///   body[16..20) u32  = -1       buyout (unset by the client)
    ///   body[20..24) u32  = 0        reserved
    ///   body[24..28) u32  = 24       duration hours
    ///
    /// There is NO gold-amount field on the wire; the client never writes one, so
    /// an amount cannot be inferred from the packet and must not be invented.
    /// Provisional policy numbers are marked 待确认 and live in one place.
    /// </summary>
    internal static class GoldConsignment
    {
        // Provisional policy. The gold amount comes from the ladder item id, whose
        // range is 1百万..2亿, so the bounds simply follow the ladder.
        internal const int ProvisionalDurationHours = 24;
        /// <summary>持拍卖行优惠券时的上架时长（官方：无券 24 小时，持券 48 小时）。</summary>
        internal const int CouponDurationHours = 48;
        internal const int ProvisionalFeePercent = 5;
        /// <summary>
        /// 单角色在挂笔数上限。官方规则：**恒为 3**，拍卖行优惠券不放宽笔数
        /// （优惠券在金币寄售上只放宽上架时长与正常档手续费）。
        /// </summary>
        internal const int ProvisionalListingCap = 3;
        internal const int ProvisionalMinGold = GoldLadder.MinGold;
        internal const int ProvisionalMaxGold = GoldLadder.MaxGold;

        internal static byte[] Place(
            InventoryLease lease,
            CmdPacketTypeA21 command,
            GoldConsignRegisterRequest request)
        {
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Field map, confirmed by the PVF ladder plus live captures:
            //   [4..8)  = the SELECTED ladder item id (PVF stackable/cash/chn_trade_gold)
            //             0x0028EB7D=2681725=1百万, 0x0028EB7E=2百万, ...
            //   [16..20) = edi = atoi([esi+0x184]) = the 一口价 (cera) edit box
            //             Live proof: typed 10000 -> 0x2710, typed 1000 -> 0x3E8
            //   [24..28) = hours (24/48 per the checkbox)
            //   [12..16) = sender constant -1
            var goldAmount = GoldLadder.GoldForItem(request.Marker);
            var buyoutCera = request.GoldAmount;
            var hours = (int)Math.Min(request.DurationHours, int.MaxValue);

            FileLogger.Log(
                $"[GoldConsign] register cid={lease.CharacterId} ladderItem=0x{request.Marker:X8} "
                + $"gold={goldAmount} buyoutCera={buyoutCera} hasValue={request.HasAmount} "
                + $"hours={hours} constNeg1=0x{request.ConstantNeg1:X8} qty={request.Quantity}");

            if (goldAmount <= 0)
            {
                // Not a gold ladder item: the client cannot have produced this, and we
                // must not invent an amount.
                FileLogger.Log($"[GoldConsign] reject cid={lease.CharacterId} "
                    + $"reason=UnknownLadderItem item=0x{request.Marker:X8}");
                return AuctionMutationReplyBuilder.ConsignmentRejection(
                    command, AuctionMutationReplyBuilder.InvalidPriceFailure);
            }

            if (!request.HasAmount || buyoutCera <= 0)
            {
                // The client forces -1 when the 一口价 box is empty, so this is the
                // "nothing typed" case rather than a malformed packet. No gold moves.
                // 0x98 shows "最低竞拍价或一口价中输入了错误数值".
                FileLogger.Log($"[GoldConsign] reject cid={lease.CharacterId} reason=BuyoutEmpty");
                return AuctionMutationReplyBuilder.ConsignmentRejection(
                    command, AuctionMutationReplyBuilder.InvalidPriceFailure);
            }

            return TryPlaceConfirmed(lease, command, goldAmount, unitPriceCera: buyoutCera,
                hours, nowUnix);
        }

        /// <summary>
        /// Listing path for a caller that already validated the gold amount.
        /// <see cref="Place"/> is the native entry; this exists so the escrow /
        /// commit / persistence sequence can be exercised with an explicit amount.
        /// </summary>
        internal static byte[] TryPlaceConfirmed(
            InventoryLease lease,
            CmdPacketTypeA21 command,
            int goldAmount,
            int unitPriceCera,
            int hours,
            long nowUnix)
        {
            var reject = ValidateOrder(goldAmount, unitPriceCera);
            if (reject != null)
            {
                FileLogger.Log($"[GoldConsign] reject cid={lease.CharacterId} reason={reject}");
                return AuctionMutationReplyBuilder.Failure(command, busy: true);
            }

            // Official tier: below 40% of the recent average price cannot be listed at all,
            // and the tier is locked onto the listing now. This read also tells us whether
            // the 拍卖行优惠券 is active, which decides both the fee and the max duration.
            var feeReject = ResolveFeePercent(
                lease, goldAmount, unitPriceCera, nowUnix,
                out var feePercent, out var couponActive);
            if (feeReject != null)
            {
                FileLogger.Log($"[GoldConsign] reject cid={lease.CharacterId} reason={feeReject}");
                // 0x98 renders "最低竞拍价或一口价中输入了错误数值" (verified in game).
                return AuctionMutationReplyBuilder.ConsignmentRejection(
                    command, AuctionMutationReplyBuilder.InvalidPriceFailure);
            }

            // 官方时长规则：无券 24 小时，持券 48 小时。客户端按勾选框发 24/48，
            // 服务端必须自己按持券状态夹取，否则改包就能白拿 48 小时。
            var maxHours = couponActive ? CouponDurationHours : ProvisionalDurationHours;
            if (hours <= 0 || hours > maxHours) hours = ProvisionalDurationHours;

            long listingId;
            var committed = false;
            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                if (inventory == null) return AuctionMutationReplyBuilder.Failure(command, busy: true);

                var wallet = inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart);
                var currentGold = wallet?.Count ?? 0;
                if (currentGold < goldAmount)
                {
                    FileLogger.Log($"[GoldConsign] insufficient gold cid={lease.CharacterId} "
                        + $"have={currentGold} need={goldAmount}");
                    // 0x90 shows "所持金币不足，将无法进行上架或竞拍" (verified in game).
                    return AuctionMutationReplyBuilder.ConsignmentRejection(
                        command, AuctionMutationReplyBuilder.InsufficientGoldFailure);
                }

                var cap = CountActive(lease, out var capOk);
                if (!capOk || cap >= ProvisionalListingCap)
                {
                    FileLogger.Log($"[GoldConsign] listing cap reached cid={lease.CharacterId} active={cap}");
                    // 0x97 shows "已超过上架数量上限，需要拍卖行优惠券才能继续上架".
                    return AuctionMutationReplyBuilder.ConsignmentRejection(
                        command, AuctionMutationReplyBuilder.ListingCapFailure);
                }

                // Deduct inside the online model first, then persist through the
                // shared commit coordinator in the SAME transaction as the listing
                // row, so either both land or neither does.
                if (!inventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart, currentGold - goldAmount))
                {
                    FileLogger.Log($"[GoldConsign] gold deduction failed cid={lease.CharacterId}");
                    return AuctionMutationReplyBuilder.Failure(command, busy: true);
                }

                listingId = 0;
                committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "gold-consignment-place",
                    (connection, transaction) =>
                    {
                        listingId = GoldConsignmentRepository.InsertListing(
                            connection, transaction, lease.CharacterId, lease.AccountId,
                            goldAmount, unitPriceCera, hours, nowUnix, feePercent);
                        return listingId > 0;
                    },
                    immediate: true);
            }

            if (!committed || listingId <= 0)
            {
                FileLogger.Log($"[GoldConsign] commit failed cid={lease.CharacterId}");
                return AuctionMutationReplyBuilder.Failure(command, busy: true);
            }

            FileLogger.Log($"[GoldConsign] listed id={listingId} cid={lease.CharacterId} "
                + $"gold={goldAmount} unit={unitPriceCera} hours={hours}");
            // body[1] must be 1 so the shared 0x00B7 ACK handler picks the
            // consignment window (control 8) rather than the item-auction one.
            return AuctionMutationReplyBuilder.ConsignmentSuccess();
        }

        /// <summary>
        /// Builds the 0x00BA "金币搜索" reply body listing every active consignment
        /// row on the server, so OTHER players can see and buy the gold. Returns null
        /// when the board is empty (caller falls back to the ordinary search reply).
        /// </summary>
        internal static byte[] TryBuildSearchReply(
            InventoryLease lease, long nowUnix, uint ladderItemId = 0)
        {
            var database = lease?.Inventory?.Database;
            if (database == null) return null;

            List<GoldConsignmentBoardRow> rows;
            try
            {
                rows = database.Read(connection =>
                    GoldConsignmentRepository.LoadBoard(connection, nowUnix, ladderItemId));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GoldConsign] board read failed: {ex.Message}");
                return null;
            }

            if (rows == null || rows.Count == 0) return null;

            var body = GoldConsignmentSearchBuilder.TryBuild(
                rows, row => GoldSnapshot(row.GoldAmount));
            FileLogger.Log($"[GoldConsign] 0x00BA board rows={rows.Count} "
                + $"len={(body == null ? -1 : body.Length)}");
            return body;
        }

        /// <summary>
        /// 117-byte item snapshot for a gold stack.
        ///
        /// Field positions MUST match AuctionNativeDisplayProjection, which is the
        /// authoritative writer for this block:
        ///     native83[0]        = SealFlag
        ///     WriteInt32(1, ...) = ItemId
        ///     native83[5]        = Attr
        ///     WriteInt32(6, ...) = Value / Count
        /// (An earlier revision wrote the count at 5 and Attr at 9, which collided
        /// with Attr and left the count in the wrong field, so the client saw a
        /// zero-count row and filtered it out.)
        /// </summary>
        private static (byte[] native83, byte[] additional30, byte[] additional4) GoldSnapshot(int gold)
        {
            // The consigned thing is a REAL PVF item from stackable/cash/chn_trade_gold,
            // one id per ladder step (1百万 = 2681725 ... 2亿 = 2683073). Writing itemId 0
            // made the client render a generic currency named "金币"; the correct item id
            // is what makes it appear as the selected consignment item.
            var itemId = GoldLadder.ItemForGold(gold);
            var native83 = new byte[83];
            native83[0] = 0;                                                    // SealFlag
            BinaryPrimitives.WriteInt32LittleEndian(native83.AsSpan(1, 4), (int)itemId);
            native83[5] = 0;                                                    // Attr
            // Each ladder item is a single stack representing its full gold value.
            BinaryPrimitives.WriteInt32LittleEndian(native83.AsSpan(6, 4), 1);  // Count
            return (native83, new byte[30], new byte[4]);
        }

        /// <summary>
        /// Builds the 0x00BC "my consignment listings" reply body, or null when this
        /// character has no active consignment rows (caller then keeps the ordinary
        /// auction behaviour untouched).
        ///
        /// Each row is the standard 151-byte listing record; the item snapshot is a
        /// virtual-currency gold entry (slot 0 / itemId 0 / count = gold), matching
        /// ItemListProtocolWriter.WriteVirtualCountEntry84, so the UI can render the
        /// consigned gold as a currency stack.
        /// </summary>
        internal static byte[] TryBuildListingsReply(
            InventoryLease lease, long nowUnix)
        {
            var database = lease?.Inventory?.Database;
            if (database == null) return null;

            List<GoldConsignmentListing> listings;
            byte[] sellerName;
            try
            {
                (listings, sellerName) = database.Read(connection =>
                {
                    GoldConsignmentRepository.TryLoadActive(
                        connection, lease.CharacterId, out var loaded);
                    return (loaded, GoldConsignmentRepository.LoadCharacterNameBytes(
                        connection, lease.CharacterId));
                });
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GoldConsign] listings read failed cid={lease.CharacterId}: {ex.Message}");
                return null;
            }

            if (listings == null || listings.Count == 0) return null;

            var tag = ClientDstrBytes(sellerName);
            if (tag.Length != 13)
            {
                FileLogger.Log($"[GoldConsign] seller name cannot form a 13-byte tag "
                    + $"cid={lease.CharacterId} rawLen={sellerName?.Length ?? -1}");
                return null;
            }

            return GoldConsignmentListBuilder.TryBuild(listings, tag,
                listing => GoldSnapshot(listing.GoldAmount));
        }

        /// <summary>
        /// Encodes a client-side dstr: 2-byte code page marker, then the name bytes
        /// and a NUL, padded to 13 bytes. Returns an empty array when it cannot fit.
        ///
        /// <paramref name="rawName"/> is the BLOB stored in <c>characters.name</c>,
        /// already in the client's encoding.
        /// </summary>
        private static byte[] ClientDstrBytes(byte[] rawName)
        {
            if (rawName == null || rawName.Length == 0) return Array.Empty<byte>();
            string name;
            try
            {
                name = System.Text.Encoding.GetEncoding(936).GetString(rawName);
            }
            catch
            {
                return Array.Empty<byte>();
            }
            // Canonical encoder: name at offset 0, zero-padded to 13, terminator >= 1.
            // A hand-rolled 2-byte prefix put the NUL at index 0, which the client
            // rejects outright.
            return AuctionSellerNameTag13.TryEncodeFrozenRoleName(name, out var tag)
                ? tag : Array.Empty<byte>();
        }

        /// <summary>
        /// Cancels one consignment listing and refunds its escrowed gold.
        ///
        /// Returns null when <paramref name="listingId"/> is not an active listing of
        /// this character, so the caller can fall through to the ordinary auction
        /// cancel path untouched.
        ///
        /// Refund follows the same single-transaction shape as expiry: credit the
        /// online wallet first, then let the coordinator persist the inventory together
        /// with the state change. Two separate commits would not be atomic.
        /// </summary>
        internal static byte[] TryCancel(
            InventoryLease lease, CmdPacketTypeA21 command, long listingId)
        {
            if (lease?.Inventory == null || listingId <= 0) return null;
            var database = lease.Inventory.Database;
            if (database == null) return null;

            long refund;
            try
            {
                refund = database.Read(connection =>
                {
                    var owned = GoldConsignmentRepository.LoadRefundAmount(
                        connection, listingId, lease.CharacterId);
                    return owned;
                });
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GoldConsign] cancel read failed id={listingId}: {ex.Message}");
                return null;
            }

            if (refund <= 0) return null;   // not ours, or already settled

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                var current = inventory.GetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                if (!inventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart, current + (int)refund))
                {
                    FileLogger.Log($"[GoldConsign] cancel credit failed id={listingId}");
                    // 0x9E 处理量过多 is the closest *prompted* code for a transient
                    // internal failure; the silent codes (0x7B/0xCE) would look like
                    // "nothing happened" to the player. This is an approximation, not a
                    // verified mapping for this specific failure.
                    return AuctionMutationReplyBuilder.ConsignmentRejection(
                        command, AuctionMutationReplyBuilder.ThroughputFailure);
                }

                var done = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "gold-consignment-cancel",
                    (connection, transaction) =>
                        GoldConsignmentRepository.TrySettleCancelledOrExpired(
                            connection, transaction, listingId,
                            GoldConsignmentRepository.OutcomeCancelled,
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            out _, out _),
                    immediate: true);

                if (!done)
                {
                    // Coordinator reloaded the wallet from SQL, discarding the credit.
                    FileLogger.Log($"[GoldConsign] cancel commit failed id={listingId}");
                    // Same reasoning as above: prefer a prompted code over a silent one.
                    return AuctionMutationReplyBuilder.ConsignmentRejection(
                        command, AuctionMutationReplyBuilder.ThroughputFailure);
                }
            }

            FileLogger.Log($"[GoldConsign] cancelled id={listingId} "
                + $"cid={lease.CharacterId} refunded={refund}");
            // body[1] must be 1 so the ACK updates the consignment window.
            return AuctionMutationReplyBuilder.ConsignmentSuccess();
        }

        /// <summary>
        /// Consignment PURCHASE (0x00B9). The buyer pays 点券 (accounts.cera), receives
        /// the escrowed gold, and the seller is credited 代币券 (accounts.token_cera).
        ///
        /// Returns null when the listing is not purchasable by this character, so the
        /// caller can fall through to the ordinary auction bidding path.
        ///
        /// Everything happens in ONE transaction: debit cera, mark the listing sold,
        /// credit the seller's token cera, write the settlement ledger. The ledger row
        /// is what makes a replay harmless (TrySettleSold returns false once sold).
        /// </summary>
        internal static byte[] TryPurchase(
            InventoryLease lease, CmdPacketTypeA21 command, GoldConsignPurchaseRequest request)
        {
            if (lease?.Inventory == null || request == null) return null;
            if (request.ListingId == 0 || request.ListingId > int.MaxValue) return null;

            var database = lease.Inventory.Database;
            if (database == null) return null;

            var listingId = (long)request.ListingId;
            var buyerCharacterId = lease.CharacterId;
            var buyerAccountId = lease.AccountId;
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Pre-check outside the write transaction: is this a consignment row, is it
            // someone else's, and does the client's price match the stored one?
            // SQL 留在 Repository，service 不直接拼查询。
            long storedPrice;
            int sellerCharacterId;
            int goldAmount;
            try
            {
                var found = false;
                (found, storedPrice, sellerCharacterId, goldAmount) = database.Read(connection =>
                {
                    var ok = GoldConsignmentRepository.TryLoadPurchaseTarget(
                        connection, listingId, out var price, out var seller, out var gold);
                    return (ok, price, seller, gold);
                });
                if (!found) return null;                            // not an active listing
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GoldConsign] purchase read failed id={listingId}: {ex.Message}");
                return null;
            }

            if (sellerCharacterId == buyerCharacterId)
            {
                // 0x9B = 无法竞拍自己.
                FileLogger.Log($"[GoldConsign] purchase rejected own listing id={listingId}");
                return AuctionMutationReplyBuilder.ConsignmentRejection(
                    command, AuctionMutationReplyBuilder.OwnListingFailure);
            }
            if (request.PriceCera != storedPrice)
            {
                // The client echoes the price it displayed. A mismatch means the row is
                // stale (someone else bought or the price changed) - never honour the
                // client's number.
                FileLogger.Log($"[GoldConsign] purchase price mismatch id={listingId} "
                    + $"wire={request.PriceCera} stored={storedPrice}");
                return AuctionMutationReplyBuilder.ConsignmentRejection(
                    command, AuctionMutationReplyBuilder.InvalidPriceFailure);
            }

            byte[] body;
            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                var walletBefore = inventory.GetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

                var sold = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "gold-consignment-purchase",
                    (connection, transaction) =>
                    {
                        // Buyer pays cera. If the balance is short this fails and nothing
                        // else runs, so no gold and no seller credit leak out.
                        if (!Currency.CurrencyService.TrySpendCera(
                                connection, transaction, buyerCharacterId, (int)storedPrice))
                        {
                            FileLogger.Log($"[GoldConsign] purchase insufficient cera "
                                + $"cid={buyerCharacterId} need={storedPrice}");
                            return false;
                        }
                        return GoldConsignmentRepository.TrySettleSold(
                            database, connection, transaction, listingId, buyerCharacterId,
                            buyerAccountId, nowUnix,
                            out _, out _, out _, out _);
                    },
                    immediate: true);

                if (!sold)
                {
                    return AuctionMutationReplyBuilder.ConsignmentRejection(
                        command, AuctionMutationReplyBuilder.InsufficientCeraFailure);
                }

                // Gold lands in the online wallet model; the coordinator has already
                // persisted the cera debit and the listing state.
                inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart, walletBefore + goldAmount);

                // Persist the credited gold in the same style as other wallet mutations.
                OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease, "gold-consignment-purchase-grant");
                body = AuctionMutationReplyBuilder.ConsignmentSuccess();
            }

            FileLogger.Log($"[GoldConsign] sold id={listingId} buyer={buyerCharacterId} "
                + $"seller={sellerCharacterId} gold={goldAmount} cera={storedPrice}");
            return body;
        }

        /// <summary>
        /// Settles every active listing whose expiry has passed for one character,
        /// returning the escrowed gold to the seller. Must run inside the owned
        /// lease so the refund and the state change share one transaction.
        ///
        /// Expiry is driven by the persisted <c>expires_at_unix</c>, not by a
        /// process-local timer, so it stays correct across restarts: callers invoke
        /// this on login and after the client queries the board.
        /// </summary>
        internal static int SettleExpired(InventoryLease lease, long nowUnix)
        {
            if (lease?.Inventory == null) return 0;
            var database = lease.Inventory.Database;
            if (database == null) return 0;

            List<long> expired;
            try
            {
                expired = database.Read(connection =>
                    GoldConsignmentRepository.LoadExpiredActiveIds(
                        connection, lease.CharacterId, nowUnix));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GoldConsign] expiry scan failed cid={lease.CharacterId}: {ex.Message}");
                return 0;
            }

            if (expired.Count == 0) return 0;

            var settled = 0;
            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                if (inventory == null) return 0;

                foreach (var listingId in expired)
                {
                    try
                    {
                        // Exact mirror of Place(): mutate the online wallet first, then
                        // let ONE coordinator commit persist the dirty inventory and the
                        // listing state change in the same transaction. Two separate
                        // commits would not be atomic: a failure between them would mark
                        // the listing settled while the gold never returns.
                        long goldToRefund;
                        try
                        {
                            goldToRefund = database.Read(connection =>
                                GoldConsignmentRepository.LoadRefundAmount(connection, listingId));
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log($"[GoldConsign] expiry read failed id={listingId}: {ex.Message}");
                            continue;
                        }
                        if (goldToRefund <= 0) continue;

                        var current = inventory.GetMainVirtualCount(
                            InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                        if (!inventory.SetMainVirtualCount(
                                InventoryService.MainVirtualCurrencySlotStart,
                                current + (int)goldToRefund))
                        {
                            FileLogger.Log($"[GoldConsign] expiry credit failed id={listingId}");
                            continue;
                        }

                        var done = OnlineInventoryMutationCommitCoordinator.TryCommit(
                            lease,
                            "gold-consignment-expire",
                            (connection, transaction) =>
                                GoldConsignmentRepository.TrySettleCancelledOrExpired(
                                    connection, transaction, listingId,
                                    GoldConsignmentRepository.OutcomeExpired, nowUnix,
                                    out _, out _),
                            immediate: true);

                        if (done)
                        {
                            settled++;
                            FileLogger.Log($"[GoldConsign] expired id={listingId} "
                                + $"cid={lease.CharacterId} refunded={goldToRefund}");
                        }
                        // On failure the coordinator reloads the online inventory from
                        // SQL, discarding the provisional credit above.
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[GoldConsign] expiry settle failed id={listingId}: {ex.Message}");
                    }
                }
            }

            return settled;
        }

        private static int CountActive(InventoryLease lease, out bool ok)
        {
            ok = false;
            try
            {
                var database = lease.Inventory?.Database;
                if (database == null) return 0;
                // Read-only count stays in the Repository so this service never
                // carries SQL of its own (see 服务端业务开发规范 §3 目录边界).
                var count = database.Read(connection =>
                    GoldConsignmentRepository.CountActiveListings(connection, lease.CharacterId));
                ok = true;
                return count;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GoldConsign] active count failed cid={lease.CharacterId}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Validates a consignment order against the provisional policy.
        ///
        /// The gold amount comes from the operator rate and the cera price is the
        /// player's 一口价 for the whole lot, so no per-block multiplication applies.
        /// </summary>
        internal static string ValidateOrder(int goldAmount, int unitPriceCera)
        {
            if (goldAmount <= 0) return "GoldAmountUnset";
            if (goldAmount < ProvisionalMinGold) return "GoldBelowMinimum";
            if (goldAmount > ProvisionalMaxGold) return "GoldAboveMaximum";
            if (unitPriceCera < 0) return "PriceNegative";
            return null;
        }

        /// <summary>
        /// 0x00B6「近期交易价格」: the recent average for that ladder step, falling back
        /// to the operator reference when the step has no trade history yet.
        /// </summary>
        internal static uint ResolveReferencePrice(
            InventoryLease lease, uint ladderItemId, long nowUnix)
        {
            var fallback = GoldConsignmentWire.ReferencePrice;
            var goldAmount = GoldLadder.GoldForItem(ladderItemId);
            if (goldAmount <= 0) return fallback;
            var database = lease?.Inventory?.Database;
            if (database == null) return fallback;
            try
            {
                var avg = database.Read(connection =>
                    GoldConsignmentRepository.LoadAveragePriceCera(connection, goldAmount, nowUnix));
                return avg > 0 ? (uint)avg : fallback;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GoldConsign] reference price read failed: {ex.Message}");
                return fallback;
            }
        }

        /// <summary>
        /// 官方手续费分档 + 上架下限判定。
        ///
        /// 平均价格取该金币档位近 7 天的成交均价；没有历史时退回运营配置的参考价，
        /// 这样第一批上架也有基准。一口价低于均价 40% 时按官方规则不可上架。
        /// 拍卖行优惠券（account_premiums premium_type 29）只影响 40%-150% 正常档。
        /// </summary>
        private static string ResolveFeePercent(
            InventoryLease lease, int goldAmount, int priceCera, long nowUnix,
            out int feePercent, out bool couponActive)
        {
            feePercent = GoldConsignmentFeePolicy.NormalFeePercent;
            couponActive = false;
            var database = lease?.Inventory?.Database;
            if (database == null) return "NoDatabase";

            int average;
            try
            {
                (average, couponActive) = database.Read(connection =>
                {
                    var avg = GoldConsignmentRepository.LoadAveragePriceCera(
                        connection, goldAmount, nowUnix);
                    var rights = Rules.AuctionEntitlementReader.Read(
                        connection, null, lease.AccountId, nowUnix);
                    return (avg, rights.IsActive);
                });
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GoldConsign] fee resolve failed cid={lease.CharacterId}: {ex.Message}");
                return "FeeResolveFailed";
            }

            if (average <= 0) average = (int)GoldConsignmentWire.ReferencePrice;

            if (!GoldConsignmentFeePolicy.TryResolveFeePercent(
                    priceCera, average, couponActive, out feePercent))
            {
                FileLogger.Log($"[GoldConsign] reject cid={lease.CharacterId} "
                    + $"reason=PriceBelowAverageFloor price={priceCera} avg={average} "
                    + $"ratio={GoldConsignmentFeePolicy.RatioPercent(priceCera, average)}%");
                return "PriceBelowAverageFloor";
            }

            return null;
        }

        /// <summary>
        /// Net proceeds after the official tiered fee (see GoldConsignmentFeePolicy).
        /// The caller must pass the fee tier that was locked onto the listing.
        /// </summary>
        internal static int NetProceeds(
            int totalPriceCera, int feePercent = GoldConsignmentFeePolicy.NormalFeePercent)
            => GoldConsignmentFeePolicy.NetProceeds(totalPriceCera, feePercent);
    }
}
