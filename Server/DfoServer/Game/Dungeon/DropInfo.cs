using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    public struct DropInfo
    {
        public ushort SceneSlot;
        public uint TemplateId;
        public uint StackCount;
        // A21 DIE_MONSTER 条目 +16..19：同一掉落组共享的 Unix 秒级标识。
        // 由掉落生成/注册边界写入，Builder 只负责序列化。
        public uint DropGroupId;
        public ushort Endurance;
        public byte UpgradeLevel;
        internal ItemCore Core;
        public short SourceSlotIndex;
        public bool IsPlayerDropped;

        public bool IsGold => TemplateId == 0;

        public uint PacketValue
        {
            get
            {
                return Core == null
                    ? StackCount
                    : unchecked((uint)Core.Value);
            }
        }

        internal static DropInfo CreateGold(ushort sceneSlot, int goldAmount)
        {
            return new DropInfo
            {
                SceneSlot = sceneSlot,
                TemplateId = 0,
                StackCount = (uint)Math.Max(0, goldAmount),
            };
        }

        internal static DropInfo CreateItem(ushort sceneSlot, int itemId, int count)
        {
            var safeCount = Math.Max(1, count);
            var drop = new DropInfo
            {
                SceneSlot = sceneSlot,
                TemplateId = (uint)Math.Max(0, itemId),
                StackCount = (uint)safeCount,
            };

            if (itemId <= 0)
                return drop;

            if (InventoryRewardGrantService.TryCreateOnly(
                    itemId,
                    ItemCreateReason.DungeonDrop,
                    safeCount,
                    out var created)
                && created != null
                && created.Core != null)
            {
                drop.Core = created.Core.Copy();
                drop.TemplateId = (uint)drop.Core.ItemId;
                drop.Endurance = drop.Core.Durability;
                drop.UpgradeLevel = drop.Core.Upgrade;
                return drop;
            }

            try
            {
                var metadata = ItemMetadataResolver.Resolve(itemId);
                drop.Endurance = metadata != null ? metadata.Durability : (ushort)0;
            }
            catch
            {
            }

            return drop;
        }
    }
}
