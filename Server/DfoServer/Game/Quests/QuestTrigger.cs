using System;

namespace DfoServer.Game.Quests
{
    public readonly struct QuestTrigger : IEquatable<QuestTrigger>
    {
        private const int ChannelWidth = 9;
        private const uint ChannelMask = 0x1FFu;

        public QuestTrigger(uint packedValue)
        {
            PackedValue = packedValue;
        }

        public uint PackedValue { get; }
        public bool IsComplete => PackedValue == 0;

        public int GetChannel(int channelIndex)
        {
            var shift = channelIndex * ChannelWidth;
            if (shift < 0 || shift > 18)
                return 0;
            return (int)((PackedValue >> shift) & ChannelMask);
        }

        public QuestTrigger ReplaceChannel(int channelIndex, long value)
        {
            var shift = channelIndex * ChannelWidth;
            if (shift < 0 || shift > 18)
                return this;

            var channel = value <= 0
                ? 0u
                : value >= ChannelMask
                    ? ChannelMask
                    : (uint)value;
            return new QuestTrigger(
                (PackedValue & ~(ChannelMask << shift)) | (channel << shift));
        }

        public QuestTrigger ApplyClientMutation(byte triggerType, bool increment)
        {
            if (triggerType == 1)
                return new QuestTrigger(AddSaturating(PackedValue, 1));
            if (triggerType == 0)
            {
                // type 0 没有通道位, 只递减 channel0, 到 0 即止。
                // 整包 -1 会在 channel0 已为 0 时向高位借位(512 -> 511,
                // channel0 变成 511), 把任务卡进无法完成的状态。
                if (increment)
                    return new QuestTrigger(AddSaturating(PackedValue, 1));

                return GetChannel(0) > 0 ? AdjustChannel(0, -1) : this;
            }

            var result = this;
            var delta = increment ? 1 : -1;
            if ((triggerType & 0x10) != 0)
                result = result.AdjustChannel(0, delta);
            if ((triggerType & 0x20) != 0)
                result = result.AdjustChannel(1, delta);
            if ((triggerType & 0x40) != 0)
                result = result.AdjustChannel(2, delta);
            return result;
        }

        public bool Equals(QuestTrigger other) => PackedValue == other.PackedValue;
        public override bool Equals(object obj) => obj is QuestTrigger other && Equals(other);
        public override int GetHashCode() => PackedValue.GetHashCode();
        public override string ToString() => PackedValue.ToString();
        public static implicit operator uint(QuestTrigger trigger) => trigger.PackedValue;
        public static explicit operator QuestTrigger(uint value) => new QuestTrigger(value);

        private QuestTrigger AdjustChannel(int channelIndex, int delta)
        {
            var current = GetChannel(channelIndex);
            var next = Math.Max(0, Math.Min((int)ChannelMask, current + delta));
            return ReplaceChannel(channelIndex, next);
        }

        private static uint AddSaturating(uint value, uint delta) =>
            uint.MaxValue - value < delta ? uint.MaxValue : value + delta;
    }
}
