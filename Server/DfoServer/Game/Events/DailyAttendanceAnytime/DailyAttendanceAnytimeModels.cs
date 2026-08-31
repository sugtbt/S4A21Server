using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Events.DailyAttendanceAnytime
{
    internal sealed class DailyAttendanceAnytimeReward
    {
        public int StageIndex { get; set; }

        public int DayIndex { get; set; }

        public int RequiredAttendanceCount { get; set; }

        public int ItemId { get; set; }

        public int ItemCount { get; set; }
    }

    internal sealed class DailyAttendanceAnytimeConfig
    {
        internal const int EventId = 2370;
        internal const int DefaultSeasonId = 1;
        internal const int DefaultRecommendClearTarget = 2;
        internal const string PvfPath =
            "event/chn_event/chn_dailyattendanceanytimeevent.evt";

        public int SeasonId { get; set; } = DefaultSeasonId;

        public IReadOnlyList<DailyAttendanceAnytimeReward> DailyRewards
        { get; set; } = Array.Empty<DailyAttendanceAnytimeReward>();

        public IReadOnlyList<DailyAttendanceAnytimeReward> AccumulateRewards
        { get; set; } = Array.Empty<DailyAttendanceAnytimeReward>();

        public int MaxAttendanceDays => DailyRewards.Count;

        internal DailyAttendanceAnytimeReward GetDailyRewardByDayIndex(
            int dayIndex)
            => DailyRewards.FirstOrDefault(reward => reward.DayIndex == dayIndex);
    }

    internal sealed class DailyAttendanceAnytimeSnapshot
    {
        public int AccountId { get; set; }

        public int CharacterId { get; set; }

        public int EventId { get; set; }

        public int SeasonId { get; set; }

        public int DayId { get; set; }

        public int TotalAttendanceCount { get; set; }

        public int TodayRecommendClearCount { get; set; }

        public int RecommendClearTarget { get; set; }

        public int AccumulateClaimedMask { get; set; }

        public uint AccumulateState0 { get; set; }

        public uint AccumulateState1 { get; set; }

        public uint AccumulateState2 { get; set; }

        public bool EventEnabled { get; set; }
    }

    internal sealed class DailyAttendanceAnytimeAccountProgress
    {
        public int TotalAttendanceCount { get; set; }

        public int AccumulateClaimedMask { get; set; }
    }

    internal sealed class DailyAttendanceAnytimeDailyProgress
    {
        public int RecommendClearCount { get; set; }

        public bool Attended { get; set; }

        public int DailyRewardDayIndex { get; set; }
    }

    internal enum DailyAttendanceAnytimeClearStatus
    {
        Progressed,
        Attended,
        EventClosed,
        CharacterUnavailable,
        AlreadyAttended,
        AttendanceLimitReached,
        RewardUnavailable,
        MailFailed,
        PersistenceFailed,
    }

    internal enum DailyAttendanceAnytimeClaimStatus
    {
        Claimed,
        EventClosed,
        CharacterUnavailable,
        NoClaimableReward,
        MailFailed,
        PersistenceFailed,
    }

    internal sealed class DailyAttendanceAnytimeClearResult
    {
        public DailyAttendanceAnytimeClearStatus Status { get; set; }

        public DailyAttendanceAnytimeSnapshot Snapshot { get; set; }

        public bool MailDelivered { get; set; }

        public bool Success =>
            Status != DailyAttendanceAnytimeClearStatus.MailFailed
            && Status != DailyAttendanceAnytimeClearStatus.PersistenceFailed
            && Status != DailyAttendanceAnytimeClearStatus.CharacterUnavailable;
    }

    internal sealed class DailyAttendanceAnytimeClaimResult
    {
        public DailyAttendanceAnytimeClaimStatus Status { get; set; }

        public DailyAttendanceAnytimeSnapshot Snapshot { get; set; }

        public bool MailDelivered { get; set; }

        public int ClaimedStageIndex { get; set; } = -1;

        public int ItemId { get; set; }

        public int ItemCount { get; set; }

        public bool Success =>
            Status != DailyAttendanceAnytimeClaimStatus.MailFailed
            && Status != DailyAttendanceAnytimeClaimStatus.PersistenceFailed
            && Status != DailyAttendanceAnytimeClaimStatus.CharacterUnavailable;
    }
}
