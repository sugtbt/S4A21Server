using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Auction;

namespace DfoServer.Network.Parsers.Auction
{
    /// <summary>
    /// Strict ordinary-auction framing. Source: .scratch/auction-audit-20261023/m0/protocol/wire-map.json.
    /// Sender VAs: 0232FF82, 0232F519, 02323032, 02325450, 02340254, 023403B0,
    /// 02337952, 023378D9, 023255BB. No semantic validation or history layout is inferred.
    /// </summary>
    public static class AuctionRequestParser
    {
        public static bool TryParse(CmdPacketTypeA21 command, ReadOnlySpan<byte> body, out AuctionRequest request)
        {
            request = null;

            // ---- Gold consignment (金币寄售) --------------------------------
            // Independent feature reusing the same opcodes with shorter bodies.
            // Recognised before the ordinary selector gate because the consignment
            // sender emits a leading 1 instead of the ordinary selector 0.
            if (!body.IsEmpty && body[0] == 0x01)
            {
                switch (command)
                {
                    case CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE
                        when body.Length == GoldConsignmentWire.AveragePriceBodyLength:
                        request = new GoldConsignAveragePriceRequest(U32(body, 1), body[5]);
                        return true;
                    case CmdPacketTypeA21.AUCTION_REGIST_ITEM
                        when body.Length == GoldConsignmentWire.RegisterBodyLength:
                        request = new GoldConsignRegisterRequest(U32(body, 4), U32(body, 8), U32(body, 12),
                            U32(body, 16), U32(body, 20), U32(body, 24));
                        return true;
                    // Gold "搜索" tab. Captured 14-byte body:
                    //   01 | 00000000 | 00 | 1F | 01(count) | A59C(u16) | 7DEB2800(key)
                    // = the item-auction search minus its 8 trailing footer bytes, with
                    // the gold marker as key[0].
                    case CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY
                        when body.Length == GoldConsignmentWire.SearchBodyLength:
                        request = new GoldConsignSearchRequest(U32(body, 10));
                        return true;
                    // Consignment PURCHASE ("购买"). Captured 17-byte body:
                    //   01 | price 1000 | listingId 16 | 0 | itemId 2681725
                    // Shorter than the item-auction bidding body (31B), same selector-1
                    // convention, so it must be recognised before the ordinary gate.
                    case CmdPacketTypeA21.AUCTION_BIDDING
                        when body.Length == GoldConsignmentWire.PurchaseBodyLength:
                        request = new GoldConsignPurchaseRequest(
                            U32(body, 1), U32(body, 5), U32(body, 9), U32(body, 13));
                        return true;
                }
            }

            // APIECE has no selector. All other supported senders write ordinary selector zero.
            if (command != CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE && (body.IsEmpty || body[0] != 0))
                return false;
            switch (command)
            {
                case CmdPacketTypeA21.AUCTION_ASK_AVERAGE_PRICE when body.Length == 8:
                    request = new AuctionAveragePriceRequest(U32(body, 1), body[5], body[6], body[7]);
                    break;
                case CmdPacketTypeA21.AUCTION_REGIST_ITEM when body.Length == 37:
                    request = new AuctionRegisterRequest(body[1], U16(body, 2), U32(body, 4), U32(body, 8),
                        U32(body, 12), U32(body, 16), U32(body, 20), U32(body, 24),
                        Tuple(body, 28), Tuple(body, 31), Tuple(body, 34));
                    break;
                case CmdPacketTypeA21.AUCTION_REGIST_CANCEL when body.Length == 9:
                    request = new AuctionCancelRequest(U64(body, 1));
                    break;
                case CmdPacketTypeA21.AUCTION_BIDDING when body.Length == 26:
                    request = new AuctionBidRequest(U32(body, 1), U64(body, 5), Copy(body.Slice(13, 13)));
                    break;
                case CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY:
                    if (body.Length < 18 || body.Length != 18 + 4 * body[7]) return false;
                    var keys = ImmutableArray.CreateBuilder<uint>(body[7]);
                    for (var index = 0; index < body[7]; index++) keys.Add(U32(body, 10 + 4 * index));
                    var tail = 10 + 4 * body[7];
                    // UI+226/+227 derive from numeric input controls (0234110C..113A),
                    // not an evidenced sort enum or tail flag. Preserve both bytes.
                    request = new AuctionSearchByItemKeyRequest(U32(body, 1), body[5], body[6], U16(body, 8),
                        keys.MoveToImmutable(), U16(body, tail), U16(body, tail + 2), U16(body, tail + 4),
                        body[tail + 6], body[tail + 7]);
                    break;
                case CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY when body.Length == 20:
                    // UI+233/+234 are the same input pair (02341236..126A).
                    request = new AuctionSearchWithoutItemKeyRequest(U32(body, 1), U16(body, 5),
                        body[7], body[8], body[9], body[10], body[11], U16(body, 12), U16(body, 14),
                        U16(body, 16), body[18], body[19]);
                    break;
                case CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO when body.Length == 1:
                    request = new AuctionMyListingsRequest();
                    break;
                case CmdPacketTypeA21.AUCTION_MY_BIDDING_INFO when body.Length == 1:
                    request = new AuctionMyBidsRequest();
                    break;
                case CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE when body.Length == 33:
                    // VA02325628..0232563B writes native83 item-key getter02918FB0 as finalu32.
                    request = new AuctionBuyApieceRequest(U32(body, 0), U32(body, 4), U64(body, 8),
                        Copy(body.Slice(16, 13)), U32(body, 29));
                    break;
                default:
                    return false; // Includes AUCTION_MY_AUCTION_HISTORY: no evidenced sender/ACK.
            }
            return true;
        }

        private static ushort U16(ReadOnlySpan<byte> body, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
        private static uint U32(ReadOnlySpan<byte> body, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);
        private static ulong U64(ReadOnlySpan<byte> body, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(body[offset..]);
        private static AuctionFilterTuple Tuple(ReadOnlySpan<byte> body, int offset) => new(U16(body, offset), body[offset + 2]);
        private static ImmutableArray<byte> Copy(ReadOnlySpan<byte> body) => ImmutableArray.CreateRange(body.ToArray());
    }
}
