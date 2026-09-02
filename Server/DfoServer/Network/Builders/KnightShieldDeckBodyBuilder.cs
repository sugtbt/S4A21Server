using DfoServer.Game.KnightShield;

namespace DfoServer.Network.Builders
{
    public static class KnightShieldDeckBodyBuilder
    {
        public const ushort DeckNotificationType = (ushort)NotiPacketTypeA21.SEND_DECK_INFO;
        public const ushort ChangeDeckCommandType = (ushort)CmdPacketTypeA21.CHANGE_DECK_INFO;
        public const int DeckBodyLength = KnightShieldDeckSnapshot.SlotCount * sizeof(int);
        public const int ChangeDeckAckLength = (2 * sizeof(byte)) + DeckBodyLength;
        public const int ChangeDeckAckStatusOffset = 0x00;
        public const int ChangeDeckAckReservedOffset = 0x01;
        public const int ChangeDeckAckSlotsOffset = 0x02;

        public static byte[] BuildDeck(KnightShieldDeckSnapshot snapshot)
        {
            snapshot ??= new KnightShieldDeckSnapshot();
            var writer = new GamePacketWriter();

            // 0x0245 固定 5 个 u32：槽 0 主盾，槽 1-4 备用盾，无数量前缀。
            for (var slotIndex = 0; slotIndex < KnightShieldDeckSnapshot.SlotCount; slotIndex++)
                writer.WriteInt32(snapshot.GetShieldItemId(slotIndex));
            return writer.ToArray();
        }

        public static byte[] BuildChangeDeckAck(KnightShieldDeckSnapshot snapshot)
        {
            snapshot ??= new KnightShieldDeckSnapshot();
            var writer = new GamePacketWriter();

            writer.WriteByte(1); // +0x00 [u8] 成功
            writer.WriteByte(0); // +0x01 [u8] 保留

            for (var slotIndex = 0; slotIndex < KnightShieldDeckSnapshot.SlotCount; slotIndex++)
                writer.WriteInt32(snapshot.GetShieldItemId(slotIndex)); // +0x02+slotIndex*4 [i32]
            return writer.ToArray();
        }
    }
}
