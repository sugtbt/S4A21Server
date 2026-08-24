namespace DfoServer.Game.Skills
{
    using DfoServer.Game.Inventory;

    public static class SkillResetConsumableService
    {
        public const int ForgetRiverWaterItemTemplateId = 3;
        public const int TpResetBookItemTemplateId = 1206;
        public const int EventTpResetBookItemTemplateId = 1253;

        internal static bool TryResolveRefundConsumable(
            InventoryService inventory,
            bool tpOnlyRefund,
            out int itemTemplateId)
        {
            itemTemplateId = tpOnlyRefund
                ? EventTpResetBookItemTemplateId
                : ForgetRiverWaterItemTemplateId;
            if (inventory == null)
                return false;

            if (!tpOnlyRefund)
                return inventory.CountMainItem(itemTemplateId) > 0;

            if (inventory.CountMainItem(EventTpResetBookItemTemplateId) > 0)
                return true;

            itemTemplateId = TpResetBookItemTemplateId;
            return inventory.CountMainItem(itemTemplateId) > 0;
        }

        internal static bool TryConsumeRefundConsumable(
            InventoryService inventory,
            bool tpOnlyRefund,
            out int itemTemplateId,
            out InventoryMainItemConsumeResult consumed)
        {
            consumed = null;
            if (!TryResolveRefundConsumable(
                    inventory,
                    tpOnlyRefund,
                    out itemTemplateId))
            {
                return false;
            }

            return inventory.TryConsumeMainItem(
                    itemTemplateId,
                    1,
                    out consumed)
                && consumed != null
                && consumed.Success;
        }
    }
}
