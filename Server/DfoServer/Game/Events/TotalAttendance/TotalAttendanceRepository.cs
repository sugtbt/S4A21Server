using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.TotalAttendance
{
    internal sealed class TotalAttendanceRepository
    {
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        private readonly IGameDatabase _database;

        internal TotalAttendanceRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void EnsureStaticConfigRows(TotalAttendanceConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            _database.Write((connection, transaction) =>
            {
                EnsureSchema(connection, transaction);
                EnsureStaticConfigRows(connection, transaction, config);
            });
        }

        internal void EnsureStaticConfigRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            TotalAttendanceConfig config)
        {
            EnsureSchema(connection, transaction);
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO game_event_state(event_id, state)
VALUES(@eventId, 0);",
                ("@eventId", TotalAttendanceConfig.EventId));

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
    1, 22
)
ON CONFLICT(event_id) DO NOTHING;",
                ("@eventId", TotalAttendanceConfig.EventId),
                ("@startNotice", "Total attendance event started."),
                ("@endNotice", "Total attendance event ended."),
                ("@title", "totalattendanceevent"),
                ("@shortName", "totalattendanceevent"),
                ("@startUnixTime", window.StartUnixTime),
                ("@endUnixTime", window.EndUnixTime),
                ("@description", "Weekly attendance rewards."));

            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT INTO game_event_info_extra (
    event_id, param0, param1, sort_order
) VALUES (
    @eventId, @clearTarget, @eventDuration, 22
)
ON CONFLICT(event_id) DO UPDATE SET
    param0 = CASE
        WHEN game_event_info_extra.param0 <= 0 THEN excluded.param0
        ELSE game_event_info_extra.param0
    END,
    param1 = CASE
        WHEN game_event_info_extra.param1 <= 0 THEN excluded.param1
        ELSE game_event_info_extra.param1
    END,
    sort_order = CASE
        WHEN game_event_info_extra.sort_order = 0 THEN excluded.sort_order
        ELSE game_event_info_extra.sort_order
    END;",
                ("@eventId", TotalAttendanceConfig.EventId),
                ("@clearTarget", config.RecommendClearTarget),
                ("@eventDuration", config.EventDurationWeeks));
        }

        internal bool IsEnabled(
            SqliteConnection connection,
            SqliteTransaction transaction)
            => GameEventRepository.IsEnabled(
                connection,
                transaction,
                TotalAttendanceConfig.EventId);

        internal int LoadRecommendClearTarget(
            SqliteConnection connection,
            SqliteTransaction transaction,
            TotalAttendanceConfig config)
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
                    TotalAttendanceConfig.EventId);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return config.RecommendClearTarget;

                var target = Convert.ToInt32(value);
                return target > 0 ? target : config.RecommendClearTarget;
            }
        }

        internal void EnsureStateRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            TotalAttendanceConfig config,
            int weekId,
            long nowUnix)
        {
            EnsureSchema(connection, transaction);
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_total_attendance_account (
    account_id, event_id, season_id,
    total_attendance_week_count, total_reward_sent_mask, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId,
    0, 0, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", TotalAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@nowUnix", nowUnix));

            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_total_attendance_weekly (
    account_id, event_id, season_id, week_id,
    checked, weekly_reward_index, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId, @weekId,
    0, -1, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", TotalAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@weekId", weekId),
                ("@nowUnix", nowUnix));
        }

        internal TotalAttendanceAccountProgress LoadAccountProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            TotalAttendanceConfig config)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT total_attendance_week_count, total_reward_sent_mask
FROM event_total_attendance_account
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue(
                    "@eventId",
                    TotalAttendanceConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new TotalAttendanceAccountProgress();

                    return new TotalAttendanceAccountProgress
                    {
                        TotalAttendanceWeekCount =
                            Math.Max(0, reader.GetInt32(0)),
                        TotalRewardSentMask = reader.GetInt32(1) & 0x07,
                    };
                }
            }
        }

        internal TotalAttendanceWeeklyProgress LoadWeeklyProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            TotalAttendanceConfig config,
            int weekId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT checked, weekly_reward_index
FROM event_total_attendance_weekly
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND week_id=@weekId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue(
                    "@eventId",
                    TotalAttendanceConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                command.Parameters.AddWithValue("@weekId", weekId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return new TotalAttendanceWeeklyProgress();

                    return new TotalAttendanceWeeklyProgress
                    {
                        Checked = reader.GetInt32(0) != 0,
                        WeeklyRewardIndex = reader.GetInt32(1),
                    };
                }
            }
        }

        internal bool TryCompleteWeeklyAttendance(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            TotalAttendanceConfig config,
            int weekId,
            int rewardWeekIndex,
            int totalRewardMaskToSet,
            long nowUnix)
        {
            var weeklyUpdated = ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_total_attendance_weekly
SET checked = 1,
    weekly_reward_index = @rewardWeekIndex,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND week_id=@weekId
  AND checked = 0;",
                ("@rewardWeekIndex", rewardWeekIndex),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", TotalAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@weekId", weekId));
            if (weeklyUpdated != 1)
                return false;

            var accountUpdated = ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_total_attendance_account
SET total_attendance_week_count = total_attendance_week_count + 1,
    total_reward_sent_mask = total_reward_sent_mask | @totalRewardMask,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND total_attendance_week_count < @maxWeeks;",
                ("@totalRewardMask", totalRewardMaskToSet & 0x07),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", TotalAttendanceConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@maxWeeks", config.EventDurationWeeks));
            if (accountUpdated != 1)
            {
                throw new InvalidOperationException(
                    "Total attendance account progress was not updated.");
            }

            return true;
        }

        internal TotalAttendanceSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            TotalAttendanceConfig config,
            int weekId,
            int weeklyRecommendClearCount,
            long nowUnix,
            bool eventEnabled)
        {
            EnsureStateRows(
                connection,
                transaction,
                accountId,
                config,
                weekId,
                nowUnix);
            var target = LoadRecommendClearTarget(
                connection,
                transaction,
                config);
            var account = LoadAccountProgress(
                connection,
                transaction,
                accountId,
                config);
            var weekly = LoadWeeklyProgress(
                connection,
                transaction,
                accountId,
                config,
                weekId);
            var cappedTotal = Math.Min(
                Math.Max(0, account.TotalAttendanceWeekCount),
                config.EventDurationWeeks);
            var clearCount = Math.Min(
                Math.Max(0, weeklyRecommendClearCount),
                target);

            return new TotalAttendanceSnapshot
            {
                AccountId = accountId,
                CharacterId = characterId,
                EventId = TotalAttendanceConfig.EventId,
                SeasonId = config.SeasonId,
                WeekId = weekId,
                TotalAttendanceWeekCount = cappedTotal,
                TotalRewardSentMask = account.TotalRewardSentMask & 0x07,
                ThisWeekRecommendClearCount = clearCount,
                RecommendClearTarget = target,
                CurrentEventWeekNo = ResolveCurrentEventWeekNo(
                    cappedTotal,
                    config.EventDurationWeeks),
                CheckedThisWeek = weekly.Checked,
                CanCheckThisWeek =
                    !weekly.Checked
                    && cappedTotal < config.EventDurationWeeks
                    && clearCount >= target,
                EventEnabled = eventEnabled,
            };
        }

        private static int ResolveCurrentEventWeekNo(
            int totalAttendanceWeekCount,
            int eventDurationWeeks)
        {
            if (eventDurationWeeks <= 0)
                return 1;

            return Math.Min(
                eventDurationWeeks,
                Math.Max(1, totalAttendanceWeekCount));
        }

        private static void EnsureSchema(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
CREATE TABLE IF NOT EXISTS event_total_attendance_account (
    account_id INTEGER NOT NULL,
    event_id INTEGER NOT NULL,
    season_id INTEGER NOT NULL DEFAULT 1,
    total_attendance_week_count INTEGER NOT NULL DEFAULT 0
        CHECK(total_attendance_week_count >= 0),
    total_reward_sent_mask INTEGER NOT NULL DEFAULT 0
        CHECK(total_reward_sent_mask >= 0 AND total_reward_sent_mask <= 7),
    updated_at_unix INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (account_id, event_id, season_id),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS event_total_attendance_weekly (
    account_id INTEGER NOT NULL,
    event_id INTEGER NOT NULL,
    season_id INTEGER NOT NULL DEFAULT 1,
    week_id INTEGER NOT NULL,
    checked INTEGER NOT NULL DEFAULT 0 CHECK(checked IN (0, 1)),
    weekly_reward_index INTEGER NOT NULL DEFAULT -1,
    updated_at_unix INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (account_id, event_id, season_id, week_id),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_event_total_attendance_weekly_week
    ON event_total_attendance_weekly(event_id, season_id, week_id);");
        }

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
