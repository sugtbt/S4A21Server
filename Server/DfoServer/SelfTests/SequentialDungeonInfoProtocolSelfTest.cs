using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    // CMD SEQUENTIAL_DUNGEON_INFO(0x035D) 应答逻辑的聚焦自测。
    // 背景: 客户端进入副本选择界面后会用 0x035D 询问当前区域的连续副本
    // 序列进度(body = int32 configKey, 镇魂/远古区域 key=26); 服务端未注册
    // 该 CMD 时客户端无限重发并卡死选择界面。服务端始终按请求的 key 应答
    // NOTI SEQUENTIAL_DUNGEON_INFO(0x025B)。
    public static class SequentialDungeonInfoProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== SEQUENTIAL_DUNGEON_INFO_PROTOCOL selftest ===");
            var failures = 0;

            VerifyEnumAnchors(ref failures);
            VerifyReplyBodyLayout(ref failures);
            VerifyProgressResolution(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "SEQUENTIAL_DUNGEON_INFO_PROTOCOL selftest passed."
                    : $"SEQUENTIAL_DUNGEON_INFO_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyEnumAnchors(ref int failures)
        {
            Check(
                "CMD SEQUENTIAL_DUNGEON_INFO is 0x035D",
                (ushort)CmdPacketTypeA21.SEQUENTIAL_DUNGEON_INFO == 0x035D,
                ref failures);
            Check(
                "NOTI SEQUENTIAL_DUNGEON_INFO is 0x025B",
                (ushort)NotiPacketTypeA21.SEQUENTIAL_DUNGEON_INFO == 0x025B,
                ref failures);
        }

        private static void VerifyReplyBodyLayout(ref int failures)
        {
            // 与既有主动推送同布局: int32 key + byte progress + int32 routeMask。
            var body = DungeonNotificationBuilder.BuildSequentialDungeonInfo(
                configKey: 26,
                progressIndex: 3,
                routeMask: 7);
            Check(
                "reply body is int32 key + byte progress + int32 routeMask",
                body.Length == 9
                && BitConverter.ToInt32(body, 0) == 26
                && body[4] == 3
                && BitConverter.ToInt32(body, 5) == 7,
                ref failures);
        }

        private static void VerifyProgressResolution(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath))
            {
                Console.WriteLine(
                    "real PVF sequence checks skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_sequential_dungeon_info_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(
                    tempDbPath,
                    ServerPaths.SchemaFilePath);
                var repository = new SqliteCharacterStateRepository(database);
                var notifier = new AntonNormalConquestNotifier(repository);
                const int accountId = 56000;
                const int characterId = 56001;
                SeedAccount(database, accountId, "sequential-info-a");
                SeedCharacter(database, characterId, accountId, "sequential-info-c");

                // 镇魂/远古区域(area key=26)不是安徒恩序列, 应答进度为 0。
                Check(
                    "area key 26 (ancient/镇魂) has no anton sequence",
                    !AntonNormalConquest.TryGetSequenceByKey(26, out _),
                    ref failures);
                Check(
                    "unknown sequence key answers progress 0",
                    notifier.ResolveSequentialProgress(characterId, 26) == 0,
                    ref failures);
                Check(
                    "invalid character id answers progress 0",
                    notifier.ResolveSequentialProgress(0, 26) == 0,
                    ref failures);

                if (!AntonNormalConquest.TryGetSequenceByKey(28, out var sequence))
                {
                    Check(
                        "anton normal sequence key=28 is loaded from PVF",
                        false,
                        ref failures);
                    return;
                }

                Check(
                    "known sequence without permissions answers progress 0",
                    notifier.ResolveSequentialProgress(
                        characterId, sequence.ConfigKey) == 0,
                    ref failures);

                // 只通关序列首个副本: 进度仍为 0(尚未解锁第二个副本之后的位置)。
                ApplyCompleted(repository, characterId, sequence, count: 1);
                Check(
                    "first dungeon completed answers progress 0",
                    notifier.ResolveSequentialProgress(
                        characterId, sequence.ConfigKey) == 0,
                    ref failures);

                // 全部通关: 进度推进到序列长度。
                ApplyCompleted(
                    repository, characterId, sequence, sequence.DungeonIds.Count);
                Check(
                    "fully cleared sequence answers progress == dungeon count",
                    notifier.ResolveSequentialProgress(
                        characterId, sequence.ConfigKey)
                        == sequence.DungeonIds.Count,
                    ref failures);

                // key=41 与 key=28 共享部分副本: 按 key=41 询问时必须解析
                // key=41 自己的序列, 而不是第一条匹配的序列。
                if (AntonNormalConquest.TryGetSequenceByKey(
                        41, out var sharedSequence))
                {
                    ApplyCompleted(
                        repository,
                        characterId,
                        sharedSequence,
                        sharedSequence.DungeonIds.Count);
                    Check(
                        "shared-dungeon sequence resolves by its own key",
                        notifier.ResolveSequentialProgress(
                            characterId, sharedSequence.ConfigKey)
                            == sharedSequence.DungeonIds.Count,
                        ref failures);
                }
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void ApplyCompleted(
            SqliteCharacterStateRepository repository,
            int characterId,
            AntonNormalSequence sequence,
            int count)
        {
            var updates = new List<DungeonPermissionEntrySnapshot>();
            for (var index = 0;
                index < count && index < sequence.DungeonIds.Count;
                index++)
            {
                var dungeonId = sequence.DungeonIds[index];
                if (!AntonNormalConquest.TryResolveCompletedState(
                        dungeonId,
                        sequence.Difficulty,
                        out var clearState))
                {
                    continue;
                }
                updates.Add(new DungeonPermissionEntrySnapshot
                {
                    DungeonId = (ushort)dungeonId,
                    ClearState = clearState,
                });
            }
            repository.ApplyDungeonPermissionBatch(
                characterId,
                updates,
                out _);
        }

        private static void SeedAccount(
            GameDatabase database,
            int accountId,
            string mid)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@mid", mid);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        private static void SeedCharacter(
            GameDatabase database,
            int characterId,
            int accountId,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 临时库清理由操作系统兜底, 不影响自测结果。
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
