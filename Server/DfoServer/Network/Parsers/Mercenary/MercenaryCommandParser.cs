using System;

namespace DfoServer.Network.Parsers.Mercenary
{
    // 客户端对冒险团相关 CMD 会填充到固定缓冲长度；只校验已确认字段的最小长度。
    internal readonly struct RequestCharacSkillInfoCommand
    {
        internal RequestCharacSkillInfoCommand(ushort wireSlotEcho)
        {
            WireSlotEcho = wireSlotEcho;
        }

        internal ushort WireSlotEcho { get; }

        internal byte WireSlot => (byte)(WireSlotEcho & 0xFF);
    }

    internal readonly struct SelectStrikerCommand
    {
        internal SelectStrikerCommand(byte wireSlot, ushort skillId)
        {
            WireSlot = wireSlot;
            SkillId = skillId;
        }

        internal byte WireSlot { get; }

        internal ushort SkillId { get; }
    }

    internal readonly struct MercenaryReturnCommand
    {
        internal MercenaryReturnCommand(byte purpose, int characterId)
        {
            Purpose = purpose;
            CharacterId = characterId;
        }

        internal byte Purpose { get; }

        internal int CharacterId { get; }
    }

    internal readonly struct MercenaryCompetitionCommand
    {
        internal MercenaryCompetitionCommand(int characterId, byte areaIndex, byte periodIndex)
        {
            CharacterId = characterId;
            AreaIndex = areaIndex;
            PeriodIndex = periodIndex;
        }

        internal int CharacterId { get; }

        internal byte AreaIndex { get; }

        internal byte PeriodIndex { get; }
    }

    internal static class MercenaryCommandParser
    {
        internal const int SkillInfoMinimumBodyLength = 2;
        internal const int SelectStrikerMinimumBodyLength = 3;
        internal const int ReturnMinimumBodyLength = 5;
        internal const int CompetitionMinimumBodyLength = 6;

        internal static bool TryParseSkillInfo(byte[] body, out RequestCharacSkillInfoCommand command)
        {
            command = default;
            if (body == null || body.Length < SkillInfoMinimumBodyLength)
                return false;

            command = new RequestCharacSkillInfoCommand(BitConverter.ToUInt16(body, 0));
            return true;
        }

        internal static bool TryParseSelectStriker(byte[] body, out SelectStrikerCommand command)
        {
            command = default;
            if (body == null || body.Length < SelectStrikerMinimumBodyLength)
                return false;

            command = new SelectStrikerCommand(
                body[0],
                BitConverter.ToUInt16(body, 1));
            return true;
        }

        internal static bool TryParseReturn(byte[] body, out MercenaryReturnCommand command)
        {
            command = default;
            if (body == null || body.Length < ReturnMinimumBodyLength)
                return false;

            command = new MercenaryReturnCommand(
                body[0],
                BitConverter.ToInt32(body, 1));
            return true;
        }

        internal static bool TryParseCompetition(byte[] body, out MercenaryCompetitionCommand command)
        {
            command = default;
            if (body == null || body.Length < CompetitionMinimumBodyLength)
                return false;

            command = new MercenaryCompetitionCommand(
                BitConverter.ToInt32(body, 0),
                body[4],
                body[5]);
            return true;
        }
    }
}
