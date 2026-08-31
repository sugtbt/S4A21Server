using System;
using DfoServer.Game.Events.DailyAttendanceAnytime;

namespace DfoServer.Network.Builders.Events
{
    internal static class DailyAttendanceAnytimePacketBuilder
    {
        internal const int StateBodyLength = 37;

        internal static byte[] BuildStateBody(
            DailyAttendanceAnytimeSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)DailyAttendanceAnytimeConfig.EventId);
            writer.WriteByte(0);
            writer.WriteUInt32(ToClientUInt32(snapshot?.TotalAttendanceCount ?? 0));
            writer.WriteUInt32(snapshot?.AccumulateState0 ?? 0);
            writer.WriteUInt32(snapshot?.AccumulateState1 ?? 0);
            writer.WriteUInt32(snapshot?.AccumulateState2 ?? 0);
            writer.WriteUInt32(ToClientUInt32(
                snapshot?.TodayRecommendClearCount ?? 0));
            writer.WriteUInt32(3);
            writer.WriteUInt32(4);
            writer.WriteUInt32(5);
            return writer.ToArray();
        }

        internal static byte[] BuildStatePacket(
            DailyAttendanceAnytimeSnapshot snapshot)
            => GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.INTEGRATE_EVENT_DATA,
                BuildStateBody(snapshot));

        private static uint ToClientUInt32(int value)
            => (uint)Math.Max(0, value);
    }
}
