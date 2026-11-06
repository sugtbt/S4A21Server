using System;

namespace DfoServer.Game.Auction
{
    /// <summary>
    /// 金币寄售手续费，按官方规则实现（dnf.qq.com 活动页「金币寄售系统」）。
    ///
    /// 手续费由「一口价 ÷ 平均价格」的比例决定，不是固定值：
    ///   低于平均价 40%      -> 无法上架
    ///   40% ~ 150%          -> 5%（使用拍卖行优惠券后 1%）
    ///   150% ~ 160%         -> 30%（优惠券无效）
    ///   160% ~ 170%         -> 40%
    ///   170% ~ 180%         -> 50%
    ///   高于 180%           -> 95%
    ///
    /// 边界按区间下带上限闭合处理：150% 归入 5% 档（官方写作「40%-150%」）。
    /// </summary>
    internal static class GoldConsignmentFeePolicy
    {
        /// <summary>低于该比例直接禁止上架。</summary>
        internal const int MinimumRatioPercent = 40;

        /// <summary>正常档上限；超过后进入惩罚档。</summary>
        internal const int NormalBandMaxPercent = 150;

        internal const int NormalFeePercent = 5;
        internal const int CouponFeePercent = 1;

        /// <summary>相对平均价的百分比（向下取整）。平均价无效时返回 0。</summary>
        internal static int RatioPercent(int priceCera, int averageCera)
        {
            if (averageCera <= 0) return 0;
            var ratio = (long)priceCera * 100 / averageCera;
            if (ratio > int.MaxValue) return int.MaxValue;
            return (int)ratio;
        }

        /// <summary>
        /// 计算上架应适用的手续费百分比。
        /// 返回 false 表示按官方规则不可上架（价格低于平均价 40%，或平均价不可用）。
        /// </summary>
        internal static bool TryResolveFeePercent(
            int priceCera, int averageCera, bool couponActive, out int feePercent)
        {
            feePercent = 0;
            if (priceCera <= 0 || averageCera <= 0) return false;

            // Compare price*100 against average*bound with integer math instead of
            // pre-dividing: 1801/1000 must count as 180.1%, not truncate to 180%.
            var scaled = (long)priceCera * 100;
            var avg = (long)averageCera;

            if (scaled < avg * MinimumRatioPercent) return false;

            if (scaled <= avg * NormalBandMaxPercent)
            {
                // 只有正常档吃优惠券；惩罚档官方明确「优惠券无效」。
                feePercent = couponActive ? CouponFeePercent : NormalFeePercent;
                return true;
            }

            if (scaled <= avg * 160) { feePercent = 30; return true; }
            if (scaled <= avg * 170) { feePercent = 40; return true; }
            if (scaled <= avg * 180) { feePercent = 50; return true; }
            feePercent = 95;
            return true;
        }

        /// <summary>卖家实得代币券 = 一口价 × (100 - 手续费%) / 100，向下取整。</summary>
        internal static int NetProceeds(int priceCera, int feePercent)
        {
            if (priceCera <= 0) return 0;
            var fee = Math.Clamp(feePercent, 0, 100);
            var net = (long)priceCera * (100 - fee) / 100;
            if (net < 0) return 0;
            if (net > int.MaxValue) return int.MaxValue;
            return (int)net;
        }
    }
}
