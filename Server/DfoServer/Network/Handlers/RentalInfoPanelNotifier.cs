using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal static class RentalInfoPanelNotifier
    {
        internal const ushort NotiRental =
            (ushort)NotiPacketTypeA21.EQUIPMENT_RENTAL_LIST;

        // Lucky-star or rental-item changes refresh the complete A21 rental panel state.
        internal static async Task SyncAsync(
            EnhancedClientSession session,
            int characterId,
            ushort luckyStar,
            IRentalTimeProvider rentalTimeProvider)
        {
            if (session == null || characterId <= 0)
                return;

            var rental = BuildOnlineRentalInfo(characterId)
                ?? new RentalInfoSnapshot();
            var now = (rentalTimeProvider ?? SystemRentalTimeProvider.Instance).UtcNowUnixSeconds();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, NotiRental,
                RentalInfoBodyBuilder.BuildWireBody(luckyStar, rental, now)));
        }

        private static RentalInfoSnapshot BuildOnlineRentalInfo(int characterId)
        {
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease))
                return null;

            var now = InventoryItemLifecycleService.UtcNowUnixSeconds();
            var rental = new RentalInfoSnapshot();
            lock (lease.SyncRoot)
            {
                AddOnlineRentalItems(
                    lease.Inventory,
                    rental,
                    InventoryListType.Equipment,
                    short.MinValue,
                    short.MaxValue,
                    now);
                AddOnlineRentalItems(
                    lease.Inventory,
                    rental,
                    InventoryListType.Main,
                    InventoryCharacterLifecycleService.RentalMainSlotStart,
                    InventoryCharacterLifecycleService.RentalMainSlotEnd,
                    now);
            }

            return rental;
        }

        private static void AddOnlineRentalItems(
            InventoryService inventory,
            RentalInfoSnapshot rental,
            InventoryListType listType,
            short slotStart,
            short slotEnd,
            long nowUnixSeconds)
        {
            if (inventory == null || rental == null)
                return;

            foreach (var pair in inventory.GetItems(listType))
            {
                if (pair.Key < slotStart || pair.Key > slotEnd)
                    continue;

                var core = pair.Value;
                if (core == null
                    || core.ExpireTime <= nowUnixSeconds
                    || !RentalWeaponInventoryMapper.IsValidInventoryTemplate(core.ItemId))
                    continue;

                var itemId = unchecked((uint)core.ItemId);
                rental.UpsertItem(itemId, itemId, unchecked((uint)core.ExpireTime));
            }
        }
    }
}
