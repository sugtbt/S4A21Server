using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    public static class DropItemBuilder
    {
        // A21 GET_ITEM 的金币通知固定为 117B；模板来自当前 A21 教程抓包。
        // 运行时只覆盖场景槽、拾取者和金币数量，其他字段保持客户端期望的布局。
        private static readonly byte[] A21PickupGoldTemplate = Hex(
            "660039040001080000000100000000000000000100000000010000000000000000" +
            "010000000001000000000000000001000000000100000000000000000100000000" +
            "010000000000000000010000000001000000000000000001000000000100000000" +
            "000000000100000000010000000000000000");
        
        
        public static byte[] BuildDrop(
            ushort dropperActorId,
            ushort positionX,
            ushort positionY,
            Game.Dungeon.DropInfo drop,
            ushort ownerActorId)
        {
            var w = new GamePacketWriter();

            
            w.WriteUInt16(dropperActorId);    
            w.WriteUInt16(positionX);
            w.WriteUInt16(positionY);
            w.WriteUInt16(drop.SceneSlot);
            WriteGroundItemEntry(w, drop);
            w.WriteByte(0);
            w.WriteUInt16(ownerActorId);

            return w.ToArray();
        }

        public static byte[] BuildDropSuccessAck(byte listType, ushort slotIndex, int count)
        {
            var w = new GamePacketWriter();
            w.WriteByte(1);
            w.WriteByte(listType);
            w.WriteUInt16(slotIndex);
            w.WriteInt32(count);
            return w.ToArray();
        }

        public static byte[] BuildDropFailureAck(byte errorCode, byte listType)
        {
            var w = new GamePacketWriter();
            w.WriteByte(0);
            w.WriteByte(errorCode);
            w.WriteByte(listType);
            return w.ToArray();
        }

        public static byte[] BuildGetItemSuccessAck()
        {
            return new byte[] { 0x01 };
        }

        public static byte[] BuildPickupItem(ushort sceneSlot, ushort pickerActorId, ushort dstInvSlot, byte moveFlag)
        {
            var w = new GamePacketWriter();

            w.WriteUInt16(sceneSlot);
            w.WriteUInt16(pickerActorId);

            // A21: flag + two dwords + picker + destination slot + reserved byte.
            w.WriteByte(1);
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteUInt16(pickerActorId);
            w.WriteUInt16(dstInvSlot);
            w.WriteByte(0);

            return w.ToArray();
        }

        public static byte[] BuildPickupEpicPiece(ushort sceneSlot, ushort pickerActorId)
        {
            return BuildPickupItem(sceneSlot, pickerActorId, 0, 7);
        }

        public static byte[] BuildPickupGold(ushort sceneSlot, ushort pickerActorId, int goldAmount, int extraGold = 0)
        {
            var body = (byte[])A21PickupGoldTemplate.Clone();
            WriteUInt16(body, 0, sceneSlot);
            WriteUInt16(body, 2, pickerActorId);
            WriteInt32(body, 6, goldAmount > 0 ? goldAmount : 1);
            return body;
        }

        private static void WriteUInt16(byte[] destination, int offset, ushort value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteInt32(byte[] destination, int offset, int value)
        {
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteGroundItemEntry(GamePacketWriter writer, Game.Dungeon.DropInfo drop)
        {
            var core = CreateGroundItemCore(drop);
            if (InventoryStackRuleService.IsStackable(core))
                core.Count = NormalizeStackCount(drop.StackCount);

            ItemListProtocolWriter.WriteCommonEntry84(
                writer,
                drop.SourceSlotIndex,
                core);
        }

        private static ItemCore CreateGroundItemCore(Game.Dungeon.DropInfo drop)
        {
            if (drop.Core != null)
                return drop.Core.Copy();

            var itemId = unchecked((int)drop.TemplateId);
            var itemKind = ItemCore.KindConsumable;
            if (itemId > 0
                && !ItemMetadataResolver.TryResolveItemKind(itemId, out itemKind))
            {
                itemKind = ItemCore.KindConsumable;
            }

            var core = ItemCore.Create(itemKind, itemId);
            core.Count = NormalizeStackCount(drop.StackCount);
            core.Durability = drop.Endurance;
            core.Upgrade = drop.UpgradeLevel;
            return core;
        }

        private static int NormalizeStackCount(uint stackCount)
        {
            if (stackCount == 0)
                return 1;

            return stackCount > int.MaxValue ? int.MaxValue : (int)stackCount;
        }

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var index = 0; index < bytes.Length; index++)
                bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
            return bytes;
        }
    }
}
