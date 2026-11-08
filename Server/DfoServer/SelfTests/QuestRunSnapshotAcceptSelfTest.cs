using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // 回归: 进本后才接取的任务必须能推进本局掉落/击杀/清图。
    // 覆盖 QuestRunSnapshot 增量合并、快照过滤、以及 QuestTrigger 通道递减不下溢。
    internal static class QuestRunSnapshotAcceptSelfTest
    {
        private const int AccountId = 991951;
        private const int CharacterId = 991952;
        private const ushort InDungeonQuestId = 2683;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_RUN_SNAPSHOT_ACCEPT selftest ===");
            var failures = 0;
            var path = Path.Combine(
                Path.GetTempPath(),
                $"s4a21-quest-run-snapshot-{Guid.NewGuid():N}.db");
            try
            {
                VerifySnapshotMerge(ref failures);
                VerifyFilterEligibility(ref failures);
                VerifyTriggerDecrement(ref failures);

                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                SeedCharacter(database);
                VerifyDungeonAcceptance(database, ref failures);
                VerifyAcceptEntryPoint(database, ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] quest run snapshot check threw: {ex}");
                failures++;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                {
                    try
                    {
                        if (File.Exists(file))
                            File.Delete(file);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            Console.WriteLine(failures == 0
                ? "QUEST_RUN_SNAPSHOT_ACCEPT selftest passed."
                : $"QUEST_RUN_SNAPSHOT_ACCEPT selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static ActiveQuest Quest(ushort questId, string activation)
        {
            QuestActivationId.TryParse(activation, out var activationId);
            return new ActiveQuest
            {
                QuestId = questId,
                Slot = 1,
                TriggerValue = 3,
                Version = 7,
                ActivationId = activationId,
            };
        }

        private static void VerifySnapshotMerge(ref int failures)
        {
            var first = Quest(101, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var second = Quest(202, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var captured = QuestRunSnapshot.Capture(new[] { first });

            Check("capture keeps the dungeon-entry quest",
                captured.Contains(101) && !captured.Contains(202),
                ref failures);

            var merged = captured.WithQuest(second);
            Check("with-quest adds the post-entry accept without dropping existing ones",
                merged.Contains(101) && merged.Contains(202) && merged.Count == 2,
                ref failures);
            Check("with-quest does not mutate the captured snapshot",
                !captured.Contains(202) && captured.Count == 1,
                ref failures);

            var reactivated = Quest(
                101, "cccccccccccccccccccccccccccccccc");
            var replaced = merged.WithQuest(reactivated);
            Check("with-quest replaces a stale activation for the same quest",
                replaced.TryGet(101, out var entry)
                && entry.ActivationId.Equals(reactivated.ActivationId)
                && replaced.Count == 2,
                ref failures);
            Check("with-quest ignores an empty quest",
                ReferenceEquals(replaced.WithQuest(null), replaced)
                && ReferenceEquals(replaced.WithQuest(new ActiveQuest()), replaced),
                ref failures);
        }

        private static void VerifyFilterEligibility(ref int failures)
        {
            var entered = Quest(101, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var acceptedInside = Quest(202, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var snapshot = QuestRunSnapshot.Capture(new[] { entered });

            Check("quest accepted inside the run is filtered out by the stale snapshot",
                QuestDropService.FilterEligibleQuestActivations(
                    snapshot,
                    new[] { acceptedInside }) == null,
                ref failures);

            var merged = snapshot.WithQuest(acceptedInside);
            var eligible = QuestDropService.FilterEligibleQuestActivations(
                merged,
                new[] { entered, acceptedInside });
            Check("quest accepted inside the run becomes eligible after the merge",
                eligible != null && eligible.Contains(202) && eligible.Contains(101),
                ref failures);

            var staleActivation = Quest(202, "dddddddddddddddddddddddddddddddd");
            Check("activation mismatch still filters the quest out",
                QuestDropService.FilterEligibleQuestActivations(
                    merged,
                    new[] { staleActivation }) == null,
                ref failures);

            // 每次掉落判定都会过滤一遍: 同一局同一任务同一原因只能记一条日志。
            var run = new DungeonRun(243, 0)
            {
                QuestSnapshot = QuestRunSnapshot.Capture(new[] { entered }),
            };
            var firstLog = run.TryMarkQuestDropFilterLogged(
                acceptedInside.QuestId, "not in run snapshot");
            var secondLog = run.TryMarkQuestDropFilterLogged(
                acceptedInside.QuestId, "not in run snapshot");
            Check("repeated snapshot-filter logging is deduplicated per run",
                firstLog && !secondLog, ref failures);
            Check("a different filter reason still logs once",
                run.TryMarkQuestDropFilterLogged(
                    acceptedInside.QuestId, "activation mismatch"),
                ref failures);
        }

        private static void VerifyTriggerDecrement(ref int failures)
        {
            // 512 = channel1:1, channel0:0。整包 -1 会借位成 511,
            // channel0 变成 511, 任务永久无法完成。
            var borrowed = new QuestTrigger(512).ApplyClientMutation(0, false);
            Check("packed decrement does not borrow across trigger channels",
                borrowed.PackedValue == 512
                && borrowed.GetChannel(0) == 0
                && borrowed.GetChannel(1) == 1,
                ref failures);

            var normal = new QuestTrigger(5).ApplyClientMutation(0, false);
            Check("packed decrement still reduces the occupied channel",
                normal.GetChannel(0) == 4,
                ref failures);

            var saturated = new QuestTrigger(0).ApplyClientMutation(0, false);
            Check("packed decrement stops at zero",
                saturated.PackedValue == 0,
                ref failures);

            var incremented = new QuestTrigger(512).ApplyClientMutation(0, true);
            Check("packed increment keeps the existing addition semantics",
                incremented.PackedValue == 513,
                ref failures);
        }

        private static void SeedCharacter(GameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'quest-run-snapshot-test', '');
INSERT INTO characters (character_id, account_id, name, job, level)
VALUES (@cid, @aid, 'quest-run-snapshot', 0, 86);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }

                // 只满足「完成前置任务」这一类, 让接取走到真实事务;
                // 不伪造客户端答案, 也不放宽任何校验。
                var definition = QuestPrerequisiteCatalog.Get(InDungeonQuestId);
                if (definition != null && definition.IsValid)
                {
                    foreach (var group in definition.CompletedQuestGroups)
                    {
                        if (group == null || group.Length == 0)
                            continue;
                        foreach (var prerequisiteQuestId in group)
                        {
                            QuestRepository.MarkQuestCleared(
                                connection,
                                transaction,
                                CharacterId,
                                (ushort)prerequisiteQuestId);
                        }
                        break;
                    }
                }
                transaction.Commit();
            }
        }

        private static void VerifyDungeonAcceptance(
            GameDatabase database,
            ref int failures)
        {
            var sessionId = Guid.NewGuid();
            InventoryService inventory;
            using (var connection = database.OpenConnection())
            {
                inventory = InventoryService.LoadFromDb(
                    connection, CharacterId, AccountId, database);
            }
            var lease = InventoryContext.Register(sessionId, CharacterId, inventory);
            try
            {
                var owner = new QuestCommandOwnerContext(
                    CharacterId, AccountId, sessionId, lease);
                var result = new QuestAcceptanceApplicationService(
                        database.ConnectionString)
                    .Apply(owner, new QuestAcceptCommand(InDungeonQuestId));
                Check($"PVF quest {InDungeonQuestId} accepts for the fixture character",
                    result.Success, ref failures);
                if (!result.Success)
                {
                    Console.WriteLine(
                        $"  accept error code=0x{result.ErrorCode:X2}");
                    return;
                }

                Check("accept reports the activation written by the transaction",
                    result.ActivationId.IsValid, ref failures);

                var reloaded = new QuestRepository(database.ConnectionString)
                    .LoadActiveQuests(CharacterId);
                ActiveQuest accepted = null;
                foreach (var quest in reloaded)
                {
                    if (quest != null && quest.QuestId == InDungeonQuestId)
                        accepted = quest;
                }
                Check("reported activation matches the persisted active quest",
                    accepted != null
                    && accepted.ActivationId.Equals(result.ActivationId),
                    ref failures);
                if (accepted == null)
                    return;

                // 进本时快照先建立(此时任务尚未接取)。
                var run = new DungeonRun(243, 0)
                {
                    QuestSnapshot = QuestRunSnapshot.Capture(
                        Array.Empty<ActiveQuest>()),
                };
                Check("run snapshot captured before the accept has no entry",
                    !run.QuestSnapshot.Contains(InDungeonQuestId),
                    ref failures);
                Check("drop filter sees no eligible quest before the merge",
                    QuestDropService.FilterEligibleQuestActivations(
                        run.QuestSnapshot,
                        reloaded) == null,
                    ref failures);

                run.MergeAcceptedQuestIntoSnapshot(accepted);
                Check("merging the accepted quest updates this run snapshot",
                    run.QuestSnapshot.TryGet(
                        InDungeonQuestId,
                        out var entry)
                    && entry.ActivationId.Equals(accepted.ActivationId),
                    ref failures);

                var eligible = QuestDropService.FilterEligibleQuestActivations(
                    run.QuestSnapshot,
                    reloaded);
                Check("drop filter accepts the quest after the in-run merge",
                    eligible != null && eligible.Contains(InDungeonQuestId),
                    ref failures);

                var otherRun = new DungeonRun(243, 0)
                {
                    QuestSnapshot = QuestRunSnapshot.Capture(
                        Array.Empty<ActiveQuest>()),
                };
                Check("another run does not inherit the in-run accept",
                    !otherRun.QuestSnapshot.Contains(InDungeonQuestId)
                    && QuestDropService.FilterEligibleQuestActivations(
                        otherRun.QuestSnapshot,
                        reloaded) == null,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
            }
        }

        // 直接驱动真实的 ACCEPT 入口: 覆盖 QuestManager 里「先合并再回包」的接线,
        // 以及进本前接取(无 run)与重复接取两个边界。
        private static void VerifyAcceptEntryPoint(
            GameDatabase database,
            ref int failures)
        {
            const int characterId = CharacterId + 1;
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job, level)
VALUES (@cid, @aid, 'quest-run-snapshot-entry', 0, 86);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", AccountId);
                command.ExecuteNonQuery();
            }
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var definition = QuestPrerequisiteCatalog.Get(InDungeonQuestId);
                if (definition != null && definition.IsValid)
                {
                    foreach (var group in definition.CompletedQuestGroups)
                    {
                        if (group == null || group.Length == 0)
                            continue;
                        foreach (var prerequisiteQuestId in group)
                        {
                            QuestRepository.MarkQuestCleared(
                                connection,
                                transaction,
                                characterId,
                                (ushort)prerequisiteQuestId);
                        }
                        break;
                    }
                }
                transaction.Commit();
            }

            var sessionId = Guid.NewGuid();
            InventoryService inventory;
            using (var connection = database.OpenConnection())
            {
                inventory = InventoryService.LoadFromDb(
                    connection, characterId, AccountId, database);
            }
            InventoryContext.Register(sessionId, characterId, inventory);
            var sender = new RecordingSender(characterId);
            try
            {
                var manager = new QuestManager(sender, database.ConnectionString);
                var body = new byte[]
                {
                    (byte)(InDungeonQuestId & 0xFF),
                    (byte)(InDungeonQuestId >> 8),
                    (byte)(InDungeonQuestId & 0xFF),
                    (byte)(InDungeonQuestId >> 8),
                };

                // 1) 城镇接取(无 run): 应当成功, 且不因缺少 run 而出错。
                manager.HandleAcceptQuestAsync(
                        (ushort)CmdPacketTypeA21.ACCEPT_QUEST,
                        body,
                        sessionId)
                    .GetAwaiter()
                    .GetResult();
                Check("accept in town succeeds without a dungeon run",
                    sender.AckCount > 0
                    && new QuestRepository(database.ConnectionString)
                        .LoadActiveQuests(characterId).Exists(
                            quest => quest.QuestId == InDungeonQuestId),
                    ref failures);

                // 2) 进本后在副本内接取: 必须并入该 run 的快照。
                var repository = new QuestRepository(database.ConnectionString);
                var townAccepted = repository.LoadActiveQuests(characterId)
                    .Find(quest => quest.QuestId == InDungeonQuestId);
                repository.DeleteActiveQuest(
                    characterId,
                    townAccepted.Slot,
                    townAccepted.ActivationId);

                var run = new DungeonRun(243, 0);
                sender.Player.CurrentRun = run;
                manager.HandleAcceptQuestAsync(
                        (ushort)CmdPacketTypeA21.ACCEPT_QUEST,
                        body,
                        sessionId)
                    .GetAwaiter()
                    .GetResult();
                var accepted = new QuestRepository(database.ConnectionString)
                    .LoadActiveQuests(characterId)
                    .Find(quest => quest.QuestId == InDungeonQuestId);
                Check("accept entry point merges the quest into the current run",
                    accepted != null
                    && run.QuestSnapshot.TryGet(InDungeonQuestId, out var entry)
                    && entry.ActivationId.Equals(accepted.ActivationId),
                    ref failures);

                // 3) 重复接取: 拒绝且不得改动已并入的快照。
                var snapshotBefore = run.QuestSnapshot;
                manager.HandleAcceptQuestAsync(
                        (ushort)CmdPacketTypeA21.ACCEPT_QUEST,
                        body,
                        sessionId)
                    .GetAwaiter()
                    .GetResult();
                Check("duplicate accept is rejected without disturbing the snapshot",
                    ReferenceEquals(run.QuestSnapshot, snapshotBefore)
                    && run.QuestSnapshot.Contains(InDungeonQuestId),
                    ref failures);

                // 4) 跨局隔离: 任务仍然接取着, 但换到新一局后新 run 是空的,
                //    在副本内重新接取(放弃重接)之前不得凭空可推进。
                var nextRun = new DungeonRun(243, 0);
                sender.Player.CurrentRun = nextRun;
                var stillActive = new QuestRepository(database.ConnectionString)
                    .LoadActiveQuests(characterId);
                Check("a new run does not inherit the previous run's snapshot",
                    !nextRun.QuestSnapshot.Contains(InDungeonQuestId)
                    && stillActive.Exists(
                        quest => quest.QuestId == InDungeonQuestId),
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, characterId);
            }
        }

        private sealed class RecordingSender : ISessionPacketSender
        {
            internal RecordingSender(int characterId)
            {
                CharacterId = characterId;
                Player.CharacterId = characterId;
            }

            public PlayerContext Player { get; } = new PlayerContext();
            public int CharacterId { get; }
            public int AccountId => QuestRunSnapshotAcceptSelfTest.AccountId;
            internal int AckCount { get; private set; }

            public Task SendPacketAsync(byte[] rawPacket)
                => Task.CompletedTask;

            public Task SendNotiAsync(ushort notiType, byte[] body)
                => Task.CompletedTask;

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                AckCount++;
                return Task.CompletedTask;
            }
        }
    }
}
