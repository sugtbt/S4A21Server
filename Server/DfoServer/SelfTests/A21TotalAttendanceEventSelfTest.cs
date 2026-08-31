using System;
using System.IO;
using System.Text;
using DfoServer.Game.Events.RecommendedDungeons;
using DfoServer.Game.Events.TotalAttendance;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Events;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class A21TotalAttendanceEventSelfTest
    {
        private const int AccountId = 2208001;
        private const int CharacterId = 2208101;
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        public static int Run()
        {
            Console.WriteLine("=== A21_TOTAL_ATTENDANCE_EVENT selftest ===");

            var failures = 0;
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"dfo_a21_total_attendance_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var migrationDatabasePath = Path.Combine(
                tempDirectory,
                "total_attendance_migration.db");
            var serviceDatabasePath = Path.Combine(
                tempDirectory,
                "total_attendance_service.db");

            try
            {
                VerifyConfig(ref failures);
                VerifyPackets(ref failures);
                VerifyV16ToV17Migration(migrationDatabasePath, ref failures);
                VerifyServiceRecommendedClearAndWeeklyCheck(
                    serviceDatabasePath,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] unhandled: " + ex);
                failures++;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempDirectory))
                        Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WARN] temp cleanup failed: " + ex.Message);
                }
            }

            Console.WriteLine(failures == 0
                ? "=== A21_TOTAL_ATTENDANCE_EVENT PASS ==="
                : $"=== A21_TOTAL_ATTENDANCE_EVENT FAIL ({failures}) ===");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyConfig(ref int failures)
        {
            var config = TotalAttendanceConfigParser.Parse(BuildConfigText());
            Check(
                "config parses duration, weekly rewards and total rewards",
                config.EventDurationWeeks == 12
                && config.RecommendClearTarget == 4
                && config.WeeklyRewards.Count == 12
                && config.WeeklyRewards[0].RequiredAttendanceCount == 1
                && config.WeeklyRewards[3].ItemId == 490003196
                && config.TotalRewards.Count == 3
                && config.TotalRewards[0].RequiredAttendanceCount == 4
                && config.TotalRewards[2].ItemId == 490003221,
                ref failures);

            VerifyRealPvfConfigIfAvailable(ref failures);
        }

        private static void VerifyRealPvfConfigIfAvailable(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine(
                    "[SKIP] real PVF total attendance parse: PVF_ARCHIVE_PATH is not set");
                return;
            }

            try
            {
                var config = TotalAttendanceConfigParser.Parse(
                    GameWorld.PvfArchiveAccessor.ReadText(
                        TotalAttendanceConfig.PvfPath));
                Check(
                    "real PVF total attendance config parses expected rewards",
                    config.EventDurationWeeks == 12
                    && config.RecommendClearTarget == 4
                    && config.WeeklyRewards.Count == 12
                    && config.TotalRewards.Count == 3,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] real PVF total attendance parse: " + ex.Message);
                failures++;
            }
        }

        private static void VerifyPackets(ref int failures)
        {
            var snapshot = new TotalAttendanceSnapshot
            {
                TotalAttendanceWeekCount = 5,
                ThisWeekRecommendClearCount = 4,
                CurrentEventWeekNo = 5,
                CanCheckThisWeek = true,
            };

            var body = TotalAttendancePacketBuilder.BuildStateBody(snapshot);
            var packet = TotalAttendancePacketBuilder.BuildStatePacket(snapshot);
            var ack = TotalAttendancePacketBuilder.BuildCheckThisWeekAckPacket(0);

            Check(
                "EVENT_TOTAL_ATTENDANCE body matches 450B key-offset layout",
                body.Length == TotalAttendancePacketBuilder.StateBodyLength
                && BitConverter.ToUInt32(body, 0x004) == 5
                && body[0x198] == 1
                && body[0x19C] == 5
                && BitConverter.ToUInt32(body, 0x1B0) == 4
                && BitConverter.ToUInt32(body, 0x1BC) == 5
                && body[0x1C0] == 1
                && body[0x1C1] == 0,
                ref failures);

            Check(
                "total attendance packet is NOTI 1069 with 465B total size",
                packet.Length == 465
                && packet[0] == 0x00
                && BitConverter.ToUInt16(packet, 1)
                    == (ushort)NotiPacketTypeA21.EVENT_TOTAL_ATTENDANCE
                && BitConverter.ToUInt32(packet, 3) == 465,
                ref failures);

            Check(
                "check-this-week ack is CMD 1089 with u32 result body",
                ack.Length == 19
                && ack[0] == 0x01
                && BitConverter.ToUInt16(ack, 1)
                    == (ushort)CmdPacketTypeA21
                        .EVENT_TOTAL_ATTENDANCE_CHECK_THISWEEK
                && BitConverter.ToUInt32(ack, 15) == 0,
                ref failures);
        }

        private static void VerifyV16ToV17Migration(
            string databasePath,
            ref int failures)
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var database = new GameDatabase(
                databasePath,
                ServerPaths.SchemaFilePath);
            using (var connection = database.OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
DROP TABLE IF EXISTS event_total_attendance_weekly;
DROP TABLE IF EXISTS event_total_attendance_account;
DROP TABLE IF EXISTS account_recommend_dungeon_clear_stats;
DELETE FROM game_event_state WHERE event_id=2208;
UPDATE schema_metadata SET schema_version=16 WHERE singleton_id=1;
PRAGMA user_version=16;";
                    command.ExecuteNonQuery();
                }

                SqliteMigrations.Apply(connection);
                Check(
                    "schema v16 migrates continuously to v17 total attendance tables",
                    SqliteMigrations.ReadVersion(connection)
                        == SqliteMigrations.CurrentVersion
                    && TableExists(
                        connection,
                        "account_recommend_dungeon_clear_stats")
                    && TableExists(
                        connection,
                        "event_total_attendance_account")
                    && TableExists(
                        connection,
                        "event_total_attendance_weekly")
                    && CountRows(
                        connection,
                        "game_event_state",
                        "event_id=2208") == 1,
                    ref failures);
            }
        }

        private static void VerifyServiceRecommendedClearAndWeeklyCheck(
            string databasePath,
            ref int failures)
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var database = new GameDatabase(
                databasePath,
                ServerPaths.SchemaFilePath);
            Seed(database);
            var config = TotalAttendanceConfigParser.CreateFallback();
            DateTimeOffset now = LocalWeek(0);
            var statsService = new RecommendDungeonClearStatsService(
                database,
                nowProvider: () => now);
            statsService.Initialize();
            var service = new TotalAttendanceService(
                database,
                new MailboxService(new MailboxRepository(database)),
                config: config,
                nowProvider: () => now);
            service.Initialize();
            EnableEvent(database);

            TotalAttendanceClearResult lastClear = null;
            for (var index = 0; index < 4; index++)
            {
                var stats = statsService.RecordClear(AccountId);
                lastClear = service.ApplyRecommendedDungeonClear(
                    AccountId,
                    CharacterId,
                    dungeonId: 1,
                    weeklyRecommendClearCount: stats.WeeklyClearCount,
                    sourceEventId: Guid.NewGuid());
            }

            Check(
                "four suitable clears share-count to 4/4 and light weekly check",
                lastClear != null
                && lastClear.Status == TotalAttendanceClearStatus.ReadyToCheck
                && lastClear.Snapshot.ThisWeekRecommendClearCount == 4
                && lastClear.Snapshot.CanCheckThisWeek
                && CountRows(database, "mailbox_messages") == 0,
                ref failures);

            var firstCheck = service.CheckThisWeek(
                AccountId,
                CharacterId,
                "total-attendance",
                86);
            Check(
                "click weekly check grants week reward by mail",
                firstCheck.Status == TotalAttendanceCheckStatus.Checked
                && firstCheck.MailDelivered
                && firstCheck.Snapshot.TotalAttendanceWeekCount == 1
                && firstCheck.Snapshot.CheckedThisWeek
                && !firstCheck.Snapshot.CanCheckThisWeek
                && CountRows(database, "mailbox_messages") == 1
                && CountRows(
                    database,
                    "mailbox_attachments",
                    "item_template_id=490003187") == 1,
                ref failures);

            var duplicate = service.CheckThisWeek(
                AccountId,
                CharacterId,
                "total-attendance",
                86);
            Check(
                "same week cannot check twice",
                duplicate.Status == TotalAttendanceCheckStatus.AlreadyChecked
                && CountRows(database, "mailbox_messages") == 1,
                ref failures);

            SetAccountProgress(
                database,
                totalWeekCount: 3,
                totalRewardSentMask: 0);
            now = LocalWeek(1);
            for (var index = 0; index < 4; index++)
                statsService.RecordClear(AccountId);

            var fourthCheck = service.CheckThisWeek(
                AccountId,
                CharacterId,
                "total-attendance",
                86);
            Check(
                "third to fourth total week mails weekly and first total reward",
                fourthCheck.Status == TotalAttendanceCheckStatus.Checked
                && fourthCheck.MailedRewardCount == 2
                && fourthCheck.Snapshot.TotalAttendanceWeekCount == 4
                && fourthCheck.Snapshot.TotalRewardSentMask == 1
                && CountRows(
                    database,
                    "mailbox_attachments",
                    "item_template_id IN (490003196,490003219)") == 2,
                ref failures);

            SetAccountProgress(
                database,
                totalWeekCount: 12,
                totalRewardSentMask: 7);
            now = LocalWeek(2);
            var limitStats = statsService.RecordClear(AccountId);
            var afterLimitClear = service.ApplyRecommendedDungeonClear(
                AccountId,
                CharacterId,
                dungeonId: 1,
                weeklyRecommendClearCount: limitStats.WeeklyClearCount,
                sourceEventId: Guid.NewGuid());
            var afterLimitCheck = service.CheckThisWeek(
                AccountId,
                CharacterId,
                "total-attendance",
                86);
            Check(
                "after 12 total weeks no further weekly attendance or mail",
                afterLimitClear.Status
                    == TotalAttendanceClearStatus.AttendanceLimitReached
                && afterLimitCheck.Status
                    == TotalAttendanceCheckStatus.AttendanceLimitReached
                && CountRows(database, "mailbox_messages") == 3,
                ref failures);
        }

        private static string BuildConfigText()
        {
            var builder = new StringBuilder();
            builder.AppendLine("[event duration]");
            builder.AppendLine("12");
            builder.AppendLine("[attendance condition]");
            builder.AppendLine("4 44");
            builder.AppendLine("[attendance week]");
            var weeklyItemIds = new[]
            {
                490003187, 490003188, 490003189, 490003196,
                490003190, 490003191, 490003192, 490003197,
                490003193, 490003194, 490003195, 490003198,
            };
            for (var index = 1; index <= weeklyItemIds.Length; index++)
                builder.Append(index)
                    .Append(' ')
                    .Append(weeklyItemIds[index - 1])
                    .AppendLine(" 1");
            builder.AppendLine("[/attendance week]");
            builder.AppendLine("[total attendance week]");
            builder.AppendLine("4 490003219 1 8 490003220 1 11 490003221 1");
            builder.AppendLine("[/total attendance week]");
            return builder.ToString();
        }

        private static void Seed(GameDatabase database)
        {
            ExecuteSql(database, @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (2208001, 'a21-total-attendance-selftest', '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES (2208101, 2208001, 'a21-total-attendance', 86);");
        }

        private static void EnableEvent(GameDatabase database)
        {
            ExecuteSql(database, @"
INSERT INTO game_event_state(event_id, state)
VALUES(2208, 1)
ON CONFLICT(event_id) DO UPDATE SET state=1;
INSERT INTO game_event_info_extra(event_id, param0, param1, sort_order)
VALUES(2208, 4, 12, 22)
ON CONFLICT(event_id) DO UPDATE SET param0=4, param1=12;");
        }

        private static void SetAccountProgress(
            GameDatabase database,
            int totalWeekCount,
            int totalRewardSentMask)
        {
            ExecuteSql(database, $@"
INSERT INTO event_total_attendance_account (
    account_id, event_id, season_id,
    total_attendance_week_count, total_reward_sent_mask
) VALUES (
    {AccountId}, 2208, 1, {totalWeekCount}, {totalRewardSentMask}
)
ON CONFLICT(account_id, event_id, season_id) DO UPDATE SET
    total_attendance_week_count={totalWeekCount},
    total_reward_sent_mask={totalRewardSentMask};");
        }

        private static DateTimeOffset LocalWeek(int weekOffset)
        {
            return new DateTimeOffset(
                2026,
                8,
                24,
                9,
                0,
                0,
                BeijingOffset).AddDays(7 * weekOffset);
        }

        private static bool TableExists(SqliteConnection connection, string table)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type='table' AND name=@name;";
                command.Parameters.AddWithValue("@name", table);
                return Convert.ToInt32(command.ExecuteScalar()) == 1;
            }
        }

        private static int CountRows(GameDatabase database, string table)
        {
            using (var connection = database.OpenConnection())
                return CountRows(connection, table, null);
        }

        private static int CountRows(
            GameDatabase database,
            string table,
            string whereClause)
        {
            using (var connection = database.OpenConnection())
                return CountRows(connection, table, whereClause);
        }

        private static int CountRows(
            SqliteConnection connection,
            string table,
            string whereClause)
        {
            return ExecuteScalarInt(
                connection,
                "SELECT COUNT(*) FROM " + table
                + (string.IsNullOrWhiteSpace(whereClause)
                    ? string.Empty
                    : " WHERE " + whereClause)
                + ";");
        }

        private static int ExecuteScalarInt(
            SqliteConnection connection,
            string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static void ExecuteSql(GameDatabase database, string sql)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
