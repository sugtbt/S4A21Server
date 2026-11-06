using System;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// Gold consignment (金币寄售) wire constants.
    ///
    /// Consignment is an independent feature that happens to reuse the ordinary
    /// auction opcodes (0x00B6 / 0x00B7) with shorter bodies. Evidence captured
    /// 2026-10-30 against the current A21 client:
    ///
    ///   0x00B6 ASK_AVERAGE_PRICE -> body 6B  (item-auction requires 8B)
    ///        01 | itemKey:u32 | attrA:u8
    ///   0x00B7 REGIST_ITEM      -> body 28B (item-auction requires 37B)
    ///        01 00 00 00 | 7D EB 28 00 | 01 00 00 00 | ...
    ///
    /// The itemKey observed in both is 0x28EB7D (2681725). It is far outside the
    /// item-id space and repeats across unrelated requests, so it is treated as
    /// the fixed "gold" marker rather than an inventory template id.
    /// </summary>
    public static class GoldConsignmentWire
    {
        /// <summary>
        /// The 1百万 ladder item id (0x0028EB7D). Retained for the SelfTest that pins
        /// the captured marker; the full table lives in <see cref="GoldLadder"/>.
        /// </summary>
        public const uint GoldMarker = 0x0028EB7D;

        /// <summary>
        /// The cera (代币券) currency item id from PVF
        /// <c>stackable/cash/chn_trade_gold</c>.
        /// </summary>
        public const uint CeraItemId = 2681762;

        /// <summary>Body length of the consignment price-reference request.</summary>
        public const int AveragePriceBodyLength = 6;

        /// <summary>Body length of the consignment registration request.</summary>
        public const int RegisterBodyLength = 28;

        /// <summary>
        /// Body length of the consignment "金币搜索" request. Captured 14 bytes:
        ///   01 | 00000000 | 00 | 1F | 01(count) | A59C(u16) | 7DEB2800(gold key)
        /// i.e. the item-auction search body minus its trailing 8 footer bytes.
        /// </summary>
        public const int SearchBodyLength = 14;

        /// <summary>
        /// Body length of the consignment cancel ("取消上架") request. Captured 9 bytes:
        ///   01 | listingId:u64
        /// </summary>
        public const int CancelBodyLength = 9;

        /// <summary>
        /// Body length of the consignment purchase ("购买"). Captured 17 bytes:
        ///   selector(1) | priceCera(4) | listingId(4) | reserved(4) | ladderItemId(4)
        /// </summary>
        public const int PurchaseBodyLength = 17;

        public static bool IsGoldMarker(uint itemKey) => itemKey == GoldMarker;

        /// <summary>
        /// Suggested "latest sale price" reported to the client, in cera points.
        /// Operator-tunable via GOLD_CONSIGN_REFERENCE_PRICE while the real order
        /// book is still being built; the client keeps the buyout/upload inputs
        /// disabled when this reads zero.
        /// </summary>
        public static uint ReferencePrice
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("GOLD_CONSIGN_REFERENCE_PRICE");
                return uint.TryParse(raw, out var v) && v > 0 ? v : 1000u;
            }
        }

        /// <summary>
        /// ctrlId selector byte of the 0x00B6 success body at body[1].
        ///
        /// Native handler 0x0112CBB0:
        ///     0x0112CBED  mov al,[ebp+0xf]     ; = body[1]
        ///     0x0112CBF3  cmp al,1
        ///     0x0112CBF7  mov [ebp-0x1c],8     ; ==1 -> ctrlId 8 -> setter 0x1456910
        ///     0x0112CC04  mov [ebp-0x1c],7     ; ==0 -> ctrlId 7 -> setter 0x2335F20
        ///
        /// NOTE: body[0] is consumed by the dispatcher and does NOT affect this
        /// selector. Both selectors are legitimate paths.
        ///
        /// In-game observation: with body[1]=0 (ctrlId 7) the "latest sale price"
        /// label still read 0. ctrlId 8 is the remaining branch; the default is 1
        /// so the newer path is exercised. Tunable via GOLD_CONSIGN_PRICE_CTRL.
        /// </summary>
        public static byte PriceSelectorByte
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("GOLD_CONSIGN_PRICE_CTRL");
                return byte.TryParse(raw, out var v) && v <= 1 ? v : (byte)1;
            }
        }
    }
}
