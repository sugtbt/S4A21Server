namespace DfoServer.Game.Mercenary
{
    // 0x01E5 ACK 与 0x019F 记录尾部共用的支援技能表项。
    // ComboIndex 是支援角色技能树 Slot，不是 striker.etc 第四字段（预览视频 ID）。
    public sealed class StrikerSupportSkillWireEntry
    {
        public StrikerSupportSkillWireEntry(byte comboIndex, ushort skillId, byte displayLevel)
        {
            ComboIndex = comboIndex;
            SkillId = skillId;
            DisplayLevel = displayLevel;
        }

        public byte ComboIndex { get; }

        public ushort SkillId { get; }

        public byte DisplayLevel { get; }
    }
}
