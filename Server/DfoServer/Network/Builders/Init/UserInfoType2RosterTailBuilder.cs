namespace DfoServer.Network.Builders
{
    internal static class UserInfoType2RosterTailBuilder
    {
        // fatigue 后第 4 字节。选角模型 bit0=觉醒装扮、bit1=转职特效；选角默认全开。
        internal const int A21DisplayStateBitsOffset = 24;
        internal const byte SelectScreenDisplayStateBits = 0x03;

        public static void WriteA21(GamePacketWriter writer, uint cloneTitleItemId)
        {
            writer.WriteUInt32(cloneTitleItemId);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(SelectScreenDisplayStateBits);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0xFF);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0);
        }
    }
}
