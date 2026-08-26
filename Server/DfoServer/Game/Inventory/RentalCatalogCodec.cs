using System;

namespace DfoServer.Game.Inventory
{
    /// A21 租赁商店幸运星请求字段和业务上限。
    public static class RentalCatalogCodec
    {
        public const int ChargeRentPointRequestSize = 22;
        public const int ChargeRentPointModeOffset = 14;
        public const int ChargeRentPointQuantityOffset = 18;
        public const int MaxLuckyStar = 999;
        public const int GoldCostPerStar = 100_000;

        public static bool TryParseShopPacketBuyCount(byte[] body, out int buyCount)
        {
            buyCount = 0;
            if (body == null || body.Length < ChargeRentPointRequestSize)
                return false;

            var mode = BitConverter.ToInt32(body, ChargeRentPointModeOffset);
            buyCount = BitConverter.ToInt32(body, ChargeRentPointQuantityOffset);
            return mode == 1 && IsValidBuyCount(buyCount);
        }

        private static bool IsValidBuyCount(int count) => count > 0 && count <= MaxLuckyStar;
    }
}
