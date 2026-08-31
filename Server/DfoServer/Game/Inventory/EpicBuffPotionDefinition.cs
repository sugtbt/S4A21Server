namespace DfoServer.Game.Inventory
{
    internal static class EpicBuffPotionDefinition
    {
        internal const int BuffId = 1136;

        private static readonly int[] ItemIds =
        {
            490000413,
            490002458,
            490003224,
        };

        internal static bool IsItem(int itemTemplateId)
        {
            for (var index = 0; index < ItemIds.Length; index++)
            {
                if (ItemIds[index] == itemTemplateId)
                    return true;
            }

            return false;
        }

        internal static bool TryGetActiveEffectExpireTime(
            InventoryService inventory,
            long nowUnixSeconds,
            out int expireTime)
        {
            expireTime = 0;
            if (inventory == null)
                return false;

            for (var index = 0; index < ItemIds.Length; index++)
            {
                if (!inventory.ItemStates.TryGetExpireTime(
                        ItemStateKinds.Effect,
                        ItemIds[index],
                        out var candidate)
                    || candidate <= nowUnixSeconds)
                {
                    continue;
                }

                if (candidate > expireTime)
                    expireTime = candidate;
            }

            return expireTime > nowUnixSeconds;
        }

        internal static bool IsActiveForCharacter(
            int characterId,
            long nowUnixSeconds)
        {
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease))
            {
                return false;
            }

            lock (lease.SyncRoot)
            {
                return TryGetActiveEffectExpireTime(
                    lease.Inventory,
                    nowUnixSeconds,
                    out _);
            }
        }
    }
}
