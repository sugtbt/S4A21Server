using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders.Auction
{
    /// <summary>
    /// Builds the 0x00BA "金币搜索" reply so OTHER players can see consigned gold.
    ///
    /// The ordinary search reply is:
    ///     body[0] = 1, [2..6) reported total, [6..8) record count, then N * 168 bytes
    /// (AuctionOrdinaryBodyBuilder.SearchSuccess). Each 168-byte record is
    /// AuctionRecordBodyBuilder.Build(AuctionSearchRecordWire):
    ///     [0..8) identity, [8] wire8, [9..13) bid, [13..17) buyout, [17..21) arg21,
    ///     [21..34) seller tag, [34..42) wire34, [42] hours,
    ///     [43..160) native item (117B), [160..164) warning, [164..168) wire164
    ///
    /// Consigned gold is rendered as a virtual-currency item (itemId 0, count = gold),
    /// matching ItemListProtocolWriter.WriteVirtualCountEntry84, so it appears as 金币.
    /// Bidding is meaningless here: bid is written as 0 and only the buyout carries the
    /// price, exactly like the "my listings" reply.
    /// </summary>
    internal static class GoldConsignmentSearchBuilder
    {
        internal const int SearchRecordStride = 168;
        internal const int SearchHeaderLength = 8;
        internal const int MaxRecords = 60;

        /// <summary>
        /// body[1] window selector: 1 = consignment board (control 8), 0 = item auction
        /// (control 7). Same convention as the 0x00B7 listing ACK.
        /// </summary>
        internal const byte ConsignmentSelector = 1;

        /// <summary>
        /// 竞拍价的「无」哨兵。金币寄售是纯一口价，客户端只有收到这个值才会隐藏
        /// 「当前竞拍」这一行；写 0 会渲染成「当前竞拍 0 点券」（实机验证）。
        /// </summary>
        internal const uint NoBidSentinel = uint.MaxValue;

        /// <summary>
        /// Valid reply for "this ladder step has no listings": flag, selector 1, total 0,
        /// count 0. Returning a failure instead would make the client keep the previous
        /// page, and omitting the selector would route it to the item-auction window.
        /// </summary>
        internal static byte[] EmptyBody()
        {
            var body = new byte[SearchHeaderLength];
            body[0] = 1;
            body[1] = ConsignmentSelector;
            return body;
        }

        /// <summary>
        /// Builds the reply body. Returns null when there is nothing to show so the
        /// caller can fall back to the ordinary (empty) search reply.
        /// </summary>
        internal static byte[] TryBuild(
            IReadOnlyList<GoldConsignmentBoardRow> rows,
            Func<GoldConsignmentBoardRow, (byte[] native83, byte[] additional30, byte[] additional4)> snapshotFor)
        {
            if (rows == null || rows.Count == 0) return null;
            var count = Math.Min(rows.Count, MaxRecords);

            var body = new byte[SearchHeaderLength + SearchRecordStride * count];
            body[0] = 1;
            // body[1] is the WINDOW SELECTOR, not a reserved byte. Native 0x00BA ACK
            // 0x0111F970 (success path):
            //     0x0111FB7E  mov al,[ebp+0xf]        ; body[1]
            //     0x0111FB81  cmp al,1
            //     0x0111FB9A  call 0x1458970          ; ==1 -> control 8 (consignment)
            //     0x0111FBCA  push 7 ; 0x23009B0      ; ==0 -> control 7 (item auction)
            // Sending 0 routed every row to the item-auction window, which is closed,
            // so the consignment board always reported 0 results.
            body[1] = ConsignmentSelector;
            // Reported total drives the client's page counter.
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2, 4), (uint)rows.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6, 2), (ushort)count);

            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                var tag = SellerTag(row.SellerName);
                if (tag.Length != 13) return null;

                var parts = snapshotFor(row);
                if (!AuctionNativeItemWireSnapshot.TryCreate(
                        parts.native83, parts.additional30, parts.additional4, out var item))
                    return null;

                if (!AuctionSearchRecordWire.TryCreate(
                        (ulong)row.ListingId,
                        0,                              // wire8
                        // 纯一口价：竞拍价写 0xFFFFFFFF（"无"哨兵），客户端才会整行隐藏
                        // 「当前竞拍」标签。实机确认：写 0 会显示「当前竞拍 0 点券」，
                        // 写 0xFFFFFFFF 该行消失。
                        NoBidSentinel,
                        (uint)Math.Max(0, row.TotalPriceCera),
                        0,                              // arg21
                        tag,
                        0ul,                            // wire34
                        (byte)Math.Clamp(row.DurationHours, 0, 255),
                        item,
                        0,                              // warning tier
                        0,                              // wire164
                        out var record))
                    return null;

                var bytes = AuctionRecordBodyBuilder.Build(record);
                if (bytes.Length != SearchRecordStride) return null;
                bytes.CopyTo(body, SearchHeaderLength + SearchRecordStride * i);
            }

            return body;
        }

        /// <summary>
        /// 13-byte seller tag. Delegates to the canonical encoder so the client can
        /// decode it: the name starts at offset 0 and the tail is zero-filled, with a
        /// NUL terminator at index &gt;= 1. Hand-rolling a 2-byte prefix produced
        /// "\0\0Name...", whose terminator sits at index 0, which the client rejects -
        /// that dropped every row and the board showed 0 results.
        /// </summary>
        internal static byte[] SellerTag(byte[] rawName)
        {
            if (rawName == null || rawName.Length == 0) return Array.Empty<byte>();
            // characters.name is already in the client's encoding; convert through the
            // project's strict codec so the tag matches AuctionSellerNameTag13.
            string name;
            try
            {
                name = System.Text.Encoding.GetEncoding(936).GetString(rawName);
            }
            catch
            {
                return Array.Empty<byte>();
            }
            return AuctionSellerNameTag13.TryEncodeFrozenRoleName(name, out var tag)
                ? tag : Array.Empty<byte>();
        }
    }
}
