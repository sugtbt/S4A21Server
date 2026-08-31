using System;
using System.Linq;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.DailyAttendanceAnytime
{
    internal sealed class DailyAttendanceAnytimeRepository
    {
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        private readonly IGameDatabase _database;

        internal DailyAttendanceAnytimeRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void EnsureStaticConfigRows(DailyAttendanceAnytimeConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            _database.Write((connection, transaction) =>
            {
                EnsureStaticConfigRows(connection, transaction, config);
            });
        }

        internal void EnsureStaticConfigRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            DailyAttendanceAnytimeConfig config)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO game_event_state(event_id, state)
VALUES(@eventId, 0);",
                ("@eventId", DailyAttendanceAnytimeConfig.EventId));

            var window = GetCalendarWindowUnix();
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT INTO game_event_info_details (
    event_id, unknown0, start_notice, end_notice, detail_flag,
    flag_a, flag_b, title, short_name, reserved_or_icon,
    start_unix_time, end_unix_time, link_key, description,
    detail_enabled, sort_order
) VALUES (
    @eventId, 0, @startNotice, @endNotice, 1,
    0, 5, @title, @shortName, '',
    @startUnixTime, @endUnixTime, '', @description,
    1, 21
)
ON CONFLICT(event_id) DO NOTHING;",
                ("@eventId", DailyAttendanceAnytimeConfig.EventId),
                ("@startNotice", "Daily attendance event started."),
                ("@endNotice", "Daily attendance event ended."),
                ("@title", "dailyattendanceanytimeevent"),
                ("@shortName", "dailyattendanceanytimeevent"),
                ("@startUnixTime", window.StartUnixTime),
                ("@endUnixTime", window.EndUnixTime),
                ("@description", "Daily attendance rewards."));

            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT INTO game_event_info_extra (
    event_id, param0, sort_order
) VALUES (
    @eventId, @clearTarget, 21
)
ON CONFLICT(event_id) DO UPDATE SET
    param0 = CASE
        WHEN game_event_info_extra.param0 <= 0 THEN excluded.param0
        ELSE game_event_info_extra.param0
    END,
    sort_order = CASE
        WHEN game_event_info_extra.sort_order = 0 THEN excluded.sort_order
        ELSE game_event_info_extra.sort_order
    END;",
                ("@eventId", DailyAttendanceAnytimeConfig.EventId),
                ("@clearTarget", DailyAttendanceAnytimeConfig
                    .DefaultRecommendClearTarget));
        }

        internal bool IsEnabled(
            SqliteConnection connection,
            SqliteTransaction transaction)
            => GameEventRepository.IsEnabled(
                connection,
                transaction,
                DailyAttendanceAnytimeConfig.EventId);

        internal int LoadRecommendClearTarget(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT param0
FROM game_event_info_extra
WHERE event_id=@eventId;";
                command.Parameters.AddWithValue(
                    "@eventId",
                    DailyAttendanceAnytimeConfig.EventId);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return DailyAttendanceAnytimeConfig
                        .DefaultRecommendClearTarget;

                var target = Convert.ToInt32(value);
                return target > 0
                    ? target
                    : DailyAttendanceAnytimeConfig.DefaultRecommendClearTarget;
            }
        }

        internal void EnsureStateRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            DailyAttendanceAnytimeConfig config,
            int dayId,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_daily_attendance_anytime_account (
    account_id, event_id, season_id,
    total_attendance_count, accumulate_claimed_mask, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId,
    0, 0, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", DailyAttendanceAnytimeConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@nowUnix", nowUnix));

            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_daily_attendance_anytime_daily (
    account_id, event_id, season_id, day_id,
    recommend_clear_count, attended,
    daily_reward_day_index, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId, @dayId,
    0, 0, -1, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", DailyAttendanceAnytimeConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId),
                ("@nowUnix", nowUnix));
        }

        internal DailyAttendanceAnytimeAccountProgress LoadAccountProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            DailyAttendanceAnytimeConfig config)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT total_attendance_count, accumulate_claimed_mask
FROM event_daily_attendance_anytime_account
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue(
                    "@eventId",
                    DailyAttendanceAnytimeConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new DailyAttendanceAnytimeAccountProgress();

                    return new DailyAttendanceAnytimeAccountProgress
                    {
                        TotalAttendanceCount = Math.Max(0, reader.GetInt32(0)),
                        // Historical column name; value is the claimable mask.
                        AccumulateClaimedMask = reader.GetInt32(1) & 0x07,
                    };
                }
            }
        }

        internal DailyAttendanceAnytimeDailyProgress LoadDailyProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            DailyAttendanceAnytimeConfig config,
            int dayId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT recommend_clear_count, attended, daily_reward_day_index
FROM event_daily_attendance_anytime_daily
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue(
                    "@eventId",
                    DailyAttendanceAnytimeConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                command.Parameters.AddWithValue("@dayId", dayId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new DailyAttendanceAnytimeDailyProgress
                        {
                            DailyRewardDayIndex = -1,
                        };

                    return new DailyAttendanceAnytimeDailyProgress
                    {
                        RecommendClearCount = Math.Max(0, reader.GetInt32(0)),
                        Attended = reader.GetInt32(1) != 0,
                        DailyRewardDayIndex = reader.GetInt32(2),
                    };
                }
            }
        }

        internal bool TrySetRecommendClearCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            DailyAttendanceAnytimeConfig config,
            int dayId,
            int recommendClearCount,
            long nowUnix)
        {
            return ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_daily_attendance_anytime_daily
SET recommend_clear_count = @recommendClearCount,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId
  AND attended = 0
  AND recommend_clear_count < @recommendClearCount;",
                ("@recommendClearCount", recommendClearCount),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", DailyAttendanceAnytimeConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId)) == 1;
        }

        internal bool TryCompleteDailyAttendance(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            DailyAttendanceAnytimeConfig config,
            int dayId,
            int recommendClearTarget,
            int rewardDayIndex,
            int accumulateClaimUnlockMask,
            long nowUnix)
        {
            var dailyUpdated = ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_daily_attendance_anytime_daily
SET recommend_clear_count = @recommendClearTarget,
    attended = 1,
    daily_reward_day_index = @rewardDayIndex,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId
  AND attended = 0;",
                ("@recommendClearTarget", recommendClearTarget),
                ("@rewardDayIndex", rewardDayIndex),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", DailyAttendanceAnytimeConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId));
            if (dailyUpdated != 1)
                return false;

            var accountUpdated = ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_daily_attendance_anytime_account
SET total_attendance_count = total_attendance_count + 1,
    accumulate_claimed_mask = accumulate_claimed_mask | @unlockMask,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND total_attendance_count < @maxAttendanceDays;",
                ("@unlockMask", accumulateClaimUnlockMask & 0x07),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", DailyAttendanceAnytimeConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@maxAttendanceDays", config.MaxAttendanceDays));
            if (accountUpdated != 1)
            {
                throw new InvalidOperationException(
                    "Daily attendance account progress was not updated.");
            }

            return true;
        }

        internal bool TryConsumeAccumulateClaimMask(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            DailyAttendanceAnytimeConfig config,
            DailyAttendanceAnytimeReward reward,
            long nowUnix)
        {
            if (reward == null)
                throw new ArgumentNullException(nameof(reward));

            var bit = StageBit(reward.StageIndex);
            if (bit == 0)
                return false;

            return ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_daily_attendance_anytime_account
SET accumulate_claimed_mask = accumulate_claimed_mask & ~@bit,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND total_attendance_count >= @requiredCount
  AND (accumulate_claimed_mask & @bit) != 0;",
                ("@bit", bit),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", DailyAttendanceAnytimeConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@requiredCount", reward.RequiredAttendanceCount)) == 1;
        }

        internal DailyAttendanceAnytimeSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            DailyAttendanceAnytimeConfig config,
            int dayId,
            int todayRecommendClearCount,
            long nowUnix,
            bool eventEnabled)
        {
            EnsureStateRows(
                connection,
                transaction,
                accountId,
                config,
                dayId,
                nowUnix);
            var target = LoadRecommendClearTarget(connection, transaction);
            var account = LoadAccountProgress(
                connection,
                transaction,
                accountId,
                config);
            var snapshot = new DailyAttendanceAnytimeSnapshot
            {
                AccountId = accountId,
                CharacterId = characterId,
                EventId = DailyAttendanceAnytimeConfig.EventId,
                SeasonId = config.SeasonId,
                DayId = dayId,
                TotalAttendanceCount = Math.Min(
                    Math.Max(0, account.TotalAttendanceCount),
                    config.MaxAttendanceDays),
                TodayRecommendClearCount = Math.Min(
                    Math.Max(0, todayRecommendClearCount),
                    target),
                RecommendClearTarget = target,
                AccumulateClaimedMask = account.AccumulateClaimedMask & 0x07,
                EventEnabled = eventEnabled,
            };

            FillAccumulateStates(snapshot, config);
            return snapshot;
        }

        private static void FillAccumulateStates(
            DailyAttendanceAnytimeSnapshot snapshot,
            DailyAttendanceAnytimeConfig config)
        {
            snapshot.AccumulateState0 = DeriveAccumulateState(
                config,
                snapshot.TotalAttendanceCount,
                snapshot.AccumulateClaimedMask,
                0);
            snapshot.AccumulateState1 = DeriveAccumulateState(
                config,
                snapshot.TotalAttendanceCount,
                snapshot.AccumulateClaimedMask,
                1);
            snapshot.AccumulateState2 = DeriveAccumulateState(
                config,
                snapshot.TotalAttendanceCount,
                snapshot.AccumulateClaimedMask,
                2);
        }

        private static uint DeriveAccumulateState(
            DailyAttendanceAnytimeConfig config,
            int totalAttendanceCount,
            int claimedMask,
            int stageIndex)
        {
            var reward = config.AccumulateRewards
                .FirstOrDefault(stage => stage.StageIndex == stageIndex);
            if (reward == null
                || totalAttendanceCount < reward.RequiredAttendanceCount)
            {
                return 0;
            }

            return (claimedMask & StageBit(stageIndex)) != 0 ? 1u : 2u;
        }

        private static int StageBit(int stageIndex)
            => stageIndex >= 0 && stageIndex < 3 ? 1 << stageIndex : 0;

        private static (uint StartUnixTime, uint EndUnixTime)
            GetCalendarWindowUnix()
        {
            var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
            var start = new DateTimeOffset(
                now.Year,
                1,
                1,
                0,
                0,
                0,
                BeijingOffset);
            var end = new DateTimeOffset(
                now.Year,
                12,
                31,
                23,
                59,
                59,
                BeijingOffset);
            return (
                (uint)start.ToUnixTimeSeconds(),
                (uint)end.ToUnixTimeSeconds());
        }

        private static int ExecuteNonQuery(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var parameter in parameters)
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                return command.ExecuteNonQuery();
            }
        }
    }
}
