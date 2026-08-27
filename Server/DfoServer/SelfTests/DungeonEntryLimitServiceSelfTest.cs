using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DungeonEntryLimitServiceSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_ENTRY_LIMIT selftest ===");
            var failures = 0;

            VerifySchemaAndDefaults(ref failures);
            VerifySpecialDungeonConsumeAndScope(ref failures);
            VerifyDimensionGateConsumeAndRollover(ref failures);
            VerifyV12ToCurrentMigration(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "DUNGEON_ENTRY_LIMIT selftest passed."
                    : $"DUNGEON_ENTRY_LIMIT selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifySchemaAndDefaults(ref int failures)
        {
            var databasePath = TempDbPath("schema");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    Check(
                        "new schema creates dungeon entry limit tables",
                        TableExists(connection, "dungeon_limit_config")
                        && TableExists(connection, "dungeon_limit_records")
                        && TableExists(connection, "character_dimensiongate_records")
                        && SqliteMigrations.ReadVersion(connection)
                            == SqliteMigrations.CurrentVersion,
                        ref failures);

                    var rows = LoadConfigRows(connection);
                    Check(
                        "special dungeon config seeds match A21 login table",
                        RowsMatchDefaults(rows),
                        ref failures);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifySpecialDungeonConsumeAndScope(
            ref int failures)
        {
            var databasePath = TempDbPath("special");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacters(database);

                var preBoundaryUtc = new DateTime(
                    2026,
                    8,
                    20,
                    21,
                    30,
                    0,
                    DateTimeKind.Utc);
                var postBoundaryUtc = new DateTime(
                    2026,
                    8,
                    20,
                    22,
                    30,
                    0,
                    DateTimeKind.Utc);
                var service = new DungeonEntryLimitService(
                    database,
                    () => preBoundaryUtc);

                var entries = service.LoadSpecialDungeonLimits(9001, 9101);
                Check(
                    "special dungeon projection loads seeded defaults",
                    entries.Count == SpecialDungeonEntryLimitDefaults.Entries.Length
                    && entries[0].DungeonId == 11006
                    && entries[0].CurrentCount == 3
                    && entries.Single(x => x.DungeonId == 122).CurrentCount == 9,
                    ref failures);

                Check(
                    "character-scoped special dungeon consume decrements one character",
                    service.TryConsumeSpecialDungeonLimit(
                        9001,
                        9101,
                        11006,
                        1,
                        out var consume)
                    && consume.Allowed
                    && consume.CurrentCount == 2
                    && consume.UsedCount == 1
                    && service.LoadSpecialDungeonLimits(9001, 9102)
                        .Single(x => x.DungeonId == 11006).CurrentCount == 3,
                    ref failures);

                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE dungeon_limit_config
SET scope_type = 'account', limit_count = 2
WHERE dgn_id = 11007;";
                    command.ExecuteNonQuery();
                }

                Check(
                    "account-scoped special dungeon consume is shared",
                    service.TryConsumeSpecialDungeonLimit(
                        9001,
                        9101,
                        11007,
                        1,
                        out var accountConsume)
                    && accountConsume.Allowed
                    && service.LoadSpecialDungeonLimits(9001, 9102)
                        .Single(x => x.DungeonId == 11007).CurrentCount == 1,
                    ref failures);

                var postService = new DungeonEntryLimitService(
                    database,
                    () => postBoundaryUtc);
                Check(
                    "special dungeon projection rolls over at Beijing 06:00",
                    postService.LoadSpecialDungeonLimits(9001, 9101)
                        .Single(x => x.DungeonId == 11006).CurrentCount == 3
                    && postService.TryConsumeSpecialDungeonLimit(
                        9001,
                        9101,
                        11006,
                        1,
                        out var postConsume)
                    && postConsume.CurrentCount == 2
                    && ReadSpecialDayId(database, 9001, 9101, 11006)
                        == DailyResetService.TodayId(postBoundaryUtc),
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyDimensionGateConsumeAndRollover(
            ref int failures)
        {
            var databasePath = TempDbPath("dimensiongate");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacters(database);

                var preBoundaryUtc = new DateTime(
                    2026,
                    8,
                    20,
                    21,
                    30,
                    0,
                    DateTimeKind.Utc);
                var postBoundaryUtc = new DateTime(
                    2026,
                    8,
                    20,
                    22,
                    30,
                    0,
                    DateTimeKind.Utc);
                var service = new DungeonEntryLimitService(
                    database,
                    () => preBoundaryUtc);

                Check(
                    "dimension gate starts from supplied default counts",
                    service.LoadDimensionGateLimit(9101, 5, 0).CurrentCount == 5
                    && service.TryConsumeDimensionGateLimit(
                        9101,
                        5,
                        0,
                        1,
                        out var consume)
                    && consume.Allowed
                    && consume.CurrentCount == 4
                    && consume.UsedCount == 1,
                    ref failures);

                SeedDimensionGateState(
                    database,
                    9101,
                    DailyResetService.TodayId(preBoundaryUtc),
                    currentCount: 0,
                    extraCount: 2,
                    usedCount: 3);
                Check(
                    "dimension gate consumes extra count after base count",
                    service.TryConsumeDimensionGateLimit(
                        9101,
                        5,
                        0,
                        1,
                        out var extraConsume)
                    && extraConsume.Allowed
                    && extraConsume.CurrentCount == 0
                    && extraConsume.ExtraCount == 1
                    && extraConsume.UsedCount == 4,
                    ref failures);

                var postService = new DungeonEntryLimitService(
                    database,
                    () => postBoundaryUtc);
                Check(
                    "dimension gate projection rolls over at Beijing 06:00",
                    postService.LoadDimensionGateLimit(9101, 5, 0).CurrentCount == 5
                    && postService.LoadDimensionGateLimit(9101, 5, 0).ExtraCount == 0,
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyV12ToCurrentMigration(ref int failures)
        {
            var databasePath = TempDbPath("migration");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
DROP TABLE IF EXISTS character_dimensiongate_records;
DROP TABLE IF EXISTS dungeon_limit_records;
DROP TABLE IF EXISTS dungeon_limit_config;
UPDATE schema_metadata SET schema_version = 12
WHERE singleton_id = 1;
PRAGMA user_version = 12;";
                        command.ExecuteNonQuery();
                    }

                    SqliteMigrations.Apply(connection);
                    Check(
                        "schema v12 migrates to current dungeon entry limit tables",
                        SqliteMigrations.ReadVersion(connection)
                            == SqliteMigrations.CurrentVersion
                        && TableExists(connection, "dungeon_limit_config")
                        && TableExists(connection, "dungeon_limit_records")
                        && TableExists(connection, "character_dimensiongate_records")
                        && LoadConfigRows(connection).Count
                            == SpecialDungeonEntryLimitDefaults.Entries.Length,
                        ref failures);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void SeedCharacters(GameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(9001, 'dungeon-entry-limit-account', '');
INSERT INTO characters(character_id, account_id, name, job)
VALUES
    (9101, 9001, 'dungeon-entry-limit-a', 0),
    (9102, 9001, 'dungeon-entry-limit-b', 0);";
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        private static void SeedDimensionGateState(
            GameDatabase database,
            int characterId,
            int dayId,
            int currentCount,
            int extraCount,
            int usedCount)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO character_dimensiongate_records (
    character_id, day_id, current_count, extra_count, used_count
) VALUES (
    @cid, @day, @current, @extra, @used
)
ON CONFLICT(character_id) DO UPDATE SET
    day_id = excluded.day_id,
    current_count = excluded.current_count,
    extra_count = excluded.extra_count,
    used_count = excluded.used_count;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@day", dayId);
                command.Parameters.AddWithValue("@current", currentCount);
                command.Parameters.AddWithValue("@extra", extraCount);
                command.Parameters.AddWithValue("@used", usedCount);
                command.ExecuteNonQuery();
            }
        }

        private static int ReadSpecialDayId(
            GameDatabase database,
            int accountId,
            int characterId,
            int dungeonId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT day_id
FROM dungeon_limit_records
WHERE account_id = @accountId
  AND character_id = @characterId
  AND dgn_id = @dgnId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@dgnId", dungeonId);
                return Convert.ToInt32(command.ExecuteScalar() ?? 0);
            }
        }

        private static List<(int DungeonId, byte CurrentCount)>
            LoadConfigRows(SqliteConnection connection)
        {
            var result = new List<(int DungeonId, byte CurrentCount)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT dgn_id, limit_count
FROM dungeon_limit_config
WHERE enabled = 1
ORDER BY sort_order, dgn_id;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add((
                            reader.GetInt32(0),
                            (byte)reader.GetInt32(1)));
                    }
                }
            }

            return result;
        }

        private static bool RowsMatchDefaults(
            List<(int DungeonId, byte CurrentCount)> rows)
        {
            if (rows.Count != SpecialDungeonEntryLimitDefaults.Entries.Length)
                return false;

            for (var i = 0; i < rows.Count; i++)
            {
                var expected = SpecialDungeonEntryLimitDefaults.Entries[i];
                if (rows[i].DungeonId != expected.DungeonId
                    || rows[i].CurrentCount != expected.CurrentCount)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TableExists(
            SqliteConnection connection,
            string tableName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type = 'table'
  AND name = @name;";
                command.Parameters.AddWithValue("@name", tableName);
                return Convert.ToInt32(command.ExecuteScalar()) != 0;
            }
        }

        private static string TempDbPath(string purpose)
            => Path.Combine(
                Path.GetTempPath(),
                $"dfo_dungeon_entry_limit_{purpose}_{Guid.NewGuid():N}.db");

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (File.Exists(path + "-wal"))
                    File.Delete(path + "-wal");
                if (File.Exists(path + "-shm"))
                    File.Delete(path + "-shm");
            }
            catch
            {
            }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
