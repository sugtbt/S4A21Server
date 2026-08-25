using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class BuySkillTpRefundSelfTest
    {
        private const int AccountIdBase = 93000;
        private const int CharacterIdBase = 93100;
        private const int JobSwordman = 0;
        private const int JobMage = 3;
        private const byte Level = 60;
        private const byte GrowTypeAsura = 4;
        private const byte LevelElementalIgnite = 99;
        private const byte GrowTypeElementalist = 1;
        private const ushort ElementalIgniteSkillId = 29;
        private const ushort BasicAttackUpExSkillId = 161;
        private const ushort GrandWaveExSkillId = 211;
        private const byte BasicAttackUpExSlot = 0x9B;
        private const byte GrandWaveExSlot = 0x9A;

        public static int Run()
        {
            Console.WriteLine("=== BUY_SKILL_TP_REFUND selftest ===");
            var failures = 0;

            VerifyPvfTpSkillData(ref failures);
            VerifyMultiValueSkillPurchase(ref failures);
            VerifyTpRefundRequiresTpResetBook(ref failures);
            VerifyTpRefundConsumesNormalTpResetBook(ref failures);
            VerifyTpRefundConsumesEventTpResetBook(ref failures);
            VerifyBuySkillAckWritesCommandBytes(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "BUY_SKILL_TP_REFUND selftest passed."
                    : $"BUY_SKILL_TP_REFUND selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyPvfTpSkillData(ref int failures)
        {
            var basic = SkillDataProvider.GetSkill(
                JobSwordman,
                BasicAttackUpExSkillId);
            var grandWave = SkillDataProvider.GetSkill(
                JobSwordman,
                GrandWaveExSkillId);

            Check(
                "PVF feature skill type marks 161 and 211 as TP skills",
                basic != null
                && grandWave != null
                && basic.IsTpSkill
                && grandWave.IsTpSkill
                && basic.TpCostFor(0, 2) == 2
                && grandWave.TpCostFor(2, 5) == 6,
                ref failures);
        }

        private static void VerifyMultiValueSkillPurchase(ref int failures)
        {
            var tempDbPath = BuildTempDatabasePath("multi-value-skill");
            const int accountId = AccountIdBase + 10;
            const int characterId = CharacterIdBase + 10;

            try
            {
                var database = CreateSeededDatabase(
                    tempDbPath,
                    accountId,
                    characterId,
                    "buy-skill-multi-value",
                    JobMage,
                    GrowTypeElementalist,
                    LevelElementalIgnite);
                var repo = new SqliteCharacterProgressRepository(database);
                var snapshot = new SkillInfoSnapshot();
                var page0 = new SkillInfoPageSnapshot();
                page0.Entries.Add(new SkillInfoEntrySnapshot
                {
                    Slot = 0,
                    SkillId = 16,
                    Level = 1,
                });
                page0.Entries.Add(new SkillInfoEntrySnapshot
                {
                    Slot = 1,
                    SkillId = 18,
                    Level = 1,
                });
                snapshot.Pages.Add(page0);
                snapshot.Pages.Add(new SkillInfoPageSnapshot());
                repo.SaveSkillProgress(characterId, snapshot);

                var result = BuySkillService.Execute(
                    repo,
                    characterId,
                    accountId,
                    JobMage,
                    skillTree: 0,
                    entries: new List<BuySkillEntry>
                    {
                        new BuySkillEntry
                        {
                            SkillIndex = ElementalIgniteSkillId,
                            IsRefund = 0,
                            Level = 20,
                        },
                    },
                    bonusSp: 100000,
                    level: LevelElementalIgnite,
                    growType: GrowTypeElementalist);

                Check(
                    "BUY_SKILL applies the active growType maximum-level cap",
                    result != null
                    && result.Success
                    && result.Entries.Count == 1
                    && result.Entries[0].SkillId == ElementalIgniteSkillId
                    && result.Entries[0].Level == 20
                    && ReadSkillLevel(
                        repo,
                        characterId,
                        ElementalIgniteSkillId) == 20,
                    ref failures);
            }
            finally
            {
                TryDeleteDatabase(tempDbPath);
            }
        }

        private static void VerifyTpRefundRequiresTpResetBook(ref int failures)
        {
            var tempDbPath = BuildTempDatabasePath("missing-book");
            var sessionId = Guid.NewGuid();
            const int accountId = AccountIdBase + 1;
            const int characterId = CharacterIdBase + 1;
            InventoryLease lease = null;

            try
            {
                var database = CreateSeededDatabase(
                    tempDbPath,
                    accountId,
                    characterId,
                    "buy-skill-tp-missing");
                var repo = new SqliteCharacterProgressRepository(database);
                SaveInitialSkills(repo, characterId);
                lease = RegisterInventoryWithMainItem(
                    database,
                    sessionId,
                    characterId,
                    accountId,
                    slotIndex: 3,
                    itemTemplateId:
                        SkillResetConsumableService.ForgetRiverWaterItemTemplateId,
                    count: 1);

                var result = BuySkillService.ExecuteWithRefundConsumable(
                    lease,
                    repo,
                    characterId,
                    accountId,
                    JobSwordman,
                    skillTree: 0,
                    entries: CreateTpRefundRequest(),
                    level: Level,
                    growType: GrowTypeAsura);

                Check(
                    "TP-only refund fails when only ordinary reset item exists",
                    result != null
                    && !result.Success
                    && result.ErrorCode == 3
                    && ReadMainItemCount(
                        database,
                        characterId,
                        accountId,
                        SkillResetConsumableService
                            .ForgetRiverWaterItemTemplateId) == 1
                    && ReadSkillLevel(repo, characterId, GrandWaveExSkillId) == 5
                    && ReadSkillLevel(repo, characterId, BasicAttackUpExSkillId) == 0,
                    ref failures);
            }
            finally
            {
                ReleaseLease(sessionId, characterId, lease);
                TryDeleteDatabase(tempDbPath);
            }
        }

        private static void VerifyTpRefundConsumesEventTpResetBook(
            ref int failures)
        {
            var tempDbPath = BuildTempDatabasePath("event-book");
            var sessionId = Guid.NewGuid();
            const int accountId = AccountIdBase + 2;
            const int characterId = CharacterIdBase + 2;
            InventoryLease lease = null;

            try
            {
                var database = CreateSeededDatabase(
                    tempDbPath,
                    accountId,
                    characterId,
                    "buy-skill-tp-event");
                var repo = new SqliteCharacterProgressRepository(database);
                SaveInitialSkills(repo, characterId);
                lease = RegisterInventoryWithMainItem(
                    database,
                    sessionId,
                    characterId,
                    accountId,
                    slotIndex: 3,
                    itemTemplateId:
                        SkillResetConsumableService.EventTpResetBookItemTemplateId,
                    count: 1);

                var result = BuySkillService.ExecuteWithRefundConsumable(
                    lease,
                    repo,
                    characterId,
                    accountId,
                    JobSwordman,
                    skillTree: 0,
                    entries: CreateTpRefundRequest(),
                    level: Level,
                    growType: GrowTypeAsura);

                Check(
                    "TP-only refund consumes one 1253 and updates TP skill levels",
                    result != null
                    && result.Success
                    && result.ConsumedForgetRiverWater
                    && result.ConsumedForgetRiverWaterItem != null
                    && result.ConsumedForgetRiverWaterItem.ItemTemplateId
                        == SkillResetConsumableService
                            .EventTpResetBookItemTemplateId
                    && result.ConsumedForgetRiverWaterItem.SlotIndex == 3
                    && result.RemainTp == 5
                    && result.Entries.Count == 2
                    && result.Entries[0].Slot == GrandWaveExSlot
                    && result.Entries[0].SkillId == GrandWaveExSkillId
                    && result.Entries[0].Level == 2
                    && result.Entries[0].HasCmd
                    && result.Entries[0].CommandBytes.Count == 1
                    && result.Entries[0].CommandBytes[0] == 1
                    && result.Entries[1].Slot == BasicAttackUpExSlot
                    && result.Entries[1].SkillId == BasicAttackUpExSkillId
                    && result.Entries[1].Level == 2
                    && result.Entries[1].HasCmd
                    && ReadMainItemCount(
                        database,
                        characterId,
                        accountId,
                        SkillResetConsumableService
                            .EventTpResetBookItemTemplateId) == 0
                    && ReadSkillLevel(repo, characterId, GrandWaveExSkillId) == 2
                    && ReadSkillLevel(repo, characterId, BasicAttackUpExSkillId) == 2,
                    ref failures);
            }
            finally
            {
                ReleaseLease(sessionId, characterId, lease);
                TryDeleteDatabase(tempDbPath);
            }
        }

        private static void VerifyTpRefundConsumesNormalTpResetBook(
            ref int failures)
        {
            var tempDbPath = BuildTempDatabasePath("normal-book");
            var sessionId = Guid.NewGuid();
            const int accountId = AccountIdBase + 3;
            const int characterId = CharacterIdBase + 3;
            InventoryLease lease = null;

            try
            {
                var database = CreateSeededDatabase(
                    tempDbPath,
                    accountId,
                    characterId,
                    "buy-skill-tp-normal");
                var repo = new SqliteCharacterProgressRepository(database);
                SaveInitialSkills(repo, characterId);
                lease = RegisterInventoryWithMainItem(
                    database,
                    sessionId,
                    characterId,
                    accountId,
                    slotIndex: 4,
                    itemTemplateId:
                        SkillResetConsumableService.TpResetBookItemTemplateId,
                    count: 1);

                var result = BuySkillService.ExecuteWithRefundConsumable(
                    lease,
                    repo,
                    characterId,
                    accountId,
                    JobSwordman,
                    skillTree: 0,
                    entries: CreateTpRefundRequest(),
                    level: Level,
                    growType: GrowTypeAsura);

                Check(
                    "TP-only refund consumes 1206 when 1253 is absent",
                    result != null
                    && result.Success
                    && result.ConsumedForgetRiverWaterItem != null
                    && result.ConsumedForgetRiverWaterItem.ItemTemplateId
                        == SkillResetConsumableService.TpResetBookItemTemplateId
                    && result.ConsumedForgetRiverWaterItem.SlotIndex == 4
                    && ReadMainItemCount(
                        database,
                        characterId,
                        accountId,
                        SkillResetConsumableService.TpResetBookItemTemplateId) == 0
                    && ReadSkillLevel(repo, characterId, GrandWaveExSkillId) == 2
                    && ReadSkillLevel(repo, characterId, BasicAttackUpExSkillId) == 2,
                    ref failures);
            }
            finally
            {
                ReleaseLease(sessionId, characterId, lease);
                TryDeleteDatabase(tempDbPath);
            }
        }

        private static void VerifyBuySkillAckWritesCommandBytes(
            ref int failures)
        {
            var result = new BuySkillResult
            {
                Success = true,
                SkillTree = 0,
                RemainSp = 0x00F0,
                RemainTp = 5,
            };
            AddAckEntry(result, GrandWaveExSlot, GrandWaveExSkillId, 2);
            AddAckEntry(result, BasicAttackUpExSlot, BasicAttackUpExSkillId, 2);

            Check(
                "BUY_SKILL ACK writes has_cmd, length and command bytes",
                BytesEqual(
                    BuySkillAckBuilder.Build(result),
                    new byte[]
                    {
                        0x01, 0x00, 0xF0, 0x00, 0x05, 0x00, 0x02,
                        0x9A, 0xD3, 0x00, 0x02, 0x01, 0x01, 0x01,
                        0x9B, 0xA1, 0x00, 0x02, 0x01, 0x01, 0x01,
                    }),
                ref failures);
        }

        private static GameDatabase CreateSeededDatabase(
            string tempDbPath,
            int accountId,
            int characterId,
            string name,
            int job = JobSwordman,
            int growType = GrowTypeAsura,
            int level = Level)
        {
            var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @name, '');
INSERT INTO characters (character_id, account_id, name, job, grow_type, level)
VALUES (@cid, @aid, @name, @job, @grow, @level);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@job", job);
                command.Parameters.AddWithValue("@grow", growType);
                command.Parameters.AddWithValue("@level", level);
                command.ExecuteNonQuery();
            }

            return database;
        }

        private static void SaveInitialSkills(
            SqliteCharacterProgressRepository repo,
            int characterId)
        {
            var snapshot = new SkillInfoSnapshot();
            var page0 = new SkillInfoPageSnapshot();
            page0.Entries.Add(
                new SkillInfoEntrySnapshot
                {
                    Slot = GrandWaveExSlot,
                    SkillId = GrandWaveExSkillId,
                    Level = 5,
                });
            page0.Entries.Add(
                new SkillInfoEntrySnapshot
                {
                    Slot = BasicAttackUpExSlot,
                    SkillId = BasicAttackUpExSkillId,
                    Level = 0,
                });
            snapshot.Pages.Add(page0);
            snapshot.Pages.Add(new SkillInfoPageSnapshot());
            repo.SaveSkillProgress(characterId, snapshot);
        }

        private static List<BuySkillEntry> CreateTpRefundRequest()
        {
            return new List<BuySkillEntry>
            {
                new BuySkillEntry
                {
                    SkillIndex = GrandWaveExSkillId,
                    IsRefund = 1,
                    Level = 3,
                },
                new BuySkillEntry
                {
                    SkillIndex = BasicAttackUpExSkillId,
                    IsRefund = 0,
                    Level = 2,
                },
            };
        }

        private static InventoryLease RegisterInventoryWithMainItem(
            GameDatabase database,
            Guid sessionId,
            int characterId,
            int accountId,
            short slotIndex,
            int itemTemplateId,
            int count)
        {
            InventoryService inventory;
            using (var connection = database.OpenConnection())
            {
                inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
            }

            inventory.SetItem(
                InventoryListType.Main,
                slotIndex,
                CreateStackable(itemTemplateId, count));

            var lease = InventoryContext.Register(
                sessionId,
                characterId,
                inventory);
            if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "selftest-seed-buy-skill-refund-item"))
            {
                throw new InvalidOperationException(
                    "failed to persist buy skill selftest item");
            }

            return lease;
        }

        private static ItemCore CreateStackable(int itemId, int count)
        {
            var core = ItemCore.Create(ItemCore.KindConsumable, itemId);
            core.Count = count;
            return core;
        }

        private static int ReadMainItemCount(
            GameDatabase database,
            int characterId,
            int accountId,
            int itemId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return inventory.CountMainItem(itemId);
            }
        }

        private static int ReadSkillLevel(
            SqliteCharacterProgressRepository repo,
            int characterId,
            ushort skillId)
        {
            var snapshot = repo.LoadSkills(characterId);
            if (snapshot.Pages.Count == 0)
                return 0;

            foreach (var entry in snapshot.Pages[0].Entries)
            {
                if (entry.SkillId == skillId)
                    return entry.Level;
            }

            return 0;
        }

        private static void AddAckEntry(
            BuySkillResult result,
            byte slot,
            ushort skillId,
            byte level)
        {
            var entry = new BuySkillResultEntry
            {
                Slot = slot,
                SkillId = skillId,
                Level = level,
                HasCmd = true,
            };
            entry.CommandBytes.Add(0x01);
            result.Entries.Add(entry);
        }

        private static string BuildTempDatabasePath(string suffix)
        {
            return Path.Combine(
                Path.GetTempPath(),
                "s4a21-buy-skill-tp-refund-" + suffix + "-"
                    + Guid.NewGuid().ToString("N") + ".db");
        }

        private static void ReleaseLease(
            Guid sessionId,
            int characterId,
            InventoryLease lease)
        {
            if (lease == null)
                return;

            InventoryContext.Unregister(sessionId, characterId);
        }

        private static void TryDeleteDatabase(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (File.Exists(path + "-wal"))
                    File.Delete(path + "-wal");
                if (File.Exists(path + "-shm"))
                    File.Delete(path + "-shm");
            }
            catch
            {
            }
        }

        private static bool BytesEqual(byte[] actual, byte[] expected)
        {
            if (actual == null || expected == null || actual.Length != expected.Length)
                return false;

            for (var index = 0; index < actual.Length; index++)
            {
                if (actual[index] != expected[index])
                    return false;
            }

            return true;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
