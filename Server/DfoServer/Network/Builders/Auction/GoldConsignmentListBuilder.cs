using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DfoServer.Game.Auction;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders.Auction
{
    /// <summary>
    /// Builds the 0x00BC "my consignment listings" reply body.
    ///
    /// Native ACK 0x0111FC90 (the table-1 handler, i.e. the one that also serves
    /// 0x00B7 and therefore the consignment session) reads:
    ///     0x0111FCB8  cmp byte [ebp+0xc],0 ; je fail   ; body[0] = result flag
    ///     0x0111FCC8  read u8             -> body[1]
    ///     0x0111FCD3  read u8             -> count
    ///     0x0111FCE0  mov edx,0x97 ; mul  -> count * 151 bytes
    /// so the body is:  flag | selector | count | count * 151-byte records.
    ///
    /// The 151-byte record layout is NOT guessed here: it is the same structure the
    /// ordinary auction already emits, implemented by AuctionRecordBodyBuilder
    /// (native 0232DDC4..0232E02F):
    ///     [0..8)   identity (u64)
    ///     [8..12)  current/minimum bid
    ///     [12..16) buyout total
    ///     [16..29) seller tag (13 bytes)
    ///     [29]     display hours
    ///     [30..147) native item snapshot (117 bytes)
    ///     [147..151) native arg21
    /// </summary>
    internal static class GoldConsignmentListBuilder
    {
        /// <summary>Consignment selector byte, same convention as the 0x00B7 ACK.</summary>
        internal const byte Selector = 1;

        internal const byte SuccessFlag = 1;
        internal const int RecordStride = 151;

        /// <summary>
        /// Builds the reply for <paramref name="listings"/>. When the seller has no
        /// consignment rows this returns <c>null</c> so the caller can fall back to
        /// the ordinary auction listings reply unchanged.
        /// </summary>
        internal static byte[] TryBuild(
            IReadOnlyList<GoldConsignmentListing> listings,
            ReadOnlySpan<byte> sellerTag,
            Func<GoldConsignmentListing, (byte[] native83, byte[] additional30, byte[] additional4)> snapshotFor)
        {
            if (listings == null || listings.Count == 0) return null;
            if (listings.Count > 255) return null;
            if (sellerTag.Length != 13) return null;

            var body = new byte[3 + RecordStride * listings.Count];
            body[0] = SuccessFlag;
            body[1] = Selector;
            body[2] = (byte)listings.Count;

            for (var i = 0; i < listings.Count; i++)
            {
                var listing = listings[i];
                var parts = snapshotFor(listing);
                if (!AuctionNativeItemWireSnapshot.TryCreate(
                        parts.native83, parts.additional30, parts.additional4, out var item))
                    return null;

                // Gold consignment is BUYOUT-ONLY. The bid field carries the "no bid"
                // sentinel; writing 0 made the client render "当前竞拍 0 点券" under the
                // buyout line, while 0xFFFFFFFF hides that row (verified in game).
                if (!AuctionMyListingsRecordWire.TryCreate(
                        (ulong)listing.ListingId,
                        GoldConsignmentSearchBuilder.NoBidSentinel,
                        (uint)Math.Max(0, listing.TotalPriceCera),
                        sellerTag,
                        (byte)Math.Clamp(listing.DurationHours, 0, 255),
                        item,
                        0,
                        out var wire))
                    return null;

                var record = AuctionRecordBodyBuilder.Build(wire);
                if (record.Length != RecordStride) return null;
                record.CopyTo(body, 3 + RecordStride * i);
            }

            return body;
        }
    }
}
