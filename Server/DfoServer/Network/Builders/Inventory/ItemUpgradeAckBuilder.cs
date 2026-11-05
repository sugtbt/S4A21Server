using DfoServer.Game.ItemUpgrade;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class ItemUpgradeAckBuilder
    {
        public static byte[] BuildSuccess(ItemUpgradeResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteByte((byte)result.Method);
            writer.WriteInt16(result.MaterialSlotIndex);
            writer.WriteInt32(result.MaterialRemainingStackCount);
            writer.WriteInt16(result.OptionalTicketSlotIndex);
            writer.WriteByte(0x00);
            writer.WriteByte(result.OldLevel);
            writer.WriteByte(result.ResultCode);
            writer.WriteByte(result.NewLevel);
            // 0113D950 passes this byte and the following slot to 016F0000.
            writer.WriteByte((byte)result.TargetListType);
            writer.WriteInt16(result.TargetSlotIndex);
            writer.WriteInt16(result.OptionalTicketSlotIndex);
            if (result.ResultCode == 3)
            {
                var rewardCount = result.DestroyRewardItems.Count > byte.MaxValue ? byte.MaxValue : result.DestroyRewardItems.Count;
                writer.WriteByte((byte)rewardCount);
                for (var i = 0; i < rewardCount; i++)
                {
                    var reward = result.DestroyRewardItems[i];
                    writer.WriteInt16(reward.SlotIndex);
                    writer.WriteInt32(reward.ItemTemplateId);
                    writer.WriteInt32(reward.Count);
                }

                return writer.ToArray();
            }

            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(errorCode);
            return writer.ToArray();
        }
    }
}
