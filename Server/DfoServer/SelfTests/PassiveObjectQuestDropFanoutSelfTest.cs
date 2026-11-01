using DfoServer.Game.Characters;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.SelfTests
{
    // 被动物体任务掉落组队扇出回归：
    // 修复缺陷 - "收集零件"类任务（[enemy reward item] enemyType=3）此前只对
    // 上报者本人发放，队友拿不到物品。本套件走真实 HandleDieMonster 入口，
    // 用多个真实 loopback 会话 + 共享房间账本验证逐队员扇出。
    // 需要 PVF_ARCHIVE_PATH 与 INVENTORY_DATABASE_CONNECTION（MariaDB）。
    // 已知覆盖边界：队员侧 DungeonActorQuestSync 在当前 PVF 下没有可独立观测的
    // 服务端进度落点（寻物进度由掉落事务驱动、enemyType=3 的 hunt-enemy 为
    // 客户端驱动），队员侧验证以掉落事务 + 背包刷新通知为准。
    internal static class PassiveObjectQuestDropFanoutSelfTest
    {
        private static int _fixtureSeq;

        private const int QuestId = 2201;        // 收集零件（epic_61_supplycut_3）
        private const int DungeonId = 84;        // 补给线阻断战
        private const int ObjectCode = 8961;     // 小型发电机（MAP-owned 被动物体）
        private const int ItemId = 10149153;     // 损伤的机械零件
        private const int PartyId = 6;
        private const int InventoryRefreshNoti = 0x000E;

        public static int Run()
        {
            var failures = 0;

            // 未配置 PVF 时按既有套件风格跳过；配置了但 PVF 里没有该任务
            // 或没有掉落候选则判失败，防止拿错 PVF 静默通过。
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH")))
            {
                Console.WriteLine(
                    "passive object quest drop fanout skipped: " +
                    "PVF_ARCHIVE_PATH is not set");
                return 0;
            }

            var quest = QuestData.GetQuestFile(QuestId);
            var candidates = quest == null
                ? null
                : QuestDropProvider.CheckEnemyDrop(
                    new List<int> { QuestId },
                    DungeonId,
                    difficulty: 0,
                    enemyCode: ObjectCode,
                    QuestDropProvider.EnemyTypePassiveObject);
            if (quest == null
                || candidates == null
                || candidates.Count == 0
                || candidates.Any(candidate => candidate.ItemId != ItemId))
            {
                Console.WriteLine(
                    $"passive object quest drop fanout FAILED: quest {QuestId} " +
                    "missing or no passive drop candidate in current PVF");
                return 1;
            }

            // master 基线：SQLite 临时库，item_schema.sql 建完整库；
            // quest_progress_event_inbox 不在 schema v1 内，按 QuestRepository
            // 运行时形态在测试内补建。
            var dbPath = Path.Combine(
                Path.GetTempPath(),
                $"passive-object-fanout-{Guid.NewGuid():N}.db");
            var db = new GameDatabase(dbPath, ServerPaths.SchemaFilePath);
            var characters = new SqliteCharacterRepository(db);
            var dataSource = new SqliteSelectCharacterDataSource(db, characters);
            var refresh = new InventoryRefreshSender(dataSource, characters, db);
            var questDrops = new QuestDropService(
                refresh,
                connectionString: null,
                rollDrop: (candidate, held) => Math.Max(1, candidate.Count),
                itemAcquisition: null,
                database: db,
                rollQuestAssistantBonus: () => 0);
            var registry = new DungeonInstanceRegistry();
            var sessions = new SessionDirectory();
            var services = new DungeonSharedServices(
                new ReviveCoinService(new DailyResetService(db)),
                characters,
                dataSource,
                rentalTimeProvider: null,
                connectionString: null,
                inventoryRefresh: refresh,
                partyManager: null,
                sessions: sessions,
                questDropService: questDrops,
                instanceRegistry: registry,
                database: db);
            var settlement = new DungeonSettlementHandler(services, entry: null);
            var tournament = new TournamentDungeonCoordinator(services, settlement);
            var bloodAltar = new BloodAltarDungeonCoordinator(services, settlement);
            var handler = new DungeonCombatHandler(
                services,
                settlement,
                tournament,
                bloodAltar);

            // schema v1 不含 quest_progress_event_inbox，按 QuestRepository
            // 运行时形态在测试内补建。
            db.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS quest_progress_event_inbox (
    character_id INTEGER NOT NULL, activation_id TEXT NOT NULL,
    event_id TEXT NOT NULL, event_kind TEXT NOT NULL,
    PRIMARY KEY (character_id, activation_id, event_id, event_kind));";
                command.ExecuteNonQuery();
            });

            try
            {
                RunPartyScenario(db, sessions, registry, questDrops, handler, ref failures);
                RunSingleInstanceDedupeScenario(
                    db, sessions, registry, questDrops, handler, ref failures);
                RunConcurrentReportScenario(
                    db, sessions, registry, questDrops, handler, ref failures);
                RunSoloRegressionScenario(
                    db, sessions, registry, questDrops, handler, ref failures);
                RunRoomIsolationScenario(
                    db, sessions, registry, questDrops, handler, ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"passive object quest drop fanout failed: {ex}");
                failures++;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(dbPath + suffix))
                        File.Delete(dbPath + suffix);
            }

            if (failures == 0)
                Console.WriteLine("passive object quest drop fanout: all checks passed");
            return failures == 0 ? 0 : 1;
        }

        private sealed class Member : IDisposable
        {
            public int CharacterId;
            public EnhancedClientSession Session;
            public DungeonRun Run;
            public InventoryService Inventory;
            public TcpClient ClientSide;
            public TcpListener Listener;
            public uint InitialTrigger;

            public int Held => Inventory.CountMainItem(ItemId);

            public void Dispose()
            {
                if (Session != null)
                    InventoryContext.Unregister(Session.SessionId, CharacterId);
                Session?.TcpClient?.Dispose();
                ClientSide?.Dispose();
                Listener?.Stop();
            }
        }

        private sealed class Fixture : IDisposable
        {
            public SessionDirectory Sessions;
            public DungeonInstanceRegistry Registry;
            public QuestDropService QuestDrops;
            public DungeonCombatHandler Handler;
            public DungeonInstance Instance;
            public List<Member> Members = new List<Member>();

            public Member Source => Members[0];

            public void Dispose()
            {
                foreach (var member in Members)
                {
                    try
                    {
                        // 排空待发批量通知，避免残留 one-shot 留在全局 Clock 队列。
                        QuestDrops?.FlushPendingNotificationsAsync(
                            member.Session).GetAwaiter().GetResult();
                    }
                    catch
                    {
                    }

                    try
                    {
                        Sessions?.UnregisterAsync(
                            member.CharacterId,
                            member.Session).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // 清理尽力而为；套件间字段互不共享。
                    }

                    try
                    {
                        Registry?.Terminate(
                            member.CharacterId,
                            member.Run.CaptureIdentity(),
                            "SelfTestFixtureDisposed");
                    }
                    catch
                    {
                    }

                    member.Dispose();
                }
            }
        }

        // ---- 场景 ----

        private static void RunPartyScenario(
            GameDatabase db,
            SessionDirectory sessions,
            DungeonInstanceRegistry registry,
            QuestDropService questDrops,
            DungeonCombatHandler handler,
            ref int failures)
        {
            // 三个队员：上报者与第二个队员都有 2201；第三个无任务（不误发）。
            using var fixture = CreateFixture(
                db, sessions, registry, questDrops, handler,
                new[] { true, true, false },
                objectInstanceCount: 10);

            Fire(fixture, fixture.Source, times: 10);

            Check("party: reporter collects all 10 parts",
                fixture.Source.Held == 10, ref failures);
            Check("party: questing teammate receives her own 10 parts (fanout)",
                fixture.Members[1].Held == 10, ref failures);
            Check("party: member without the quest receives nothing",
                fixture.Members[2].Held == 0, ref failures);

            var expectedTrigger = QuestData.ReplaceTriggerChannel(
                fixture.Source.InitialTrigger, 0, 0);
            Check("party: reporter seeking trigger fully collected (channel0=0)",
                ReadTrigger(db, fixture.Source.CharacterId) == expectedTrigger,
                ref failures);
            Check("party: teammate seeking trigger fully collected (channel0=0)",
                ReadTrigger(db, fixture.Members[1].CharacterId) == expectedTrigger,
                ref failures);
            Check("party: no quest-progress rows for the questless member",
                CountInboxRows(db, fixture.Members[2].CharacterId) == 0,
                ref failures);

            FlushMemberNotifications(fixture);

            Check("party: inventory refresh notification reached the reporter",
                ContainsNotification(fixture.Source, InventoryRefreshNoti),
                ref failures);
            Check("party: inventory refresh notification reached the teammate (fanout)",
                ContainsNotification(fixture.Members[1], InventoryRefreshNoti),
                ref failures);
        }

        private static void RunSingleInstanceDedupeScenario(
            GameDatabase db,
            SessionDirectory sessions,
            DungeonInstanceRegistry registry,
            QuestDropService questDrops,
            DungeonCombatHandler handler,
            ref int failures)
        {
            using var fixture = CreateFixture(
                db, sessions, registry, questDrops, handler,
                new[] { true, true },
                objectInstanceCount: 1);

            Fire(fixture, fixture.Source, times: 1);
            Check("single instance: one accepted report grants both members once",
                fixture.Source.Held == 1 && fixture.Members[1].Held == 1,
                ref failures);

            Fire(fixture, fixture.Source, times: 1);
            Check("single instance: exhausted code is deduped (no extra grants)",
                fixture.Source.Held == 1 && fixture.Members[1].Held == 1,
                ref failures);
        }

        private static void RunConcurrentReportScenario(
            GameDatabase db,
            SessionDirectory sessions,
            DungeonInstanceRegistry registry,
            QuestDropService questDrops,
            DungeonCombatHandler handler,
            ref int failures)
        {
            using var fixture = CreateFixture(
                db, sessions, registry, questDrops, handler,
                new[] { true, true },
                objectInstanceCount: 1);

            // 两个客户端同时上报同一单实例：账本只接受一个，双方合计各 1。
            var barrier = new Barrier(2);
            Task HandleDieFrom(Member member) => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return fixture.Handler.HandleDieMonster(
                    member.Session,
                    new GamePacketHeader(),
                    BuildDieMonsterBody(ObjectCode));
            });
            var first = HandleDieFrom(fixture.Source);
            var second = HandleDieFrom(fixture.Members[1]);
            Task.WhenAll(first, second).GetAwaiter().GetResult();

            Check("concurrent reports: exactly one report wins, no double grants",
                fixture.Source.Held == 1 && fixture.Members[1].Held == 1,
                ref failures);
        }

        private static void RunSoloRegressionScenario(
            GameDatabase db,
            SessionDirectory sessions,
            DungeonInstanceRegistry registry,
            QuestDropService questDrops,
            DungeonCombatHandler handler,
            ref int failures)
        {
            using var fixture = CreateFixture(
                db, sessions, registry, questDrops, handler,
                new[] { true },
                objectInstanceCount: 10);

            Fire(fixture, fixture.Source, times: 10);
            Check("solo: reporter still collects all 10 parts",
                fixture.Source.Held == 10, ref failures);
            Check("solo: seeking trigger fully collected",
                QuestData.GetTriggerChannel(
                    ReadTrigger(db, fixture.Source.CharacterId), 0) == 0,
                ref failures);
        }

        private static void RunRoomIsolationScenario(
            GameDatabase db,
            SessionDirectory sessions,
            DungeonInstanceRegistry registry,
            QuestDropService questDrops,
            DungeonCombatHandler handler,
            ref int failures)
        {
            // 501 上报 + 有任务；502 有任务但在另一个房间（名册房间过滤，主防线）；
            // 503 有任务且同房间。2 次上报后 501/503 各 2，502 为 0。
            using var fixture = CreateFixture(
                db, sessions, registry, questDrops, handler,
                new[] { true, true, true },
                objectInstanceCount: 3);

            MoveToOtherRoom(fixture.Members[1]);

            Fire(fixture, fixture.Source, times: 2);
            Check("room isolation: reporter collects normally",
                fixture.Source.Held == 2, ref failures);
            Check("room isolation: same-room teammate collects (fanout)",
                fixture.Members[2].Held == 2, ref failures);
            Check("room isolation: other-room teammate excluded from the roster",
                fixture.Members[1].Held == 0, ref failures);

            // 二级防线：名册已捕获（含同房 503）后 503 才换房 →
            // 直接断言扇出资格守卫对旧名册条目返回 false（守卫被移除时这两条会红；
            // 下游 QuestDropService.MatchesExpectedRun 是独立兜底，不参与本断言）。
            var roster = fixture.Registry.CaptureParticipantRoster(
                fixture.Source.Run.CaptureRoomIdentity(), partyId: 0);
            Check("room isolation: captured roster contains same-room teammate only",
                roster.Any(entry => entry.CharacterId == fixture.Members[2].CharacterId)
                && roster.All(entry => entry.CharacterId != fixture.Members[1].CharacterId),
                ref failures);
            var movedEntry = roster.Single(
                entry => entry.CharacterId == fixture.Members[2].CharacterId);
            Check("room isolation: same-room teammate is current before moving",
                DungeonCombatHandler.IsFanoutMemberCurrent(
                    fixture.Members[2].Run, movedEntry),
                ref failures);
            MoveToOtherRoom(fixture.Members[2]);
            Check("room isolation: moved teammate fails the fanout currency guard",
                !DungeonCombatHandler.IsFanoutMemberCurrent(
                    fixture.Members[2].Run, movedEntry),
                ref failures);

            // 守卫条件敏觉性：ReferenceEquals 与 RunIdentity 两条防线的直接负例。
            var twinRun = new DungeonRun(
                fixture.Instance,
                movedEntry.RunIdentity.RunId,
                movedEntry.RunIdentity.RunGeneration,
                DungeonRunState.Active);
            twinRun.SetCurrentRoom(fixture.Instance.GetOrCreateRoom(
                new RoomKey(0, 0, 0),
                id => new DungeonInstanceRoom(
                    id, new RoomKey(0, 0, 0),
                    new GameWorld.Dungeon.MazeSumInfo { Index = 1 }, 1),
                out _));
            Check("guard sensitivity: distinct run object with identical identity fails",
                !ReferenceEquals(twinRun, movedEntry.Run)
                && !DungeonCombatHandler.IsFanoutMemberCurrent(twinRun, movedEntry),
                ref failures);
            var staleIdentityEntry = new DungeonParticipantRosterEntry(
                movedEntry.CharacterId,
                movedEntry.ParticipantUserId,
                fixture.Members[2].Run,
                new DungeonRunIdentity(
                    movedEntry.RunIdentity.PartyDungeonInstanceId,
                    movedEntry.RunIdentity.RunId,
                    movedEntry.RunIdentity.RunGeneration + 1),
                // 房间用成员当前房间：保证本断言只隔离 RunIdentity 条件
                // （删除 RunIdentity 检查后此断言必须变红）。
                fixture.Members[2].Run.CaptureRoomIdentity(),
                movedEntry.AttachmentGeneration);
            Check("guard sensitivity: stale run identity fails",
                !DungeonCombatHandler.IsFanoutMemberCurrent(
                    fixture.Members[2].Run, staleIdentityEntry),
                ref failures);
            handler.FanoutPassiveObjectDeathAsync(
                fixture.Source.Session,
                fixture.Source.Run,
                roster,
                BuildCanonicalEvent(fixture.Source),
                ObjectCode).GetAwaiter().GetResult();
            Check("room isolation: moved teammate skipped by identity guard (no extra grant)",
                fixture.Members[2].Held == 2, ref failures);
        }

        // ---- 装配 ----

        private static Fixture CreateFixture(
            GameDatabase db,
            SessionDirectory sessions,
            DungeonInstanceRegistry registry,
            QuestDropService questDrops,
            DungeonCombatHandler handler,
            bool[] memberHasQuest,
            int objectInstanceCount)
        {
            var fixture = new Fixture
            {
                Sessions = sessions,
                Registry = registry,
                QuestDrops = questDrops,
                Handler = handler,
                Instance = new DungeonInstance(DungeonId, 0),
            };

            try
            {
            var accountId = 910000 + (++_fixtureSeq) * 100;
            InsertAccount(db, accountId, $"fanout-a{accountId}");
            for (var index = 0; index < memberHasQuest.Length; index++)
            {
                var characterId = accountId + index + 1;
                InsertCharacter(db, characterId, accountId, $"fanout-c{characterId}");

                var run = BuildRun(fixture.Instance, 9000 + characterId, objectInstanceCount);
                var member = AttachSession(
                    fixture, db, sessions, registry, characterId, accountId, run);

                if (!memberHasQuest[index])
                    continue;

                var activation = QuestActivationId.New();
                member.InitialTrigger = QuestData.GetInitTrigger(QuestId);
                run.QuestSnapshot = QuestRunSnapshot.Capture(new[]
                {
                    new ActiveQuest
                    {
                        Slot = index,
                        QuestId = (ushort)QuestId,
                        TriggerValue = member.InitialTrigger,
                        Version = 0,
                        ActivationId = activation,
                    },
                });
                InsertActiveQuest(
                    db, characterId, index, QuestId, member.InitialTrigger, activation);
            }

            return fixture;
            }
            catch
            {
                // 半成品 fixture 也要释放已建立的连接与租约。
                fixture.Dispose();
                throw;
            }
        }

        private static DungeonRun BuildRun(
            DungeonInstance instance,
            long runId,
            int objectInstanceCount)
        {
            var run = new DungeonRun(
                instance,
                runId,
                runId % 1000 + 1,
                DungeonRunState.Active);
            var roomKey = new RoomKey(0, 0, 0);
            var maze = new GameWorld.Dungeon.MazeSumInfo
            {
                Index = 1,
                PassiveObjectCodes = Enumerable
                    .Repeat(ObjectCode, objectInstanceCount)
                    .ToArray(),
            };
            var room = instance.GetOrCreateRoom(
                roomKey,
                id => new DungeonInstanceRoom(id, roomKey, maze, 1),
                out _);
            run.RoomKey = roomKey;
            run.SetCurrentRoom(room);
            run.RoomStates = new Dictionary<RoomKey, RoomState>
            {
                [roomKey] = new RoomState
                {
                    InstanceRoom = room,
                    Maze = maze,
                },
            };
            return run;
        }

        // 资源一旦创建立即挂到 fixture.Members（所有权先移交），
        // 任一步骤抛异常时由 fixture.Dispose 统一回收，不留泄漏窗口。
        private static Member AttachSession(
            Fixture fixture,
            GameDatabase db,
            SessionDirectory sessions,
            DungeonInstanceRegistry registry,
            int characterId,
            int accountId,
            DungeonRun run)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var member = new Member
            {
                CharacterId = characterId,
                Listener = listener,
                Run = run,
            };
            // 所有权先移交 fixture.Members：任一步骤抛异常时由
            // CreateFixture 的 fixture.Dispose 统一回收，不提前移除。
            fixture.Members.Add(member);

            var clientSide = new TcpClient();
            member.ClientSide = clientSide;
            var connect = clientSide.ConnectAsync(
                IPAddress.Loopback,
                ((IPEndPoint)listener.LocalEndpoint).Port);
            var serverSide = listener.AcceptTcpClient();
            connect.GetAwaiter().GetResult();

            var session = new EnhancedClientSession(serverSide, new GamePacketHeader());
            member.Session = session;
            session.Player.CharacterId = characterId;
            session.Player.UserId = (ushort)characterId;
            session.Player.CurrentRun = run;
            session.GameSession = new GameSession(session, db);

            var inventory = new InventoryService(characterId, accountId);
            member.Inventory = inventory;
            InventoryContext.Register(session.SessionId, characterId, inventory);

            sessions.Register(characterId, session);
            registry.RegisterActive(new DungeonParticipantRegistration(
                accountId,
                characterId,
                (ushort)characterId,
                PartyId,
                session.SessionId,
                run));

            return member;
        }

        private static void MoveToOtherRoom(Member member)
        {
            var otherRoomKey = new RoomKey(5, 5, 0);
            var maze = new GameWorld.Dungeon.MazeSumInfo { Index = 9 };
            var room = member.Run.Instance.GetOrCreateRoom(
                otherRoomKey,
                id => new DungeonInstanceRoom(id, otherRoomKey, maze, 1),
                out _);
            member.Run.RoomKey = otherRoomKey;
            member.Run.SetCurrentRoom(room);
            member.Run.RoomStates = new Dictionary<RoomKey, RoomState>
            {
                [otherRoomKey] = new RoomState
                {
                    InstanceRoom = room,
                    Maze = maze,
                },
            };
        }

        // ---- 行为 ----

        private static void Fire(Fixture fixture, Member member, int times)
        {
            for (var index = 0; index < times; index++)
            {
                fixture.Handler.HandleDieMonster(
                    member.Session,
                    new GamePacketHeader(),
                    BuildDieMonsterBody(ObjectCode)).GetAwaiter().GetResult();
            }
        }

        private static DungeonEventEnvelope BuildCanonicalEvent(Member source)
        {
            var identity = source.Run.CaptureRoomIdentity();
            return new DungeonEventEnvelope(
                Guid.NewGuid(),
                source.Run.CaptureIdentity(),
                identity.RoomInstanceId,
                source.CharacterId,
                source.CharacterId,
                0,
                ObjectCode,
                "passive-object-destroyed",
                DateTime.UtcNow.Ticks);
        }

        private static byte[] BuildDieMonsterBody(ushort code)
        {
            // 与客户端地图物体变体报文一致：code, userId=0, 0xFFFF 哨兵, 其余 0。
            var body = new byte[32];
            BitConverter.GetBytes(code).CopyTo(body, 0);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 4);
            return body;
        }

        // ---- DB / 断言辅助 ----

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            Console.WriteLine($"[FAIL] {name}");
            failures++;
        }

        private static uint ReadTrigger(GameDatabase db, int characterId)
        {
            using var connection = db.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT trigger_value FROM character_active_quests " +
                "WHERE character_id=@cid AND quest_id=@qid;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@qid", QuestId);
            var value = command.ExecuteScalar();
            return value == null || value is DBNull
                ? uint.MaxValue
                : Convert.ToUInt32(value);
        }

        private static int CountInboxRows(GameDatabase db, int characterId)
        {
            using var connection = db.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM quest_progress_event_inbox " +
                "WHERE character_id=@cid;";
            command.Parameters.AddWithValue("@cid", characterId);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static void InsertAccount(
            GameDatabase db,
            int accountId,
            string mid)
        {
            using var connection = db.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO accounts (account_id, m_id, password_hash, created_at) " +
                "VALUES (@aid, @mid, '', CURRENT_TIMESTAMP);";
            command.Parameters.AddWithValue("@aid", accountId);
            command.Parameters.AddWithValue("@mid", mid);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        private static void InsertCharacter(
            GameDatabase db,
            int characterId,
            int accountId,
            string name)
        {
            using var connection = db.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO characters (character_id, account_id, name, job, created_at, updated_at) " +
                "VALUES (@cid, @aid, @name, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@aid", accountId);
            command.Parameters.AddWithValue("@name", name);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        private static void InsertActiveQuest(
            GameDatabase db,
            int characterId,
            int slot,
            int questId,
            uint triggerValue,
            QuestActivationId activation)
        {
            using var connection = db.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO character_active_quests " +
                "(character_id, slot, quest_id, trigger_value, version, activation_id) " +
                "VALUES (@cid, @slot, @qid, @trigger, 0, @activation);";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@slot", slot);
            command.Parameters.AddWithValue("@qid", questId);
            command.Parameters.AddWithValue("@trigger", triggerValue);
            command.Parameters.AddWithValue("@activation", activation.ToStorageString());
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        // ---- 通知读取 ----

        // 批量通知不再依赖全局 Clock 定时器：逐成员显式排空待发队列后
        // 直接读取 loopback 流，规避跨套件的 Clock 状态污染。
        private static void FlushMemberNotifications(Fixture fixture)
        {
            foreach (var member in fixture.Members)
            {
                try
                {
                    fixture.QuestDrops.FlushPendingNotificationsAsync(
                        member.Session).GetAwaiter().GetResult();
                }
                catch
                {
                    // 排空失败交给 Dispose 再兜底；断言以实际读包为准。
                }
            }
        }

        private static bool ContainsNotification(Member member, ushort notiType)
        {
            var buffer = ReadAvailable(member.ClientSide, waitMs: 2000);
            return CollectPacketTypes(buffer).Contains(notiType);
        }

        private static byte[] ReadAvailable(TcpClient client, int waitMs)
        {
            using var stream = new MemoryStream();
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            var quiet = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline)
            {
                if (client.Available > 0)
                {
                    var chunk = new byte[client.Available];
                    var read = client.GetStream().Read(chunk, 0, chunk.Length);
                    stream.Write(chunk, 0, read);
                    quiet = DateTime.UtcNow;
                }
                else if (stream.Length > 0
                    && (DateTime.UtcNow - quiet).TotalMilliseconds > 400)
                {
                    break;
                }
                else
                {
                    Task.Delay(50).GetAwaiter().GetResult();
                }
            }

            return stream.ToArray();
        }

        // 封包格式：[flag u8][type u16][totalLen i32][body...]（见 PartyDeathMapMoveSelfTest）。
        private static HashSet<int> CollectPacketTypes(byte[] buffer)
        {
            var types = new HashSet<int>();
            var position = 0;
            while (position + 7 <= buffer.Length)
            {
                var totalLength = BitConverter.ToInt32(buffer, position + 3);
                if (totalLength < 7 || position + totalLength > buffer.Length)
                {
                    position++;
                    continue;
                }

                types.Add(BitConverter.ToUInt16(buffer, position + 1));
                position += totalLength;
            }

            return types;
        }
    }
}
