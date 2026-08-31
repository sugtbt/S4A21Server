using System;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.RecommendedDungeons
{
    internal sealed class RecommendDungeonClearStatsRepository
    {
        private readonly IGameDatabase _database;

        internal RecommendDungeonClearStatsRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void EnsureSchema()
        {
            _database.Write((connection, transaction) =>
            {
                EnsureSchema(connection, transaction);
            });
        }

        internal static void EnsureSchema(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
CREATE TABLE IF NOT EXISTS account_recommend_dungeon_clear_stats (
    account_id INTEGER NOT NULL,
    period_type INTEGER NOT NULL,
    period_id INTEGER NOT NULL,
    clear_count INTEGER NOT NULL DEFAULT 0
        CHECK(clear_count >= 0),
    updated_at_unix INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (account_id, period_type, period_id),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_account_recommend_dungeon_clear_stats_period
    ON account_recommend_dungeon_clear_stats(period_type, period_id);");
        }

        internal RecommendDungeonClearStatsSnapshot RecordClear(
            int accountId,
            int dayId,
            int weekId,
            long nowUnix)
        {
            if (accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(accountId));

            return _database.Write((connection, transaction) =>
            {
                EnsureSchema(connection, transaction);
                return RecordClear(
                    connection,
                    transaction,
                    accountId,
                    dayId,
                    weekId,
                    nowUnix);
            });
        }

        internal RecommendDungeonClearStatsSnapshot RecordClear(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int dayId,
            int weekId,
            long nowUnix)
        {
            if (accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(accountId));

            IncrementPeriod(
                connection,
                transaction,
                accountId,
                RecommendDungeonClearPeriodTypes.Day,
                dayId,
                nowUnix);
            IncrementPeriod(
                connection,
                transaction,
                accountId,
                RecommendDungeonClearPeriodTypes.Week,
                weekId,
                nowUnix);
            return LoadSnapshot(
                connection,
                transaction,
                accountId,
                dayId,
                weekId);
        }

        internal RecommendDungeonClearStatsSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int dayId,
            int weekId)
        {
            return new RecommendDungeonClearStatsSnapshot
            {
                AccountId = accountId,
                DayId = dayId,
                WeekId = weekId,
                DailyClearCount = LoadCount(
                    connection,
                    transaction,
                    accountId,
                    RecommendDungeonClearPeriodTypes.Day,
                    dayId),
                WeeklyClearCount = LoadCount(
                    connection,
                    transaction,
                    accountId,
                    RecommendDungeonClearPeriodTypes.Week,
                    weekId),
            };
        }

        internal int LoadDailyCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int dayId)
            => LoadCount(
                connection,
                transaction,
                accountId,
                RecommendDungeonClearPeriodTypes.Day,
                dayId);

        internal int LoadWeeklyCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int weekId)
            => LoadCount(
                connection,
                transaction,
                accountId,
                RecommendDungeonClearPeriodTypes.Week,
                weekId);

        private static void IncrementPeriod(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int periodType,
            int periodId,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT INTO account_recommend_dungeon_clear_stats (
    account_id, period_type, period_id, clear_count, updated_at_unix
) VALUES (
    @accountId, @periodType, @periodId, 1, @nowUnix
)
ON CONFLICT(account_id, period_type, period_id) DO UPDATE SET
    clear_count = clear_count + 1,
    updated_at_unix = excluded.updated_at_unix;",
                ("@accountId", accountId),
                ("@periodType", periodType),
                ("@periodId", periodId),
                ("@nowUnix", nowUnix));
        }

        private static int LoadCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int periodType,
            int periodId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT clear_count
FROM account_recommend_dungeon_clear_stats
WHERE account_id=@accountId
  AND period_type=@periodType
  AND period_id=@periodId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@periodType", periodType);
                command.Parameters.AddWithValue("@periodId", periodId);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return 0;

                return Math.Max(0, Convert.ToInt32(value));
            }
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
