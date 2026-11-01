using System;
using System.Collections.Generic;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// The gold consignment ladder, taken from PVF
    /// <c>stackable/cash/chn_trade_gold</c>.
    ///
    /// Each dropdown step is a REAL item id, and the client sends the selected one at
    /// body[4..8) of the 0x00B7 register packet. Live captures line up exactly:
    ///     0x0028EB7D = 2681725 = 1百万
    ///     0x0028EB7E = 2681726 = 2百万
    ///     0x0028EB7F = 2681727 = 3百万
    /// This corrects an earlier (wrong) conclusion that the byte was a fixed "gold
    /// marker" or a session cursor, and that the amount had to be derived from the
    /// cera price via an exchange rate.
    /// </summary>
    internal static class GoldLadder
    {
        /// <summary>itemId -> gold amount.</summary>
        private static readonly Dictionary<uint, int> Amounts = new()
        {
            [2681725] = 1_000_000,    // 1百万   0x0028EB7D
            [2681726] = 2_000_000,    // 2百万   0x0028EB7E
            [2681727] = 3_000_000,    // 3百万   0x0028EB7F
            [2681728] = 4_000_000,    // 4百万   0x0028EB80
            [2681729] = 5_000_000,    // 5百万   0x0028EB81
            [2681730] = 6_000_000,    // 6百万   0x0028EB82
            [2681731] = 7_000_000,    // 7百万   0x0028EB83
            [2681732] = 8_000_000,    // 8百万   0x0028EB84
            [2681733] = 9_000_000,    // 9百万   0x0028EB85
            [2681734] = 10_000_000,   // 1千万   0x0028EB86
            [2681735] = 20_000_000,   // 2千万   0x0028EB87
            [2681736] = 30_000_000,   // 3千万   0x0028EB88
            [2683069] = 40_000_000,   // 4千万   0x0028F0BD
            [2683070] = 50_000_000,   // 5千万   0x0028F0BE
            [2683071] = 100_000_000,  // 1亿     0x0028F0BF
            [2683072] = 150_000_000,  // 1.5亿   0x0028F0C0
            [2683073] = 200_000_000,  // 2亿     0x0028F0C1
        };

        /// <summary>All ladder item ids, ascending, for iterating the dropdown.</summary>
        internal static IReadOnlyCollection<uint> ItemIds => Amounts.Keys;

        /// <summary>
        /// Gold amount for a ladder item id. Returns 0 when the id is not a gold
        /// ladder item, which is how a malformed packet is rejected.
        /// </summary>
        internal static int GoldForItem(uint itemId)
            => Amounts.TryGetValue(itemId, out var gold) ? gold : 0;

        /// <summary>
        /// Ladder item id for a gold amount, or 0 when the amount is not a ladder
        /// step. Used when projecting a stored listing back onto the wire: the client
        /// resolves the row through this item id, so writing 0 (virtual currency)
        /// makes it render as generic "金币" instead of the actual consigned item.
        /// </summary>
        internal static uint ItemForGold(int gold)
        {
            foreach (var pair in Amounts)
                if (pair.Value == gold) return pair.Key;
            return 0;
        }

        internal static bool IsLadderItem(uint itemId) => Amounts.ContainsKey(itemId);

        /// <summary>
        /// 档位显示名，用于官方成交邮件正文「已成功拍卖[100万]」。
        /// 注意官方正文按「万」计数（100万 / 1000万），与道具名「1百万」不同。
        /// 返回 false 表示该金额不是档位值。
        /// </summary>
        internal static bool TryGetLabel(int gold, out string label)
        {
            label = gold switch
            {
                1_000_000 => "100万",
                2_000_000 => "200万",
                3_000_000 => "300万",
                4_000_000 => "400万",
                5_000_000 => "500万",
                6_000_000 => "600万",
                7_000_000 => "700万",
                8_000_000 => "800万",
                9_000_000 => "900万",
                10_000_000 => "1000万",
                20_000_000 => "2000万",
                30_000_000 => "3000万",
                40_000_000 => "4000万",
                50_000_000 => "5000万",
                100_000_000 => "1亿",
                150_000_000 => "1.5亿",
                200_000_000 => "2亿",
                _ => string.Empty
            };
            return label.Length > 0;
        }

        /// <summary>Largest gold amount the ladder offers (2亿).</summary>
        internal const int MaxGold = 200_000_000;

        /// <summary>Smallest gold amount the ladder offers (1百万).</summary>
        internal const int MinGold = 1_000_000;
    }
}
