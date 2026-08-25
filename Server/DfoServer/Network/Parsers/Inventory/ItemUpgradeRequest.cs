using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public sealed class ItemUpgradeRequest
    {
        public ItemUpgradeMethod Method { get; set; }
        public ItemUpgradeMode Mode { get; set; }
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

            var rawMethod = BitConverter.ToUInt16(body, 0);
            if (rawMethod > (ushort)ItemUpgradeMethod.AdvancedReinforce)
                return false;

            var nameLength = BitConverter.ToInt32(body, 12);
            if (nameLength < 0 || 16 + nameLength > body.Length)
                return false;

            request = new ItemUpgradeRequest
            {
                Method = (ItemUpgradeMethod)rawMethod,
                Mode = rawMethod == (ushort)ItemUpgradeMethod.Amplify
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
