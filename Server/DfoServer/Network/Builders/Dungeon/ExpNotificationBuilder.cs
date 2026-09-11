using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Skills;

namespace DfoServer.Network.Builders
{
    public static class ExpNotificationBuilder
    {
        // A21 NOTI 0x0025 fixed body layout. A12-only member reward and tail
        // fields are intentionally not serialized.
        public const int PvpVictoryPointOffset = 0x11;
        public const int VariableEntryCountOffset = 0x2E;
        public const int GrowthCapsuleExpOffset = 0x37;
        public const int HonorLevelOffset = 0x3B;
        public const int HonorExpOffset = 0x3F;
        // A21 sub_1178CD0 consumes four trailing u32 values. The current
        // version has no channel EXP source; the fixed slot remains zero.
        public const int RemovedChannelExpOffset = 0x4B;
        public const int ClientReadLengthWithNoVariableEntries = 83;
        public const int CompatibilityTailLength = 0;
        public const int BodyLength = ClientReadLengthWithNoVariableEntries;

        public static byte[] Build(
            byte level,
            uint totalExp,
            SkillPointProtocolState skillPoints,
            HonorLevelSummary honorLevel,
            uint pvpVictoryPointSnapshot = 0,
            uint partyRewardExp = 0,
            uint memberRewardExp = 0,
            uint fatigueBuffBonusExp = 0,
            uint seriaBlessingBonusExp = 0,
            uint growthContractBonusExp = 0,
            uint fatigueBurnBonusExp = 0,
            uint internetCafeBonusExp = 0,
            uint eliteMonsterKillBonusExp = 0,
            uint growthCapsuleExp = 0)
        {
            // Keep the parameter for existing callers; A21 has no member
            // reward field between party EXP and the SP/TP block.
            _ = memberRewardExp;

            // 0x0025 honor slots are consumed as a level-up banner. Alts still
            // get account honor from USERINFO; only max-level EXP may carry it.
            if (level < ExpTableProvider.MaxLevel)
                honorLevel = null;

            var w = new GamePacketWriter();
            w.WriteByte(level);                         // +0x00 level
            w.WriteUInt32(totalExp);                    // +0x01 total EXP
            w.WriteUInt32(partyRewardExp);              // +0x05 party EXP
            w.WriteUInt16(skillPoints.Page0Sp);         // +0x09 page 0 SP
            w.WriteUInt16(skillPoints.Page1Sp);         // +0x0B page 1 SP
            w.WriteUInt16(skillPoints.Page0Tp);         // +0x0D page 0 TP
            w.WriteUInt16(skillPoints.Page1Tp);         // +0x0F page 1 TP
            w.WriteUInt32(pvpVictoryPointSnapshot);     // +0x11 PvP victory points
            w.WriteUInt32(fatigueBuffBonusExp);         // +0x15 fatigue buff EXP
            w.WriteByte(0);                             // +0x19 presentation mode
            w.WriteUInt32(seriaBlessingBonusExp);       // +0x1A Seria blessing EXP
            w.WriteUInt32(growthContractBonusExp);      // +0x1E growth contract EXP
            w.WriteUInt32(0);                           // +0x22 unknown
            w.WriteUInt32(fatigueBurnBonusExp);         // +0x26 fatigue burn EXP
            w.WriteUInt32(internetCafeBonusExp);        // +0x2A internet cafe EXP
            w.WriteByte(0);                             // +0x2E variable entry count
            w.WriteUInt32(eliteMonsterKillBonusExp);    // +0x2F elite monster EXP
            w.WriteUInt32(0);                           // +0x33 unknown
            w.WriteUInt32(growthCapsuleExp);            // +0x37 growth capsule EXP
            w.WriteUInt32(honorLevel?.HonorLevel ?? 0); // +0x3B honor level
            w.WriteUInt32(honorLevel?.HonorExp ?? 0);   // +0x3F honor EXP
            w.WriteUInt32(0);                           // +0x43 speed-growth buff EXP
            w.WriteUInt32(0);                           // +0x47 unknown
            w.WriteUInt32(0);                           // +0x4B removed channel EXP
            w.WriteUInt32(0);                           // +0x4F guild bonus EXP
            return w.ToArray();
        }
    }
}
