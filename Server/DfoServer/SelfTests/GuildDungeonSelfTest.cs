using DfoServer.Game.Dungeon;
using DfoServer.Game.Guilds;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Guilds;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.SelfTests
{
    public static class GuildDungeonSelfTest
    {
        public static int Run()
        {
            int failures = 0;
            void Check(string name, bool passed)
            { Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] GuildDungeon {name}"); if (!passed) failures++; }
            string path = Path.Combine(Path.GetTempPath(), $"guild-dungeon-{Guid.NewGuid():N}.db");
            string migrationPath = Path.Combine(Path.GetTempPath(), $"guild-dungeon-migration-{Guid.NewGuid():N}.db");
            try
            {
                CheckSchemaMigration(migrationPath, Check);
                var catalog = GuildDungeonCatalog.Current;
                Check("current PVF six dungeons", catalog.Dungeons.Count == 6 && catalog.StyleId == 0);
                var first = catalog.Dungeons.Values.Single(d => d.Prerequisites.Count == 0);
                var next = catalog.Dungeons.Values.First(d => d.Prerequisites.Contains(first.Id));
                Check("clear counts convert to purification", first.Ratio(1) == 0 && first.Ratio(100) == 50
                    && first.Ratio(200) == 100 && first.Ratio(int.MaxValue) == 100);
                var before = new DateTimeOffset(2026, 11, 1, 5, 59, 59, TimeSpan.FromHours(8));
                var current = before.AddSeconds(1);
                Check("monthly Beijing 06:00 boundary", GuildDungeonPeriod.At(before).EndUnix == current.ToUnixTimeSeconds()
                    && GuildDungeonPeriod.At(current).StartUnix == current.ToUnixTimeSeconds());
                var now = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
                var db = new GameDatabase(path, ServerPaths.SchemaFilePath);
                db.Write((c, t) =>
                {
                    using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = @"
INSERT INTO accounts(account_id,m_id,password_hash,created_at) VALUES(901,'guild-dungeon-test','',CURRENT_TIMESTAMP);
INSERT INTO characters(character_id,account_id,name,level,created_at,updated_at) VALUES(901,901,'guild-tester',85,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP),(902,901,'guild-other',85,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP);
INSERT INTO guilds(guild_id,name,leader_character_id,created_at) VALUES(901,'DungeonTest',901,CURRENT_TIMESTAMP);
INSERT INTO guild_members(character_id,guild_id,joined_at) VALUES(901,901,CURRENT_TIMESTAMP);";
                    cmd.ExecuteNonQuery();
                });
                var service = new GuildDungeonService(db, () => now);
                var repository = new GuildDungeonRepository(db);
                var snapshot = service.Read(901);
                var packet = GuildDungeonPacketBuilder.Build(snapshot);
                Check("exact client body shape", packet.Length == 15 + 17 + 6 * 8 && packet[15] == 1
                    && BitConverter.ToInt32(packet,16) == 0
                    && BitConverter.ToUInt32(packet,20) == snapshot.Period.EndUnix
                    && BitConverter.ToInt32(packet,24) == 0 && BitConverter.ToInt32(packet,28) == 6);
                Check("positive countdown", snapshot.Period.EndUnix > now.ToUnixTimeSeconds());
                Check("nonmember cannot enter", !service.TryAdmit(902, new DungeonRun((short)first.Id,0),out _));
                Check("prerequisite dungeon locked", !service.TryAdmit(901, new DungeonRun((short)next.Id,0),out _));
                var run = new DungeonRun((short)first.Id,0);
                Check("first dungeon admits", service.TryAdmit(901,run,out _));
                var result = service.ApplyClear(901,run);
                Check("clear progress and reward committed", result.Applied && result.ClearCount == 1 && result.MailId > 0);
                var replay = service.ApplyClear(901,run);
                Check("duplicate event does not advance or mail twice", !replay.Applied && replay.MailId == result.MailId
                    && service.Read(901).ClearCounts[first.Id] == 1);
                Check("restart restores progress", new GuildDungeonService(db,()=>now).Read(901).ClearCounts[first.Id] == 1);
                long Count(string table) => db.Read(c =>
                { using var cmd=c.CreateCommand(); cmd.CommandText="SELECT COUNT(*) FROM "+table; return Convert.ToInt64(cmd.ExecuteScalar()); });
                Check("one durable clear record", Count("guild_dungeon_clears") == 1);

                // Force the last operation to fail and verify the shared transaction
                // rolls back reward mail, contribution and progress together.
                db.Write((c,t) =>
                {
                    using var cmd=c.CreateCommand(); cmd.Transaction=t;
                    cmd.CommandText="UPDATE guilds SET contribution=2147483647 WHERE guild_id=901;"; cmd.ExecuteNonQuery();
                });
                long mailsBefore=Count("mailbox_messages");
                bool rejected=false;
                try { repository.RecordClear(901,first.Id,Guid.NewGuid(),run.Selection.GuildDungeon,catalog,now); }
                catch (InvalidOperationException) { rejected=true; }
                Check("failed contribution rolls back mail and progress", rejected && Count("mailbox_messages")==mailsBefore
                    && service.Read(901).ClearCounts[first.Id]==1);
                db.Write((c,t) =>
                {
                    using var cmd=c.CreateCommand(); cmd.Transaction=t;
                    cmd.CommandText="UPDATE guilds SET contribution=20 WHERE guild_id=901; UPDATE guild_dungeon_progress SET clear_count=100 WHERE guild_id=901;";
                    cmd.ExecuteNonQuery();
                });
                Check("50 percent conquest unlocks next route",service.TryAdmit(901,new DungeonRun((short)next.Id,0),out _));
                var commonEvent=Guid.NewGuid();
                var concurrent=Task.WhenAll(Enumerable.Range(0,2).Select(_=>Task.Run(() =>
                    repository.RecordClear(901,first.Id,commonEvent,run.Selection.GuildDungeon,catalog,now)))).GetAwaiter().GetResult();
                Check("concurrent duplicate commits once",concurrent.Count(r=>r.Applied)==1
                    && service.Read(901).ClearCounts[first.Id]==101);
                now=current;
                Check("new period resets visible progress and locks routes",service.Read(901).ClearCounts.Count==0
                    && !service.TryAdmit(901,new DungeonRun((short)next.Id,0),out _));
                Check("old run cannot advance new period",!repository.RecordClear(901,first.Id,Guid.NewGuid(),
                    run.Selection.GuildDungeon,catalog,now).Applied && service.Read(901).ClearCounts.Count==0);
                var newRun=new DungeonRun((short)first.Id,0); service.TryAdmit(901,newRun,out _);
                db.Write((c,t)=> { using var cmd=c.CreateCommand();cmd.Transaction=t;
                    cmd.CommandText="DELETE FROM guild_members WHERE character_id=901;";cmd.ExecuteNonQuery(); });
                Check("departure invalidates frozen admission",!service.ApplyClear(901,newRun).Applied);
                var empty=GuildDungeonPacketBuilder.Build(null);
                Check("guild cleared notification is complete",empty.Length==32 && empty[15]==0 && BitConverter.ToInt32(empty,16)==-1);
            }
            catch(Exception ex) { Console.WriteLine("[FAIL] GuildDungeon "+ex); failures++; }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var file in new[] { path, migrationPath })
                    foreach (var suffix in new[] { "", "-wal", "-shm" })
                        if (File.Exists(file + suffix)) File.Delete(file + suffix);
            }
            Console.WriteLine($"GuildDungeon: {failures} failure(s)");
            return failures==0?0:1;
        }

        // Real SQLite migration path: the fresh baseline already contains the three
        // guild tables, so a copy is rolled back to v37 and upgraded through the
        // same SqliteMigrations.Apply call the database bootstrap uses.
        private static void CheckSchemaMigration(string path, Action<string,bool> check)
        {
            string source = Path.Combine(Path.GetTempPath(), $"guild-dungeon-baseline-{Guid.NewGuid():N}.db");
            try
            {
                _ = new GameDatabase(source, ServerPaths.SchemaFilePath);
                using (var fresh = OpenRaw(source))
                    check("fresh baseline schema contains the three guild tables",
                        SqliteMigrations.ReadVersion(fresh) == SqliteMigrations.CurrentVersion
                        && SqliteMigrations.CurrentVersion >= 41
                        && HasTable(fresh, "guild_dungeon_progress")
                        && HasTable(fresh, "guild_dungeon_clears")
                        && HasTable(fresh, "guild_contents"));
                SqliteConnection.ClearAllPools();
                File.Copy(source, path);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(source + suffix)) File.Delete(source + suffix);
            }
            using var connection = OpenRaw(path);
            Execute(connection, @"DROP TABLE guild_contents;
DROP TABLE guild_dungeon_clears;
DROP TABLE guild_dungeon_progress;
DROP TABLE character_equipment_growth;
DROP TABLE character_rename_history;
UPDATE schema_metadata SET schema_version=37 WHERE singleton_id=1;
PRAGMA user_version=37;");
            check("v37 baseline has no guild dungeon or content tables",
                SqliteMigrations.ReadVersion(connection) == 37
                && !HasTable(connection, "guild_dungeon_progress")
                && !HasTable(connection, "guild_dungeon_clears")
                && !HasTable(connection, "guild_contents"));
            SqliteMigrations.Apply(connection);
            check("v37 upgrades through v38/v39/v40/v41 and creates the guild tables",
                SqliteMigrations.ReadVersion(connection) == SqliteMigrations.CurrentVersion
                && ReadSchemaVersion(connection) == SqliteMigrations.CurrentVersion
                && Columns(connection, "guild_dungeon_progress").SequenceEqual(new[]
                    { "guild_id:1", "period_start:2", "style_id:3", "dungeon_id:4", "clear_count:0" })
                && Columns(connection, "guild_dungeon_clears").SequenceEqual(new[]
                    { "event_id:1", "character_id:2", "guild_id:0", "period_start:0", "dungeon_id:0", "mail_id:0", "clear_count:0" })
                && Columns(connection, "guild_contents").SequenceEqual(new[]
                    { "guild_id:1", "content_type:2", "content_id:0", "content_value:0", "expires_at:0" })
                && ForeignKeys(connection, "guild_contents").SequenceEqual(new[] { "guilds" })
                && ForeignKeys(connection, "guild_dungeon_progress").SequenceEqual(new[] { "guilds" }));
            Execute(connection,
                "INSERT INTO accounts(account_id,m_id) VALUES(1,'migration-guild');",
                "INSERT INTO characters(character_id,account_id,name) VALUES(1,1,'migration-guild');",
                "INSERT INTO guilds(guild_id,name,leader_character_id) VALUES(1,'migration-guild',1);",
                "INSERT INTO guild_dungeon_progress(guild_id,period_start,style_id,dungeon_id,clear_count) VALUES(1,1,1,1,7);");
            SqliteMigrations.Apply(connection);
            check("repeated startup keeps current schema and existing dungeon progress",
                SqliteMigrations.ReadVersion(connection) == SqliteMigrations.CurrentVersion
                && Convert.ToInt32(ExecuteScalar(connection,
                    "SELECT clear_count FROM guild_dungeon_progress WHERE guild_id=1;")) == 7);
            Execute(connection, @"DROP TABLE guild_contents;
UPDATE schema_metadata SET schema_version=39 WHERE singleton_id=1;
PRAGMA user_version=39;");
            SqliteMigrations.Apply(connection);
            check("v39 resumes through current schema without losing the v39 dungeon tables",
                SqliteMigrations.ReadVersion(connection) == SqliteMigrations.CurrentVersion
                && HasTable(connection, "guild_contents")
                && Convert.ToInt32(ExecuteScalar(connection,
                    "SELECT clear_count FROM guild_dungeon_progress WHERE guild_id=1;")) == 7);
        }

        private static SqliteConnection OpenRaw(string path)
        {
            var connection = new SqliteConnection(SqliteDatabaseBootstrap.BuildConnectionString(path));
            connection.Open();
            return connection;
        }

        private static void Execute(SqliteConnection connection, params string[] sql)
        {
            foreach (var statement in sql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = statement;
                cmd.ExecuteNonQuery();
            }
        }

        private static object ExecuteScalar(SqliteConnection connection, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }

        private static bool HasTable(SqliteConnection connection, string table)
            => Convert.ToInt32(ExecuteScalar(connection,
                $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';")) == 1;

        private static int ReadSchemaVersion(SqliteConnection connection)
            => Convert.ToInt32(ExecuteScalar(connection,
                "SELECT schema_version FROM schema_metadata WHERE singleton_id=1;"));

        private static IReadOnlyList<string> Columns(SqliteConnection connection, string table)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read()) columns.Add($"{reader.GetString(1)}:{reader.GetInt32(5)}");
            return columns;
        }

        private static IReadOnlyList<string> ForeignKeys(SqliteConnection connection, string table)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA foreign_key_list({table});";
            using var reader = cmd.ExecuteReader();
            var tables = new List<string>();
            while (reader.Read()) tables.Add(reader.GetString(2));
            return tables;
        }
    }
}
