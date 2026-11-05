using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Pvp;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Pvp;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Pvp;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static partial class A21PvpRoomProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_PVP_ROOM_PROTOCOL selftest ===");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var directory = Path.Combine(Path.GetTempPath(), "a21_pvp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                GameNetworkConfig.ConfigureChannelCatalog(ChannelProtocolHandler.ParseScriptChannels(
                    File.ReadAllText(ServerPaths.ChannelInfoFilePath)));
                CheckSchema(Path.Combine(directory, "migration.db"));
                var database = new GameDatabase(Path.Combine(directory, "inventory.db"), ServerPaths.SchemaFilePath);
                Seed(database);
                CheckProgress(database);
                CheckRoomWireLayout();
                CheckUserInfoAddition();
                CheckSkillPointsAsync(database).GetAwaiter().GetResult();
                CheckRoomsAsync(database).GetAwaiter().GetResult();
                CheckInvitationsAsync(database).GetAwaiter().GetResult();
                CheckTeamDispatchAsync(database).GetAwaiter().GetResult();
                CheckDisconnectGuardrailsAsync(database).GetAwaiter().GetResult();
                var settlementDatabase = new GameDatabase(Path.Combine(directory, "settlement.db"), ServerPaths.SchemaFilePath);
                Seed(settlementDatabase);
                CheckSettlements(settlementDatabase);
                CheckTraditionalAsync(settlementDatabase).GetAwaiter().GetResult();
                CheckTeamWaitingRoomAsync(settlementDatabase).GetAwaiter().GetResult();
                Console.WriteLine("A21_PVP_ROOM_PROTOCOL: PASS");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"A21_PVP_ROOM_PROTOCOL: FAIL {ex}");
                return 1;
            }
            finally
            {
                GameNetworkConfig.ConfigureChannelCatalog(null);
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }

        private static void CheckSchema(string path)
        {
            var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
            Require(Scalar(database, "PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                && Scalar(database, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('character_pvp_records','account_pvp_total_match_teams');") == 2,
                "fresh schema includes PvP progress and account team ownership");
            Execute(database, @"
DROP TABLE account_pvp_total_match_teams;
DROP TABLE character_pvp_records;
DROP TABLE pvp_match_results;
DROP TABLE pvp_matches;
UPDATE schema_metadata SET schema_version=26;
PRAGMA user_version=26;
CREATE TRIGGER reject_pvp_migration BEFORE UPDATE OF schema_version ON schema_metadata
BEGIN SELECT RAISE(ABORT, 'selftest migration rollback'); END;");
            var rejected = false;
            using (var connection = database.OpenConnection())
            {
                try { SqliteMigrations.Apply(connection); }
                catch (SqliteException) { rejected = true; }
            }
            Require(rejected && Scalar(database, "PRAGMA user_version;") == 26
                && Scalar(database, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='character_pvp_records';") == 0,
                "failed v27 migration rolls back tables and version together");
            Execute(database, "DROP TRIGGER reject_pvp_migration;");
            using (var connection = database.OpenConnection())
            {
                SqliteMigrations.Apply(connection);
                SqliteMigrations.Apply(connection);
            }
            Require(Scalar(database, "PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                && Scalar(database, "SELECT schema_version FROM schema_metadata;") == DfoServer.Sqlite.SqliteMigrations.CurrentVersion,
                "v26 upgrades to current schema and repeated migration is idempotent");
            Execute(database, @"
DROP TABLE pvp_match_results;
DROP TABLE pvp_matches;
UPDATE schema_metadata SET schema_version=27;
PRAGMA user_version=27;
CREATE TRIGGER reject_pvp_migration BEFORE UPDATE OF schema_version ON schema_metadata
BEGIN SELECT RAISE(ABORT, 'selftest v28 rollback'); END;");
            rejected = false;
            using (var connection = database.OpenConnection())
            {
                try { SqliteMigrations.Apply(connection); }
                catch (SqliteException) { rejected = true; }
            }
            Require(rejected && Scalar(database, "PRAGMA user_version;") == 27
                && Scalar(database, "SELECT COUNT(*) FROM sqlite_master WHERE name='pvp_matches';") == 0,
                "v28 failure rolls back match tables and version together");
            Execute(database, "DROP TRIGGER reject_pvp_migration;");
            using (var connection = database.OpenConnection()) SqliteMigrations.Apply(connection);
            Require(Scalar(database, "PRAGMA user_version;") == SqliteMigrations.CurrentVersion,
                "v27 can retry to current schema after migration rollback");
        }

        private static void Seed(IGameDatabase database)
        {
            Execute(database, @"
INSERT INTO accounts(account_id,m_id,password_hash) VALUES
 (5001,'pvp-one',''),(5002,'pvp-two',''),(5003,'pvp-three','');
INSERT INTO characters(character_id,account_id,slot_index,name,job,grow_type,level,pvp_grade,pvp_rating_grade,town_id) VALUES
 (5001,5001,0,'pvp-one',0,33,86,1,0,1),
 (5002,5001,1,'pvp-mage',3,33,86,10,0,1),
 (5003,5001,2,'pvp-priest',4,33,86,20,7,1),
 (5004,5001,3,'pvp-same-job',0,17,86,0,0,1),
 (6001,5002,0,'pvp-two',1,33,86,10,0,1),
 (6002,5002,1,'pvp-gunner',2,33,86,20,0,1),
 (6003,5002,2,'pvp-thief',6,33,86,20,0,1),
 (7001,5003,0,'pvp-three',0,33,86,1,0,1),
 (7002,5003,1,'pvp-third-mage',3,33,86,1,0,1),
 (7003,5003,2,'pvp-third-priest',4,33,86,1,0,1);
INSERT INTO character_pvp_records(character_id,experience_in_grade,win_count,loss_count,rank_point,
 peak_rank_point,rank_warmup_games,total_match_point,total_match_warmup_games) VALUES
 (5001,3500,12,7,1300,1450,19,1580,20),
 (6001,4500,30,12,1700,1750,20,1400,7);
");
        }

        private static void CheckProgress(IGameDatabase database)
        {
            var repository = new SqlitePvpRecordRepository(database);
            var record = repository.Load(5001);
            var body = PvpRecordBodyBuilder.BuildBody(record);
            // Independent field walk from A21 1165CB0 / setters 1839E50, 183A450.
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream);
            Require(reader.ReadInt32() == 12 && reader.ReadInt32() == 7
                && reader.ReadInt32() == 15500 && reader.ReadInt32() == 12000
                && reader.ReadInt32() == 27000 && reader.ReadByte() == 1
                && reader.ReadByte() == 0 && reader.ReadInt32() == -1,
                "PVP_RECORD preserves grade and positive experience denominator");
            Require(reader.ReadInt32() == 1300 && reader.ReadInt32() == 1450
                && reader.ReadInt32() == 19 && reader.ReadByte() == 0
                && reader.ReadInt32() == 0 && reader.ReadInt32() == 1580
                && reader.ReadInt32() == 20 && stream.Position == 51 && body.Length == 51,
                "PVP_RECORD carries independent RP/GP values and certification counts in 51 bytes");
            var intermediate = repository.Load(6001);
            Require(intermediate.ExperienceFloor == 709000 && intermediate.ExperienceCeiling == 949000
                && intermediate.Experience == 713500,
                "intermediate grade uses current PVF thresholds and persisted within-grade progress");
            var empty = repository.Load(5003);
            Require(empty.Grade == 20 && empty.RatingGrade == 7
                && empty.Experience == empty.ExperienceFloor && empty.RankWarmupGames == 0
                && empty.TotalMatchWarmupGames == 0 && empty.RankPoint == 0 && empty.TotalMatchPoint == 0,
                "high rating without records does not fabricate certification or points");
            Require(repository.Load(5004).ExperienceCeiling == 12000
                && repository.Load(6003).Grade == PvpExperienceRules.Current.MaxGrade
                && repository.Load(6003).ExperienceCeiling > repository.Load(6003).ExperienceFloor,
                "unranked and MAX-grade projections both have valid experience intervals");
            var sequence = NewCharacterInitSequence.Build();
            var index = sequence.FindIndex(packet => packet.Type == (ushort)NotiPacketTypeA21.PVP_RECORD);
            Require(index > 1 && sequence[index - 1].Type == (ushort)NotiPacketTypeA21.HOTKEY_OPTION
                && sequence[index - 2].Type == (ushort)NotiPacketTypeA21.USERINFO
                && sequence[index - 2].OccurrenceIndex == 3,
                "authoritative PvP record follows both USERINFO0 publications and HOTKEY");
        }

        private static FreeDuelRoom TerminalMatch(byte mode = 1, bool draw = false, bool active = false)
        {
            var states = Enumerable.Repeat(FreeDuelRoom.EmptySeatState, 8).ToArray();
            states[0] = 0; states[1] = 1;
            var sessions = new Guid[8]; sessions[0] = Guid.NewGuid(); sessions[1] = Guid.NewGuid();
            var ids = new int[8]; ids[0] = 5001; ids[1] = 6001;
            var uids = new ushort[8]; uids[0] = 101; uids[1] = 102;
            var room = new FreeDuelRoom(1, 10050, 5001, sessions[0], 101, 0,
                Array.Empty<byte>(), 1, false, Array.Empty<byte>(), mode,
                seatStates: states, seatSessionIds: sessions, seatCharacterIds: ids, seatUserIds: uids)
                .CreateStartedSnapshot(1);
            if (active) return room;
            if (draw)
                return new FreeDuelRoom(1, 10050, 5001, sessions[0], 101, 0,
                    Array.Empty<byte>(), 1, false, Array.Empty<byte>(), mode,
                    seatStates: states, seatSessionIds: sessions, seatCharacterIds: ids, seatUserIds: uids,
                    roomState: FreeDuelRoom.StartedRoomState, settlementPhase: FreeDuelRoom.AwaitingRankSettlementPhase,
                    matchGeneration: 1, matchParticipants: room.MatchParticipants.ToArray());
            Require(room.TryCreateDeathSnapshot(1, 0, out var terminal, out var complete) && complete,
                "terminal fact freezes both participants and killer attribution");
            Require(!terminal.TryCreateDeathSnapshot(1, 0, out _, out _), "duplicate death is rejected");
            return terminal;
        }

        private static void CheckSettlements(IGameDatabase database)
        {
            var repository = new SqlitePvpRecordRepository(database);
            var rules = PvpExperienceRules.Current;
            Require(rules.GetBounds(0) == (0, 12000) && rules.GetGrade(11999) == 0
                && rules.GetGrade(12000) == 1 && rules.MaxGrade == 20
                && rules.GetGrade(rules.MaximumExperience) == 20,
                "native resource grade indices include zero, exact thresholds and MAX 20");
            Execute(database, "UPDATE characters SET pvp_grade=0 WHERE character_id IN (5001,6001); UPDATE character_pvp_records SET experience_in_grade=0 WHERE character_id IN (5001,6001);");
            var now = new DateTime(2026, 9, 15, 4, 0, 0, DateTimeKind.Utc);
            var room = TerminalMatch();
            var before = repository.Load(5001);
            var practice = repository.Settle(room, false, now);
            Require(!practice.NewlyCommitted && repository.Load(5001).Wins == before.Wins
                && Scalar(database, "SELECT COUNT(*) FROM pvp_matches;") == 0,
                "practice preserves progress and creates no durable match");
            Execute(database, @"CREATE TRIGGER reject_pvp_result BEFORE INSERT ON pvp_match_results
WHEN NEW.character_id=6001 BEGIN SELECT RAISE(ABORT, 'second participant failure'); END;");
            var failed = false;
            try { repository.Settle(room, true, now); } catch (SqliteException) { failed = true; }
            Require(failed && repository.Load(5001).Experience == before.Experience
                && repository.Load(5001).Wins == before.Wins
                && Scalar(database, "SELECT COUNT(*) FROM pvp_matches;") == 0,
                "second-participant failure rolls back winner grade, experience, counters and match journal");
            Execute(database, "DROP TRIGGER reject_pvp_result;");
            var result = repository.Settle(room, true, now);
            Require(result.NewlyCommitted && result.Players[5001].Record.Grade == 1
                && result.Players[5001].Record.Experience == 12000 && result.Players[5001].ExperienceChange == 12000
                && result.Players[6001].Record.Experience == 0,
                "retry commits beginner first-win promotion from zero and preserves beginner loss at zero");
            var repeated = new SqlitePvpRecordRepository(database).Settle(room, true, now);
            Require(!repeated.NewlyCommitted && repeated.Players[5001].ExperienceChange == 12000
                && repository.Load(5001).Wins == before.Wins + 1
                && Scalar(database, "SELECT COUNT(*) FROM pvp_match_results;") == 2,
                "repository reload and duplicate settlement preserve exactly one durable result per participant");
            var score = repository.LoadSeasonScore(5001, 3);
            Require(score.Individual.Wins == 1 && score.RecentWins == 1 && score.WinStreak == 1
                && score.Jobs.Single().Score.Wins == 1 && repository.LoadSeasonScore(5001, 2).Individual.Wins == 0,
                "personal score derives current-season mode, recent results and opponent statistics from committed matches");
            repository.Settle(TerminalMatch(2), true, now);
            repository.Settle(TerminalMatch(3), true, now);
            repository.Settle(TerminalMatch(draw: true), true, now);
            score = repository.LoadSeasonScore(5001, 3);
            Require(score.Team.Wins == 1 && score.Relay.Wins == 1 && score.Individual.Draws == 1
                && score.RecentDraws == 1 && score.WinStreak == 0 && score.PeakWinStreak == 3,
                "team, relay and draw results remain distinct and reset the current streak");
            for (var i = 3; i < 50; i++) repository.Settle(TerminalMatch(), true, now);
            var capped = repository.Load(5001).Experience;
            var cappedLoss = repository.Load(6001).Experience;
            repository.Settle(TerminalMatch(), true, now);
            Require(repository.Load(5001).Experience == capped && repository.Load(6001).Experience == cappedLoss,
                "daily winning and losing eligibility caps prevent extra experience");
            Require(repository.Settle(TerminalMatch(), true, now.AddDays(1)).Players[5001].ExperienceChange == 1000,
                "next game day restores experience eligibility without resetting cumulative history");
            Require(repository.Load(5001).RankPoint == before.RankPoint
                && repository.Load(5001).RankWarmupGames == before.RankWarmupGames,
                "traditional results never fabricate ranked points or certification matches");
            Execute(database, "UPDATE characters SET pvp_grade=10 WHERE character_id=6001; UPDATE character_pvp_records SET experience_in_grade=20000 WHERE character_id=6001;");
            for (var i = 0; i < 10; i++)
                Require(repository.Settle(TerminalMatch(), true, now.AddDays(2)).Players[6001].ExperienceChange == -756,
                    "eligible loss deducts the actual grade-ten resource amount");
            Require(repository.Settle(TerminalMatch(), true, now.AddDays(2)).Players[6001].ExperienceChange == 0,
                "eleventh loss preserves experience even when well above zero");
            Execute(database, "UPDATE characters SET pvp_grade=20 WHERE character_id=5001; UPDATE character_pvp_records SET experience_in_grade=0 WHERE character_id=5001;");
            Require(repository.Settle(TerminalMatch(), true, now.AddDays(3)).Players[5001].ExperienceChange == 0
                && repository.Load(5001).Grade == 20 && repository.Load(5001).ExperienceCeiling > repository.Load(5001).Experience,
                "MAX keeps a valid bar interval and never advances to the sentinel grade");
            var departed = TerminalMatch(active: true).WithoutMember(1);
            Require(departed.TryCreateDisconnectSettlementSnapshot(out var disconnect)
                && disconnect.MatchParticipants.Count == 2 && disconnect.GetDeathCount(1) == 1
                && disconnect.GetKillCount(0) == 0,
                "disconnect keeps the starting roster and death without inventing a killer");
            var loserWins = repository.Load(6001).Losses;
            repository.Settle(disconnect, true, now.AddDays(4));
            Require(repository.Load(6001).Losses == loserWins + 1
                && repository.Settle(disconnect, true, now.AddDays(4)).NewlyCommitted == false,
                "departed character receives exactly one persisted loss after its waiting seat is cleared");
            var deadDeparted = TerminalMatch().WithoutMember(1);
            Require(deadDeparted.GetDeathCount(1) == 1 && deadDeparted.GetKillCount(0) == 1,
                "leaving after death preserves the already recorded combat counters");
            Require(TerminalMatch(active: true).TryRemoveOwnerAndPromote(out var promoted, out var vacated)
                && vacated == 0 && promoted.MatchParticipants.Count == 2 && promoted.GetDeathCount(0) == 1,
                "manager disconnect preserves match attribution while promoting the remaining room owner");
        }

        private static async Task CheckTraditionalAsync(IGameDatabase database)
        {
            Execute(database, @"DELETE FROM pvp_match_results; DELETE FROM pvp_matches;
UPDATE characters SET pvp_grade=0 WHERE character_id IN (5001,6001);
UPDATE character_pvp_records SET experience_in_grade=1000 WHERE character_id=5001;
UPDATE character_pvp_records SET experience_in_grade=0 WHERE character_id=6001;");
            var sessions = new SessionDirectory();
            var characters = new SqliteCharacterRepository(database);
            var source = new SqliteSelectCharacterDataSource(database, characters);
            var town = new TownHandler(characters, source, null, sessions, null, null, null, database);
            var rooms = new FreeDuelRoomRegistry();
            using var pvp = new PvpRoomHandler(sessions, town.BuildFullUserInfoPacket,
                new CharacterTransitionCoordinator(sessions), () => true, rooms,
                town.AnnouncePvpTownArrivalWithinTransitionAsync, database: database,
                settlementAckTimeout: TimeSpan.FromSeconds(30));
            using var owner = await Peer.CreateAsync(database, sessions, 5001, 5001, 0x4001, 10050);
            using var member = await Peer.CreateAsync(database, sessions, 6001, 5002, 0x4002, 10050);
            await pvp.HandleLobbyReadyAsync(owner.Session);
            await pvp.HandleLobbyReadyAsync(member.Session);
            await pvp.HandleMakeRoom(owner.Session, Header(CmdPacketTypeA21.MAKE_PVP_ROOM), MakeRequest(false, Array.Empty<byte>()));
            var room = rooms.SnapshotForListener(10050).Single();
            await pvp.HandleEnterRoom(member.Session, Header(CmdPacketTypeA21.ENTER_PVP_ROOM), EnterRequest(room.RoomId));
            await pvp.HandleSetReadyState(member.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
            await pvp.HandleSetReadyState(owner.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
            await pvp.HandleDiePvpCharacter(member.Session, Header(CmdPacketTypeA21.DIE_PVP_CHARACTER), new byte[] { 2, 0x40, 1, 0x40, 0 });
            owner.Drain(); member.Drain();
            var before = new SqlitePvpRecordRepository(database).Load(5001);
            await pvp.HandlePvpRankResponse(owner.Session, Header(CmdPacketTypeA21.RES_PVP_RANK), new byte[70]);
            Execute(database, @"CREATE TRIGGER reject_pvp_result BEFORE INSERT ON pvp_match_results
BEGIN SELECT RAISE(ABORT, 'handler commit failure'); END;");
            var failed = false;
            try { await pvp.HandlePvpRankResponse(member.Session, Header(CmdPacketTypeA21.RES_PVP_RANK), new byte[70]); }
            catch (SqliteException) { failed = true; }
            Require(failed && rooms.SnapshotForListener(10050).Single().SettlementPhase == FreeDuelRoom.AwaitingRankSettlementPhase
                && owner.Drain().Count == 0 && member.Drain().Count == 0,
                "failed handler commit leaves rank phase retryable and publishes no successful result");
            Execute(database, "DROP TRIGGER reject_pvp_result;");
            await pvp.HandlePvpRankResponse(member.Session, Header(CmdPacketTypeA21.RES_PVP_RANK), new byte[70]);
            var packets = owner.Drain(); member.Drain();
            var end = FindBody(packets, NotiPacketTypeA21.END_PVP);
            var record = FindBody(packets, NotiPacketTypeA21.PVP_RECORD);
            var after = new SqlitePvpRecordRepository(database).Load(5001);
            Require(after.Wins == before.Wins + 1 && after.Grade == 1 && after.Experience - before.Experience == 11000
                && BitConverter.ToInt32(end, 13) == 1
                && BitConverter.ToInt32(end, 6) == after.Experience - before.Experience
                && BitConverter.ToInt32(record, 0) == after.Wins && BitConverter.ToInt32(record, 8) == after.Experience
                && owner.Session.Player.PvpGrade == after.Grade,
                "traditional handler publishes actual kills, committed experience and refreshed cumulative record");
            await pvp.HandlePvpRankResponse(member.Session, Header(CmdPacketTypeA21.RES_PVP_RANK), new byte[70]);
            Require(new SqlitePvpRecordRepository(database).Load(5001).Wins == after.Wins,
                "duplicate network rank response cannot award the match twice");
            Require(!rooms.TryForceRankSettlement(room.RoomId, Guid.NewGuid(), 1, out _,
                _ => throw new InvalidOperationException("old generation reached settlement")),
                "old room generation cannot enter the durable settlement callback");
        }

        private static void CheckRoomWireLayout()
        {
            var registry = new FreeDuelRoomRegistry();
            var owner = Guid.NewGuid();
            Require(MakePvpRoomRequest.TryParse(MakeRequest(true, Array.Empty<byte>()), out var request, out _)
                && registry.TryCreate(10068, 1, owner, 101, request, out _, out _),
                "native empty password shape creates a room");
            var room = registry.SnapshotForListener(10068).Single();
            Require(!room.HasPassword, "zero-length password never marks or locks the room");
            CheckRoomBody(PvpRoomNotificationBuilder.BuildRoomInfoBody(new[] { room }), room.RoomId,
                new ushort[] { 101 }, false);
            Require(MakePvpRoomRequest.TryParse(MakeRequest(true, Encoding.ASCII.GetBytes("secret")), out request, out _)
                && registry.TryCreate(10068, 2, Guid.NewGuid(), 102, request, out _, out _),
                "nonempty password creates a protected room");
            var locked = registry.SnapshotForListener(10068).Last();
            CheckRoomBody(PvpRoomNotificationBuilder.BuildRoomInfoBody(new[] { locked }), locked.RoomId,
                new ushort[] { 102 }, true);
            EnterPvpRoomRequest.TryParse(EnterRequest(locked.RoomId), out var join, out _);
            Require(!registry.TryJoin(10068, 3, Guid.NewGuid(), 103, join, out _, out _, out _),
                "protected room refuses an empty password");
            var maps = PvpMapRules.Current;
            Require(!maps.CanSelect(-1) && !maps.CanSelect(6) && !maps.CanSelect(5000)
                && Enumerable.Range(0, 6).All(i => maps.CanSelect((short)i)),
                "map selection uses current PVF order indices, excluding unavailable entries");
            for (var i = 0; i < 30; i++)
            {
                var selected = maps.SelectStartMap(0);
                if (selected < 1 || selected > 5)
                    throw new InvalidOperationException("random PvP map left current PVF order");
            }
        }

        private static void CheckUserInfoAddition()
        {
            var skills = new SkillInfoSnapshot();
            for (var page = 0; page < 2; page++)
            {
                var value = new SkillInfoPageSnapshot();
                value.Entries.Add(new SkillInfoEntrySnapshot { SkillId = 5, Level = 4 });
                value.Entries.Add(new SkillInfoEntrySnapshot { SkillId = 16, Level = 2 });
                skills.Pages.Add(value);
            }
            var addition = new UserInfoAdditionSnapshot { SkillTreeIndex = 1, ManageLevel = 7, ManagePoint = -123 };
            addition.SpecialRewardQuestIds.Add(0x34BE);
            var decoded = ReadAddition(UserInfoSubtype1Builder.BuildFromSnapshot(addition, skills));
            Require(decoded.Pages[0].SequenceEqual(new[] { ((ushort)5, (byte)4), ((ushort)16, (byte)2) })
                && decoded.Pages[1].SequenceEqual(decoded.Pages[0]) && decoded.SkillTree == 1,
                "remote USERINFO1 restores both identical skill pages instead of treating page 1 as a copy marker");
            Require(decoded.DimensionCount == 26 && decoded.QuestIds.SequenceEqual(new uint[] { 0x34BE })
                && decoded.ManageLevel == 7 && decoded.ManagePoint == -123,
                "native USERINFO1 walk reaches dimensions, quest IDs and signed group points without padding");
            while (skills.Pages[0].Entries.Count <= 255)
                skills.Pages[0].Entries.Add(new SkillInfoEntrySnapshot { SkillId = 5, Level = 1 });
            var rejected = false;
            try { UserInfoSubtype1Builder.BuildFromSnapshot(addition, skills); }
            catch (InvalidDataException ex) { rejected = ex.Message.Contains("count", StringComparison.Ordinal); }
            Require(rejected, "USERINFO1 rejects an overflowing skill count instead of corrupting the following fields");
        }

        private static async Task CheckSkillPointsAsync(IGameDatabase database)
        {
            Execute(database, "UPDATE characters SET bonus_sp=37, bonus_tp=3 WHERE character_id=5001;");
            var characters = new SqliteCharacterRepository(database);
            var character = characters.GetById(5001);
            var repository = new SqlitePvpSkillRepository(database);
            var normal = new SqliteCharacterProgressRepository(database);
            var initial = SkillStateService.LoadPvpAndSync(repository, character, character.Level);
            var before = ReadSkillPoints(SkillInfoBodyBuilder.BuildFrom(initial.Skills));
            Require(before[0] == initial.Points.RemainingSp && before[1] == initial.Points.RemainingSpPage1
                && before[2] == initial.Points.RemainingTp && before[3] == initial.Points.RemainingTpPage1
                && before.All(value => value < ushort.MaxValue)
                && initial.Points.TotalSp == SpTableProvider.GetTotalSp(character.Level) + 37
                && initial.Points.TotalTp == TpTableProvider.GetTotalTp(character.Level) + 3,
                "PvP SKILLINFO carries finite ledger balances including the character's earned and bonus points");

            BuySkillResult Buy(params BuySkillEntry[] entries) => BuySkillService.ExecutePvp(
                repository, 5001, 5001, 0, 0, entries, character.BonusSp, character.Level,
                character.BonusTp, character.GrowType);
            var spPurchase = Buy(new BuySkillEntry { SkillIndex = 5, Level = 1 });
            Require(spPurchase.Success && spPurchase.RemainSp == before[0] - 15
                && spPurchase.RemainTp == before[2], "PvP buying Ghost Slash deducts the actual PVF cost of 15 SP");
            var tpPurchase = Buy(new BuySkillEntry { SkillIndex = 161, Level = 2 });
            Require(tpPurchase.Success && tpPurchase.RemainTp == before[2] - 2
                && tpPurchase.RemainSp == spPurchase.RemainSp, "PvP buying Basic Attack Mastery EX deducts two TP independently");
            var reloaded = SkillStateService.LoadPvpAndSync(new SqlitePvpSkillRepository(database), character, character.Level);
            var after = ReadSkillPoints(SkillInfoBodyBuilder.BuildFrom(reloaded.Skills));
            Require(after.SequenceEqual(new ushort[] { spPurchase.RemainSp, before[1], tpPurchase.RemainTp, before[3] }),
                "reloading PvP skills preserves both purchases and the untouched second-page balances");

            // Keep a deliberately different normal tree to expose accidental PvE refreshes.
            normal.SaveSkillProgress(5001, initial.Skills);
            var sessions = new SessionDirectory();
            using var peer = await Peer.CreateAsync(database, sessions, 5001, 5001, 0x4101, 10068);
            var capsule = new GrowthCapsuleSyncService(characters, database);
            await capsule.SendExpProgressAsync(peer.Session, "pvp-points-selftest");
            var exp = FindBody(peer.Drain(), NotiPacketTypeA21.EXP);
            Require(Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt16(exp, 9 + i * 2)).SequenceEqual(after),
                "max-level capsule EXP refresh preserves PvP balances instead of overwriting them with normal skills");

            var source = new SqliteSelectCharacterDataSource(database, characters);
            var town = new TownHandler(characters, source, null, null, null, null, null, database);
            var roomInfo = town.BuildFullUserInfoPacket(peer.Session);
            var roomSkills = ReadAddition(roomInfo.AsSpan(35).ToArray()).Pages;
            Require(roomSkills[0].SequenceEqual(reloaded.Skills.Pages[0].Entries.Where(e => e.Level > 0).Select(e => (e.SkillId, e.Level)))
                && roomSkills[1].SequenceEqual(reloaded.Skills.Pages[1].Entries.Where(e => e.Level > 0).Select(e => (e.SkillId, e.Level))),
                "room USERINFO1 sends the independent PvP skills for both pages to the other client");

            using (var runtime = new ServerRuntimeBuilder(database))
            {
                var protocol = runtime.BuildGameProtocolHandler(sessions);
                await protocol.OnPacketReceived_86JP(peer.Session, Header(CmdPacketTypeA21.SKILL_INIT), Array.Empty<byte>());
                Require(ReadSkillPoints(FindBody(peer.Drain(), NotiPacketTypeA21.SKILLINFO)).SequenceEqual(after),
                    "real SKILL_INIT dispatch refreshes the same PvP ledger as selection and capsule EXP");
            }
            var failed = BuySkillService.ExecutePvp(repository, 5001, 5001, 0, 0,
                new[] { new BuySkillEntry { SkillIndex = 5, Level = 1 }, new BuySkillEntry { SkillIndex = 161, Level = 1 } },
                character.BonusSp, character.Level, -TpTableProvider.GetTotalTp(character.Level), character.GrowType);
            Require(!failed.Success && failed.ErrorCode == 2
                && ReadSkillPoints(SkillInfoBodyBuilder.BuildFrom(SkillStateService.LoadPvpAndSync(repository, character, character.Level).Skills)).SequenceEqual(after),
                "insufficient TP rejects the complete PvP purchase batch without persisting an earlier SP purchase");
            var refundSp = Buy(new BuySkillEntry { SkillIndex = 5, Level = 1, IsRefund = 1 });
            var refundTp = Buy(new BuySkillEntry { SkillIndex = 161, Level = 2, IsRefund = 1 });
            Require(refundSp.Success && refundTp.Success
                && ReadSkillPoints(SkillInfoBodyBuilder.BuildFrom(SkillStateService.LoadPvpAndSync(repository, character, character.Level).Skills)).SequenceEqual(before)
                && normal.LoadSkills(5001).Pages[0].Entries.All(entry => entry.SkillId != 161),
                "PvP refunds restore the ledger and leave the normal skill tree independent");
        }

        private static ushort[] ReadSkillPoints(byte[] body)
        {
            using var reader = new BinaryReader(new MemoryStream(body));
            var result = new ushort[4];
            for (var page = 0; page < 2; page++)
            {
                result[page] = reader.ReadUInt16();
                var count = reader.ReadByte();
                for (var i = 0; i < count; i++)
                {
                    Skip(reader, 4); // slot, skill ID, level
                    Skip(reader, reader.ReadByte());
                }
            }
            result[2] = reader.ReadUInt16();
            result[3] = reader.ReadUInt16();
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("SKILLINFO did not end after the second TP value.");
            return result;
        }

        private static (byte SkillTree, List<(ushort Id, byte Level)>[] Pages, int DimensionCount,
            uint[] QuestIds, byte ManageLevel, int ManagePoint) ReadAddition(byte[] body)
        {
            // A21 13E6370, 13E2540, 13E1D60, 13E1E40, 13E1F50, 13E1640.
            // These fixtures intentionally have no equipment; equipped entries
            // have separate template-dependent coverage in the inventory tests.
            using var reader = new BinaryReader(new MemoryStream(body));
            reader.ReadUInt32();
            if (reader.ReadInt32() != 88) throw new InvalidDataException("USERINFO1 stat block must be 88 bytes.");
            Skip(reader, 88);
            reader.ReadByte();
            if (reader.ReadByte() != 0) throw new InvalidDataException("This USERINFO1 fixture requires empty equipment.");
            Skip(reader, 12); // clone title and name tag
            var tree = reader.ReadByte();
            var pages = new List<(ushort Id, byte Level)>[2];
            for (var page = 0; page < 2; page++)
            {
                pages[page] = new List<(ushort, byte)>();
                var count = reader.ReadByte();
                for (var i = 0; i < count; i++) pages[page].Add((reader.ReadUInt16(), reader.ReadByte()));
            }
            reader.ReadByte(); // creature level
            var dimensions = reader.ReadByte();
            Skip(reader, dimensions * 6);
            Skip(reader, 3); // independent status bytes
            Skip(reader, reader.ReadByte() * 8); // account entry counts
            reader.ReadByte(); // character quest piece
            var quests = new uint[reader.ReadUInt32()];
            for (var i = 0; i < quests.Length; i++) quests[i] = reader.ReadUInt32();
            var level = reader.ReadByte();
            var points = reader.ReadInt32();
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("USERINFO1 has bytes after the last native field.");
            return (tree, pages, dimensions, quests, level, points);
        }

        private static ushort[] ReadBasicUserIds(byte[] body)
        {
            using var reader = new BinaryReader(new MemoryStream(body));
            if (reader.ReadByte() != 0) throw new InvalidDataException("Expected USERINFO subtype 0.");
            var ids = new ushort[reader.ReadUInt16()];
            for (var i = 0; i < ids.Length; i++)
            {
                Skip(reader, 38); // 13E7340 reads this before every UID, not before the list.
                ids[i] = reader.ReadUInt16();
                Skip(reader, reader.ReadInt32()); // name
                Skip(reader, 6); // job, grow, level, grades and state
                Skip(reader, reader.ReadByte() * 23); // roster appearance records
                Skip(reader, 22); // clone title through event-character flag
                reader.ReadUInt32(); // creature template
                Skip(reader, reader.ReadInt32());
                reader.ReadByte();
                Skip(reader, 64); // no-guild tail used by the fixture
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length || ids.Any(id => id == 0)
                || ids.Distinct().Count() != ids.Length)
                throw new InvalidDataException("Invalid A21 basic-user roster.");
            return ids;
        }

        private static void Skip(BinaryReader reader, int length)
        {
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Native field extends beyond its packet.");
            reader.BaseStream.Position += length;
        }

        private static void CheckIdentityDependencies(List<byte[]> packets, IEnumerable<ushort> initialIds)
        {
            var known = new HashSet<ushort>(initialIds);
            foreach (var packet in packets.Where(packet => packet[0] == 0))
            {
                var body = packet.AsSpan(15).ToArray();
                var type = (NotiPacketTypeA21)Type(packet);
                if (type == NotiPacketTypeA21.USERINFO && body[0] == 0)
                {
                    known.UnionWith(ReadBasicUserIds(body));
                }
                else if (type == NotiPacketTypeA21.USERINFO && body[0] == 1)
                {
                    if (!known.Contains(BitConverter.ToUInt16(body, 18)))
                        throw new InvalidDataException("USERINFO1 reached a client before its UID was registered.");
                    ReadAddition(body.AsSpan(20).ToArray());
                }
                else if (type == NotiPacketTypeA21.USER_UDP_IP_PORT)
                {
                    if (body.Length != 1 + body[0] * 22) throw new InvalidDataException("Invalid A21 UDP member size.");
                    for (var i = 0; i < body[0]; i++)
                        if (!known.Contains(BitConverter.ToUInt16(body, 1 + i * 22)))
                            throw new InvalidDataException("UDP endpoint reached a client before its UID was registered.");
                }
                else if (type == NotiPacketTypeA21.PVP_SEAT_STATE)
                {
                    if (body.Length != 4 + body[3] * 4) throw new InvalidDataException("Invalid A21 seat member size.");
                    for (var i = 0; i < body[3]; i++)
                        if (body[5 + i * 4] < 0xFE && !known.Contains(BitConverter.ToUInt16(body, 6 + i * 4)))
                            throw new InvalidDataException("Occupied seat reached a client before its UID was registered.");
                }
                else if (type == NotiPacketTypeA21.REQUEST_PEER && body[2] == 2
                    && !known.Contains(BitConverter.ToUInt16(body, 0)))
                    throw new InvalidDataException("PvP invitation reached a client before the inviter was registered.");
            }
            Require(true, "client consumes identities before full skills, UDP endpoints, seat changes and invitations");
        }

        private static async Task CheckRoomsAsync(IGameDatabase database)
        {
            var sessions = new SessionDirectory();
            var characters = new SqliteCharacterRepository(database);
            var source = new SqliteSelectCharacterDataSource(database, characters);
            var town = new TownHandler(characters, source, null, sessions, null, null, null, database);
            var rooms = new FreeDuelRoomRegistry();
            using var pvp = new PvpRoomHandler(sessions, town.BuildFullUserInfoPacket,
                new CharacterTransitionCoordinator(sessions), () => true, rooms,
                town.AnnouncePvpTownArrivalWithinTransitionAsync, database: database,
                settlementAckTimeout: TimeSpan.FromSeconds(30));
            using var owner = await Peer.CreateAsync(database, sessions, 5001, 5001, 0x4001, 10068);
            using var member = await Peer.CreateAsync(database, sessions, 6001, 5002, 0x4002, 10068);
            var snapshot = source.Load(5001, 5001);
            new UserInfoBodyBuilder().TryBuild(snapshot, 1, out var expectedFull);
            var full = town.BuildFullUserInfoPacket(owner.Session);
            Require(full != null && BitConverter.ToUInt16(full, 15 + 18) == 0x4001
                && full.AsSpan(15 + 3, 15).SequenceEqual(expectedFull.AsSpan(3, 15)),
                "room USERINFO changes UID at body+18 without overwriting capsule/honor prefix");
            foreach (var port in new[] { 10050, 10060, 10068, 10070 })
            {
                var spawn = GameChannelSpawnPolicy.Resolve(port, 1);
                Require(spawn.TownId == 10 && spawn.AreaId == 0 && spawn.X == 474 && spawn.Y == 234
                    && spawn.IsTransient && !GameChannelSpawnPolicy.ShouldPersistPosition(port, 2),
                    $"PvP listener {port} enters the resource-defined town and preserves normal town storage (actual {spawn.TownId}/{spawn.AreaId} {spawn.X},{spawn.Y})");
            }
            Require(!GameChannelSpawnPolicy.CanEnterTown(10011, 10)
                && !GameChannelSpawnPolicy.CanEnterTown(10068, 1)
                && GameChannelSpawnPolicy.Resolve(10011, 10).TownId == GameChannelSpawnPolicy.NormalTownFallbackId,
                "channel policy keeps PvP and normal town routing distinct, including invalid stored town IDs");
            await pvp.HandleLobbyReadyAsync(owner.Session);
            var ownerLobby = owner.Drain();
            Require(ReadBasicUserIds(FindBody(ownerLobby, NotiPacketTypeA21.USERINFO))
                .SequenceEqual(new ushort[] { 0x4001 }), "first lobby snapshot contains its real session UID after the A21 header");
            await pvp.HandleLobbyReadyAsync(member.Session);
            var memberLobby = member.Drain();
            Require(ReadBasicUserIds(FindBody(memberLobby, NotiPacketTypeA21.USERINFO))
                .SequenceEqual(new ushort[] { 0x4001, 0x4002 }), "second lobby snapshot contains both identities in complete records");
            owner.Drain();
            await pvp.HandleMakeRoom(owner.Session, Header(CmdPacketTypeA21.MAKE_PVP_ROOM), MakeRequest(true, Array.Empty<byte>()));
            var room = rooms.SnapshotForListener(10068).Single();
            var packets = owner.Drain(); member.Drain();
            CheckRoomBody(FindBody(packets, NotiPacketTypeA21.PVP_ROOM_INFO), room.RoomId, new ushort[] { 0x4001 }, false);
            Require(owner.Session.Player.UserState == 2 && !owner.Session.Player.TownPresenceReady,
                "room entry commits PvP state after its required handshake");
            using (var later = await Peer.CreateAsync(database, sessions, 7001, 5003, 0x4003, 10068))
            {
                await pvp.HandleLobbyReadyAsync(later.Session);
                var laterLobby = later.Drain();
                Require(ReadBasicUserIds(FindBody(laterLobby, NotiPacketTypeA21.USERINFO))
                    .SequenceEqual(new ushort[] { 0x4001, 0x4002, 0x4003 })
                    && Type(laterLobby[0]) == (ushort)NotiPacketTypeA21.USERINFO
                    && Type(laterLobby[1]) == (ushort)NotiPacketTypeA21.PVP_ROOM_INFO,
                    "late lobby arrival receives identities for room occupants before its room preview");
                await sessions.UnregisterAsync(7001, later.Session);
            }
            owner.Drain(); member.Drain();
            await pvp.HandleEnterRoom(member.Session, Header(CmdPacketTypeA21.ENTER_PVP_ROOM), EnterRequest(room.RoomId));
            packets = member.Drain();
            var ownerJoinPackets = owner.Drain();
            CheckIdentityDependencies(packets, Array.Empty<ushort>());
            CheckIdentityDependencies(ownerJoinPackets, new ushort[] { 0x4001 });
            var enter = packets.Single(packet => packet[0] == 1 && Type(packet) == (ushort)CmdPacketTypeA21.ENTER_PVP_ROOM);
            Require(enter.Length == 24 && enter[15] == 1, "A21 enter response has success plus eight ready bytes");
            room = rooms.SnapshotForListener(10068).Single();
            CheckRoomBody(PvpRoomNotificationBuilder.BuildRoomInfoBody(new[] { room }), room.RoomId,
                new ushort[] { 0x4001, 0x4002 }, false);
            var seatBody = PvpRoomNotificationBuilder.BuildSeatStateBody(room, 1);
            Require(seatBody.Length == 8 && seatBody[3] == 1 && seatBody[4] == 1
                && BitConverter.ToUInt16(seatBody, 6) == 0x4002, "SEAT_STATE consumes eight bytes without an extra location field");

            await pvp.HandleSetMapIndex(member.Session, Header(CmdPacketTypeA21.SET_PVP_MAP_INDEX), BitConverter.GetBytes((short)2));
            Require(member.Drain().Count == 0 && rooms.SnapshotForListener(10068).Single().MapIndex == 0,
                "non-owner map mutation is rejected without state change");
            await pvp.HandleSetMapIndex(owner.Session, Header(CmdPacketTypeA21.SET_PVP_MAP_INDEX), BitConverter.GetBytes((short)6));
            Require(owner.Drain().Count == 0 && rooms.SnapshotForListener(10068).Single().MapIndex == 0,
                "map outside current PVF order is rejected without an unregistered command reply");
            await pvp.HandleSetMapIndex(owner.Session, Header(CmdPacketTypeA21.SET_PVP_MAP_INDEX), BitConverter.GetBytes((short)3));
            Require(BitConverter.ToInt16(FindBody(owner.Drain(), NotiPacketTypeA21.PVP_ROOM_STATE), 4) == 3,
                "accepted map reaches the room-state projection");
            member.Drain();

            await pvp.HandleSetReadyState(owner.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
            Require(owner.Drain().Single()[15] == 0 && rooms.SnapshotForListener(10068).Single().RoomState == 1,
                "start fails until the participant is ready");
            await pvp.HandleSetReadyState(member.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
            Require(FindBody(owner.Drain(), NotiPacketTypeA21.PVP_READY_STATE).SequenceEqual(new byte[] { 1, 1 }),
                "participant ready state reaches the owner with native seat/flag layout");
            member.Drain();
            await pvp.HandleSetReadyState(owner.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
            Require(FindBody(owner.Drain(), NotiPacketTypeA21.START_PVP).SequenceEqual(new byte[] { 3, 2 })
                && FindBody(member.Drain(), NotiPacketTypeA21.START_PVP).SequenceEqual(new byte[] { 3, 2 }),
                "ready participant lets owner start the selected map for both clients");
            room = rooms.SnapshotForListener(10068).Single();
            Require(room.RoomState == 2, "start commits a combat generation");

            await pvp.HandleDiePvpCharacter(member.Session, Header(CmdPacketTypeA21.DIE_PVP_CHARACTER),
                new byte[] { 2, 0x40, 1, 0x40, 0 });
            packets = owner.Drain(); member.Drain();
            Require(FindBody(packets, NotiPacketTypeA21.DIE_PVP_CHARACTER).SequenceEqual(new byte[] { 1, 0 })
                && FindBody(packets, NotiPacketTypeA21.REQ_PVP_RANK).Length == 0,
                "victim report publishes death and enters rank acknowledgement");
            await pvp.HandlePvpRankResponse(owner.Session, Header(CmdPacketTypeA21.RES_PVP_RANK), new byte[69]);
            Require(rooms.SnapshotForListener(10068).Single().SettlementPhase == FreeDuelRoom.AwaitingRankSettlementPhase,
                "truncated rank report cannot advance settlement");
            await pvp.HandlePvpRankResponse(owner.Session, Header(CmdPacketTypeA21.RES_PVP_RANK), new byte[70]);
            await pvp.HandlePvpRankResponse(member.Session, Header(CmdPacketTypeA21.RES_PVP_RANK), new byte[70]);
            CheckEndBody(FindBody(owner.Drain(), NotiPacketTypeA21.END_PVP), database, 1);
            CheckEndBody(FindBody(member.Drain(), NotiPacketTypeA21.END_PVP), database, 10);
            await pvp.HandleEndPvpResult(owner.Session, Header(CmdPacketTypeA21.END_PVP_RESULT), Array.Empty<byte>());
            await pvp.HandleEndPvpResult(member.Session, Header(CmdPacketTypeA21.END_PVP_RESULT), Array.Empty<byte>());
            owner.Drain(); member.Drain();
            room = rooms.SnapshotForListener(10068).Single();
            Require(room.RoomState == 1 && !room.GetReadyState(1)
                && Scalar(database, "SELECT experience_in_grade FROM character_pvp_records WHERE character_id=5001;") == 3500
                && Scalar(database, "SELECT rank_warmup_games FROM character_pvp_records WHERE character_id=5001;") == 19,
                "settlement returns to waiting without awarding ranked progress in a free room");

            await pvp.HandleSetSeatState(member.Session, Header(CmdPacketTypeA21.SET_PVP_SEAT_STATE), new byte[] { 1, 0xFE });
            packets = member.Drain(); owner.Drain();
            Require(member.Session.Player.UserState == 0 && member.Session.Player.TownPresenceReady
                && member.Session.Player.CurTownId == 10 && packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.AREA_USERS),
                "participant exits to PvP town with a live connection and a town roster");
            await pvp.HandleEnterRoom(member.Session, Header(CmdPacketTypeA21.ENTER_PVP_ROOM), EnterRequest(room.RoomId));
            member.Drain(); owner.Drain();
            Require(member.Session.Player.UserState == 2, "same connection can re-enter after leaving a room");
            await pvp.HandleSetSeatState(owner.Session, Header(CmdPacketTypeA21.SET_PVP_SEAT_STATE), new byte[] { 0, 0xFE });
            owner.Drain(); member.Drain();
            room = rooms.SnapshotForListener(10068).Single();
            Require(room.OwnerCharacterId == 6001 && owner.Session.Player.TownPresenceReady,
                "owner exit promotes the remaining member and returns owner to PvP town");
            await pvp.HandleSetSeatState(member.Session, Header(CmdPacketTypeA21.SET_PVP_SEAT_STATE), new byte[] { 1, 0xFE });
            member.Drain(); owner.Drain();
            Require(rooms.SnapshotForListener(10068).Count == 0, "last owner exit tears down the room");
            var previousGeneration = room.GenerationId;
            await pvp.HandleMakeRoom(owner.Session, Header(CmdPacketTypeA21.MAKE_PVP_ROOM), MakeRequest());
            owner.Drain(); member.Drain();
            var next = rooms.SnapshotForListener(10068).Single();
            Require(next.GenerationId != previousGeneration
                && !rooms.TryForceEndSettlement(next.RoomId, previousGeneration, room.MatchGeneration, out _),
                "reused room ID rejects settlement from the old room generation");
            using var replacement = await Peer.CreateAsync(database, sessions, 6001, 5002, 0x4002, 10068);
            await pvp.HandleMakeRoom(member.Session, Header(CmdPacketTypeA21.MAKE_PVP_ROOM), MakeRequest());
            Require(member.Drain().Count == 0 && rooms.SnapshotForListener(10068).Count == 1,
                "superseded session cannot create or mutate rooms");
            Require(Scalar(database, "SELECT town_id FROM characters WHERE character_id IN (5001,6001) GROUP BY town_id;") == 1,
                "room lifecycle never overwrites the persistent normal-town location");
        }

        private static async Task CheckInvitationsAsync(IGameDatabase database)
        {
            var sessions = new SessionDirectory();
            var characters = new SqliteCharacterRepository(database);
            var source = new SqliteSelectCharacterDataSource(database, characters);
            var town = new TownHandler(characters, source, null, sessions, null, null, null, database);
            var rooms = new FreeDuelRoomRegistry();
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using var runtime = new ServerRuntimeBuilder(database);
            var protocol = runtime.BuildGameProtocolHandler(sessions);
            using var pvp = new PvpRoomHandler(sessions, town.BuildFullUserInfoPacket,
                new CharacterTransitionCoordinator(sessions), () => true, rooms,
                town.AnnouncePvpTownArrivalWithinTransitionAsync,
                utcNow: () => now, roomInviteLifetime: TimeSpan.FromSeconds(2), database: database);
            // Use the production REQUEST_PEER/RESPONSE_PEER dispatcher and Party
            // checkout while injecting only the invitation clock and availability.
            runtime.GetOrCreateGameProtocolSocialHandlers().Party.AttachPvpRoomHandler(pvp);
            using var owner = await Peer.CreateAsync(database, sessions, 5001, 5001, 0x4201, 10068);
            using var member = await Peer.CreateAsync(database, sessions, 6001, 5002, 0x4202, 10068);
            await pvp.HandleLobbyReadyAsync(owner.Session);
            await pvp.HandleLobbyReadyAsync(member.Session);
            owner.Drain(); member.Drain();
            await pvp.HandleMakeRoom(owner.Session, Header(CmdPacketTypeA21.MAKE_PVP_ROOM), MakeRequest());
            owner.Drain(); member.Drain();
            Task Invite() => protocol.OnPacketReceived_86JP(owner.Session,
                Header(CmdPacketTypeA21.REQUEST_PEER), PeerRequest(0x4202, 0));
            Task Accept(int token = 0) => protocol.OnPacketReceived_86JP(member.Session,
                Header(CmdPacketTypeA21.RESPONSE_PEER), PeerRequest(0x4201, token));

            await Invite();
            var invitation = member.Drain();
            CheckIdentityDependencies(invitation, Array.Empty<ushort>());
            Require(invitation.Count == 2 && Type(invitation[0]) == (ushort)NotiPacketTypeA21.USERINFO
                && FindBody(invitation, NotiPacketTypeA21.REQUEST_PEER).SequenceEqual(PeerRequest(0x4201, 0))
                && pvp.PendingRoomInviteCountForTest == 1,
                "real PvP invite dispatch registers the inviter before the native seven-byte popup notification");
            await Accept(1);
            Require(member.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 0, 19, 2 })
                && pvp.PendingRoomInviteCountForTest == 1 && member.Session.Player.UserState == 0,
                "a mismatched invite token cannot consume the current invitation or enter the room");
            // A21 2535A30 closes a rejected type-2 popup without sending a
            // RESPONSE_PEER; an eventual late acceptance must still expire.
            now = now.AddSeconds(3);
            await Accept();
            Require(member.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 0, 19, 2 })
                && pvp.PendingRoomInviteCountForTest == 0 && member.Session.Player.UserState == 0,
                "an unanswered or locally rejected invitation expires without joining or clearing a newer room");

            foreach (var mode in new byte[] { 1, 2 })
            {
                await pvp.HandleSetTeamMode(owner.Session, Header(CmdPacketTypeA21.SET_PVP_TEAM_MODE), new[] { mode });
                owner.Drain(); member.Drain();
                await Invite();
                CheckIdentityDependencies(member.Drain(), Array.Empty<ushort>());
                await Accept();
                var joining = member.Drain();
                var existing = owner.Drain();
                CheckIdentityDependencies(joining, Array.Empty<ushort>());
                CheckIdentityDependencies(existing, new ushort[] { 0x4201 });
                Require(member.Session.Player.UserState == 2 && pvp.PendingRoomInviteCountForTest == 0
                    && rooms.TryGetRoomForMember(6001, member.Session.SessionId, out var room, out var seat)
                    && room.BattleMode == mode && seat == 1,
                    $"mode {mode} invitation enters with complete peer identities and consumes exactly one pending invite");
                await Accept();
                Require(member.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 0, 19, 2 }),
                    "replaying an accepted invitation does not add another room member");
                await pvp.HandleSetSeatState(member.Session, Header(CmdPacketTypeA21.SET_PVP_SEAT_STATE), new byte[] { 1, 0xFE });
                member.Drain(); owner.Drain();
                Require(member.Session.Player.UserState == 0 && member.Session.Player.TownPresenceReady,
                    "invited member leaves without disconnecting and can be invited again");
            }
            await Invite();
            member.Drain();
            using var replacement = await Peer.CreateAsync(database, sessions, 6001, 5002, 0x4202, 10068);
            await Accept();
            Require(member.Drain().Count == 0 && replacement.Session.Player.UserState == 0,
                "response from a replaced session cannot join on behalf of the reconnected character");
        }

        private static byte[] PeerRequest(ushort uid, int token)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(uid);
            writer.WriteByte(2);
            writer.WriteInt32(token);
            return writer.ToArray();
        }

        private static async Task CheckTeamWaitingRoomAsync(IGameDatabase database)
        {
            var sessions = new SessionDirectory();
            var characters = new SqliteCharacterRepository(database);
            var source = new SqliteSelectCharacterDataSource(database, characters);
            var town = new TownHandler(characters, source, null, sessions, null, null, null, database);
            var rooms = new FreeDuelRoomRegistry();
            using var pvp = new PvpRoomHandler(sessions, town.BuildFullUserInfoPacket,
                new CharacterTransitionCoordinator(sessions), () => true, rooms,
                town.AnnouncePvpTownArrivalWithinTransitionAsync, database: database);
            using var owner = await Peer.CreateAsync(database, sessions, 5001, 5001, 0x4001, 10068);
            using var member = await Peer.CreateAsync(database, sessions, 6001, 5002, 0x4002, 10068);
            var repository = new SqlitePvpTotalMatchTeamRepository(database);
            Task Check(Peer peer, string name) => pvp.HandleCheckTotalMatchTeamName(peer.Session,
                Header(CmdPacketTypeA21.CHECK_PVP_TOTAL_MATCH_TEAM_NAME), TeamRequest(name));
            Task Set(Peer peer, string name, byte[] slots) => pvp.HandleSetTotalMatchTeam(peer.Session,
                Header(CmdPacketTypeA21.SET_PVP_TOTAL_MATCH_TEAM), TeamRequest(name, slots));
            await pvp.HandleLobbyReadyAsync(owner.Session);
            await pvp.HandleLobbyReadyAsync(member.Session);
            await pvp.HandleMakeRoom(owner.Session, Header(CmdPacketTypeA21.MAKE_PVP_ROOM), MakeRequest(false, Array.Empty<byte>()));
            var room = rooms.SnapshotForListener(10068).Single();
            await pvp.HandleEnterRoom(member.Session, Header(CmdPacketTypeA21.ENTER_PVP_ROOM), EnterRequest(room.RoomId));
            owner.Drain(); member.Drain();
            Require(owner.Session.Player.UserState == 2 && !owner.Session.Player.TownPresenceReady
                && member.Session.Player.UserState == 2 && !member.Session.Player.TownPresenceReady,
                "team dialog regression uses actual free-room membership rather than a town-ready fixture");
            // Captured CHECK body: 02 00 00 00 72 72. Every click needs an ACK.
            for (var i = 0; i < 3; i++)
            {
                await Check(owner, "rr");
                Require(owner.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 1 }),
                    "waiting room CHECK rr enables the native name confirmation on every click");
            }
            await Set(owner, "rr", new byte[] { 0, 1, 2 });
            var packets = owner.Drain();
            Require(packets.Count == 2 && Type(packets[0]) == (ushort)NotiPacketTypeA21.PVP_TOTAL_MATCH_TEAM_INFO
                && packets[1].AsSpan(15).SequenceEqual(new byte[] { 1, 0, 1, 2 })
                && repository.Load(5001).Name == "rr" && rooms.SnapshotForListener(10068).Single().Revision == room.Revision + 1,
                "waiting-room SET saves the team and acknowledges without changing duel room composition");
            await Check(member, "rr");
            Require(member.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 0, 3 }),
                "waiting room duplicate name returns the native duplicate-name response");
            await Check(member, "ss");
            Require(member.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 1 }),
                "non-manager room member can check its own account team name");
            await Set(member, "ss", new byte[] { 0, 1, 2 });
            Require(member.Drain().Count == 2 && repository.Load(5002).Name == "ss",
                "non-manager room member can confirm its own valid account team");
            await pvp.HandleSetReadyState(member.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
            await pvp.HandleSetReadyState(owner.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
            owner.Drain(); member.Drain();
            Require(rooms.SnapshotForListener(10068).Single().RoomState == FreeDuelRoom.StartedRoomState,
                "room generation is in actual combat for the mutation guard test");
            await Check(owner, "rr");
            await Set(owner, "rr", new byte[] { 1, 0, 2 });
            Require(owner.Drain().Count == 0 && repository.Load(5001).Slots.SequenceEqual(new byte[] { 0, 1, 2 }),
                "combat state cannot check or mutate waiting-room team configuration");
        }

        private static async Task CheckTeamDispatchAsync(IGameDatabase database)
        {
            var sessions = new SessionDirectory();
            using var runtime = new ServerRuntimeBuilder(database);
            var protocol = runtime.BuildGameProtocolHandler(sessions);
            using var peer = await Peer.CreateAsync(database, sessions, 5001, 5001, 0x4001, 10011);
            var repository = new SqlitePvpTotalMatchTeamRepository(database);
            var builder = new PvpTotalMatchTeamBodyBuilder(database);
            var snapshot = new SelectCharacterDataSnapshot { CharacterRecord = new SqliteCharacterRepository(database).GetById(5001) };
            Require(!builder.TryBuild(snapshot, 0, out _), "absent team does not create a fake client team during initialization");
            Task Send(CmdPacketTypeA21 type, byte[] bytes) => protocol.OnPacketReceived_86JP(peer.Session, Header(type), bytes);
            await Send(CmdPacketTypeA21.CHECK_PVP_TOTAL_MATCH_TEAM_NAME, TeamRequest("星辰队"));
            Require(peer.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 1 }),
                "real CHECK_TEAM_NAME dispatch enables the native confirm button");
            await Send(CmdPacketTypeA21.SET_PVP_TOTAL_MATCH_TEAM, TeamRequest("星辰队", new byte[] { 0, 1, 2 }));
            var packets = peer.Drain();
            var team = repository.Load(5001);
            Require(team != null && team.Name == "星辰队" && team.CharacterIds.SequenceEqual(new[] { 5001, 5002, 5003 })
                && packets.Count == 2 && Type(packets[0]) == (ushort)NotiPacketTypeA21.PVP_TOTAL_MATCH_TEAM_INFO
                && packets[1].AsSpan(15).SequenceEqual(new byte[] { 1, 0, 1, 2 }),
                "SET_TEAM persists stable character IDs and sends native team notification plus success");
            using (var reader = new BinaryReader(new MemoryStream(packets[0], 15, packets[0].Length - 15)))
            {
                Require(ClientTextEncoding.GetString(reader.ReadBytes(reader.ReadInt32())) == "星辰队"
                    && reader.ReadBytes(3).SequenceEqual(new byte[] { 0, 1, 2 })
                    && reader.BaseStream.Position == reader.BaseStream.Length,
                    "team notification is a GBK Dstr followed by exactly three roster indices");
            }
            await Send(CmdPacketTypeA21.SET_PVP_TOTAL_MATCH_TEAM, TeamRequest("星辰队", new byte[] { 0, 1, 2 }));
            Require(peer.Drain().Count == 2 && Scalar(database, "SELECT COUNT(*) FROM account_pvp_total_match_teams WHERE account_id=5001;") == 1,
                "repeated team confirmation is idempotent");
            foreach (var slots in new[] { new byte[] { 0, 0, 2 }, new byte[] { 0, 1, 255 }, new byte[] { 0, 1, 3 } })
            {
                await Send(CmdPacketTypeA21.SET_PVP_TOTAL_MATCH_TEAM, TeamRequest("星辰队", slots));
                Require(peer.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 0, 2 })
                    && repository.Load(5001).CharacterIds.SequenceEqual(new[] { 5001, 5002, 5003 }),
                    "invalid, repeated or duplicate-profession members leave the stored team intact");
            }
            await Send(CmdPacketTypeA21.CHECK_PVP_TOTAL_MATCH_TEAM_NAME, new byte[] { 1, 0, 0, 0, 0x81 });
            Require(peer.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 0, 0x9F }),
                "malformed GBK name returns the native invalid-name error");
            Require(!repository.TrySet(5002, 5001, ClientTextEncoding.GetBytes("跨账号队"), new byte[] { 0, 1, 2 }, out _, out _),
                "team transaction verifies current character ownership");
            using var second = await Peer.CreateAsync(database, sessions, 6001, 5002, 0x4002, 10011);
            await protocol.OnPacketReceived_86JP(second.Session, Header(CmdPacketTypeA21.CHECK_PVP_TOTAL_MATCH_TEAM_NAME), TeamRequest("星辰队"));
            Require(second.Drain().Single().AsSpan(15).SequenceEqual(new byte[] { 0, 3 }),
                "duplicate team name uses CHECK error 3 rather than a generic notice");
            Require(!repository.TrySet(5002, 6001, ClientTextEncoding.GetBytes("星辰队"), new byte[] { 0, 1, 2 }, out _, out var duplicate)
                && duplicate == 1 && repository.Load(5002) == null,
                "SET rechecks name uniqueness inside the write transaction");
            Execute(database, @"
UPDATE characters SET slot_index=9 WHERE character_id=5001;
UPDATE characters SET slot_index=0 WHERE character_id=5002;
UPDATE characters SET slot_index=1 WHERE character_id=5001;");
            Require(repository.Load(5001).Slots.SequenceEqual(new byte[] { 1, 0, 2 })
                && builder.TryBuild(snapshot, 0, out var restored) && restored.AsSpan(restored.Length - 3).SequenceEqual(new byte[] { 1, 0, 2 }),
                "relogin rebuilds team indices from stable IDs after roster reordering");
            Execute(database, "UPDATE characters SET delete_flag=1 WHERE character_id=5003;");
            Require(repository.Load(5001) == null && !builder.TryBuild(snapshot, 0, out _),
                "deleted team member cannot silently bind to the next roster occupant");
            Execute(database, "UPDATE characters SET delete_flag=0 WHERE character_id=5003;");
            Execute(database, @"
CREATE TRIGGER reject_team_update BEFORE UPDATE ON account_pvp_total_match_teams
BEGIN SELECT RAISE(ABORT, 'selftest team rollback'); END;");
            var failedWrite = false;
            try
            {
                repository.TrySet(5001, 5001, ClientTextEncoding.GetBytes("星辰队"), new byte[] { 0, 1, 2 }, out _, out _);
            }
            catch (SqliteException) { failedWrite = true; }
            Execute(database, "DROP TRIGGER reject_team_update;");
            Require(failedWrite && repository.Load(5001).CharacterIds.SequenceEqual(new[] { 5001, 5002, 5003 }),
                "team persistence failure rolls back the entire member change");
            var competing = await Task.WhenAll(new[] { (5002, 6001), (5003, 7001) }.Select(identity => Task.Run(() =>
            {
                var accepted = repository.TrySet(identity.Item1, identity.Item2, ClientTextEncoding.GetBytes("竞名队"),
                    new byte[] { 0, 1, 2 }, out _, out var error);
                return (accepted, error);
            })));
            Require(competing.Count(result => result.accepted) == 1
                && competing.Single(result => !result.accepted).error == 1,
                "concurrent name claims have exactly one winner and one native duplicate-name failure");
            var sendLock = (SemaphoreSlim)typeof(EnhancedClientSession).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(peer.Session);
            await sendLock.WaitAsync();
            Task pending;
            using var replacement = await Peer.CreateAsync(database, new SessionDirectory(), 5001, 5001, 0x4001, 10011);
            try
            {
                pending = Send(CmdPacketTypeA21.CHECK_PVP_TOTAL_MATCH_TEAM_NAME, TeamRequest("可用名"));
                Require(!pending.IsCompleted, "team-name reply waits behind the session send lock");
                sessions.Register(5001, replacement.Session);
            }
            finally { sendLock.Release(); }
            await pending;
            Require(peer.Drain().Count == 0, "reconnect suppresses queued response for the superseded session");
        }

        private static void CheckRoomBody(byte[] body, ushort id, ushort[] occupants, bool password)
        {
            // A21 1173790: no per-seat location byte. A generated name room is 2+37 bytes.
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream);
            Require(reader.ReadUInt16() == 1 && reader.ReadUInt16() == id && reader.ReadByte() == 1
                && reader.ReadByte() == 1 && reader.ReadByte() == 0, "ROOM_INFO header preserves count, ID, name type, state and owner");
            reader.ReadInt16();
            Require(reader.ReadByte() == 2, "ROOM_INFO retains normal duel mode");
            var ids = new List<ushort>();
            for (var i = 0; i < 8; i++)
            {
                var state = reader.ReadByte();
                var uid = reader.ReadUInt16();
                if (state != 0xFF && state != 0xFE)
                    ids.Add(uid);
            }
            Require(ids.SequenceEqual(occupants) && reader.ReadByte() == (password ? 1 : 0)
                && reader.ReadInt32() == 0 && stream.Position == stream.Length && body.Length == 39,
                $"ROOM_INFO native walk finds {occupants.Length}/8 players and correct password flag in 39 bytes");
        }

        private static void CheckEndBody(byte[] body, IGameDatabase database, byte recipientGrade)
        {
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream);
            Require(reader.ReadByte() == 0 && reader.ReadInt32() == 0 && reader.ReadByte() == recipientGrade
                && reader.ReadInt32() == 0 && reader.ReadByte() == 2, "END_PVP starts with winner seat and recipient's existing grade");
            var records = new SqlitePvpRecordRepository(database);
            foreach (var (uid, id) in new[] { ((ushort)0x4001, 5001), ((ushort)0x4002, 6001) })
            {
                var record = records.Load(id);
                Require(reader.ReadUInt16() == uid && reader.ReadInt32() == (uid == 0x4001 ? 1 : 0) && reader.ReadByte() == record.Grade
                    && reader.ReadInt32() == record.Experience && reader.ReadInt32() == record.RankPoint
                    && reader.ReadInt32() == record.PeakRankPoint, "each native result entry is 19 bytes and preserves progression");
            }
            Require(reader.ReadUInt16() == 0xFFFF && reader.ReadUInt16() == 0xFFFF
                && reader.ReadInt32() == 0 && reader.ReadByte() == 0 && reader.ReadByte() == 0xFF
                && reader.ReadInt32() == 0 && reader.ReadByte() == 0 && stream.Position == body.Length
                && body.Length == 64, "END_PVP consumes the complete A21 tail with no fabricated rewards");
        }

        private static GamePacketHeader Header(CmdPacketTypeA21 type) => new GamePacketHeader { cmd = 1, type = (ushort)type };
        private static ushort Type(byte[] packet) => BitConverter.ToUInt16(packet, 1);
        private static byte[] FindBody(List<byte[]> packets, NotiPacketTypeA21 type)
            => packets.Single(packet => packet[0] == 0 && Type(packet) == (ushort)type).AsSpan(15).ToArray();

        private static byte[] MakeRequest(bool passwordFlag = false, byte[] password = null)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt16(0);
            writer.WriteByte(passwordFlag ? (byte)1 : (byte)0);
            if (passwordFlag)
            {
                password ??= Array.Empty<byte>();
                writer.WriteInt32(password.Length);
                writer.WriteBytes(password);
            }
            writer.WriteByte(0);
            return writer.ToArray();
        }

        private static byte[] EnterRequest(ushort room)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(room);
            writer.WriteByte(0);
            return writer.ToArray();
        }

        private static byte[] TeamRequest(string name, byte[] slots = null)
        {
            var writer = new GamePacketWriter();
            var bytes = ClientTextEncoding.GetBytes(name);
            writer.WriteInt32(bytes.Length);
            writer.WriteBytes(bytes);
            if (slots != null)
                writer.WriteBytes(slots);
            return writer.ToArray();
        }

        private static void Execute(IGameDatabase database, string sql) => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        });

        private static long Scalar(IGameDatabase database, string sql) => database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        });

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
            Console.WriteLine("[PASS] " + message);
        }

        private sealed class Peer : IDisposable
        {
            private readonly TcpClient _reader;
            internal EnhancedClientSession Session { get; }

            private Peer(TcpClient reader, EnhancedClientSession session)
            {
                _reader = reader;
                Session = session;
            }

            internal static async Task<Peer> CreateAsync(IGameDatabase database, SessionDirectory sessions,
                int characterId, int accountId, ushort uid, int port)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var reader = new TcpClient { ReceiveTimeout = 3000 };
                TcpClient writer;
                try
                {
                    var connecting = reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    writer = await listener.AcceptTcpClientAsync();
                    await connecting;
                }
                finally { listener.Stop(); }
                var session = new EnhancedClientSession(writer, new GamePacketHeader(), port)
                {
                    Account = new AccountRecord { AccountId = accountId }
                };
                var record = new SqliteCharacterRepository(database).GetById(characterId);
                session.Player.CharacterId = characterId;
                session.Player.UserId = uid;
                session.Player.Name = record.Name;
                session.Player.Level = record.Level;
                session.Player.Job = record.Job;
                session.Player.GrowType = record.GrowType;
                session.Player.PvpGrade = record.PvpGrade;
                session.Player.PvpRatingGrade = record.PvpRatingGrade;
                session.Player.Subtype0Tail = new UserInfoMinimumTailSnapshot();
                session.Player.UserState = 0;
                session.Player.TownPresenceReady = true;
                var spawn = GameChannelSpawnPolicy.Resolve(port, record.TownId);
                session.Player.CurTownId = spawn.TownId;
                session.Player.CurAreaId = spawn.AreaId;
                session.Player.CurPosX = spawn.X;
                session.Player.CurPosY = spawn.Y;
                session.Player.CurDirection = spawn.Direction;
                session.Player.CurAreaState = spawn.AreaState;
                session.GameSession = new GameSession(session, database);
                sessions.Register(characterId, session);
                return new Peer(reader, session);
            }

            internal List<byte[]> Drain()
            {
                var packets = new List<byte[]>();
                var stream = _reader.GetStream();
                while (_reader.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_reader.Available == 0)
                        throw new InvalidOperationException("PvP flow closed the connection");
                    var header = new byte[15];
                    stream.ReadExactly(header);
                    var length = BitConverter.ToInt32(header, 3);
                    if (length < 15 || length > 65536)
                        throw new InvalidDataException("invalid PvP packet length");
                    var packet = new byte[length];
                    header.CopyTo(packet, 0);
                    stream.ReadExactly(packet.AsSpan(15));
                    packets.Add(packet);
                }
                return packets;
            }

            public void Dispose()
            {
                Session.Close();
                _reader.Dispose();
            }
        }
    }
}
