using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Friends;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Friends;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static partial class UnitedFriendSystemSelfTest
    {
        private static async Task RunRequestTests(string tempDir)
        {
            ResetState(tempDir);
            string path = Path.Combine(tempDir, $"inventory_{_dbSeq - 1}.db");
            void Execute(string sql)
            {
                using var connection = new SqliteConnection($"Data Source={path}");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            Execute("INSERT INTO accounts(account_id,m_id) VALUES(1,'friends-test');"
                + "INSERT INTO characters(character_id,account_id,name,level) VALUES(101,1,'甲',20),(102,1,'乙',30),(103,1,'旁观者',40),(104,1,'离线',50);");
            SetStatic("_characterRepository", new SqliteCharacterRepository(path, ServerPaths.SchemaFilePath));
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            var registry = new GameCommandRegistry();
            registry.RegisterGroup("friend", group => UnitedFriendSystem.RegisterHandlers(group, sessions, transitions));
            Check("ordinary ADD/DELETE register without PVP buddy response", registry.Count == 2
                && !registry.TryGetValue((ushort)CmdPacketTypeA21.RESPONSE_ADD_PVP_BUDDY, out _));
            using var a = await FriendPeer.Create(sessions, 101, "甲");
            using var b = await FriendPeer.Create(sessions, 102, "乙");
            using var outsider = await FriendPeer.Create(sessions, 103, "旁观者", GameNetworkConfig.Channel100GamePort);
            var init = new UnitedServerFriendInfoBodyBuilder(sessions);
            var snapshot = new SelectCharacterDataSnapshot { CharacterRecord = new CharacterRecord { CharacterId = 101 } };
            Check("login registers one complete ordinary friend list", NewCharacterInitSequence.Build().Count(p =>
                p.Command == 0 && p.Type == (ushort)NotiPacketTypeA21.UNITED_SERVER_FRIEND_INFO) == 1);
            Check("login with no friends emits the native empty list", init.TryBuild(snapshot, 0, out var initBody)
                && initBody.AsSpan().SequenceEqual(new byte[8]));
            Task Dispatch(FriendPeer peer, CmdPacketTypeA21 command, byte[] body)
            {
                if (!registry.TryGetValue((ushort)command, out var handler)) throw new InvalidOperationException("unregistered friend command");
                return handler(peer.Session, new GamePacketHeader { type = (ushort)command }, body);
            }
            Task Add(FriendPeer peer, string target) => Dispatch(peer, CmdPacketTypeA21.ADD_UNITED_SERVER_FRIEND, FriendAddBody(target));
            Task Delete(FriendPeer peer, string target) => Dispatch(peer, CmdPacketTypeA21.DELETE_UNITED_SERVER_FRIEND, FriendNameBody(target));
            bool EmptyPair() => !UnitedFriendSystem.IsFriend("甲", "乙") && !UnitedFriendSystem.IsFriend("乙", "甲");
            bool MutualPair() => UnitedFriendSystem.IsFriend("甲", "乙") && UnitedFriendSystem.IsFriend("乙", "甲");

            byte[] captured = { 0xEB, 3, 1, 8, 0, 0, 0, 0xB8, 0xC4, 0xC3, 0xFB, 0xB2, 0xE2, 0xCA, 0xD4 };
            Check("current A21 captured ADD decodes GBK and server byte", UnitedFriendRequest.TryAdd(captured, out var name) && name == "改名测试");
            var invalid = new[] { Array.Empty<byte>(), captured[..^1], captured.Concat(new byte[] { 0 }).ToArray(),
                new byte[] { 0, 0, 1, 255, 255, 255, 127 }, new byte[] { 0, 0, 1, 1, 0, 0, 0, 0x81 },
                new byte[] { 0, 0, 1, 1, 0, 0, 0, 0 }, FriendAddBody(new string('x', 256)) };
            foreach (var body in invalid) await Dispatch(a, CmdPacketTypeA21.ADD_UNITED_SERVER_FRIEND, body);
            var invalidAcks = a.Drain();
            Check("malformed/trailing/overflow/invalid GBK requests never persist or notify target",
                invalidAcks.Count == invalid.Length && invalidAcks.All(p => p[15] == 0)
                && b.Drain().Count == 0 && EmptyPair() && Repository.LoadAll().Count == 0);
            Check("DELETE consumes DSTR length, not a character ID", UnitedFriendRequest.TryDelete(FriendNameBody("乙"), out name) && name == "乙"
                && !UnitedFriendRequest.TryDelete(new byte[] { 1, 102, 0, 0, 0, 0xD2, 0xD2 }, out _));
            Check("native DSTR byte limit accepts 255 but rejects 256 GBK bytes",
                UnitedFriendRequest.TryAdd(FriendAddBody(new string('x', 255)), out _)
                && !UnitedFriendRequest.TryAdd(FriendAddBody(new string('甲', 128)), out _));
            await Add(a, "甲"); await Add(a, "missing");
            Check("self and nonexistent targets are rejected", a.Drain().All(p => p[15] == 0) && EmptyPair());

            var addGate = await transitions.AcquireAsync(101);
            var staleAdd = Add(a, "乙");
            a.Session.Player.UserId = 201;
            addGate.Dispose(); await staleAdd;
            Check("queued ADD is bound to original selected-character UID", a.Drain().Count == 0 && b.Drain().Count == 0 && EmptyPair());
            a.Session.Player.UserId = 101;

            await Add(a, "乙");
            var added = a.Drain();
            Check("ADD directly commits only owner edge and immediately refreshes owner", UnitedFriendSystem.IsFriend("甲", "乙")
                && !UnitedFriendSystem.IsFriend("乙", "甲") && Repository.LoadAll().Count == 1
                && HasFriendList(added, "乙") && added.Single(p => p[0] == 1).AsSpan(15).SequenceEqual(new byte[] { 1 })
                && b.Drain().Count == 0 && outsider.Drain().Count == 0);
            Check("same-channel identity follows the complete ordinary list", ListBeforeIdentity(added));
            Check("login and runtime publish identical native friend entries", init.TryBuild(snapshot, 0, out initBody)
                && added.Single(p => IsFriendSubcommand(p, 0)).AsSpan(15).SequenceEqual(initBody));
            await Task.WhenAll(Add(a, "乙"), Add(a, "乙"));
            var repeated = a.Drain();
            Check("duplicate ADD is idempotent without reverse edge or invitation", repeated.Count(p => p[0] == 1 && p[15] == 1) == 2
                && Repository.LoadAll()["甲"].SetEquals(new[] { "乙" }) && !UnitedFriendSystem.IsFriend("乙", "甲") && b.Drain().Count == 0);

            b.Session.Player.Level = 60; b.Session.Player.Job = 2; b.Session.Player.GrowType = 1;
            await UnitedFriendSystem.NotifyFriendListInfoChanged(b.Session, sessions);
            var levelList = a.Drain().Single();
            Check("one-way observer receives online level/job refresh", HasFriendList(new[] { levelList }, "乙")
                && levelList[32] == 60 && BitConverter.ToUInt32(levelList, 33) == 2 && levelList[37] == 1 && b.Drain().Count == 0);
            SetStatic("_loaded", false);
            ((Dictionary<string, HashSet<string>>)GetStatic("Friends")).Clear();
            Check("reload preserves exactly one directed edge", UnitedFriendSystem.IsFriend("甲", "乙") && !UnitedFriendSystem.IsFriend("乙", "甲"));

            await RunPvpFriendDepartureTest();

            Execute("ALTER TABLE united_friend_relations RENAME TO unavailable_friends;");
            SetStatic("_loaded", false);
            ((Dictionary<string, HashSet<string>>)GetStatic("Friends")).Clear();
            bool loadFailed = false;
            try { UnitedFriendSystem.GetFriends("甲"); }
            catch (SqliteException) { loadFailed = true; }
            Check("failed repository load is not cached as an empty graph", loadFailed && !(bool)GetStatic("_loaded"));
            Execute("ALTER TABLE unavailable_friends RENAME TO united_friend_relations;");
            Check("repository recovery retries and restores persisted graph", UnitedFriendSystem.IsFriend("甲", "乙"));

            await Add(b, "甲"); b.Drain();
            Check("reverse ADD is independent and does not refresh the original owner", MutualPair() && Repository.LoadAll().Count == 2 && a.Drain().Count == 0);
            var deleteGate = await transitions.AcquireAsync(101);
            var staleDelete = Delete(a, "乙");
            a.Session.Player.UserId = 201;
            deleteGate.Dispose(); await staleDelete;
            Check("queued DELETE cannot affect a new selection on same socket", MutualPair() && a.Drain().Count == 0 && b.Drain().Count == 0);
            a.Session.Player.UserId = 101;

            Execute("CREATE TRIGGER fail_friend_delete BEFORE DELETE ON united_friend_relations BEGIN SELECT RAISE(ABORT,'test delete rollback'); END;");
            await Delete(a, "乙");
            Check("failed delete preserves DB/memory and sends no refresh", a.Drain().Single()[15] == 0 && b.Drain().Count == 0
                && MutualPair() && Repository.LoadAll().Count == 2);
            Execute("DROP TRIGGER fail_friend_delete;");
            await Delete(a, "乙");
            var deleted = a.Drain();
            Check("DELETE clears native entity friend flag before rebuilding list", DeleteBeforeFullList(deleted, "乙"));
            Check("DELETE removes only owner edge and sends an empty snapshot", !UnitedFriendSystem.IsFriend("甲", "乙")
                && UnitedFriendSystem.IsFriend("乙", "甲") && Repository.LoadAll().Count == 1
                && Repository.LoadAll()["乙"].Contains("甲") && HasFriendList(deleted) && b.Drain().Count == 0);
            await Delete(a, "乙");
            Check("repeated DELETE cannot remove reverse edge", a.Drain().Single()[15] == 0 && UnitedFriendSystem.IsFriend("乙", "甲"));
            await Delete(b, "甲"); b.Drain();

            UnitedFriendSystem.RecordFriendship("甲", "乙");
            var sendLock = (System.Threading.SemaphoreSlim)typeof(EnhancedClientSession)
                .GetField("_sendLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(a.Session);
            await sendLock.WaitAsync();
            var staleList = UnitedFriendSystem.SendFriendListAsync(a.Session, sessions, UnitedFriendSystem.GetFriends("甲"));
            UnitedFriendSystem.RemoveFriendship("甲", "乙");
            sendLock.Release(); await staleList;
            Check("queued stale list cannot resurrect removed relationship", a.Drain().Count == 0 && EmptyPair());

            UnitedFriendSystem.RecordFriendship("甲", "乙");
            await sendLock.WaitAsync();
            var staleIdentity = UnitedFriendSystem.NotifyFriendAddedAsync(a.Session, "乙", sessions);
            await sessions.UnregisterAsync(102, b.Session);
            sendLock.Release(); await staleIdentity;
            Check("queued identity never resurrects an offline target", a.Drain().Count == 0);
            sessions.Register(102, b.Session);
            await sendLock.WaitAsync();
            staleIdentity = UnitedFriendSystem.NotifyFriendAddedAsync(a.Session, "乙", sessions);
            b.Session.Player.UserId = 202;
            sendLock.Release(); await staleIdentity;
            Check("queued identity is bound to target selected-character UID", a.Drain().Count == 0);
            b.Session.Player.UserId = 102;
            await sendLock.WaitAsync();
            staleIdentity = UnitedFriendSystem.NotifyFriendAddedAsync(a.Session, "乙", sessions);
            a.Session.Player.UserId = 201;
            sendLock.Release(); await staleIdentity;
            Check("queued identity is bound to receiving selected-character UID", a.Drain().Count == 0);
            a.Session.Player.UserId = 101;
            UnitedFriendSystem.RemoveFriendship("甲", "乙");
            await sendLock.WaitAsync();
            var staleRemoval = UnitedFriendSystem.SendFriendDeletedAsync(a.Session, "乙", sessions);
            UnitedFriendSystem.RecordFriendship("甲", "乙");
            sendLock.Release(); await staleRemoval;
            Check("queued removal cannot clear a re-added friend", a.Drain().Count == 0 && UnitedFriendSystem.IsFriend("甲", "乙"));
            UnitedFriendSystem.RemoveFriendship("甲", "乙");
            await sendLock.WaitAsync();
            staleRemoval = UnitedFriendSystem.SendFriendDeletedAsync(a.Session, "乙", sessions);
            a.Session.Player.UserId = 201;
            sendLock.Release(); await staleRemoval;
            Check("queued removal cannot clear a newly selected character's list", a.Drain().Count == 0);
            a.Session.Player.UserId = 101;

            Execute("CREATE TRIGGER fail_friend_insert BEFORE INSERT ON united_friend_relations BEGIN SELECT RAISE(ABORT,'test insert rollback'); END;");
            await Add(a, "乙");
            Check("failed insert leaves DB/memory empty without success projection", a.Drain().Single()[15] == 0
                && b.Drain().Count == 0 && EmptyPair() && Repository.LoadAll().Count == 0);
            Execute("DROP TRIGGER fail_friend_insert;");

            await Add(a, "离线");
            var offline = a.Drain();
            Check("existing offline character can be added without confirmation", UnitedFriendSystem.IsFriend("甲", "离线")
                && !UnitedFriendSystem.IsFriend("离线", "甲") && HasFriendList(offline, "离线") && !offline.Any(IsIdentity)
                && offline.Single(p => p[0] == 0)[34] == 50);
            await Delete(a, "离线"); a.Drain();

            for (int i = 0; i < UnitedFriendSystem.MaximumFriends; i++) UnitedFriendSystem.RecordFriendship("乙", "full-" + i);
            await Add(a, "乙");
            Check("target capacity does not limit owner's unilateral ADD", HasFriendList(a.Drain(), "乙") && b.Drain().Count == 0 && UnitedFriendSystem.IsFriend("甲", "乙"));
            await Add(b, "甲");
            Check("owner capacity rejects new edge", b.Drain().Single()[16] == 4 && !UnitedFriendSystem.IsFriend("乙", "甲"));
            for (int i = 0; i < UnitedFriendSystem.MaximumFriends; i++) UnitedFriendSystem.RemoveFriendship("乙", "full-" + i);
            await Delete(a, "乙"); a.Drain();

            var gate = await transitions.AcquireAsync(101);
            var waiting = Add(a, "乙");
            using (var replacement = await FriendPeer.Create(sessions, 101, "甲"))
            {
                gate.Dispose(); await waiting;
                Check("reconnect rejects queued old-session ADD", EmptyPair() && replacement.Drain().Count == 0 && a.Drain().Count == 0);
            }
            sessions.Register(101, a.Session);
            await Add(a, "旁观者");
            var cross = a.Drain();
            Check("cross-channel ADD refreshes owner without foreign UID or target notification", HasFriendList(cross, "旁观者")
                && !cross.Any(IsIdentity) && outsider.Drain().Count == 0 && !UnitedFriendSystem.IsFriend("旁观者", "甲"));
            await Add(a, "乙");
            Check("adding another friend preserves every existing list entry", HasFriendList(a.Drain(), "乙", "旁观者"));
            await Delete(a, "乙");
            var remaining = a.Drain();
            Check("deleting one friend preserves other entries and clears only the named entity", HasFriendList(remaining, "旁观者")
                && DeleteBeforeFullList(remaining, "乙"));
            await Delete(a, "旁观者"); a.Drain();

            a.Session.Stream.Dispose();
            await Add(a, "乙");
            Check("failed owner socket does not undo durable direct ADD", UnitedFriendSystem.IsFriend("甲", "乙")
                && !UnitedFriendSystem.IsFriend("乙", "甲") && Repository.LoadAll()["甲"].Contains("乙") && b.Drain().Count == 0);
            using var freshA = await FriendPeer.Create(sessions, 101, "甲");
            await UnitedFriendSystem.SendFriendListAsync(freshA.Session, sessions, UnitedFriendSystem.GetFriends("甲"));
            Check("reconnected owner recovers committed list after failed publication", HasFriendList(freshA.Drain(), "乙"));
        }

        private static byte[] FriendAddBody(string name)
        {
            var writer = new GamePacketWriter(); writer.WriteUInt16(0xFFFF); writer.WriteBytes(FriendNameBody(name));
            return writer.ToArray();
        }

        private static byte[] FriendNameBody(string name)
        {
            var writer = new GamePacketWriter(); writer.WriteByte(1); writer.WriteClientDstr(name); return writer.ToArray();
        }

        private static bool IsIdentity(byte[] packet)
            => packet[0] == 0 && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.USERINFO;

        private static bool ListBeforeIdentity(List<byte[]> packets)
            => packets.FindIndex(p => p[0] == 0 && BitConverter.ToUInt16(p, 1) == (ushort)NotiPacketTypeA21.UNITED_SERVER_FRIEND_INFO)
                < packets.FindIndex(IsIdentity) && packets.Any(IsIdentity);

        private static bool HasFriendList(IEnumerable<byte[]> packets, params string[] expected)
        {
            var lists = packets.Where(p => IsFriendSubcommand(p, 0)).ToArray();
            if (lists.Length != 1) return false;
            using var stream = new MemoryStream(lists[0], 15, lists[0].Length - 15);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != 0 || reader.ReadInt32() != expected.Length) return false;
            foreach (string name in expected)
            {
                if (reader.ReadByte() != 1) return false;
                reader.ReadUInt16();
                int length = reader.ReadInt32();
                if (ClientTextEncoding.GetString(reader.ReadBytes(length)) != name) return false;
                reader.ReadByte(); reader.ReadUInt32(); reader.ReadByte();
                if (reader.ReadByte() != 0 || reader.ReadInt32() != 0) return false;
            }
            return stream.Position == stream.Length;
        }

        private static bool IsFriendSubcommand(byte[] packet, int subcommand)
            => packet[0] == 0 && BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.UNITED_SERVER_FRIEND_INFO
                && packet.Length >= 23 && BitConverter.ToInt32(packet, 15) == subcommand;

        private static bool DeleteBeforeFullList(List<byte[]> packets, string name)
        {
            var removal = packets.FindIndex(p => IsFriendSubcommand(p, 2));
            var full = packets.FindIndex(p => IsFriendSubcommand(p, 0));
            var writer = new GamePacketWriter();
            writer.WriteInt32(2); writer.WriteUInt32(1); writer.WriteBytes(FriendNameBody(name));
            return removal > 0 && full > removal && packets[0][0] == 1
                && packets[0].AsSpan(15).SequenceEqual(new byte[] { 1 })
                && packets[removal].AsSpan(15).SequenceEqual(writer.ToArray());
        }

        private sealed class FriendPeer : IDisposable
        {
            private readonly TcpClient _reader;
            internal EnhancedClientSession Session { get; }
            private FriendPeer(TcpClient reader, EnhancedClientSession session) { _reader = reader; Session = session; }
            internal static async Task<FriendPeer> Create(SessionDirectory sessions, int id, string name, int port = 10011)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                var reader = new TcpClient { ReceiveTimeout = 3000 }; TcpClient writer;
                try
                {
                    var connecting = reader.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    writer = await listener.AcceptTcpClientAsync(); await connecting;
                }
                finally { listener.Stop(); }
                var session = new EnhancedClientSession(writer, new GamePacketHeader(), port);
                session.Player.CharacterId = id; session.Player.UserId = (ushort)id; session.Player.Name = ClientTextEncoding.GetBytes(name);
                session.Player.Level = 20;
                session.Player.AppearanceEntries = new[] { new CharacterAppearanceEntry(0, 54601, 4, new byte[4], 0, 0, 0, 0) };
                sessions.Register(id, session);
                return new FriendPeer(reader, session);
            }
            internal List<byte[]> Drain()
            {
                var packets = new List<byte[]>(); var stream = _reader.GetStream();
                while (_reader.Client.Poll(20000, SelectMode.SelectRead))
                {
                    if (_reader.Available == 0) throw new InvalidOperationException("friend test connection closed");
                    var header = new byte[15]; stream.ReadExactly(header); int length = BitConverter.ToInt32(header, 3);
                    if (length < 15 || length > 65536) throw new InvalidOperationException("invalid friend packet length");
                    var packet = new byte[length]; header.CopyTo(packet, 0); stream.ReadExactly(packet.AsSpan(15)); packets.Add(packet);
                }
                return packets;
            }
            public void Dispose() { Session.Close(); _reader.Dispose(); }
        }
    }
}
