using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class ExperienceBonusPotionSelfTest
    {
        private const int AccountId = 930021;
        private const int CharacterId = 930121;
        private const int PotionItemId = 10147838;
        private const int SiblingPotionItemId = 7377;

        public static int Run()
        {
            Console.WriteLine("=== EXP_BONUS_POTION selftest ===");

            var failures = 0;

            // PVF [exp bonus rate] 解析：整数与小数都接受，统一成千分率。
            Check("integer rate 1 scales to 1000",
                ExperienceBonusPotionService.TryParseScaledRate("1", out var r1) && r1 == 1000,
                ref failures);
            Check("fractional rate 0.5 scales to 500",
                ExperienceBonusPotionService.TryParseScaledRate("0.5", out var r05) && r05 == 500,
                ref failures);
            Check("fractional rate 2.5 scales to 2500",
                ExperienceBonusPotionService.TryParseScaledRate("2.5", out var r25) && r25 == 2500,
                ref failures);
            Check("fractional rate 0.2 scales to 200",
                ExperienceBonusPotionService.TryParseScaledRate("0.2", out var r02) && r02 == 200,
                ref failures);
            Check("backticked rate `1.5` scales to 1500",
                ExperienceBonusPotionService.TryParseScaledRate("`1.5`", out var r15) && r15 == 1500,
                ref failures);

            // 非法输入一律拒绝。
            Check("zero rate rejected",
                !ExperienceBonusPotionService.TryParseScaledRate("0", out _),
                ref failures);
            Check("blank rate rejected",
                !ExperienceBonusPotionService.TryParseScaledRate("  ", out _),
                ref failures);
            Check("non-numeric rate rejected",
                !ExperienceBonusPotionService.TryParseScaledRate("abc", out _),
                ref failures);
            Check("tiny rate rounding to zero rejected",
                !ExperienceBonusPotionService.TryParseScaledRate("0.0001", out _),
                ref failures);

            // 加成计算：按千分率折算回实际倍率。
            Check("2x potion adds 100% bonus",
                ExperienceBonusPotionService.CalculateBonus(1000, 1000) == 1000,
                ref failures);
            Check("1.5x potion adds 50% bonus",
                ExperienceBonusPotionService.CalculateBonus(1000, 500) == 500,
                ref failures);
            Check("3.5x potion adds 250% bonus",
                ExperienceBonusPotionService.CalculateBonus(1000, 2500) == 2500,
                ref failures);
            Check("zero rate gives no bonus",
                ExperienceBonusPotionService.CalculateBonus(1000, 0) == 0,
                ref failures);
            Check("zero experience gives no bonus",
                ExperienceBonusPotionService.CalculateBonus(0, 1000) == 0,
                ref failures);
            Check("bonus saturates at uint max",
                ExperienceBonusPotionService.CalculateBonus(uint.MaxValue, 2000) == uint.MaxValue,
                ref failures);

            // ── 共享冷却（[cooltime group] 99 = 远古精灵秘药全系，依赖 PVF）──
            Check("both potions resolve cooltime group 99",
                StackableItemProvider.ResolveCooltimeGroup(PotionItemId) == 99
                    && StackableItemProvider.ResolveCooltimeGroup(SiblingPotionItemId) == 99,
                ref failures);
            var groupMembers = StackableItemProvider.ResolveCooltimeGroupMembers(PotionItemId);
            Check("group 99 members include both potions",
                groupMembers.Contains(PotionItemId) && groupMembers.Contains(SiblingPotionItemId),
                ref failures);
            Check("ungrouped item expands to itself only",
                StackableItemProvider.ResolveCooltimeGroupMembers(14).Count == 1,
                ref failures);

            // ── 冷却拦截（在线 ItemStates book，成员列表注入，不依赖 PVF）──
            const long now = 1_000_000L;
            var inventory = new InventoryService(CharacterId, AccountId);
            inventory.ItemStates.Upsert(
                ItemStateKinds.Cooltime,
                PotionItemId,
                (int)(now + 600));
            Check("same item blocked while on cooldown",
                ExperienceBonusPotionService.IsCooltimeBlocked(
                    inventory,
                    new[] { PotionItemId },
                    now),
                ref failures);
            Check("same-group sibling blocked while on cooldown",
                ExperienceBonusPotionService.IsCooltimeBlocked(
                    inventory,
                    new[] { SiblingPotionItemId, PotionItemId },
                    now),
                ref failures);
            Check("ungrouped item not blocked by group cooldown",
                !ExperienceBonusPotionService.IsCooltimeBlocked(
                    inventory,
                    new[] { 14 },
                    now),
                ref failures);
            Check("expired cooldown no longer blocks",
                !ExperienceBonusPotionService.IsCooltimeBlocked(
                    inventory,
                    new[] { PotionItemId },
                    now + 601),
                ref failures);

            RunPersistenceChecks(ref failures);

            Console.WriteLine(failures == 0
                ? "EXP_BONUS_POTION selftest passed"
                : $"EXP_BONUS_POTION selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        // 冷却/效果的持久化与登录恢复换算（临时 SQLite 库）。
        // GetActiveRate 内部用真实时钟，因此本段基准时间必须取当前真实时间。
        private static void RunPersistenceChecks(ref int failures)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_exp_bonus_potion_{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    Check(
                        "new schema creates character_experience_bonus_effects at current version",
                        TableExists(connection, "character_experience_bonus_effects")
                        && SqliteMigrations.ReadVersion(connection) == SqliteMigrations.CurrentVersion,
                        ref failures);
                }
                SeedAccount(database, AccountId, "exp-bonus-selftest");
                SeedCharacter(database, CharacterId, AccountId, "exp-bonus-selftest");

                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    CharacterExperienceBonusEffectRepository.UpsertEffect(
                        connection,
                        transaction,
                        CharacterId,
                        PotionItemId,
                        500,
                        now + 600);
                    transaction.Commit();
                }

                Check("active effect restored with remaining seconds",
                    ExperienceBonusPotionService.TryGetActiveEffect(
                        database.ConnectionString,
                        CharacterId,
                        now,
                        out var effectItemId,
                        out var effectRemainingSec)
                        && effectItemId == PotionItemId
                        && effectRemainingSec == 600,
                    ref failures);
                Check("active effect exposes its rate for settlement",
                    ExperienceBonusPotionService.GetActiveRate(
                        database.ConnectionString,
                        CharacterId) == 500,
                    ref failures);
                Check("expired effect not restored",
                    !ExperienceBonusPotionService.TryGetActiveEffect(
                        database.ConnectionString,
                        CharacterId,
                        now + 601,
                        out _,
                        out _),
                    ref failures);

                // 重复写入同角色效果应覆盖而不是堆叠。
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    CharacterExperienceBonusEffectRepository.UpsertEffect(
                        connection,
                        transaction,
                        CharacterId,
                        SiblingPotionItemId,
                        1000,
                        now + 900);
                    transaction.Commit();
                }

                Check("second use overwrites the active effect row",
                    ExperienceBonusPotionService.TryGetActiveEffect(
                        database.ConnectionString,
                        CharacterId,
                        now,
                        out var overwrittenItemId,
                        out var overwrittenRemainingSec)
                        && overwrittenItemId == SiblingPotionItemId
                        && overwrittenRemainingSec == 900,
                    ref failures);

                // 登录 0x00AE 效果列表追加（剩余秒），已有同道具条目时不重复。
                var effectItems = new List<ItemStateEntrySnapshot>();
                SqliteSelectCharacterDataSource.AppendExperienceBonusPotionEffect(
                    database.ConnectionString,
                    CharacterId,
                    effectItems,
                    now);
                Check("login restores potion effect into the 0x00AE list",
                    effectItems.Count == 1
                        && effectItems[0].ItemId == SiblingPotionItemId
                        && effectItems[0].ExpireTime == 900,
                    ref failures);
                SqliteSelectCharacterDataSource.AppendExperienceBonusPotionEffect(
                    database.ConnectionString,
                    CharacterId,
                    effectItems,
                    now);
                Check("login effect append does not duplicate an existing entry",
                    effectItems.Count == 1,
                    ref failures);

                // 登录 0x00AC 只下发实际记录的冷却条目，不做 [cooltime group] 展开：
                // 同组冷却是使用时按组校验拦截（IsCooltimeBlocked），
                // 客户端收到单条冷却后自行按 PVF 组数据联动显示。
                InsertItemState(
                    database,
                    CharacterId,
                    ItemStateKinds.Cooltime,
                    PotionItemId,
                    (int)now + 600);
                var snapshot = new SelectCharacterInitializationSnapshot();
                snapshot.CooltimeItemStates.Add(new ItemStateEntrySnapshot
                {
                    ItemId = PotionItemId,
                    ExpireTime = (int)now + 600,
                });
                SqliteSelectCharacterDataSource.ApplyOnlineItemStates(
                    CharacterId,
                    snapshot,
                    now);
                Check("login cooltime list contains only actually recorded entries",
                    snapshot.CooltimeItemStates.Any(
                        entry => entry.ItemId == PotionItemId && entry.ExpireTime == 600)
                        && snapshot.CooltimeItemStates.All(
                            entry => entry.ItemId != SiblingPotionItemId),
                    ref failures);
                Check("expired cooltime rows are dropped on login",
                    snapshot.CooltimeItemStates.All(entry => entry.ExpireTime > 0),
                    ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
            }
        }

        private static void SeedAccount(GameDatabase database, int accountId, string mid)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@mid", mid);
                command.ExecuteNonQuery();
            }
        }

        private static void SeedCharacter(
            GameDatabase database,
            int characterId,
            int accountId,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertItemState(
            GameDatabase database,
            int characterId,
            string stateKind,
            int itemId,
            int expireTime)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO character_item_states(character_id, state_kind, item_id, expire_time)
VALUES (@cid, @kind, @itemId, @expireTime);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@kind", stateKind);
                command.Parameters.AddWithValue("@itemId", itemId);
                command.Parameters.AddWithValue("@expireTime", expireTime);
                command.ExecuteNonQuery();
            }
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type = 'table' AND name = @name;";
                command.Parameters.AddWithValue("@name", tableName);
                return Convert.ToInt32(command.ExecuteScalar()) != 0;
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine($"[FAIL] {name}");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
