using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.PcRoomTimePoint
{
    internal sealed class PcRoomTimePointRepository
    {
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        private readonly IGameDatabase _database;

        internal PcRoomTimePointRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void EnsureStaticConfigRows(PcRoomTimePointConfig config)
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
            PcRoomTimePointConfig config)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO game_event_state(event_id, state)
VALUES(@eventId, 0);",
                ("@eventId", PcRoomTimePointConfig.EventId));

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
    0, 5, @title, @title, '',
    @startUnixTime, @endUnixTime, '', @description,
    1, 20
)
ON CONFLICT(event_id) DO NOTHING;",
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@startNotice", "PC room time point event started."),
                ("@endNotice", "PC room time point event ended."),
                ("@title", "pcroomtimepoint"),
                ("@startUnixTime", window.StartUnixTime),
                ("@endUnixTime", window.EndUnixTime),
                ("@description", "Online time point rewards."));
        }

        internal bool IsEnabled(
            SqliteConnection connection,
            SqliteTransaction transaction)
            => GameEventRepository.IsEnabled(
                connection,
                transaction,
                PcRoomTimePointConfig.EventId);

        internal void EnsureStateRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            PcRoomTimePointConfig config,
            int dayId,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_pcroom_timepoint_daily (
    account_id, event_id, season_id, day_id,
    online_millis, daily_claim_mask, cycle_recorded,
    last_flushed_at_unix, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId, @dayId,
    0, 0, 0, @nowUnix, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId),
                ("@nowUnix", nowUnix));

            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_pcroom_timepoint_period (
    account_id, event_id, season_id,
    completed_cycle_count, period_claim_mask, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId,
    0, 0, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@nowUnix", nowUnix));
        }

        internal void AddOnlineMillis(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            PcRoomTimePointConfig config,
            int dayId,
            long deltaMillis,
            long nowUnix)
        {
            if (deltaMillis <= 0)
                return;

            EnsureStateRows(connection, transaction, accountId, config, dayId, nowUnix);
            ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_pcroom_timepoint_daily
SET online_millis = online_millis + @deltaMillis,
    last_flushed_at_unix = @nowUnix,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId;",
                ("@deltaMillis", deltaMillis),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId));

            TryRecordCompletedCycle(
                connection,
                transaction,
                accountId,
                config,
                dayId,
                nowUnix);
        }

        internal bool TrySetDailyClaimed(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            PcRoomTimePointConfig config,
            int dayId,
            int stageIndex,
            long nowUnix)
        {
            var bit = StageBit(stageIndex);
            if (bit == 0)
                return false;

            EnsureStateRows(connection, transaction, accountId, config, dayId, nowUnix);
            return ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_pcroom_timepoint_daily
SET daily_claim_mask = daily_claim_mask | @bit,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId
  AND (daily_claim_mask & @bit) = 0;",
                ("@bit", bit),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId)) == 1;
        }

        internal bool TryClearPeriodClaimable(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            PcRoomTimePointConfig config,
            int stageIndex,
            long nowUnix)
        {
            var bit = StageBit(stageIndex);
            if (bit == 0)
                return false;
            var clearMask = 0x0F & ~bit;

            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_pcroom_timepoint_period (
    account_id, event_id, season_id,
    completed_cycle_count, period_claim_mask, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId,
    0, 0, @nowUnix
);",
                ("@accountId", accountId),
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@nowUnix", nowUnix));

            return ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_pcroom_timepoint_period
SET period_claim_mask = period_claim_mask & @clearMask,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND (period_claim_mask & @bit) != 0;",
                ("@bit", bit),
                ("@clearMask", clearMask),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@seasonId", config.SeasonId)) == 1;
        }

        internal PcRoomTimePointSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            PcRoomTimePointConfig config,
            int dayId,
            bool eventEnabled)
        {
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            EnsureStateRows(connection, transaction, accountId, config, dayId, nowUnix);
            var daily = LoadDaily(connection, transaction, accountId, config, dayId);
            var period = LoadPeriod(connection, transaction, accountId, config);
            var snapshot = new PcRoomTimePointSnapshot
            {
                AccountId = accountId,
                CharacterId = characterId,
                EventId = PcRoomTimePointConfig.EventId,
                SeasonId = config.SeasonId,
                DayId = dayId,
                DailyOnlineMillis = daily.OnlineMillis,
                DailyClaimMask = (byte)(daily.DailyClaimMask & 0x0F),
                PeriodCompletedCount = period.CompletedCycleCount,
                PeriodClaimMask = (uint)(period.PeriodClaimMask & 0x0F),
                EventEnabled = eventEnabled,
            };

            FillDerivedState(snapshot, config);
            return snapshot;
        }

        private void TryRecordCompletedCycle(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            PcRoomTimePointConfig config,
            int dayId,
            long nowUnix)
        {
            var updated = ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_pcroom_timepoint_daily
SET cycle_recorded = 1,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId
  AND cycle_recorded = 0
  AND online_millis >= @requiredMillis;",
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@dayId", dayId),
                ("@requiredMillis", config.TotalDailyRequiredMillis));
            if (updated == 0)
                return;

            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT INTO event_pcroom_timepoint_period (
    account_id, event_id, season_id,
    completed_cycle_count, period_claim_mask, updated_at_unix
) VALUES (
    @accountId, @eventId, @seasonId,
    0, 0, @nowUnix
)
ON CONFLICT(account_id, event_id, season_id) DO NOTHING;",
                ("@accountId", accountId),
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@seasonId", config.SeasonId),
                ("@nowUnix", nowUnix));

            var beforeCount = LoadPeriod(
                connection,
                transaction,
                accountId,
                config).CompletedCycleCount;
            var afterCount = beforeCount + 1;
            var unlockMask = NewlyUnlockedPeriodMask(config, beforeCount, afterCount);

            ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_pcroom_timepoint_period
SET completed_cycle_count = @afterCount,
    period_claim_mask = period_claim_mask | @unlockMask,
    updated_at_unix = @nowUnix
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId;",
                ("@afterCount", afterCount),
                ("@unlockMask", unlockMask),
                ("@nowUnix", nowUnix),
                ("@accountId", accountId),
                ("@eventId", PcRoomTimePointConfig.EventId),
                ("@seasonId", config.SeasonId));
        }

        private static int NewlyUnlockedPeriodMask(
            PcRoomTimePointConfig config,
            int beforeCount,
            int afterCount)
        {
            var mask = 0;
            foreach (var stage in config.PeriodRewards)
            {
                if (beforeCount < stage.CumulativeRequiredCount
                    && afterCount >= stage.CumulativeRequiredCount)
                {
                    mask |= StageBit(stage.StageIndex);
                }
            }

            return mask;
        }

        private static (long OnlineMillis, int DailyClaimMask) LoadDaily(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            PcRoomTimePointConfig config,
            int dayId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT online_millis, daily_claim_mask
FROM event_pcroom_timepoint_daily
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId
  AND day_id=@dayId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@eventId", PcRoomTimePointConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                command.Parameters.AddWithValue("@dayId", dayId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (0, 0);

                    return (reader.GetInt64(0), reader.GetInt32(1));
                }
            }
        }

        private static (int CompletedCycleCount, int PeriodClaimMask) LoadPeriod(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            PcRoomTimePointConfig config)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT completed_cycle_count, period_claim_mask
FROM event_pcroom_timepoint_period
WHERE account_id=@accountId
  AND event_id=@eventId
  AND season_id=@seasonId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@eventId", PcRoomTimePointConfig.EventId);
                command.Parameters.AddWithValue("@seasonId", config.SeasonId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (0, 0);

                    return (reader.GetInt32(0), reader.GetInt32(1));
                }
            }
        }

        private static void FillDerivedState(
            PcRoomTimePointSnapshot snapshot,
            PcRoomTimePointConfig config)
        {
            byte dailyAvailable = 0;
            foreach (var stage in config.DailyRewards)
            {
                if (snapshot.DailyOnlineMillis >= stage.CumulativeRequiredMillis)
                    dailyAvailable |= (byte)StageBit(stage.StageIndex);
                else if (snapshot.NextDailyStageIndex == 0)
                {
                    snapshot.NextDailyStageIndex = stage.StageIndex;
                    snapshot.NextDailyStageRemainingMillis =
                        stage.CumulativeRequiredMillis - snapshot.DailyOnlineMillis;
                }
            }

            byte periodAvailable = 0;
            foreach (var stage in config.PeriodRewards)
            {
                if (snapshot.PeriodCompletedCount >= stage.CumulativeRequiredCount)
                    periodAvailable |= (byte)StageBit(stage.StageIndex);
            }

            snapshot.DailyAvailableMask = dailyAvailable;
            snapshot.PeriodAvailableMask = periodAvailable;
        }

        private static int StageBit(int stageIndex)
        {
            return stageIndex >= 1 && stageIndex <= 4
                ? 1 << (stageIndex - 1)
                : 0;
        }

        private static (uint StartUnixTime, uint EndUnixTime) GetCalendarWindowUnix()
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
            return ((uint)start.ToUnixTimeSeconds(), (uint)end.ToUnixTimeSeconds());
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
