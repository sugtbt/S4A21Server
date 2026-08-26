using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryItemExpirationService
    {
        internal static List<InventoryItem> FilterActive(
            IEnumerable<InventoryItem> items,
            AvatarDetailManager avatarDetails,
            long nowUnixTime)
        {
            var result = new List<InventoryItem>();
            if (items == null)
                return result;

            foreach (var item in items)
            {
                var avatarDetail = avatarDetails?.GetDetail(item.ItemUid);
                if (!IsExpired(item, avatarDetail, nowUnixTime))
                    result.Add(item);
            }

            return result;
        }

        internal static bool IsExpired(InventoryItem item, AvatarDetail avatarDetail, long nowUnixTime)
        {
            return IsExpired(item?.Core, avatarDetail, null, nowUnixTime);
        }

        internal static bool IsExpired(ItemCore core, AvatarDetail avatarDetail, long nowUnixTime)
        {
            return IsExpired(core, avatarDetail, null, nowUnixTime);
        }

        internal static bool IsExpired(
            ItemCore core,
            AvatarDetail avatarDetail,
            CreatureDetail creatureDetail,
            long nowUnixTime)
        {
            if (core == null)
                return true;

            if (core.ItemKind == ItemCore.KindAvatar)
                return avatarDetail != null
                    && avatarDetail.ExpireDate > 0
                    && avatarDetail.ExpireDate <= nowUnixTime;

            if (core.ItemKind == ItemCore.KindCreature)
                return creatureDetail != null
                    && creatureDetail.ExpireDate > 0
                    && creatureDetail.ExpireDate <= nowUnixTime;

            return core.ExpireTime > 0 && core.ExpireTime <= nowUnixTime;
        }
    }
}
