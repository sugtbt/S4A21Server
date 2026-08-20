using DfoServer.Game.Mercenary;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    // 0x01E5 / 0x019F 技能表项：[combo=技能树 Slot, skillId, displayLevel]
    public static class StrikerSupportSkillListWriter
    {
        public const int EntrySize = 4;

        public static byte[] BuildSkillListSuccessAck(
            ushort requestEcho,
            byte job,
            byte growType,
            IReadOnlyList<StrikerSupportSkillWireEntry> skills)
        {
            skills = skills ?? Array.Empty<StrikerSupportSkillWireEntry>();
            if (skills.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(skills));

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteUInt16(requestEcho);
            writer.WriteByte(job);
            writer.WriteByte(growType);
            writer.WriteByte((byte)skills.Count);
            WriteEntries(writer, skills);
            return writer.ToArray();
        }

        public static byte[] BuildFailureAck(byte errorCode = 0)
            => new byte[] { 0x00, errorCode };

        public static void WriteEntries(
            GamePacketWriter writer,
            IReadOnlyList<StrikerSupportSkillWireEntry> skills)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (skills == null)
                return;

            for (var i = 0; i < skills.Count; i++)
            {
                var skill = skills[i];
                if (skill == null)
                    throw new InvalidOperationException($"striker skill entry {i} is null");
                writer.WriteByte(skill.ComboIndex);
                writer.WriteUInt16(skill.SkillId);
                writer.WriteByte(skill.DisplayLevel);
            }
        }
    }
}
