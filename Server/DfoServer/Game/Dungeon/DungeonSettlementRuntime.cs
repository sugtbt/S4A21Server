using DfoServer.Game.Progression;
using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    // Frozen settlement inputs and outputs for one participant. Retries reuse
    // this object so random rewards and irreversible grants are not recomputed.
    internal sealed class DungeonSettlementRuntime
    {
        internal bool IsTowerOfDespair;
        internal int TowerOfDespairFloor;
        internal bool ShouldScheduleCardRewardFlow;
        internal BloodAltar.BloodAltarParticipantSettlementRuntime BloodAltar;
        internal LicensedDungeonRewardRuntime LicensedDungeon;

        // The current protocol supplies a bounded, first-write-wins rank via
        // SET_PLAY_RESULT. It can adjust score EXP; independent server-side
        // score validation remains a separate authority boundary.
        internal byte ClientRankPoint;
        internal int PresentationRankPoint;
        internal byte PresentationRankGrade;
        internal int PresentationRankBonusIndex;
        internal bool AuthoritativeRankCaptured;
        internal int TimeBonusPoint;
        internal int RankPoint;
        internal byte RankGrade;
        internal int RankBonusIndex;

        internal uint ClearBaseExp;
        internal uint ScoreBonusExp;
        internal uint PartyClearBreakdownExp;
        internal uint AvatarBonusExp;
        internal uint CreatureBonusExp;
        internal uint GrowthContractBonusExp;
        internal uint BlackDiamondBonusExp;
        internal uint AdventureGroupBonusExp;
        internal int ExperiencePotionBonusRate;
        internal uint ExperiencePotionBonusExp;
        internal uint ClearBonusExp;
        internal uint ClearTotalExp;
        internal byte PreviousLevel;
        internal uint PreviousExp;
        internal ExperienceGrantResult ExperienceGrant;
        internal ExperienceGrantResult ScoreAdjustmentExperienceGrant;

        internal int DungeonLevel;
        internal int PaidCardCost;
        internal bool PaidCardUsesDevilContract;
        internal ClearRewardGenerator.CardReward FreeGold;
        internal ClearRewardGenerator.CardReward FreeItem;
        internal IReadOnlyList<ClearRewardGenerator.CardReward>
            TowerRewardCandidates = Array.Empty<ClearRewardGenerator.CardReward>();
        internal IReadOnlyList<TowerOfDespairGrantedReward>
            TowerGrantedRewards = Array.Empty<TowerOfDespairGrantedReward>();

        // Dungeon permission is a persistent effect followed by a separate
        // client projection. Keeping the plan on the runtime lets a retry
        // resend the projection after the database write already succeeded.
        internal bool DungeonPermissionPlanReady;
        internal bool DungeonPermissionChanged;
        internal int DungeonPermissionAccountId;
        internal IReadOnlyList<DungeonPermissionEntrySnapshot>
            DungeonPermissionEntries = Array.Empty<DungeonPermissionEntrySnapshot>();

        internal uint MonsterTotalExp;
        internal uint BossTotalExp;
        internal uint ChampionTotalExp;
        internal uint SuperChampionTotalExp;
        internal uint NamedMonsterTotalExp;
        internal uint MonsterGrowthContractBonusExp;
        internal uint MonsterEquipmentBonusExp;
        internal IReadOnlyList<DungeonObjectExperienceEntry>
            ObjectExperienceEntries = Array.Empty<DungeonObjectExperienceEntry>();
        internal int ClearTimeMilliseconds;
    }
}
