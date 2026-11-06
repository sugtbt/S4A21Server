namespace DfoServer.Game.Inventory
{
    public sealed class SpecialRewardOutcome
    {
        public SpecialRewardKind Kind { get; set; }
        public int ItemTemplateId { get; set; }
        public int Count { get; set; }
        public short WalletSlot { get; set; }
        public int WalletNewTotal { get; set; }
    }

    public enum SpecialRewardKind
    {
        Premium,
        ReviveCoin,
        HappyTokenCera,
        /// <summary>代币券: the account currency accounts.token_cera.</summary>
        TokenCera,
        EpicPiece,
    }

    internal static class SpecialRewardRouter
    {
        // Current PVF: stackable/cash/chn_Trade_Gold/trade_point_event.stk.
        internal const int HappyTokenCeraVoucherItemId = 2681917;

        // Current PVF: stackable/cash/chn_Trade_Gold/trade_point.stk (name 代币券).
        // This voucher must become accounts.token_cera instead of a bag item: the client
        // hard-codes the id at 0x0117E503 and reports the gain from its own currency
        // object, so a bag item only produced a bogus "获得0代币券" prompt and left a
        // dead item behind.
        internal const int TokenCeraVoucherItemId = 2681762;

        internal static bool TryResolveAccountCurrencyReward(
            int itemTemplateId,
            int count,
            out SpecialRewardOutcome outcome)
        {
            outcome = null;
            if (count <= 0)
                return false;

            if (itemTemplateId == HappyTokenCeraVoucherItemId)
            {
                outcome = new SpecialRewardOutcome
                {
                    Kind = SpecialRewardKind.HappyTokenCera,
                    ItemTemplateId = itemTemplateId,
                    Count = count,
                };
                return true;
            }

            if (itemTemplateId == TokenCeraVoucherItemId)
            {
                outcome = new SpecialRewardOutcome
                {
                    Kind = SpecialRewardKind.TokenCera,
                    ItemTemplateId = itemTemplateId,
                    Count = count,
                };
                return true;
            }

            return false;
        }
    }
}
