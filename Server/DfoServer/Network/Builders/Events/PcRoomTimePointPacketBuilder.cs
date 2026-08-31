using DfoServer.Game.Events.PcRoomTimePoint;

namespace DfoServer.Network.Builders.Events
{
    internal static class PcRoomTimePointPacketBuilder
    {
        internal const int StateBodyLength = 17;
        internal const int AckBodyLength = 6;

        internal static byte[] BuildStateBody(PcRoomTimePointSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(snapshot?.DailyOnlineSecondsForClient ?? 0);
            writer.WriteUInt32((uint)System.Math.Max(0, snapshot?.PeriodCompletedCount ?? 0));
            writer.WriteByte(snapshot?.DailyClaimMask ?? 0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(snapshot?.PeriodClaimMaskForClient ?? 0);
            return writer.ToArray();
        }

        internal static byte[] BuildStatePacket(PcRoomTimePointSnapshot snapshot)
            => GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.PCROOM_TIME_POINT,
                BuildStateBody(snapshot));

        internal static byte[] BuildAckBody()
            => new byte[AckBodyLength];

        internal static byte[] BuildAckPacket()
            => GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.GET_PCROOM_TIME_POINT_ITEM,
                BuildAckBody());
    }
}
