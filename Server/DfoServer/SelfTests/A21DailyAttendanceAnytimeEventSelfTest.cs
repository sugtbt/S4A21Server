using System;
using System.IO;
using System.Linq;
using System.Text;
using DfoServer.Game.Events.DailyAttendanceAnytime;
using DfoServer.Game.Events.RecommendedDungeons;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Events;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.SelfTests
{
    public static class A21DailyAttendanceAnytimeEventSelfTest
    {
        private const int AccountId = 2370001;
        private const int CharacterId = 2370101;
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        public static int Run()
        {
            Console.WriteLine("=== A21_DAILY_ATTENDANCE_ANYTIME_EVENT selftest ===");

            var failures = 0;
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"dfo_a21_daily_attendance_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var migrationDatabasePath = Path.Combine(
                tempDirectory,
                "daily_attendance_migration.db");
            var serviceDatabasePath = Path.Combine(
                tempDirectory,
                "daily_attendance_service.db");

            try
            {
                VerifyConfig(ref failures);
                VerifyPackets(ref failures);
                VerifyV15ToV16Migration(migrationDatabasePath, ref failures);
                VerifyServiceRecommendedClearAndMail(
                    serviceDatabasePath,
                    ref failures);
                VerifySuitableDungeonPredicate(ref failures);
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
                ? "=== A21_DAILY_ATTENDANCE_ANYTIME_EVENT PASS ==="
                : $"=== A21_DAILY_ATTENDANCE_ANYTIME_EVENT FAIL ({failures}) ===");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyConfig(ref int failures)
        {
            var config = DailyAttendanceAnytimeConfigParser.Parse(
                BuildConfigText());

            Check(
                "config parses 28 daily rewards and three accumulate rewards",
                config.DailyRewards.Count == 28
                && config.DailyRewards[0].DayIndex == 0
                && config.DailyRewards[27].DayIndex == 27
                && config.AccumulateRewards.Count == 3
                && config.AccumulateRewards[0].RequiredAttendanceCount == 5
                && config.AccumulateRewards[1].RequiredAttendanceCount == 15
                && config.AccumulateRewards[2].RequiredAttendanceCount == 20,
                ref failures);

            VerifyRealPvfConfigIfAvailable(ref failures);
        }

        private static void VerifyRealPvfConfigIfAvailable(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine(
                    "[SKIP] real PVF daily attendance parse: PVF_ARCHIVE_PATH is not set");
                return;
            }

            try
            {
                var config = DailyAttendanceAnytimeConfigParser.Parse(
                    PvfArchiveAccessor.ReadText(
                        DailyAttendanceAnytimeConfig.PvfPath));
                Check(
                    "real PVF daily attendance config parses 28 daily rewards",
                    config.DailyRewards.Count == 28
                    && config.AccumulateRewards.Count == 3,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] real PVF daily attendance parse: " + ex.Message);
                failures++;
            }
        }

        private static void VerifyPackets(ref int failures)
        {
            var snapshot = new DailyAttendanceAnytimeSnapshot
            {
                TotalAttendanceCount = 20,
                TodayRecommendClearCount = 2,
                AccumulateState0 = 2,
                AccumulateState1 = 1,
                AccumulateState2 = 0,
            };

            var body = DailyAttendanceAnytimePacketBuilder
                .BuildStateBody(snapshot);
            var packet = DailyAttendanceAnytimePacketBuilder
                .BuildStatePacket(snapshot);

            Check(
                "INTEGRATE_EVENT_DATA body matches 37B daily attendance layout",
                body.Length == DailyAttendanceAnytimePacketBuilder.StateBodyLength
                && BitConverter.ToUInt32(body, 0) == 2370
                && body[4] == 0
                && BitConverter.ToUInt32(body, 5) == 20
                && BitConverter.ToUInt32(body, 9) == 2
                && BitConverter.ToUInt32(body, 13) == 1
                && BitConverter.ToUInt32(body, 17) == 0
                && BitConverter.ToUInt32(body, 21) == 2
                && BitConverter.ToUInt32(body, 25) == 3
                && BitConverter.ToUInt32(body, 29) == 4
                && BitConverter.ToUInt32(body, 33) == 5,
                ref failures);

            Check(
                "daily attendance packet is NOTI 1181 with 52B total size",
                packet.Length == 52
                && packet[0] == 0x00
                && BitConverter.ToUInt16(packet, 1)
                    == (ushort)NotiPacketTypeA21.INTEGRATE_EVENT_DATA
                && BitConverter.ToUInt32(packet, 3) == 52,
                ref failures);
        }

        private static void VerifyV15ToV16Migration(
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
DROP TABLE IF EXISTS event_daily_attendance_anytime_clear_events;
DROP TABLE IF EXISTS event_daily_attendance_anytime_daily;
DROP TABLE IF EXISTS event_daily_attendance_anytime_account;
DROP TABLE IF EXISTS event_total_attendance_weekly;
DROP TABLE IF EXISTS event_total_attendance_account;
DROP TABLE IF EXISTS account_recommend_dungeon_clear_stats;
DELETE FROM game_event_state WHERE event_id=2208;
DELETE FROM game_event_state WHERE event_id=2370;
UPDATE schema_metadata SET schema_version=15 WHERE singleton_id=1;
PRAGMA user_version=15;";
                    command.ExecuteNonQuery();
                }

                SqliteMigrations.Apply(connection);
                Check(
                    "schema v15 migrates continuously to v16 daily attendance tables",
                    SqliteMigrations.ReadVersion(connection)
                        == SqliteMigrations.CurrentVersion
                    && TableExists(
                        connection,
                        "event_daily_attendance_anytime_account")
                    && TableExists(
                        connection,
                        "event_daily_attendance_anytime_daily")
                    && TableExists(
                        connection,
                        "event_daily_attendance_anytime_clear_events")
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
                        "event_id=2370") == 1
                    && CountRows(
                        connection,
                        "game_event_state",
                        "event_id=2208") == 1,
                    ref failures);
            }
        }

        private static void VerifyServiceRecommendedClearAndMail(
            string databasePath,
            ref int failures)
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var database = new GameDatabase(
                databasePath,
                ServerPaths.SchemaFilePath);
            Seed(database);
            var config = DailyAttendanceAnytimeConfigParser.CreateFallback();
            DateTimeOffset now = LocalDay(0);
            var statsService = new RecommendDungeonClearStatsService(
                database,
                nowProvider: () => now);
            statsService.Initialize();
            var service = new DailyAttendanceAnytimeService(
                database,
                new MailboxService(new MailboxRepository(database)),
                config: config,
                nowProvider: () => now);
            service.Initialize();
            EnableEvent(database);

            var firstClear = Guid.Parse("23700000-0000-0000-0000-000000000001");
            var secondClear = Guid.Parse("23700000-0000-0000-0000-000000000002");
            var firstStats = statsService.RecordClear(AccountId);
            var first = service.ApplyRecommendedDungeonClear(
                AccountId,
                CharacterId,
                "daily-attendance",
                86,
                1,
                firstStats.DailyClearCount,
                firstClear);
            Check(
                "first suitable clear progresses recommended dungeon count to 1/2",
                first.Status == DailyAttendanceAnytimeClearStatus.Progressed
                && !first.MailDelivered
                && first.Snapshot.TodayRecommendClearCount == 1
                && first.Snapshot.TotalAttendanceCount == 0
                && CountRows(database, "mailbox_messages") == 0,
                ref failures);

            var secondStats = statsService.RecordClear(AccountId);
            var second = service.ApplyRecommendedDungeonClear(
                AccountId,
                CharacterId,
                "daily-attendance",
                86,
                1,
                secondStats.DailyClearCount,
                secondClear);
            Check(
                "same suitable dungeon cleared again signs attendance and mails daily reward",
                second.Status == DailyAttendanceAnytimeClearStatus.Attended
                && second.MailDelivered
                && second.Snapshot.TodayRecommendClearCount == 2
                && second.Snapshot.TotalAttendanceCount == 1
                && CountRows(database, "mailbox_messages") == 1
                && CountRows(
                    database,
                    "mailbox_attachments",
                    "item_template_id=490003342") == 1,
                ref failures);

            var afterAttended = service.ApplyRecommendedDungeonClear(
                AccountId,
                CharacterId,
                "daily-attendance",
                86,
                1,
                statsService.RecordClear(AccountId).DailyClearCount,
                Guid.Parse("23700000-0000-0000-0000-000000000003"));
            Check(
                "same day after attendance does not send another reward",
                afterAttended.Status
                    == DailyAttendanceAnytimeClearStatus.AlreadyAttended
                && CountRows(database, "mailbox_messages") == 1,
                ref failures);

            for (var dayOffset = 1; dayOffset < config.MaxAttendanceDays; dayOffset++)
            {
                now = LocalDay(dayOffset);
                var dailyFirstStats = statsService.RecordClear(AccountId);
                service.ApplyRecommendedDungeonClear(
                    AccountId,
                    CharacterId,
                    "daily-attendance",
                    86,
                    1,
                    dailyFirstStats.DailyClearCount,
                    Guid.NewGuid());
                var dailySecondStats = statsService.RecordClear(AccountId);
                service.ApplyRecommendedDungeonClear(
                    AccountId,
                    CharacterId,
                    "daily-attendance",
                    86,
                    1,
                    dailySecondStats.DailyClearCount,
                    Guid.NewGuid());
            }

            Check(
                "daily attendance stops at 28 automatic sign-ins and 28 mails",
                LoadTotalAttendance(database) == config.MaxAttendanceDays
                && CountRows(database, "mailbox_messages")
                    == config.MaxAttendanceDays,
                ref failures);

            now = LocalDay(config.MaxAttendanceDays);
            var overflowStats = statsService.RecordClear(AccountId);
            var overflow = service.ApplyRecommendedDungeonClear(
                AccountId,
                CharacterId,
                "daily-attendance",
                86,
                1,
                overflowStats.DailyClearCount,
                Guid.NewGuid());
            Check(
                "after 28 days further clears no longer sign or mail rewards",
                overflow.Status
                    == DailyAttendanceAnytimeClearStatus.AttendanceLimitReached
                && overflow.Snapshot.TotalAttendanceCount
                    == config.MaxAttendanceDays
                && CountRows(database, "mailbox_messages")
                    == config.MaxAttendanceDays,
                ref failures);

            SetAccountProgress(database, totalAttendanceCount: 20, claimedMask: 5);
            Check(
                "accumulate states expose reached and claimable rewards separately",
                service.TryGetSnapshot(
                    AccountId,
                    CharacterId,
                    out var snapshot)
                && snapshot.AccumulateState0 == 1
                && snapshot.AccumulateState1 == 2
                && snapshot.AccumulateState2 == 1,
                ref failures);

            SetAccountProgress(database, totalAttendanceCount: 4, claimedMask: 0);
            now = LocalDay(config.MaxAttendanceDays + 1);
            var dailyUnlockMailsBefore = CountRows(database, "mailbox_messages");
            var dailyUnlockFirstStats = statsService.RecordClear(AccountId);
            service.ApplyRecommendedDungeonClear(
                AccountId,
                CharacterId,
                "daily-attendance",
                86,
                1,
                dailyUnlockFirstStats.DailyClearCount,
                Guid.NewGuid());
            var dailyUnlockSecondStats = statsService.RecordClear(AccountId);
            var dailyUnlock = service.ApplyRecommendedDungeonClear(
                AccountId,
                CharacterId,
                "daily-attendance",
                86,
                1,
                dailyUnlockSecondStats.DailyClearCount,
                Guid.NewGuid());
            Check(
                "natural 4 to 5 attendance unlocks first accumulate claim bit",
                dailyUnlock.Status == DailyAttendanceAnytimeClearStatus.Attended
                && dailyUnlock.Snapshot.TotalAttendanceCount == 5
                && dailyUnlock.Snapshot.AccumulateClaimedMask == 1
                && dailyUnlock.Snapshot.AccumulateState0 == 1
                && CountRows(database, "mailbox_messages")
                    == dailyUnlockMailsBefore + 1,
                ref failures);

            SetAccountProgress(database, totalAttendanceCount: 20, claimedMask: 7);
            var mailsBeforeClaims = CountRows(database, "mailbox_messages");
            var claimFirst = service.ClaimAccumulateReward(
                AccountId,
                CharacterId,
                "daily-attendance",
                86);
            Check(
                "first accumulate claim grants 5-day reward and leaves later rewards claimable",
                claimFirst.Status == DailyAttendanceAnytimeClaimStatus.Claimed
                && claimFirst.MailDelivered
                && claimFirst.ClaimedStageIndex == 0
                && claimFirst.ItemId == 490003353
                && claimFirst.Snapshot.AccumulateClaimedMask == 6
                && claimFirst.Snapshot.AccumulateState0 == 2
                && claimFirst.Snapshot.AccumulateState1 == 1
                && claimFirst.Snapshot.AccumulateState2 == 1
                && CountRows(database, "mailbox_messages")
                    == mailsBeforeClaims + 1,
                ref failures);

            var claimSecond = service.ClaimAccumulateReward(
                AccountId,
                CharacterId,
                "daily-attendance",
                86);
            var claimThird = service.ClaimAccumulateReward(
                AccountId,
                CharacterId,
                "daily-attendance",
                86);
            Check(
                "subsequent accumulate claims consume 15-day then 20-day rewards",
                claimSecond.Status == DailyAttendanceAnytimeClaimStatus.Claimed
                && claimSecond.ClaimedStageIndex == 1
                && claimSecond.ItemId == 490003354
                && claimThird.Status == DailyAttendanceAnytimeClaimStatus.Claimed
                && claimThird.ClaimedStageIndex == 2
                && claimThird.ItemId == 490003355
                && claimThird.Snapshot.AccumulateClaimedMask == 0
                && claimThird.Snapshot.AccumulateState0 == 2
                && claimThird.Snapshot.AccumulateState1 == 2
                && claimThird.Snapshot.AccumulateState2 == 2
                && CountRows(database, "mailbox_messages")
                    == mailsBeforeClaims + 3
                && CountRows(
                    database,
                    "mailbox_attachments",
                    "item_template_id IN (490003353,490003354,490003355)")
                    == 3,
                ref failures);

            var noClaimable = service.ClaimAccumulateReward(
                AccountId,
                CharacterId,
                "daily-attendance",
                86);
            Check(
                "accumulate claim with no available reward sends no extra mail",
                noClaimable.Status
                    == DailyAttendanceAnytimeClaimStatus.NoClaimableReward
                && CountRows(database, "mailbox_messages")
                    == mailsBeforeClaims + 3,
                ref failures);
        }

        private static void VerifySuitableDungeonPredicate(ref int failures)
        {
            Check(
                "level band overlaps recommended dungeon range",
                DungeonData.IsCharacterLevelSuitableForDungeonRange(86, 79, 82)
                && !DungeonData.IsCharacterLevelSuitableForDungeonRange(
                    86,
                    70,
                    75),
                ref failures);

            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine(
                    "[SKIP] real PVF dimension dungeon suitability: PVF_ARCHIVE_PATH is not set");
                return;
            }

            Check(
                "[dimension dungeon] counts as suitable level dungeon",
                DungeonData.IsDimensionDungeon(62)
                && DungeonData.IsSuitableLevelDungeon(62, 1),
                ref failures);
        }

        private static string BuildConfigText()
        {
            var builder = new StringBuilder();
            builder.AppendLine("[daily attendance reward]");
            for (var index = 0; index < 28; index++)
                builder.Append(index)
                    .Append(' ')
                    .Append(490003342 + index)
                    .AppendLine(" 1");
            builder.AppendLine("[/daily attendance reward]");
            builder.AppendLine("[accumulate attendance reward]");
            builder.AppendLine("5 490003353 1 15 490003354 1 20 490003355 1");
            builder.AppendLine("[/accumulate attendance reward]");
            return builder.ToString();
        }

        private static void Seed(GameDatabase database)
        {
            ExecuteSql(database, @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (2370001, 'a21-daily-attendance-selftest', '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES (2370101, 2370001, 'a21-daily-attendance', 86);");
        }

        private static void EnableEvent(GameDatabase database)
        {
            ExecuteSql(database, @"
INSERT INTO game_event_state(event_id, state)
VALUES(2370, 1)
ON CONFLICT(event_id) DO UPDATE SET state=1;
INSERT INTO game_event_info_extra(event_id, param0, sort_order)
VALUES(2370, 2, 21)
ON CONFLICT(event_id) DO UPDATE SET param0=2;");
        }

        private static void SetAccountProgress(
            GameDatabase database,
            int totalAttendanceCount,
            int claimedMask)
        {
            ExecuteSql(database, $@"
INSERT INTO event_daily_attendance_anytime_account (
    account_id, event_id, season_id,
    total_attendance_count, accumulate_claimed_mask
) VALUES (
    {AccountId}, 2370, 1, {totalAttendanceCount}, {claimedMask}
)
ON CONFLICT(account_id, event_id, season_id) DO UPDATE SET
    total_attendance_count={totalAttendanceCount},
    accumulate_claimed_mask={claimedMask};");
        }

        private static int LoadTotalAttendance(GameDatabase database)
        {
            using (var connection = database.OpenConnection())
                return ExecuteScalarInt(
                    connection,
                    @"
SELECT total_attendance_count
FROM event_daily_attendance_anytime_account
WHERE account_id=2370001 AND event_id=2370 AND season_id=1;");
        }

        private static DateTimeOffset LocalDay(int dayOffset)
        {
            return new DateTimeOffset(
                2026,
                8,
                25,
                9,
                0,
                0,
                BeijingOffset).AddDays(dayOffset);
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
