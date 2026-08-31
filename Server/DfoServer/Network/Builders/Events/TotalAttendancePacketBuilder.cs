using System;
using DfoServer.Game.Events.TotalAttendance;

namespace DfoServer.Network.Builders.Events
{
    internal static class TotalAttendancePacketBuilder
    {
        internal const int StateBodyLength = 450;
        internal const int CheckThisWeekAckBodyLength = 4;

        private const int TotalAttendanceCountOffset = 0x004;
        private const int AttendanceWeekSequenceOffset = 0x198;
        private const int AttendanceWeekSequenceLength = 20;
        private const int RecommendClearCountOffset = 0x1B0;
        private const int ReservedOffset = 0x1B8;
        private const int CurrentEventWeekNoOffset = 0x1BC;
        private const int CheckButtonFlagOffset = 0x1C0;
        private const int BonusModeFlagOffset = 0x1C1;

        internal static byte[] BuildStateBody(TotalAttendanceSnapshot snapshot)
        {
            var body = new byte[StateBodyLength];
            WriteUInt32(
                body,
                TotalAttendanceCountOffset,
                ToClientUInt32(snapshot?.TotalAttendanceWeekCount ?? 0));
            WriteAttendanceWeekSequence(
                body,
                snapshot?.TotalAttendanceWeekCount ?? 0);
            WriteUInt32(
                body,
                RecommendClearCountOffset,
                ToClientUInt32(snapshot?.ThisWeekRecommendClearCount ?? 0));
            WriteUInt32(body, ReservedOffset, 0);
            WriteUInt32(
                body,
                CurrentEventWeekNoOffset,
                ToClientUInt32(snapshot?.CurrentEventWeekNo ?? 1));
            body[CheckButtonFlagOffset] =
                snapshot?.CanCheckThisWeek == true ? (byte)1 : (byte)0;
            body[BonusModeFlagOffset] = 0;
            return body;
        }

        internal static byte[] BuildStatePacket(TotalAttendanceSnapshot snapshot)
            => GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.EVENT_TOTAL_ATTENDANCE,
                BuildStateBody(snapshot));

        internal static byte[] BuildCheckThisWeekAckBody(uint resultCode)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(resultCode);
            return writer.ToArray();
        }

        internal static byte[] BuildCheckThisWeekAckPacket(uint resultCode)
            => GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.EVENT_TOTAL_ATTENDANCE_CHECK_THISWEEK,
                BuildCheckThisWeekAckBody(resultCode));

        private static void WriteAttendanceWeekSequence(
            byte[] body,
            int totalAttendanceWeekCount)
        {
            var count = Math.Min(
                AttendanceWeekSequenceLength,
                Math.Max(0, totalAttendanceWeekCount));
            for (var index = 0; index < count; index++)
                body[AttendanceWeekSequenceOffset + index] = (byte)(index + 1);
        }

        private static void WriteUInt32(byte[] body, int offset, uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, body, offset, bytes.Length);
        }

        private static uint ToClientUInt32(int value)
            => (uint)Math.Max(0, value);
    }
}
