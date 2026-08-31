using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Events.TotalAttendance
{
    internal sealed class TotalAttendanceReward
    {
        public int StageIndex { get; set; }

        public int RequiredAttendanceCount { get; set; }

        public int ItemId { get; set; }

        public int ItemCount { get; set; }
    }

    internal sealed class TotalAttendanceConfig
    {
        internal const int EventId = 2208;
        internal const int DefaultSeasonId = 1;
        internal const int DefaultEventDurationWeeks = 12;
        internal const int DefaultRecommendClearTarget = 4;
        internal const string PvfPath =
            "event/chn_event/chn_totalattendance.evt";

        public int SeasonId { get; set; } = DefaultSeasonId;

        public int EventDurationWeeks { get; set; } =
            DefaultEventDurationWeeks;

        public int RecommendClearTarget { get; set; } =
            DefaultRecommendClearTarget;

        public IReadOnlyList<TotalAttendanceReward> WeeklyRewards
        { get; set; } = Array.Empty<TotalAttendanceReward>();

        public IReadOnlyList<TotalAttendanceReward> TotalRewards
        { get; set; } = Array.Empty<TotalAttendanceReward>();

        internal TotalAttendanceReward GetWeeklyRewardByAttendanceCount(
            int attendanceCount)
            => WeeklyRewards.FirstOrDefault(
                reward => reward.RequiredAttendanceCount == attendanceCount);
    }

    internal sealed class TotalAttendanceSnapshot
    {
        public int AccountId { get; set; }

        public int CharacterId { get; set; }

        public int EventId { get; set; }

        public int SeasonId { get; set; }

        public int WeekId { get; set; }

        public int TotalAttendanceWeekCount { get; set; }

        public int TotalRewardSentMask { get; set; }

        public int ThisWeekRecommendClearCount { get; set; }

        public int RecommendClearTarget { get; set; }

        public int CurrentEventWeekNo { get; set; }

        public bool CheckedThisWeek { get; set; }

        public bool CanCheckThisWeek { get; set; }

        public bool EventEnabled { get; set; }
    }

    internal sealed class TotalAttendanceAccountProgress
    {
        public int TotalAttendanceWeekCount { get; set; }

        public int TotalRewardSentMask { get; set; }
    }

    internal sealed class TotalAttendanceWeeklyProgress
    {
        public bool Checked { get; set; }

        public int WeeklyRewardIndex { get; set; } = -1;
    }

    internal enum TotalAttendanceClearStatus
    {
        Progressed,
        ReadyToCheck,
        EventClosed,
        CharacterUnavailable,
        AlreadyChecked,
        AttendanceLimitReached,
        PersistenceFailed,
    }

    internal enum TotalAttendanceCheckStatus
    {
        Checked,
        EventClosed,
        CharacterUnavailable,
        NotReady,
        AlreadyChecked,
        AttendanceLimitReached,
        RewardUnavailable,
        MailFailed,
        PersistenceFailed,
    }

    internal sealed class TotalAttendanceClearResult
    {
        public TotalAttendanceClearStatus Status { get; set; }

        public TotalAttendanceSnapshot Snapshot { get; set; }

        public bool Success =>
            Status != TotalAttendanceClearStatus.PersistenceFailed
            && Status != TotalAttendanceClearStatus.CharacterUnavailable;
    }

    internal sealed class TotalAttendanceCheckResult
    {
        public TotalAttendanceCheckStatus Status { get; set; }

        public TotalAttendanceSnapshot Snapshot { get; set; }

        public bool MailDelivered { get; set; }

        public int MailedRewardCount { get; set; }

        public bool Success =>
            Status == TotalAttendanceCheckStatus.Checked;
    }
}
