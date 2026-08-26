using DfoServer.Game.Inventory;
using System;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class ItemListPacketBuilder
    {
        public static byte[] BuildBody(int characterId, int accountId, InventoryListType listType)
        {
            var lease = GetOnlineInventoryLease(characterId);
            lock (lease.SyncRoot)
                return BuildItemSpaceListBody(lease.Inventory, listType);
        }

        internal static byte[] BuildItemSpaceListBody(InventoryService inventory, InventoryListType listType)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            switch (listType)
            {
                case InventoryListType.Main:
                    return BuildMainItemListBody(inventory);
                case InventoryListType.Avatar:
                    return BuildAvatarItemListBody(inventory);
                case InventoryListType.PersonalCargo:
                    return BuildPersonalCargoItemListBody(inventory);
                case InventoryListType.Equipment:
                    return BuildEquipmentItemListBody(inventory);
                case InventoryListType.Pet:
                    return BuildPetItemListBody(inventory);
                case InventoryListType.AccountCargo:
                    return BuildAccountCargoItemListBody(inventory);
                case InventoryListType.GuildMedal:
                    return BuildGuildMedalItemListBody(inventory);
                default:
                    throw new ArgumentOutOfRangeException(nameof(listType), listType, "Unsupported inventory list type.");
            }
        }

        internal static byte[] BuildMainItemListBody(InventoryService inventory)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            var entries = new GamePacketWriter();
            ushort count = 0;
            var nowUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (short slotIndex = InventoryService.MainVirtualCurrencySlotStart;
                slotIndex <= InventoryService.MainVirtualCurrencySlotEnd;
                slotIndex++)
                WriteMainVirtualCountEntry(entries, inventory, slotIndex, ref count);

            foreach (var item in inventory.GetItems(InventoryListType.Main))
            {
                if (TryWriteOnlineEntry(entries, inventory, InventoryListType.Main, item.Key, item.Value, nowUnixTime))
                    count++;
            }

            for (short slotIndex = InventoryService.MainVirtualCubeSlotStart;
                slotIndex <= InventoryService.MainVirtualCubeSlotEnd;
                slotIndex++)
                WriteMainVirtualCountEntry(entries, inventory, slotIndex, ref count);

            for (short slotIndex = InventoryService.MainVirtualSoulSlotStart;
                slotIndex <= InventoryService.MainVirtualSoulSlotEnd;
                slotIndex++)
                WriteMainVirtualCountEntry(entries, inventory, slotIndex, ref count);

            return BuildCommonListBody(
                InventoryListType.Main,
                inventory.GetListParam16(InventoryListType.Main),
                count,
                entries);
        }

        internal static byte[] BuildAvatarItemListBody(InventoryService inventory)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            var entries = new GamePacketWriter();
            ushort count = 0;
            var nowUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var item in inventory.GetItems(InventoryListType.Avatar))
            {
                if (TryWriteOnlineEntry(entries, inventory, InventoryListType.Avatar, item.Key, item.Value, nowUnixTime))
                    count++;
            }

            return BuildCommonListBody(
                InventoryListType.Avatar,
                inventory.GetListParam16(InventoryListType.Avatar),
                count,
                entries);
        }

        internal static byte[] BuildPetItemListBody(InventoryService inventory)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            return BuildShortItemListBody(inventory, InventoryListType.Pet);
        }

        internal static byte[] BuildGuildMedalItemListBody(InventoryService inventory)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            return BuildShortItemListBody(inventory, InventoryListType.GuildMedal);
        }

        private static byte[] BuildShortItemListBody(InventoryService inventory, InventoryListType listType)
        {
            var entries = new GamePacketWriter();
            ushort count = 0;
            var nowUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var item in inventory.GetItems(listType))
            {
                if (TryWriteOnlineEntry(entries, inventory, listType, item.Key, item.Value, nowUnixTime))
                    count++;
            }

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)listType);
            writer.WriteUInt16(count);
            writer.WriteBytes(entries.ToArray());
            return writer.ToArray();
        }

        internal static byte[] BuildPersonalCargoItemListBody(InventoryService inventory)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            return BuildCommonContainerFromInventory(
                inventory,
                InventoryListType.PersonalCargo,
                inventory.GetListParam16(InventoryListType.PersonalCargo));
        }

        internal static byte[] BuildEquipmentItemListBody(InventoryService inventory)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            return BuildCommonContainerFromInventory(
                inventory,
                InventoryListType.Equipment,
                inventory.GetListParam16(InventoryListType.Equipment));
        }

        internal static byte[] BuildAccountCargoItemListBody(InventoryService inventory)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));

            var entries = new GamePacketWriter();
            ushort count = 0;
            var nowUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var item in inventory.GetItems(InventoryListType.AccountCargo))
            {
                if (TryWriteOnlineEntry(entries, inventory, InventoryListType.AccountCargo, item.Key, item.Value, nowUnixTime))
                    count++;
            }

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)InventoryListType.AccountCargo);
            writer.WriteUInt16(inventory.AccountCargo.SelectionKey);
            writer.WriteInt32(inventory.AccountCargo.Money);
            writer.WriteUInt16(count);
            writer.WriteBytes(entries.ToArray());
            return writer.ToArray();
        }

        internal static bool TryWriteOnlineEntry(
            GamePacketWriter writer,
            InventoryService inventory,
            InventoryListType itemSpace,
            short slotIndex,
            ItemCore core,
            long nowUnixTime)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (core == null || core.IsEmpty)
                return false;

            AvatarDetail avatarDetail = null;
            if (core.ItemKind == ItemCore.KindAvatar)
                avatarDetail = inventory.AvatarDetails.GetDetail(core.Value);
            CreatureDetail creatureDetail = null;
            if (core.ItemKind == ItemCore.KindCreature)
                creatureDetail = inventory.CreatureDetails.GetDetail(core.Value);
            if (InventoryItemExpirationService.IsExpired(core, avatarDetail, creatureDetail, nowUnixTime))
                return false;

            if (core.ItemKind == ItemCore.KindAvatar)
            {
                ItemListProtocolWriter.WriteAvatarEntry126(writer, slotIndex, core, avatarDetail);
                return true;
            }

            if (core.ItemKind == ItemCore.KindCreature)
            {
                ItemListProtocolWriter.WritePetCreatureEntry84(
                    writer,
                    slotIndex,
                    core,
                    creatureDetail);
                return true;
            }

            ItemListProtocolWriter.WriteCommonEntry84(writer, slotIndex, core);
            return true;
        }

        internal static InventoryLease GetOnlineInventoryLease(int characterId)
        {
            if (!InventoryContext.TryGetLease(characterId, out var lease))
                throw new InvalidOperationException("Online inventory is not registered for this character.");

            return lease;
        }

        private static byte[] BuildCommonContainerFromInventory(
            InventoryService inventory,
            InventoryListType listType,
            ushort listParam16)
        {
            var entries = new GamePacketWriter();
            ushort count = 0;
            var nowUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var item in inventory.GetItems(listType))
            {
                if (TryWriteOnlineEntry(entries, inventory, listType, item.Key, item.Value, nowUnixTime))
                    count++;
            }

            return BuildCommonListBody(listType, listParam16, count, entries);
        }

        private static byte[] BuildCommonListBody(
            InventoryListType listType,
            ushort listParam16,
            ushort count,
            GamePacketWriter entries)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)listType);
            writer.WriteUInt16(listParam16);
            writer.WriteUInt16(count);
            writer.WriteBytes(entries.ToArray());
            return writer.ToArray();
        }

        internal static void WriteMainVirtualCountEntry(
            GamePacketWriter writer,
            InventoryService inventory,
            short slotIndex,
            ref ushort count)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (!InventoryService.TryResolveMainVirtualItemId(slotIndex, out var itemId))
                return;

            var virtualItem = inventory.GetMainVirtualCount(slotIndex);
            ItemListProtocolWriter.WriteVirtualCountEntry84(
                writer,
                slotIndex,
                itemId,
                virtualItem?.Count ?? 0);
            count++;
        }
    }
}
