using System;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    internal static class ItemListProtocolWriter
    {
        internal const int Entry84Size = 84;
        internal const int A21CommonEntryExtra = 17;
        internal const int A21CommonEntrySize = Entry84Size + A21CommonEntryExtra;
        internal const int A21AvatarJewelBytes = 18;
        internal const int A21AvatarEntrySize =
            Entry84Size + A21CommonEntryExtra + 4 + A21AvatarJewelBytes + 4 + 4;
        internal const int CommonEntrySize = A21CommonEntrySize;
        internal const int AvatarEntrySize = A21AvatarEntrySize;

        public static void WriteNoti2EquippedEntry(
            GamePacketWriter writer,
            short slot,
            ItemCore core,
            AvatarDetail avatarDetail,
            CreatureDetail creatureDetail = null)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (core == null)
                throw new ArgumentNullException(nameof(core));
            if (slot < 0 || slot > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(slot), slot, "NOTI2 装备槽位必须是 0-255。");

            writer.WriteByte((byte)slot);
            writer.WriteInt32(core.ItemId);
            writer.WriteUInt32(unchecked((uint)ResolveNoti2Value(core, avatarDetail)));
            writer.WriteByte(core.Attr);
            writer.WriteUInt16(core.Durability);
            writer.WriteUInt32(ResolveNoti2ClearAvatarOrSeal(core, avatarDetail));
            WriteEnchantBlock(writer, core);

            if (EquipmentTypeInfo.IsCostumeBarSlot(slot))
                WriteNoti2AvatarBlock(writer, core, avatarDetail);

            if (slot == (short)EquipmentType.Creature)
                writer.WriteUInt32(ResolveNoti2Marker16(core, creatureDetail));

            WriteNoti2ChronicleBlock(writer, core);
            writer.WriteInt32(core.ExpireTime);
            WriteNoti2EmblemBlock(writer, core);
            writer.WriteUInt16(core.Rune);
            WriteNoti2RandomOptionBlock(writer, core);
            WriteNoti2TailBlock(writer, core);
            WriteA21Noti2EntryTail(writer, slot, core);
        }

        public static void WriteCommonEntry84(GamePacketWriter writer, short slot, ItemCore core)
        {
            WriteCommonEntry84(writer, slot, core, ResolveWireInstanceValue(core));
        }

        public static void WriteCommonEntry84(GamePacketWriter writer, short slot, ItemCore core, int protocolValue)
        {
            WriteEntry84(
                writer,
                slot,
                core,
                protocolValue,
                ResolveCommonMarker16(core));
            WriteA21CommonTail(writer, core);
        }

        public static void WriteAvatarEntry126(
            GamePacketWriter writer,
            short slot,
            ItemCore core,
            AvatarDetail avatarDetail)
        {
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            var remainingSeconds = avatarDetail?.GetRemainDate() ?? 0;
            WriteEntry84(
                writer,
                slot,
                core,
                remainingSeconds,
                ResolveZeroDefaultMarker16(core.Marker16));
            WriteA21CommonTail(writer, core);
            writer.WriteInt32(A21AvatarJewelBytes);
            WriteFixedBytes(writer, avatarDetail?.JewelSocket, A21AvatarJewelBytes);
            writer.WriteInt32(4);
            writer.WriteUInt16(avatarDetail?.Color1 ?? 0);
            writer.WriteUInt16(avatarDetail?.Color2 ?? 0);
        }

        public static void WritePetEntry84(GamePacketWriter writer, short slot, ItemCore core)
        {
            WriteEntry84(
                writer,
                slot,
                core,
                core?.Value ?? 0,
                ResolveZeroDefaultMarker16(
                    core?.Marker16 ?? ItemCore.Marker16Default));
            WriteA21CommonTail(writer, core);
        }

        public static void WritePetCreatureEntry84(
            GamePacketWriter writer,
            short slot,
            ItemCore core,
            CreatureDetail creatureDetail)
        {
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            WriteEntry84(writer, slot, core, core.Value, ResolvePetCreatureMarker16(core, creatureDetail));
            WriteA21CommonTail(writer, core);
        }

        public static void WriteVirtualCountEntry84(GamePacketWriter writer, short slot, int itemId, int count)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            writer.WriteInt16(slot);
            writer.WriteInt32(itemId);
            writer.WriteInt32(count);
            writer.WriteZeroBytes(CommonEntrySize - 10);
        }

        public static void WriteEmptyEntry(GamePacketWriter writer, InventoryListType itemSpace, short slot)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            var entrySize = itemSpace == InventoryListType.Avatar
                ? AvatarEntrySize
                : CommonEntrySize;
            writer.WriteInt16(slot);
            writer.WriteInt32(-1);
            writer.WriteZeroBytes(entrySize - 6);
        }

        private static void WriteEntry84(
            GamePacketWriter writer,
            short slot,
            ItemCore core,
            int protocolValue,
            int protocolMarker16)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            writer.WriteInt16(slot);
            writer.WriteInt32(core.ItemId);
            writer.WriteInt32(protocolValue);
            writer.WriteByte(core.Attr);
            writer.WriteUInt16(core.Durability);
            writer.WriteByte(core.SealFlag);
            WriteEnchantBlock(writer, core);
            writer.WriteInt32(protocolMarker16);
            WriteChronicleBlock(writer, core);
            writer.WriteInt32(core.ExpireTime);
            WriteEmblemBlock(writer, core);
            WriteRandomOptionBlock(writer, core);
            WriteTailBlock(writer, core);
        }

        private static void WriteA21CommonTail(GamePacketWriter writer, ItemCore core)
        {
            writer.WriteUInt32(core?.A21Tail_Unknown84 ?? 0u);
            for (var index = 0; index < ItemCore.GuardianGemSlotCount; index++)
                writer.WriteUInt16(core?.GetGuardianGemKey(index) ?? (ushort)0);
            writer.WriteByte(core?.A21Tail_Unknown96 ?? 0);
            writer.WriteUInt32(core?.A21Tail_Unknown97 ?? 0u);
        }

        private static void WriteEnchantBlock(GamePacketWriter writer, ItemCore core)
        {
            writer.WriteInt32(core.EnchantCardId);
            writer.WriteByte(core.EnchantUpgradeCount);
            writer.WriteByte(core.AmplifyType);
            writer.WriteUInt16(core.AmplifyValue);
        }

        private static void WriteNoti2AvatarBlock(GamePacketWriter writer, ItemCore core, AvatarDetail avatarDetail)
        {
            if (core.ItemKind == ItemCore.KindAvatar)
            {
                writer.WriteInt32(A21AvatarJewelBytes);
                WriteFixedBytes(writer, avatarDetail?.JewelSocket, A21AvatarJewelBytes);
            }
            else
            {
                writer.WriteInt32(0);
            }

            writer.WriteInt32(4);
            writer.WriteUInt16(avatarDetail?.Color1 ?? 0);
            writer.WriteUInt16(avatarDetail?.Color2 ?? 0);
        }

        private static void WriteA21Noti2EntryTail(
            GamePacketWriter writer,
            short slot,
            ItemCore core)
        {
            if (EquipmentTypeInfo.IsCostumeBarSlot(slot)
                || slot == (short)EquipmentType.Creature)
            {
                writer.WriteZeroBytes(9);
                return;
            }

            if (slot == (short)EquipmentType.GuildMedal)
            {
                WriteA21Noti2GuildMedalTail(writer, core);
                return;
            }

            writer.WriteUInt32(0);
            writer.WriteUInt32(8);
            writer.WriteZeroBytes(9);
        }

        private static void WriteA21Noti2GuildMedalTail(GamePacketWriter writer, ItemCore core)
        {
            writer.WriteUInt32(core?.A21Tail_Unknown84 ?? 0u);
            var keyCount = core?.GetGuardianGemKeyWriteCount() ?? 0;
            writer.WriteUInt32((uint)(keyCount * 2));
            for (var index = 0; index < keyCount; index++)
                writer.WriteUInt16(core.GetGuardianGemKey(index));
            writer.WriteByte(core?.A21Tail_Unknown96 ?? 0);
        }

        private static void WriteNoti2ChronicleBlock(GamePacketWriter writer, ItemCore core)
        {
            var options = core.ChronicleOptions;
            var count = Math.Min(byte.MaxValue, options.Count);
            writer.WriteByte((byte)count);
            for (var index = 0; index < count; index++)
            {
                writer.WriteInt32(options[index].OptionId);
                writer.WriteByte(options[index].CharacJob);
                writer.WriteByte(options[index].FirstGrowType);
                writer.WriteByte(options[index].EquipmentType);
                writer.WriteByte(options[index].OptionNo);
            }
        }

        private static void WriteNoti2EmblemBlock(GamePacketWriter writer, ItemCore core)
        {
            var count = Math.Min(2, (int)core.EmblemSocketCount);
            writer.WriteByte((byte)count);
            if (count > 0)
                writer.WriteInt32(core.EmblemId1);
            if (count > 1)
                writer.WriteInt32(core.EmblemId2);
        }

        private static void WriteNoti2RandomOptionBlock(GamePacketWriter writer, ItemCore core)
        {
            RandomOptionProtocolWriter.WriteDynamic(writer, core);
        }

        private static void WriteNoti2TailBlock(GamePacketWriter writer, ItemCore core)
        {
            writer.WriteByte(core.GenuineUpgrade);
            writer.WriteByte(core.EmancipateEquipmentLevel);
            writer.WriteByte(core.TradeRestriction);
            writer.WriteUInt16(core.TailUnknown0);
            writer.WriteByte(core.TailUnknown1);
            writer.WriteByte(core.TailUnknown2);
            writer.WriteByte(core.TailUnknown3);
            writer.WriteByte(core.RemainUseCount);
            writer.WriteByte(0);
        }

        private static int ResolveNoti2Value(ItemCore core, AvatarDetail avatarDetail)
        {
            if (core.ItemKind == ItemCore.KindAvatar)
                return avatarDetail?.GetRemainDate() ?? 0;

            return ResolveWireInstanceValue(core);
        }

        private static int ResolveWireInstanceValue(ItemCore core)
        {
            if (core == null)
                return 0;
            if (core.ItemKind == ItemCore.KindEquipment && core.Value == 0)
                return unchecked((int)ItemQuality.TopQualitySeed);
            return core.Value;
        }

        private static uint ResolveNoti2ClearAvatarOrSeal(ItemCore core, AvatarDetail avatarDetail)
        {
            if (core.ItemKind == ItemCore.KindAvatar
                && avatarDetail != null
                && avatarDetail.ClearAvatarId != 0
                && ItemMetadataResolver.ShouldApplyCloneAppearance(core.ItemId))
                return unchecked((uint)avatarDetail.ClearAvatarId);

            return core.SealFlag;
        }

        private static uint ResolveNoti2Marker16(ItemCore core, CreatureDetail creatureDetail)
        {
            if (core.ItemKind == ItemCore.KindCreature)
                return unchecked((uint)ResolvePetCreatureMarker16(core, creatureDetail));

            return core.Marker16 == ItemCore.Marker16Default
                ? 0u
                : unchecked((uint)core.Marker16);
        }

        private static int ResolveCommonMarker16(ItemCore core)
        {
            if (core == null)
                return ItemCore.Marker16Default;

            // Equipment uses -1 as a meaningful wire sentinel. Legacy
            // stackable/material entries used zero, even though the unified
            // item_core representation stores their missing value as -1.
            return core.ItemKind == ItemCore.KindEquipment
                ? core.Marker16
                : ResolveZeroDefaultMarker16(core.Marker16);
        }

        private static int ResolveZeroDefaultMarker16(int marker16)
        {
            return marker16 == ItemCore.Marker16Default ? 0 : marker16;
        }

        private static int ResolvePetCreatureMarker16(ItemCore core, CreatureDetail creatureDetail)
        {
            var remainingSeconds = CreatureDetail.GetStaticRemainDate(core.ItemId);
            if (remainingSeconds <= 0 && creatureDetail != null)
                remainingSeconds = creatureDetail.GetRemainDate();
            if (remainingSeconds <= 0)
                remainingSeconds = ResolveStoredPetRemainSeconds(core);

            return Math.Max(0, remainingSeconds);
        }

        private static int ResolveStoredPetRemainSeconds(ItemCore core)
        {
            if (core.Marker16 > 0)
                return core.Marker16 >= 1_000_000_000
                    ? CreatureDetail.GetRemainDate(core.Marker16)
                    : core.Marker16;

            return core.ExpireTime > 0
                ? CreatureDetail.GetRemainDate(core.ExpireTime)
                : 0;
        }

        private static void WriteChronicleBlock(GamePacketWriter writer, ItemCore core)
        {
            var options = core.ChronicleOptions;
            writer.WriteByte((byte)Math.Min(2, options.Count));
            for (var index = 0; index < 2; index++)
                writer.WriteInt32(index < options.Count ? options[index].OptionId : 0);
            for (var index = 0; index < 2; index++)
                writer.WriteByte(index < options.Count ? options[index].CharacJob : (byte)0);
            for (var index = 0; index < 2; index++)
                writer.WriteByte(index < options.Count ? options[index].FirstGrowType : (byte)0);
            for (var index = 0; index < 2; index++)
                writer.WriteByte(index < options.Count ? options[index].EquipmentType : (byte)0);
            for (var index = 0; index < 2; index++)
                writer.WriteByte(index < options.Count ? options[index].OptionNo : (byte)0);
        }

        private static void WriteEmblemBlock(GamePacketWriter writer, ItemCore core)
        {
            writer.WriteByte(core.EmblemSocketCount);
            writer.WriteInt32(core.EmblemId1);
            writer.WriteInt32(core.EmblemId2);
            writer.WriteUInt16(core.Rune);
        }

        private static void WriteRandomOptionBlock(GamePacketWriter writer, ItemCore core)
        {
            var options = core.RandomOptions;
            writer.WriteByte((byte)Math.Min(3, options.Count));
            for (var index = 0; index < 3; index++)
                writer.WriteByte(index < options.Count ? options[index].Type : (byte)0);
            for (var index = 0; index < 3; index++)
                writer.WriteByte(index < options.Count ? options[index].Value1 : (byte)0);
            for (var index = 0; index < 3; index++)
                writer.WriteByte(index < options.Count ? options[index].Value2 : (byte)0);

            writer.WriteByte(core.RandomOptionState);
            writer.WriteByte(core.RandomOptionChangedIndex);
            writer.WriteByte(core.RandomOptionChangeState);
            writer.WriteByte(core.RandomOptionChange.Type);
            writer.WriteByte(core.RandomOptionChange.Value1);
            writer.WriteByte(core.RandomOptionChange.Value2);
        }

        private static void WriteTailBlock(GamePacketWriter writer, ItemCore core)
        {
            writer.WriteByte(core.GenuineUpgrade);
            writer.WriteByte(core.EmancipateEquipmentLevel);
            writer.WriteByte(core.TradeRestriction);
            writer.WriteUInt16(core.TailUnknown0);
            writer.WriteByte(core.TailUnknown1);
            writer.WriteByte(core.TailUnknown2);
            writer.WriteByte(core.TailUnknown3);
            writer.WriteByte(core.RemainUseCount);
            writer.WriteByte(core.SortLockFlag == 1 ? (byte)1 : (byte)0);
        }

        private static void WriteFixedBytes(GamePacketWriter writer, byte[] value, int length)
        {
            if (value == null || value.Length == 0)
            {
                writer.WriteZeroBytes(length);
                return;
            }

            if (value.Length == length)
            {
                writer.WriteBytes(value);
                return;
            }

            var buffer = new byte[length];
            Buffer.BlockCopy(value, 0, buffer, 0, Math.Min(value.Length, length));
            writer.WriteBytes(buffer);
        }
    }
}
