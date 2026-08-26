using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Events;
using DfoServer.Game.Events.Joust;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Events;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Events;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class A21JoustEventSelfTest
    {
        private const int CharacterId = 9236501;
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        public static int Run()
        {
            Console.WriteLine("=== A21_JOUST_EVENT selftest ===");

            var failures = 0;
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"dfo_a21_joust_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var migrationDatabasePath = Path.Combine(tempDirectory, "joust_migration.db");
            var seedDatabasePath = Path.Combine(tempDirectory, "joust_seed.db");
            var serviceDatabasePath = Path.Combine(tempDirectory, "joust_service.db");

            try
            {
                VerifyConfig(ref failures);
                VerifySchedule(ref failures);
                VerifyPacketBuilders(ref failures);
                VerifyBettingParser(ref failures);
                VerifyEventInfoBody(ref failures);
                VerifyV9ToV10Migration(migrationDatabasePath, ref failures);
                VerifyEventInfoSeed(seedDatabasePath, ref failures);
                VerifyServiceRoundResolution(serviceDatabasePath, ref failures);
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
                ? "=== A21_JOUST_EVENT PASS ==="
                : $"=== A21_JOUST_EVENT FAIL ({failures}) ===");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyConfig(ref int failures)
        {
            var config = JoustConfig.CreateFallback();
            Check(
                "fallback config carries PVF-backed event constants",
                config.MinLevel == 17
                && config.MaxBetting == 1000
                && config.RewardItemId == 490002916
                && config.BettingRewardItemId == 490002925
                && config.MaterialItemIds.SequenceEqual(new[] { 490002916, 490700609 })
                && config.Knights.Count == 12
                && config.GetKnight(10)?.AttackType == 28,
                ref failures);
            Check(
                "odds use default 8.0 and one-decimal rounding",
                JoustRepository.CalculateOddsX10(0, 0) == 80
                && JoustRepository.CalculateOddsX10(100, 25) == 40
                && JoustRepository.CalculateOddsX10(101, 30) == 34,
                ref failures);
        }

        private static void VerifySchedule(ref int failures)
        {
            var rule = new JoustRule();
            Check(
                "before daily start is closed",
                PhaseAt(rule, 9, 59) == JoustPhase.Closed,
                ref failures);
            Check(
                "10:00 starts betting",
                PhaseAt(rule, 10, 0) == JoustPhase.Betting,
                ref failures);
            Check(
                "11:29 remains betting",
                PhaseAt(rule, 11, 29) == JoustPhase.Betting,
                ref failures);
            Check(
                "11:30 stops betting",
                PhaseAt(rule, 11, 30) == JoustPhase.StopBetting,
                ref failures);
            var raceStart = ScheduleAt(rule, 11, 40);
            Check(
                "11:40 enters racing stage 0",
                raceStart.Phase == JoustPhase.Racing
                && raceStart.CurrentRaceStage == 0,
                ref failures);
            var raceMiddle = ScheduleAt(rule, 11, 44);
            Check(
                "11:44 advances to racing stage 1",
                raceMiddle.Phase == JoustPhase.Racing
                && raceMiddle.CurrentRaceStage == 1,
                ref failures);
            var raceLast = ScheduleAt(rule, 11, 47);
            Check(
                "11:47 advances to racing stage 2",
                raceLast.Phase == JoustPhase.Racing
                && raceLast.CurrentRaceStage == 2,
                ref failures);
            Check(
                "11:50 enters result review state 2",
                PhaseAt(rule, 11, 50) == JoustPhase.ResultReview,
                ref failures);
            Check(
                "23:59 keeps the seventh round result review open",
                PhaseAt(rule, 23, 59) == JoustPhase.ResultReview,
                ref failures);
            Check(
                "24:00 closes after seven daily rounds",
                PhaseAt(rule, 24, 0) == JoustPhase.Closed,
                ref failures);
        }

        private static void VerifyPacketBuilders(ref int failures)
        {
            var snapshot = new JoustSnapshot
            {
                RoundNo = 7,
                Phase = JoustPhase.Betting,
                CharacterId = CharacterId,
                CharacterTotalBet = 25,
                CurrentResultStageIndex = 2,
                Slots = Enumerable.Range(0, 8)
                    .Select(index => new JoustRoundSlot
                    {
                        RoundNo = 7,
                        SlotNo = index,
                        KnightIndex = index == 7 ? 8 : index,
                        IsBlack = index == 7,
                        ConditionIndex = index % 5,
                        OddsX10 = index == 2 ? 42 : 80,
                        WinCount = index,
                        LossCount = index + 1,
                    })
                    .ToList(),
                Bets = new[]
                {
                    new JoustCharacterBet
                    {
                        SlotNo = 2,
                        KnightIndex = 2,
                        BetAmount = 25,
                    },
                },
                BracketSlots = new ushort[]
                {
                    0, 1, 2, 3, 4, 5, 6, 8,
                    0, 2, 4, 6,
                    2, 6,
                },
            };

            var state = JoustPacketBuilder.BuildState(new JoustStateSnapshot
            {
                RoundNo = snapshot.RoundNo,
                Phase = snapshot.Phase,
            });
            var info = JoustPacketBuilder.BuildInfo(snapshot);
            var betting = JoustPacketBuilder.BuildBettingInfo(snapshot);
            var result = JoustPacketBuilder.BuildMatchResult(snapshot);
            var history = JoustPacketBuilder.BuildMatchHistoryAck(new[]
            {
                new JoustHistoryEntry
                {
                    RoundNo = 6,
                    WinnerHorseId = 2,
                    OddsX10 = 42,
                },
            });

            Check(
                "joust packet body lengths match A21 captures",
                state.Length == 3
                && info.Length == JoustPacketBuilder.InfoBodyLength
                && betting.Length == JoustPacketBuilder.BettingInfoBodyLength
                && result.Length == JoustPacketBuilder.MatchResultBodyLength
                && history.Length == JoustPacketBuilder.MatchHistoryBodyLength,
                ref failures);
            Check(
                "betting phase hides black horse flag",
                info[2 + 7 * 11 + 10] == 1,
                ref failures);

            snapshot.Phase = JoustPhase.ResultReview;
            var reviewInfo = JoustPacketBuilder.BuildInfo(snapshot);
            Check(
                "state 2 reveals black horse after results are available",
                reviewInfo[2 + 7 * 11 + 10] == 0,
                ref failures);
            Check(
                "joust info serializes float odds and win/loss counters",
                Math.Abs(BitConverter.ToSingle(info, 4) - 8.0f) < 0.001f
                && Math.Abs(BitConverter.ToSingle(info, 2 + 2 * 11 + 2) - 4.2f) < 0.001f
                && BitConverter.ToUInt16(info, 2 + 2 * 11 + 6) == 2
                && BitConverter.ToUInt16(info, 2 + 2 * 11 + 8) == 3,
                ref failures);
            Check(
                "betting info serializes personal total and per-horse amount",
                BitConverter.ToInt32(betting, 2) == 25
                && betting[6 + 2 * 5] == 2
                && BitConverter.ToInt32(betting, 6 + 2 * 5 + 1) == 25,
                ref failures);
            Check(
                "match result carries stage index and bracket horse ids",
                result[2] == 2
                && BitConverter.ToUInt16(result, 3 + 12 * 2) == 2,
                ref failures);

            snapshot.Phase = JoustPhase.Betting;
            var bettingTransition =
                EventJoustHandler.BuildClockTransitionPackets(snapshot);
            snapshot.Phase = JoustPhase.Racing;
            snapshot.CurrentResultStageIndex = 1;
            var racingTransition =
                EventJoustHandler.BuildClockTransitionPackets(snapshot);
            snapshot.Phase = JoustPhase.ResultReview;
            var reviewTransition =
                EventJoustHandler.BuildClockTransitionPackets(snapshot);
            Check(
                "clock transition packets match observed joust phase ordering",
                bettingTransition.Count == 1
                && ReadPacketType(bettingTransition[0])
                    == (ushort)NotiPacketTypeA21.JOUST_STATE
                && bettingTransition[0][17] == (byte)JoustPhase.Betting
                && racingTransition.Count == 2
                && ReadPacketType(racingTransition[0])
                    == (ushort)NotiPacketTypeA21.JOUST_STATE
                && ReadPacketType(racingTransition[1])
                    == (ushort)NotiPacketTypeA21.JOUST_MATCH_RESULT
                && racingTransition[0][17] == (byte)JoustPhase.Racing
                && racingTransition[1][17] == 1
                && reviewTransition.Count == 0,
                ref failures);
            Check(
                "closed info and betting ACK bodies match observed compact acks",
                JoustPacketBuilder.BuildJoustInfoClosedAck()
                    .SequenceEqual(new byte[] { 1, 6, 0, 0, 0 })
                && JoustPacketBuilder.BuildJoustBettingAck(true)
                    .SequenceEqual(new byte[] { 1, 0, 0, 0, 0 })
                && JoustPacketBuilder.BuildJoustBettingAck(false)
                    .SequenceEqual(new byte[] { 1, 6, 0, 0, 0 }),
                ref failures);
        }

        private static void VerifyBettingParser(ref int failures)
        {
            var bodyAfter14ByteHeader = new byte[]
            {
                0x0C, 0xBE, 0x2B, 0x56, 0x18, 0xF7, 0x1A, 0x00,
                0x24, 0xF7, 0x1A, 0x00, 0x4E, 0x43, 0x02, 0x8C,
                0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };

            var parsed = JoustBettingRequestParser.TryParse(
                bodyAfter14ByteHeader,
                out var command);
            Check(
                "betting parser reads captured A21 body after 14-byte receive header",
                parsed
                && command.HorseId == 2
                && command.MaterialSlotIndex == 0x008C
                && command.Amount == 8,
                ref failures);

            var bodyAfter15ByteHeader = bodyAfter14ByteHeader.Skip(1).ToArray();
            parsed = JoustBettingRequestParser.TryParse(
                bodyAfter15ByteHeader,
                out command);
            Check(
                "betting parser keeps the one-byte fallback body alignment",
                parsed
                && command.HorseId == 2
                && command.MaterialSlotIndex == 0x008C
                && command.Amount == 8,
                ref failures);
        }

        private static void VerifyEventInfoBody(ref int failures)
        {
            var body = EventInfoBodyBuilder.Build(new GameEventInfoSnapshot
            {
                Events = new[]
                {
                    new GameEventInfoEntry
                    {
                        EventId = JoustConfig.EventId,
                        Unknown0 = 0,
                        StartNotice = "start",
                        EndNotice = "end",
                        HasDetail = true,
                        FlagA = 0,
                        FlagB = 5,
                        Title = "joust",
                        ShortName = "joust",
                        ReservedOrIcon = string.Empty,
                        StartUnixTime = 0,
                        EndUnixTime = 2147483647,
                        LinkKey = string.Empty,
                        Description = "desc",
                        DetailEnabled = true,
                    },
                },
                ExtraEntries = Array.Empty<GameEventExtraInfoEntry>(),
            });

            Check(
                "EVENT_INFO body starts with count and joust event id",
                BitConverter.ToUInt16(body, 0) == 1
                && BitConverter.ToUInt16(body, 2) == JoustConfig.EventId
                && body[body.Length - 1] == 0,
                ref failures);
        }

        private static void VerifyV9ToV10Migration(
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
DROP TABLE IF EXISTS event_joust_history;
DROP TABLE IF EXISTS event_joust_match_results;
DROP TABLE IF EXISTS event_joust_results;
DROP TABLE IF EXISTS event_joust_character_bets;
DROP TABLE IF EXISTS event_joust_knight_stats;
DROP TABLE IF EXISTS event_joust_round_slots;
DROP TABLE IF EXISTS event_joust_rules;
DROP TABLE IF EXISTS game_event_info_extra;
DROP TABLE IF EXISTS game_event_info_details;
DROP TABLE IF EXISTS game_event_state;
UPDATE schema_metadata SET schema_version=9 WHERE singleton_id=1;
PRAGMA user_version=9;";
                    command.ExecuteNonQuery();
                }

                SqliteMigrations.Apply(connection);
                Check(
                    "schema v9 migrates continuously to v10 event tables",
                    SqliteMigrations.ReadVersion(connection) == SqliteMigrations.CurrentVersion
                    && TableExists(connection, "game_event_state")
                    && TableExists(connection, "event_joust_character_bets")
                    && TableExists(connection, "event_joust_history"),
                    ref failures);
            }
        }

        private static void VerifyEventInfoSeed(
            string databasePath,
            ref int failures)
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT OR IGNORE INTO game_event_state(event_id, state)
VALUES(@eventId, 1);
INSERT INTO game_event_info_details (
    event_id, unknown0, start_notice, end_notice, detail_flag,
    flag_a, flag_b, title, short_name, reserved_or_icon,
    start_unix_time, end_unix_time, link_key, description,
    detail_enabled, sort_order
) VALUES (
    @eventId, 0, 'bad start', 'bad end', 1,
    0, 5, '[赛季]坏文本□□', '[赛季]坏文本□□', '',
    0, 2147483647, '', 'bad desc',
    1, 10
);
INSERT INTO event_joust_rules (
    event_id, rounds_per_day, round_interval_minutes,
    betting_duration_minutes, stop_betting_minutes,
    result_stage_interval_seconds
) VALUES (
    @eventId, 5, 120, 60, 10, 60
);";
                    command.Parameters.AddWithValue("@eventId", JoustConfig.EventId);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            var repository = new JoustRepository(database);
            repository.EnsureStaticConfigRows(JoustConfig.CreateFallback());

            var snapshot = new GameEventRepository(database).LoadEventInfoSnapshot();
            var entry = snapshot.Events.FirstOrDefault(
                candidate => candidate.EventId == JoustConfig.EventId);
            var start = entry != null
                ? DateTimeOffset.FromUnixTimeSeconds(entry.StartUnixTime).ToOffset(BeijingOffset)
                : DateTimeOffset.MinValue;
            var end = entry != null
                ? DateTimeOffset.FromUnixTimeSeconds(entry.EndUnixTime).ToOffset(BeijingOffset)
                : DateTimeOffset.MinValue;

            Check(
                "joust EVENT_INFO seed overwrites stale garbled text and calendar dates",
                entry != null
                && entry.Title == "骑士马战大竞猜"
                && entry.ShortName == "骑士马战大竞猜"
                && entry.StartNotice.Contains("正在进行[骑士马战大竞猜]")
                && entry.EndNotice.Contains("[骑士马战大竞猜]活动已结束")
                && entry.Description.Contains("活动时间每天10：00开始，共7期")
                && !entry.Description.Contains("每晚20：00一期")
                && entry.Description.IndexOf('\r') < 0
                && entry.Description.IndexOf('\n') < 0
                && start.Month == 1
                && start.Day == 1
                && end.Month == 12
                && end.Day == 31,
                ref failures);

            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var rule = repository.LoadRule(connection, transaction);
                Check(
                    "joust rule seed overwrites existing rows with production cadence",
                    rule != null
                    && rule.RoundsPerDay == 7
                    && rule.RoundIntervalMinutes == 120
                    && rule.BettingDurationMinutes == 90
                    && rule.StopBettingMinutes == 10
                    && rule.ResultStageIntervalSeconds == 200,
                    ref failures);
            }

            var body = EventInfoBodyBuilder.Build(snapshot);
            var titleBytes = ClientTextEncoding.GetBytes("骑士马战大竞猜");
            var descriptionBytes =
                ClientTextEncoding.GetBytes("活动时间每天10：00开始，共7期");
            Check(
                "joust EVENT_INFO body contains GBK title bytes",
                Contains(body, titleBytes),
                ref failures);
            Check(
                "joust EVENT_INFO body contains GBK description bytes",
                Contains(body, descriptionBytes),
                ref failures);
        }

        private static void VerifyServiceRoundResolution(
            string databasePath,
            ref int failures)
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);

            var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
            DateTimeOffset now = Local(10, 0);
            var calls = new List<int>();
            var service = new JoustService(
                database,
                new MailboxService(new MailboxRepository(database)),
                nowProvider: () => now,
                next: maxValue =>
                {
                    calls.Add(maxValue);
                    return 0;
                });
            service.Initialize();

            Check(
                "betting snapshot creates the current round without resolving matches",
                service.TryGetSnapshot(CharacterId, out var betting)
                && betting.Phase == JoustPhase.Betting
                && betting.Slots.Count == 8
                && CountRows(database, "event_joust_match_results") == 0,
                ref failures);

            calls.Clear();
            now = Local(11, 50);
            Check(
                "minute 110 result review resolves all seven matches using 50-percent draws",
                service.TryGetSnapshot(CharacterId, out var result)
                && result.Phase == JoustPhase.ResultReview
                && result.CurrentResultStageIndex == 2
                && CountRows(database, "event_joust_match_results") == 7
                && CountRows(database, "event_joust_history") == 1
                && calls.Count(value => value == 2) == 7,
                ref failures);
        }

        private static JoustPhase PhaseAt(JoustRule rule, int hour, int minute)
        {
            return ScheduleAt(rule, hour, minute).Phase;
        }

        private static JoustScheduleSnapshot ScheduleAt(
            JoustRule rule,
            int hour,
            int minute)
        {
            return JoustService.CalculateSchedule(rule, eventEnabled: true, Local(hour, minute));
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
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static ushort ReadPacketType(byte[] packet)
        {
            return BitConverter.ToUInt16(packet, 1);
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0)
                return false;
            for (var index = 0; index <= haystack.Length - needle.Length; index++)
            {
                var matched = true;
                for (var offset = 0; offset < needle.Length; offset++)
                {
                    if (haystack[index + offset] == needle[offset])
                        continue;
                    matched = false;
                    break;
                }
                if (matched)
                    return true;
            }

            return false;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}
