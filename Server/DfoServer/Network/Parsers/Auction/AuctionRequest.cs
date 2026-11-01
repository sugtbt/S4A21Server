using System.Collections.Immutable;

namespace DfoServer.Network.Parsers.Auction
{
    // Immutable wire DTOs only. WireN denotes request offset; FieldN denotes the
    // evidenced source UI offset when a variable request tail has no fixed offset.
    public abstract record AuctionRequest;
    public sealed record AuctionAveragePriceRequest(uint ItemKey, byte AttributeA, byte AttributeB, byte AttributeC) : AuctionRequest;
    public readonly record struct AuctionFilterTuple(ushort Field, byte Value);
    public sealed record AuctionRegisterRequest(byte Space, ushort Slot, uint ItemKey, uint Quantity,
        uint Wire12, uint Wire16, uint Wire20, uint Wire24,
        AuctionFilterTuple FilterA, AuctionFilterTuple FilterB, AuctionFilterTuple FilterC) : AuctionRequest;
    public sealed record AuctionCancelRequest(ulong IdentityBits) : AuctionRequest;
    public sealed record AuctionBidRequest(uint Amount, ulong IdentityBits, ImmutableArray<byte> SellerTag) : AuctionRequest;
    public sealed record AuctionSearchByItemKeyRequest(uint Position, byte Wire5, byte Wire6, ushort Wire8,
        ImmutableArray<uint> ItemKeys, ushort FilterA, ushort FilterB, ushort FilterC, byte Field226, byte Field227) : AuctionRequest;
    public sealed record AuctionSearchWithoutItemKeyRequest(uint Position, ushort Wire5,
        byte Wire7, byte Wire8, byte Wire9, byte Wire10, byte Wire11,
        ushort FilterA, ushort FilterB, ushort FilterC, byte Field233, byte Field234) : AuctionRequest;
    public sealed record AuctionMyListingsRequest : AuctionRequest;
    public sealed record AuctionMyBidsRequest : AuctionRequest;
    public sealed record AuctionBuyApieceRequest(uint Amount, uint Quantity, ulong IdentityBits,
        ImmutableArray<byte> SellerTag, uint ItemKey) : AuctionRequest;

    // ---- Gold consignment (金币寄售) -------------------------------------
    // Independent feature that reuses the ordinary auction opcodes with a
    // shorter body.
    //
    // Register layout (0x00B7, 28 bytes) was recovered statically from the
    // current client sender 0x01456F50. The sender emits, in order:
    //     B(1) B(0) C(0) D(ebx) D(1) D(-1) D(edi) D(0) D([esi+0x1dc])
    // where writer widths are proven from each writer body
    // (add [ecx+0x2bcc2c],N / push N):
    //     B = 1 byte, C = 2 bytes, D = 4 bytes
    // 1 + 1 + 2 + 6*4 = 28, matching the captured body exactly:
    //     [0]      u8   = 1        marker the sender hardcodes
    //     [1]      u8   = 0        (space: 0 = Main)
    //     [2..4)   u16  = 0        (slot: 0 = Main virtual slot 0 = GOLD)
    //     [4..8)   u32  = 0x28EB7D gold marker (ebx)
    //     [8..12)  u32  = 1        quantity
    //     [12..16) u32  = -1       constant -1 in the sender (never a UI value)
    //     [16..20) u32  = amount   edi = atoi(amount input) or -1
    //     [20..24) u32  = 0        constant 0
    //     [24..28) u32  = 24       duration hours ([esi+0x1dc])
    //
    // [16..20) is the GOLD AMOUNT: the sender loads the text control at
    // [esi+0x184], validates every character is '0'..'9', parses it with
    // 0x2C65A29 -> strtol(str, NULL, 10), and writes the result; a zero/empty
    // input is forced to -1. The capture shows -1 only because the amount box
    // was empty at that moment.
    public sealed record GoldConsignAveragePriceRequest(uint ItemKey, byte AttributeA) : AuctionRequest;

    /// <summary>
    /// Consignment "金币搜索" (0x00BA, 14B): <paramref name="Marker"/> is the gold
    /// marker carried as the single search key (0x28EB7D).
    /// </summary>
    public sealed record GoldConsignSearchRequest(uint Marker) : AuctionRequest;

    /// <summary>
    /// Consignment PURCHASE (0x00B9, 17-byte body). Captured while clicking 购买 on a
    /// 1百万 listing:
    ///     01 | E8 03 00 00 | 10 00 00 00 | 00 00 00 00 | 7D EB 28 00
    ///     sel   price=1000    listingId=16   reserved      itemId=2681725
    /// It is the item-auction bidding opcode with the consignment's shorter body and a
    /// leading selector 1. The price is re-validated server-side against the stored
    /// listing, so a tampered packet cannot underpay.
    /// </summary>
    public sealed record GoldConsignPurchaseRequest(
        uint PriceCera, uint ListingId, uint Reserved, uint LadderItemId) : AuctionRequest;

    /// <summary>
    /// Consignment register (0x00B7, 28B). Offsets are absolute into the body.
    /// <c>AmountWire == uint.MaxValue</c> means the client left the amount empty.
    /// </summary>
    public sealed record GoldConsignRegisterRequest(
        uint Marker,        // [4..8)   0x28EB7D
        uint Quantity,      // [8..12)
        uint ConstantNeg1,  // [12..16) sender constant -1
        uint AmountWire,    // [16..20) gold amount, or -1 when the input is empty
        uint Reserved,      // [20..24)
        uint DurationHours) // [24..28)
        : AuctionRequest
    {
        /// <summary>True when the client actually sent a usable gold amount.</summary>
        public bool HasAmount => AmountWire != uint.MaxValue && AmountWire > 0;

        /// <summary>Gold amount in gold units, or 0 when absent/overflow.</summary>
        public int GoldAmount => HasAmount && AmountWire <= int.MaxValue
            ? (int)AmountWire : 0;
    }
}
