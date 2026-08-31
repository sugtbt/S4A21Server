using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Events.PcRoomTimePoint;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Events;
using DfoServer.Network.Parsers.Events;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class A21PcRoomTimePointEventSelfTest
    {
        private const int AccountId = 228001;
        private const int CharacterId = 228101;
        private const int RelogAccountId = 228002;
        private const int RelogCharacterId = 228102;
        private const int PeriodAccountId = 228003;
        private const int PeriodCharacterId = 228103;
        private const int PeriodMaskAccountId = 228004;
        private const int PeriodMaskCharacterId = 228104;
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        public static int Run()
        {
            Console.WriteLine("=== A21_PCROOM_TIMEPOINT_EVENT selftest ===");

            var failures = 0;
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"dfo_a21_pcroom_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var migrationDatabasePath = Path.Combine(tempDirectory, "pcroom_migration.db");
            var serviceDatabasePath = Path.Combine(tempDirectory, "pcroom_service.db");

            try
            {
                VerifyConfig(ref failures);
                VerifyPackets(ref failures);
                VerifyRequestParser(ref failures);
                VerifyV14ToV15Migration(migrationDatabasePath, ref failures);
                VerifyServiceOnlineAndClaim(serviceDatabasePath, ref failures);
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
                ? "=== A21_PCROOM_TIMEPOINT_EVENT PASS ==="
                : $"=== A21_PCROOM_TIMEPOINT_EVENT FAIL ({failures}) ===");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyConfig(ref int failures)
        {
            var config = PcRoomTimePointConfigParser.Parse(@"
[daily reward autoget]
`false`
[daily reward loop]
1
[period reward loop]
4
[daily reward items]
1 490003510 1 1800000  # 30 minutes
`true`
`true` 1 490003662 1 1800000 # another 30 minutes
`true`
`true` 1 490003514 1 3600000 # another hour
`true`
`true` 1 490003512 1 3600000
`true`
`true`
[/daily reward items]
[period reward item]
1 490003515 1 5
`true` 2 490003516 1 5
`true` 3 490003517 1 5
`true` 4 490003518 1 5
`true`
[/period reward item]");

            Check(
                "config parses four daily cumulative thresholds",
                config.DailyRewards.Count == 4
                && config.DailyRewards[0].CumulativeRequiredMillis == 1800000
                && config.DailyRewards[1].CumulativeRequiredMillis == 3600000
                && config.DailyRewards[2].CumulativeRequiredMillis == 7200000
                && config.DailyRewards[3].CumulativeRequiredMillis == 10800000
                && config.TotalDailyRequiredMillis == 10800000,
                ref failures);
            Check(
                "config parses period rewards as 5/10/15/20",
                config.PeriodRewards.Count == 4
                && config.PeriodRewards[0].CumulativeRequiredCount == 5
                && config.PeriodRewards[1].CumulativeRequiredCount == 10
                && config.PeriodRewards[2].CumulativeRequiredCount == 15
                && config.PeriodRewards[3].CumulativeRequiredCount == 20,
                ref failures);

            var compactConfig = PcRoomTimePointConfigParser.Parse(@"
[daily reward autoget]
`false`
[daily reward loop]
1
[period reward loop]
4
[daily reward items]
1 490003510 1 120000 `true` `true` 1 490003662 1 120000 `true` `true` 1 490003514 1 120000 `true` `true` 1 490003512 1 120000 `true` `true`
[/daily reward items]
[period reward item]
1 490003515 1 5 `true` 2 490003516 1 5 `true` 3 490003517 1 5 `true` 4 490003518 1 5 `true`
[/period reward item]");
            Check(
                "config parses compact same-line PVF rewards",
                compactConfig.DailyRewards.Count == 4
                && compactConfig.DailyRewards[0].CumulativeRequiredMillis == 120000
                && compactConfig.DailyRewards[3].CumulativeRequiredMillis == 480000
                && compactConfig.TotalDailyRequiredMillis == 480000
                && compactConfig.PeriodRewards.Count == 4
                && compactConfig.PeriodRewards[3].CumulativeRequiredCount == 20,
                ref failures);

            VerifyRealPvfConfigIfAvailable(ref failures);
        }

        private static void VerifyRealPvfConfigIfAvailable(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine(
                    "[SKIP] real PVF pcroomtimepoint parse: PVF_ARCHIVE_PATH is not set");
                return;
            }

            try
            {
                var config = PcRoomTimePointConfigParser.Parse(
                    PvfArchiveAccessor.ReadText(PcRoomTimePointConfig.PvfPath));
                Check(
                    "real PVF pcroomtimepoint config parses four daily and period rewards",
                    config.DailyRewards.Count == 4
                    && config.PeriodRewards.Count == 4
                    && config.TotalDailyRequiredMillis > 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] real PVF pcroomtimepoint parse: " + ex.Message);
                failures++;
            }
        }

        private static void VerifyPackets(ref int failures)
        {
            var snapshot = new PcRoomTimePointSnapshot
            {
                DailyOnlineMillis = 2684000,
                PeriodCompletedCount = 7,
                DailyClaimMask = 0x03,
                PeriodAvailableMask = 0x03,
                PeriodClaimMask = 0x02,
            };

            var body = PcRoomTimePointPacketBuilder.BuildStateBody(snapshot);
            var packet = PcRoomTimePointPacketBuilder.BuildStatePacket(snapshot);
            var ack = PcRoomTimePointPacketBuilder.BuildAckPacket();

            Check(
                "PCROOM_TIME_POINT body matches 17B reversed layout",
                body.Length == PcRoomTimePointPacketBuilder.StateBodyLength
                && BitConverter.ToUInt32(body, 0) == 2684
                && BitConverter.ToUInt32(body, 4) == 7
                && body[8] == 0x03
                && BitConverter.ToUInt32(body, 9) == 0
                && BitConverter.ToUInt32(body, 13) == 1,
                ref failures);
            var partialPeriod = new PcRoomTimePointSnapshot
            {
                DailyOnlineMillis = 0,
                PeriodCompletedCount = 20,
                PeriodAvailableMask = 0x0F,
                PeriodClaimMask = 0x05,
            };
            Check(
                "PCROOM_TIME_POINT period field sends client gray mask",
                BitConverter.ToUInt32(
                    PcRoomTimePointPacketBuilder.BuildStateBody(partialPeriod),
                    13) == 0x0A,
                ref failures);
            var newlyUnlockedPeriod = new PcRoomTimePointSnapshot
            {
                DailyOnlineMillis = 0,
                PeriodCompletedCount = 5,
                PeriodAvailableMask = 0x01,
                PeriodClaimMask = 0x01,
            };
            Check(
                "PCROOM_TIME_POINT sends newly unlocked period reward as claimable",
                BitConverter.ToUInt32(
                    PcRoomTimePointPacketBuilder.BuildStateBody(newlyUnlockedPeriod),
                    13) == 0,
                ref failures);
            var allClaimablePeriod = new PcRoomTimePointSnapshot
            {
                DailyOnlineMillis = 0,
                PeriodCompletedCount = 20,
                PeriodAvailableMask = 0x0F,
                PeriodClaimMask = 0x0F,
            };
            Check(
                "PCROOM_TIME_POINT sends zero gray mask when all period rewards claimable",
                BitConverter.ToUInt32(
                    PcRoomTimePointPacketBuilder.BuildStateBody(allClaimablePeriod),
                    13) == 0,
                ref failures);
            var noneClaimablePeriod = new PcRoomTimePointSnapshot
            {
                DailyOnlineMillis = 0,
                PeriodCompletedCount = 20,
                PeriodAvailableMask = 0x0F,
                PeriodClaimMask = 0,
            };
            Check(
                "PCROOM_TIME_POINT grays all period rewards when none are claimable",
                BitConverter.ToUInt32(
                    PcRoomTimePointPacketBuilder.BuildStateBody(noneClaimablePeriod),
                    13) == 0x0F,
                ref failures);
            Check(
                "PCROOM_TIME_POINT packet is NOTI 562 with 32B total size",
                packet.Length == 32
                && packet[0] == 0x00
                && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.PCROOM_TIME_POINT
                && BitConverter.ToUInt32(packet, 3) == 32,
                ref failures);
            Check(
                "GET_PCROOM_TIME_POINT_ITEM ack is CMD 633 with 6B zero body",
                ack.Length == 21
                && ack[0] == 0x01
                && BitConverter.ToUInt16(ack, 1) == (ushort)CmdPacketTypeA21.GET_PCROOM_TIME_POINT_ITEM
                && ack.Skip(15).All(value => value == 0),
                ref failures);
        }

        private static void VerifyRequestParser(ref int failures)
        {
            Check(
                "parser accepts query 00 FF with padding",
                PcRoomTimePointRequestParser.TryParse(
                    new byte[] { 0x00, 0xFF, 0, 0, 0 },
                    out var query)
                && query.Kind == PcRoomTimePointRequestKind.Query,
                ref failures);
            Check(
                "parser maps daily bit selectors to stages",
                PcRoomTimePointRequestParser.TryParse(
                    new byte[] { 0x08, 0xFF },
                    out var daily)
                && daily.Kind == PcRoomTimePointRequestKind.DailyReward
                && daily.StageIndex == 4,
                ref failures);
            Check(
                "parser maps period buttons 701-704 as 10 00-03",
                PcRoomTimePointRequestParser.TryParse(
                    new byte[] { 0x10, 0x00 },
                    out var period)
                && period.Kind == PcRoomTimePointRequestKind.PeriodReward
                && period.StageIndex == 1
                && PcRoomTimePointRequestParser.TryParse(
                    new byte[] { 0x10, 0x03 },
                    out var period4)
                && period4.Kind == PcRoomTimePointRequestKind.PeriodReward
                && period4.StageIndex == 4,
                ref failures);
            Check(
                "parser rejects period selector outside 701-704 range",
                !PcRoomTimePointRequestParser.TryParse(
                    new byte[] { 0x10, 0x04 },
                    out _),
                ref failures);
        }

        private static void VerifyV14ToV15Migration(
            string databasePath,
            ref int failures)
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
            using (var connection = database.OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
DROP TABLE IF EXISTS event_pcroom_timepoint_daily;
DROP TABLE IF EXISTS event_pcroom_timepoint_period;
DELETE FROM game_event_state WHERE event_id=228;
UPDATE schema_metadata SET schema_version=14 WHERE singleton_id=1;
PRAGMA user_version=14;";
                    command.ExecuteNonQuery();
                }

                SqliteMigrations.Apply(connection);
                Check(
                    "schema v14 migrates continuously to v15 pcroom tables",
                    SqliteMigrations.ReadVersion(connection) == SqliteMigrations.CurrentVersion
                    && TableExists(connection, "event_pcroom_timepoint_daily")
                    && TableExists(connection, "event_pcroom_timepoint_period")
                    && CountRows(connection, "game_event_state", "event_id=228") == 1,
                    ref failures);
            }
        }

        private static void VerifyServiceOnlineAndClaim(
            string databasePath,
            ref int failures)
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
            Seed(database);
            DateTimeOffset now = Local(10, 0);
            var service = new PcRoomTimePointService(
                database,
                new MailboxService(new MailboxRepository(database)),
                config: PcRoomTimePointConfigParser.CreateFallback(),
                nowProvider: () => now);
            service.Initialize();
            EnableEvent(database);

            var sessionId = Guid.NewGuid();
            service.BeginSession(sessionId, AccountId, CharacterId);
            now = now.AddMinutes(29).AddSeconds(59);
            Check(
                "online time below first threshold does not light rewards",
                service.TryGetSnapshotForSession(
                    sessionId,
                    AccountId,
                    CharacterId,
                    out var early)
                && early.DailyOnlineSecondsForClient == 1799
                && early.DailyAvailableMask == 0,
                ref failures);

            now = now.AddSeconds(1);
            Check(
                "first daily threshold becomes available at 30 minutes",
                service.TryGetSnapshotForSession(
                    sessionId,
                    AccountId,
                    CharacterId,
                    out var first)
                && first.DailyOnlineSecondsForClient == 1800
                && first.DailyAvailableMask == 0x01,
                ref failures);

            var claim1 = service.Claim(
                sessionId,
                AccountId,
                CharacterId,
                "pcroom",
                61,
                DailyCommand(1));
            Check(
                "daily stage 1 claim sends one mail and records claim bit",
                claim1.Success
                && claim1.MailDelivered
                && claim1.Snapshot.DailyClaimMask == 0x01
                && CountRows(database, "mailbox_messages") == 1,
                ref failures);

            var repeat = service.Claim(
                sessionId,
                AccountId,
                CharacterId,
                "pcroom",
                61,
                DailyCommand(1));
            Check(
                "repeat daily claim is rejected without another mail",
                repeat.Status == PcRoomTimePointClaimStatus.AlreadyClaimed
                && CountRows(database, "mailbox_messages") == 1,
                ref failures);

            now = Local(13, 0);
            Check(
                "full daily online time records exactly one period completion",
                service.TryGetSnapshotForSession(
                    sessionId,
                    AccountId,
                    CharacterId,
                    out var complete)
                && complete.DailyAvailableMask == 0x0F
                && complete.PeriodCompletedCount == 1
                && complete.PeriodClaimMask == 0,
                ref failures);
            Check(
                "re-reading complete daily state does not duplicate period completion",
                service.TryGetSnapshotForSession(
                    sessionId,
                    AccountId,
                    CharacterId,
                    out var reread)
                && reread.PeriodCompletedCount == 1,
                ref failures);

            _ = service.Claim(
                sessionId,
                AccountId,
                CharacterId,
                "pcroom",
                61,
                DailyCommand(2));
            _ = service.Claim(
                sessionId,
                AccountId,
                CharacterId,
                "pcroom",
                61,
                DailyCommand(4));
            service.EndSession(sessionId);

            now = Local(13, 5);
            var relogSessionId = Guid.NewGuid();
            service.BeginSession(relogSessionId, AccountId, CharacterId);
            Check(
                "partial daily claim mask survives relog while unclaimed stages stay available",
                service.TryGetSnapshotForSession(
                    relogSessionId,
                    AccountId,
                    CharacterId,
                    out var relog)
                && relog.DailyAvailableMask == 0x0F
                && relog.DailyClaimMask == 0x0B,
                ref failures);

            SetPeriodState(database, AccountId, 5, 0x01);
            Check(
                "period completed count lights claimable stage before claim",
                service.TryGetSnapshotForSession(
                    relogSessionId,
                    AccountId,
                    CharacterId,
                    out var periodReady)
                && periodReady.PeriodCompletedCount == 5
                && periodReady.PeriodAvailableMask == 0x01
                && periodReady.PeriodClaimMask == 0x01,
                ref failures);
            var periodClaim = service.Claim(
                relogSessionId,
                AccountId,
                CharacterId,
                "pcroom",
                61,
                PeriodCommand(1));
            Check(
                "period claim uses selected 10 00 stage and clears claimable bit",
                periodClaim.Success
                && periodClaim.Snapshot.PeriodCompletedCount == 5
                && periodClaim.Snapshot.PeriodClaimMask == 0
                && periodClaim.MailDelivered,
                ref failures);

            VerifyNaturalPeriodUnlockAndClaim(database, ref failures);

            VerifyRelogOnlineTimePersistence(database, ref failures);
        }

        private static void VerifyNaturalPeriodUnlockAndClaim(
            GameDatabase database,
            ref int failures)
        {
            DateTimeOffset now = Local(10, 0);
            var service = new PcRoomTimePointService(
                database,
                new MailboxService(new MailboxRepository(database)),
                config: PcRoomTimePointConfigParser.CreateFallback(),
                nowProvider: () => now);
            service.Initialize();
            EnableEvent(database);
            SetPeriodState(database, PeriodAccountId, 4, 0);

            var sessionId = Guid.NewGuid();
            service.BeginSession(sessionId, PeriodAccountId, PeriodCharacterId);
            now = Local(13, 0);
            Check(
                "natural 4 to 5 completion unlocks first period claim bit",
                service.TryGetSnapshotForSession(
                    sessionId,
                    PeriodAccountId,
                    PeriodCharacterId,
                    out var unlocked)
                && unlocked.PeriodCompletedCount == 5
                && unlocked.PeriodAvailableMask == 0x01
                && unlocked.PeriodClaimMask == 0x01,
                ref failures);

            var mailsBefore = CountRows(database, "mailbox_messages");
            var claimFirst = service.Claim(
                sessionId,
                PeriodAccountId,
                PeriodCharacterId,
                "pcroom-period",
                61,
                PeriodCommand(1));
            Check(
                "period 10 00 claim sends mail and clears first claimable bit",
                claimFirst.Success
                && claimFirst.MailDelivered
                && claimFirst.Snapshot.PeriodClaimMask == 0
                && CountRows(database, "mailbox_messages") == mailsBefore + 1,
                ref failures);

            SetPeriodState(database, PeriodMaskAccountId, 20, 0x0F);
            var maskSessionId = Guid.NewGuid();
            service.BeginSession(maskSessionId, PeriodMaskAccountId, PeriodMaskCharacterId);
            mailsBefore = CountRows(database, "mailbox_messages");
            var claimFive = service.Claim(
                maskSessionId,
                PeriodMaskAccountId,
                PeriodMaskCharacterId,
                "pcroom-period",
                61,
                PeriodCommand(1));
            var claimFifteen = service.Claim(
                maskSessionId,
                PeriodMaskAccountId,
                PeriodMaskCharacterId,
                "pcroom-period",
                61,
                PeriodCommand(3));
            Check(
                "period claimable mask 15 clears 5 and 15 rewards to 10",
                claimFive.Success
                && claimFifteen.Success
                && claimFifteen.Snapshot.PeriodClaimMask == 0x0A
                && CountRows(database, "mailbox_messages") == mailsBefore + 2,
                ref failures);
        }

        private static void VerifyRelogOnlineTimePersistence(
            GameDatabase database,
            ref int failures)
        {
            DateTimeOffset now = Local(9, 0);
            var controlled = new PcRoomTimePointService(
                database,
                new MailboxService(new MailboxRepository(database)),
                config: PcRoomTimePointConfigParser.CreateFallback(),
                nowProvider: () => now);
            controlled.Initialize();

            var firstSession = Guid.NewGuid();
            controlled.BeginSession(firstSession, RelogAccountId, RelogCharacterId);
            now = now.AddMinutes(10);
            controlled.EndSession(firstSession);

            now = now.AddMinutes(5);
            var secondSession = Guid.NewGuid();
            controlled.BeginSession(secondSession, RelogAccountId, RelogCharacterId);
            Check(
                "offline relog resumes from persisted online time instead of zero",
                controlled.TryGetSnapshotForSession(
                    secondSession,
                    RelogAccountId,
                    RelogCharacterId,
                    out var snapshot)
                && snapshot.DailyOnlineSecondsForClient >= 600
                && snapshot.DailyOnlineSecondsForClient < 660,
                ref failures);
        }

        private static PcRoomTimePointClaimCommand DailyCommand(int stage)
        {
            return new PcRoomTimePointClaimCommand
            {
                Kind = PcRoomTimePointRequestKind.DailyReward,
                StageIndex = stage,
                Selector = (byte)(1 << (stage - 1)),
                IndexOrFF = 0xFF,
            };
        }

        private static PcRoomTimePointClaimCommand PeriodCommand(int stage)
        {
            return new PcRoomTimePointClaimCommand
            {
                Kind = PcRoomTimePointRequestKind.PeriodReward,
                StageIndex = stage,
                Selector = 0x10,
                IndexOrFF = (byte)(stage - 1),
            };
        }

        private static void Seed(GameDatabase database)
        {
            ExecuteSql(database, @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES
    (228001, 'a21-pcroom-selftest', ''),
    (228002, 'a21-pcroom-relog-selftest', ''),
    (228003, 'a21-pcroom-period-selftest', ''),
    (228004, 'a21-pcroom-period-mask-selftest', '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES
    (228101, 228001, 'a21-pcroom', 61),
    (228102, 228002, 'a21-pcroom-relog', 61),
    (228103, 228003, 'a21-pcroom-period', 61),
    (228104, 228004, 'a21-pcroom-period-mask', 61);");
        }

        private static void EnableEvent(GameDatabase database)
        {
            ExecuteSql(database, @"
INSERT INTO game_event_state(event_id, state)
VALUES(228, 1)
ON CONFLICT(event_id) DO UPDATE SET state=1;");
        }

        private static void SetPeriodState(
            GameDatabase database,
            int accountId,
            int completedCount,
            int claimMask)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO event_pcroom_timepoint_period (
    account_id, event_id, season_id, completed_cycle_count, period_claim_mask
) VALUES (
    @accountId, 228, 1, @completedCount, @claimMask
)
ON CONFLICT(account_id, event_id, season_id) DO UPDATE SET
    completed_cycle_count=@completedCount,
    period_claim_mask=@claimMask;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@completedCount", completedCount);
                command.Parameters.AddWithValue("@claimMask", claimMask);
                command.ExecuteNonQuery();
            }
        }

        private static DateTimeOffset Local(int hour, int minute)
        {
            return new DateTimeOffset(
                2026,
                8,
                25,
                0,
                0,
                0,
                BeijingOffset).AddHours(hour).AddMinutes(minute);
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
            SqliteConnection connection,
            string table,
            string whereClause)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM " + table
                    + (string.IsNullOrWhiteSpace(whereClause)
                        ? string.Empty
                        : " WHERE " + whereClause)
                    + ";";
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
