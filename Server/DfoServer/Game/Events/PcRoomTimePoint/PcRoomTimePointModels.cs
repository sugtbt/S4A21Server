using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Events.PcRoomTimePoint
{
    internal sealed class PcRoomTimePointRewardStage
    {
        public int StageIndex { get; set; }

        public int ItemId { get; set; }

        public int ItemCount { get; set; }

        public long DurationMillis { get; set; }

        public long CumulativeRequiredMillis { get; set; }

        public int CumulativeRequiredCount { get; set; }
    }

    internal sealed class PcRoomTimePointSnapshot
    {
        public int AccountId { get; set; }

        public int CharacterId { get; set; }

        public int EventId { get; set; }

        public int SeasonId { get; set; }

        public int DayId { get; set; }

        public long DailyOnlineMillis { get; set; }

        public uint DailyOnlineSecondsForClient =>
            (uint)Math.Min(uint.MaxValue, Math.Max(0, DailyOnlineMillis / 1000));

        public int PeriodCompletedCount { get; set; }

        public byte DailyClaimMask { get; set; }

        public uint PeriodClaimMask { get; set; }

        public byte DailyAvailableMask { get; set; }

        public byte PeriodAvailableMask { get; set; }

        public uint PeriodClaimMaskForClient =>
            ((uint)PeriodAvailableMask & ~PeriodClaimMask) & 0x0F;

        public bool EventEnabled { get; set; }

        public int NextDailyStageIndex { get; set; }

        public long NextDailyStageRemainingMillis { get; set; }
    }

    internal enum PcRoomTimePointRequestKind
    {
        Query,
        DailyReward,
        PeriodReward,
    }

    internal sealed class PcRoomTimePointClaimCommand
    {
        public PcRoomTimePointRequestKind Kind { get; set; }

        public int StageIndex { get; set; }

        public byte Selector { get; set; }

        public byte IndexOrFF { get; set; }
    }

    internal enum PcRoomTimePointClaimStatus
    {
        Success,
        Query,
        InvalidRequest,
        EventClosed,
        CharacterUnavailable,
        NotReady,
        AlreadyClaimed,
        MailFailed,
    }

    internal sealed class PcRoomTimePointClaimResult
    {
        public PcRoomTimePointClaimStatus Status { get; set; }

        public PcRoomTimePointSnapshot Snapshot { get; set; }

        public bool MailDelivered { get; set; }

        public bool Success => Status == PcRoomTimePointClaimStatus.Success;
    }

    internal sealed class PcRoomTimePointConfig
    {
        internal const int EventId = 228;
        internal const int DefaultSeasonId = 1;
        internal const string PvfPath = "etc/pcroomtimepoint.etc";

        public int SeasonId { get; set; } = DefaultSeasonId;

        public bool DailyRewardAutoGet { get; set; }

        public int DailyRewardLoop { get; set; } = 1;

        public int PeriodRewardLoop { get; set; } = 4;

        public IReadOnlyList<PcRoomTimePointRewardStage> DailyRewards { get; set; } =
            Array.Empty<PcRoomTimePointRewardStage>();

        public IReadOnlyList<PcRoomTimePointRewardStage> PeriodRewards { get; set; } =
            Array.Empty<PcRoomTimePointRewardStage>();

        public long TotalDailyRequiredMillis =>
            DailyRewards.Count == 0
                ? 0
                : DailyRewards.Max(stage => stage.CumulativeRequiredMillis);

        internal PcRoomTimePointRewardStage GetDailyReward(int stageIndex)
            => DailyRewards.FirstOrDefault(stage => stage.StageIndex == stageIndex);

        internal PcRoomTimePointRewardStage GetPeriodReward(int stageIndex)
            => PeriodRewards.FirstOrDefault(stage => stage.StageIndex == stageIndex);
    }
}
