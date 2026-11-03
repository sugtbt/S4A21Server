using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public sealed class ItemUpgradeRequest
    {
        public ItemUpgradeMethod Method { get; set; }
        public ItemUpgradeMode Mode { get; set; }
        public InventoryListType TargetListType { get; set; }
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public short MaterialSlotIndex { get; set; }
        public short OptionalTicketSlotIndex { get; set; } = -1;
        public string TargetItemName { get; set; }

        public static bool TryParse(byte[] body, out ItemUpgradeRequest request)
        {
            request = null;
            if (body == null || body.Length < 16)
                return false;

            // 016FD060/016FD7F0 write two u8 values, not a single u16 method.
            var rawMethod = body[0];
            var listType = (InventoryListType)body[1];
            if (listType != InventoryListType.Main && listType != InventoryListType.GuildMedal)
                return false;
            // method=5 是「幸运强化券/幸运增幅券」共用的 UI 命令，
            // 真正走强化还是增幅由材料格里的券决定，这里不能直接按越界拒绝。
            var lukcyDeed = rawMethod == (ushort)ItemUpgradeMethod.LuckyEnchantDeed;
            if (rawMethod > (ushort)ItemUpgradeMethod.AdvancedReinforce && !lukcyDeed)
                return false;

            var nameLength = BitConverter.ToInt32(body, 12);
            if (nameLength < 0 || nameLength > body.Length - 16)
                return false;

            request = new ItemUpgradeRequest
            {
                Method = (ItemUpgradeMethod)rawMethod,
                TargetListType = listType,
                Mode = lukcyDeed
                    ? ItemUpgradeMode.Reinforce
                    : rawMethod == (ushort)ItemUpgradeMethod.Amplify
                        ? ItemUpgradeMode.Amplify
                        : ItemUpgradeMode.Reinforce,
                TargetSlotIndex = BitConverter.ToInt16(body, 2),
                TargetItemTemplateId = BitConverter.ToInt32(body, 4),
                MaterialSlotIndex = BitConverter.ToInt16(body, 8),
                OptionalTicketSlotIndex = BitConverter.ToInt16(body, 10),
                TargetItemName = nameLength > 0 ? ClientTextEncoding.GetString(body, 16, nameLength) : string.Empty,
            };
            return true;
        }

        public ItemUpgradeCommand ToCommand()
        {
            return new ItemUpgradeCommand
            {
                Method = Method,
                TargetListType = TargetListType,
                Mode = Mode,
                TargetSlotIndex = TargetSlotIndex,
                TargetItemTemplateId = TargetItemTemplateId,
                MaterialSlotIndex = MaterialSlotIndex,
                OptionalTicketSlotIndex = OptionalTicketSlotIndex,
                TargetItemName = TargetItemName,
            };
        }
    }
}
