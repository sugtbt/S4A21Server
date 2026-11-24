using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    /// <summary>
    /// 随机宝箱满包开箱回归：修复「legacy/upgradable legacy 宝箱满包时整笔回滚、
    /// 宝箱不消耗，摇奖结果被丢弃造成免费重摇」。修复后与概率递增型一致，
    /// 满包奖励转投系统邮件、宝箱照常消耗；只有容量溢出（InsertPlanFailed）才
    /// 转邮件，其它规划错误（如坏奖励 id 创建失败）必须整笔回滚。
    /// </summary>
    internal static class LotteryOpenOverflowSelfTest
    {
        private const short GoldSlot = InventoryService.MainVirtualCurrencySlotStart;
        private const short MainStart = InventoryService.MainSlotStart;
        private const short MainEnd = InventoryService.MainSlotEnd;
        private const int BaseAccountId = 9668201;
        private const int BaseCharacterId = 9669201;

        // 宝箱与填格材料用测试自有 fake id（不在 PVF，definition 由注入的
        // itemLoader 提供）；奖励物品必须是 PVF 真实 id（奖励创建链
        // ItemMetadataResolver 依赖 PVF）。
        private const int ChestItemId = 0x77160001;
        private const int GoldChestItemId = 0x77160002;
        private const int BrokenChestItemId = 0x77160003;
        private const int ProgressChestItemId = 0x77160004;
        private const int FillerItemId = 0x77161001;
        private const int BrokenRewardItemId = 0x7716FFFF;
        private const int SeedGold = 1000;
        private const int GoldReward = 50000;

        // 候选奖励 id：取当前 PVF 中第一个可解析的 stackable 物品
        private static readonly int[] RewardItemCandidates = { 3326, 3262, 3341, 10093974 };

        // SQLite 版共享一个临时目录库；各场景用互不重叠的 account/character id，
        // 无需逐场景重置。
        private static GameDatabase _shared;
        private static string _sharedTempDir;
        private static readonly Dictionary<int, PvfLib.StackableItemFile> ChestFiles =
            new Dictionary<int, PvfLib.StackableItemFile>();

        public static int Run()
        {
            Console.WriteLine("=== LOTTERY_OPEN_OVERFLOW selftest ===");
            try
            {
                if (!TryResolveRewardItem(out var rewardItemId, out var rewardItemKind)
                    || !ItemSlotBoundService.TryGetSlotRange(
                            rewardItemKind,
                            ItemSlotBoundService.MainExpandStageFull,
                            out _,
                            out var rewardRange))
                {
                    Console.WriteLine(
                        "  [SKIP] 当前 PVF 中找不到候选奖励物品，跳过开箱溢出套件");
                    return 0;
                }

                RegisterChestDefinitions(rewardItemId);
                var failures = 0;
                VerifyFullInventoryOpensViaMailEachTime(
                    rewardItemId,
                    rewardItemKind,
                    ref failures);
                VerifyGoldRewardGrantsDirectlyWhenFull(rewardItemId, ref failures);
                VerifyStackableRewardWithHeadroomGrantsDirectly(
                    rewardItemId,
                    rewardItemKind,
                    rewardRange,
                    ref failures);
                VerifyFreeSlotGrantsDirectly(rewardItemId, rewardRange, ref failures);
                VerifyBrokenRewardRollsBackEntirely(ref failures);
                VerifyIncreaseChanceStillMailsAndTracksProgress(rewardItemId, ref failures);

                Console.WriteLine(failures == 0
                    ? "LOTTERY_OPEN_OVERFLOW selftest passed"
                    : $"LOTTERY_OPEN_OVERFLOW selftest failed: {failures}");
                return failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"lottery overflow check failed: {ex.Message}");
                return 1;
            }
            finally
            {
                var shared = _shared;
                var sharedTempDir = _sharedTempDir;
                _shared = null;
                _sharedTempDir = null;
                ChestFiles.Clear();
                if (shared != null)
                {
                    SqliteConnection.ClearAllPools();
                    try { Directory.Delete(sharedTempDir, recursive: true); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[WARN] lottery overflow temp cleanup: "
                            + ex.Message);
                    }
                }
            }
        }

        // 核心守护：满包下连续开 3 次物品奖励，每次都消耗宝箱并各发一封邮件。
        // 修复前该场景是"抽不中已有堆叠即整笔回滚、宝箱不减"的免费重摇。
        private static void VerifyFullInventoryOpensViaMailEachTime(
            int rewardItemId,
            byte rewardItemKind,
            ref int failures)
        {
            const int accountId = BaseAccountId;
            const int characterId = BaseCharacterId;
            const int openCount = 3;
            var database = SharedDatabase();
            InventoryLease lease = null;
            try
            {
                lease = PrepareCharacter(
                    database,
                    accountId,
                    characterId,
                    "lottery-overflow-mail",
                    ChestItemId,
                    chestCount: openCount + 2,
                    rewardItemId,
                    rewardItemKind);

                var allOpened = true;
                for (var round = 1; round <= openCount; round++)
                {
                    if (!TryOpenChest(
                            database,
                            lease,
                            accountId,
                            ChestItemId,
                            out var result))
                    {
                        allOpened = false;
                        break;
                    }

                    Check(
                        $"full inventory open #{round} delivers to mailbox and consumes chest",
                        result.DeliveredToMailbox
                        && result.SourceRemainingStackCount == openCount + 2 - round,
                        ref failures);
                    Check(
                        $"full inventory open #{round} keeps reward out of inventory",
                        lease.Inventory.CountMainItem(rewardItemId) == 0
                        && result.Rewards.Count == 1
                        && result.Rewards[0].SlotIndex == -1
                        && result.Rewards[0].ItemTemplateId == rewardItemId,
                        ref failures);
                }

                Check(
                    "every full inventory open succeeds",
                    allOpened
                    && lease.Inventory.CountMainItem(ChestItemId) == 2,
                    ref failures);
                Check(
                    "full inventory opens report one mail per open",
                    CountMails(database, characterId) == openCount,
                    ref failures);
                Check(
                    "mail attachments match reward item and count",
                    CountAttachments(database, characterId, rewardItemId) == openCount,
                    ref failures);
                Check(
                    "mail body uses readable overflow text and title falls back to body",
                    TryGetLatestMailColumn(database, characterId, "body", out var body)
                    && body == "物品栏没有剩余空间，礼物已邮件发送"
                    && TryGetLatestMailColumn(database, characterId, "title", out var title)
                    && title == body,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"full-inventory mail check failed: {ex.Message}");
                failures++;
            }
            finally
            {
                ReleaseLease(characterId, lease);
            }
        }

        // 满包 + 纯金币 roll：金币是虚拟计数，永远直接入账，不发邮件。
        private static void VerifyGoldRewardGrantsDirectlyWhenFull(
            int rewardItemId,
            ref int failures)
        {
            const int accountId = BaseAccountId + 1;
            const int characterId = BaseCharacterId + 1;
            var database = SharedDatabase();
            InventoryLease lease = null;
            try
            {
                lease = PrepareCharacter(
                    database,
                    accountId,
                    characterId,
                    "lottery-overflow-gold",
                    GoldChestItemId,
                    chestCount: 2,
                    rewardItemId,
                    0);

                var opened = TryOpenChest(
                    database,
                    lease,
                    accountId,
                    GoldChestItemId,
                    out var result);
                Check(
                    "gold reward grants directly without mailbox",
                    opened
                    && result != null
                    && !result.DeliveredToMailbox
                    && result.GrantedGold == GoldReward
                    && lease.Inventory.CountMainItem(0) == SeedGold + GoldReward
                    && CountMails(database, characterId) == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"gold direct check failed: {ex.Message}");
                failures++;
            }
            finally
            {
                ReleaseLease(characterId, lease);
            }
        }

        // 满包但目标奖励在背包里已有可堆叠余量：应直接入包，不误转邮件。
        private static void VerifyStackableRewardWithHeadroomGrantsDirectly(
            int rewardItemId,
            byte rewardItemKind,
            ItemSlotRange rewardRange,
            ref int failures)
        {
            const int accountId = BaseAccountId + 2;
            const int characterId = BaseCharacterId + 2;
            var database = SharedDatabase();
            InventoryLease lease = null;
            try
            {
                lease = PrepareCharacter(
                    database,
                    accountId,
                    characterId,
                    "lottery-overflow-stack",
                    ChestItemId,
                    chestCount: 2,
                    rewardItemId,
                    rewardItemKind,
                    existingRewardSlot: rewardRange.Start);

                var opened = TryOpenChest(
                    database,
                    lease,
                    accountId,
                    ChestItemId,
                    out var result);
                Check(
                    "stackable reward with headroom grants into existing stack",
                    opened
                    && result != null
                    && !result.DeliveredToMailbox
                    && lease.Inventory.CountMainItem(rewardItemId) == 2
                    && result.Rewards.Count == 1
                    && result.Rewards[0].SlotIndex >= 0
                    && CountMails(database, characterId) == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"stack headroom check failed: {ex.Message}");
                failures++;
            }
            finally
            {
                ReleaseLease(characterId, lease);
            }
        }

        // 奖励种类段内有空格时物品奖励直接入包（既有行为回归）。
        private static void VerifyFreeSlotGrantsDirectly(
            int rewardItemId,
            ItemSlotRange rewardRange,
            ref int failures)
        {
            const int accountId = BaseAccountId + 3;
            const int characterId = BaseCharacterId + 3;
            var database = SharedDatabase();
            InventoryLease lease = null;
            try
            {
                lease = PrepareCharacter(
                    database,
                    accountId,
                    characterId,
                    "lottery-overflow-freeslot",
                    ChestItemId,
                    chestCount: 2,
                    rewardItemId,
                    0,
                    emptySlot: rewardRange.End);

                var opened = TryOpenChest(
                    database,
                    lease,
                    accountId,
                    ChestItemId,
                    out var result);
                Check(
                    "free slot grants reward directly",
                    opened
                    && result != null
                    && !result.DeliveredToMailbox
                    && lease.Inventory.CountMainItem(rewardItemId) == 1
                    && CountMails(database, characterId) == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"free slot check failed: {ex.Message}");
                failures++;
            }
            finally
            {
                ReleaseLease(characterId, lease);
            }
        }

        // 坏奖励 id（PVF 不存在）：创建失败不是容量溢出，必须整笔回滚——
        // 宝箱不消耗、无邮件。守护"只有 InsertPlanFailed 才转邮件"的分类。
        private static void VerifyBrokenRewardRollsBackEntirely(ref int failures)
        {
            const int accountId = BaseAccountId + 4;
            const int characterId = BaseCharacterId + 4;
            var database = SharedDatabase();
            InventoryLease lease = null;
            try
            {
                lease = PrepareCharacter(
                    database,
                    accountId,
                    characterId,
                    "lottery-overflow-broken",
                    BrokenChestItemId,
                    chestCount: 2,
                    FillerItemId,
                    0);

                var opened = TryOpenChest(
                    database,
                    lease,
                    accountId,
                    BrokenChestItemId,
                    out var result);
                Check(
                    "broken reward rolls back without consuming chest or mailing",
                    !opened
                    && result == null
                    && lease.Inventory.CountMainItem(BrokenChestItemId) == 2
                    && CountMails(database, characterId) == 0,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"broken reward check failed: {ex.Message}");
                failures++;
            }
            finally
            {
                ReleaseLease(characterId, lease);
            }
        }

        // 概率递增型（increase chance lottery）满包：既有行为——转邮件且进度落库，
        // 修复不得改变该路径。
        private static void VerifyIncreaseChanceStillMailsAndTracksProgress(
            int rewardItemId,
            ref int failures)
        {
            const int accountId = BaseAccountId + 5;
            const int characterId = BaseCharacterId + 5;
            var database = SharedDatabase();
            InventoryLease lease = null;
            try
            {
                lease = PrepareCharacter(
                    database,
                    accountId,
                    characterId,
                    "lottery-overflow-progress",
                    ProgressChestItemId,
                    chestCount: 2,
                    rewardItemId,
                    0);

                var opened = TryOpenChest(
                    database,
                    lease,
                    accountId,
                    ProgressChestItemId,
                    out var result);
                var progressRowPersisted = false;
                using (var connection = database.OpenConnection())
                {
                    progressRowPersisted = CountRows(
                        connection,
                        "SELECT COUNT(*) FROM account_increase_chance_lottery_progress " +
                        "WHERE account_id = " + accountId +
                        " AND item_template_id = " + ProgressChestItemId +
                        " AND reward_index = " + (result?.Progress?.NewRewardIndex ?? -1) + ";") == 1;
                }

                Check(
                    "increase chance mails reward and records progress",
                    opened
                    && result != null
                    && result.DeliveredToMailbox
                    && result.Progress != null
                    && result.Progress.NewRewardIndex >= 0
                    && !result.Progress.AutoReset
                    && progressRowPersisted
                    && CountMails(database, characterId) == 1
                    && CountAttachments(database, characterId, rewardItemId) == 1,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"increase chance check failed: {ex.Message}");
                failures++;
            }
            finally
            {
                ReleaseLease(characterId, lease);
            }
        }

        private static GameDatabase SharedDatabase()
        {
            if (_shared != null)
                return _shared;

            _sharedTempDir = Path.Combine(
                Path.GetTempPath(),
                "lottery-overflow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sharedTempDir);
            _shared = new GameDatabase(
                Path.Combine(_sharedTempDir, "inventory.db"),
                ServerPaths.SchemaFilePath);
            return _shared;
        }

        private static bool TryResolveRewardItem(out int rewardItemId, out byte rewardItemKind)
        {
            foreach (var candidate in RewardItemCandidates)
            {
                if (!ItemMetadataResolver.TryResolveItemKind(candidate, out rewardItemKind))
                    continue;

                // 候选必须是可堆叠物品：「既有堆叠余量直接入包」场景名实需要一致。
                if (!InventoryStackRuleService.IsStackable(new ItemCore
                        {
                            ItemKind = rewardItemKind,
                            ItemId = candidate,
                            Count = 1,
                        }))
                    continue;

                rewardItemId = candidate;
                return true;
            }

            rewardItemId = 0;
            rewardItemKind = 0;
            return false;
        }

        private static void RegisterChestDefinitions(int rewardItemId)
        {
            ChestFiles[ChestItemId] = BuildChest(
                new[] { Reward(rewardItemId, 1) });
            ChestFiles[GoldChestItemId] = BuildChest(
                new[] { Reward(0, GoldReward) });
            ChestFiles[BrokenChestItemId] = BuildChest(
                new[] { Reward(BrokenRewardItemId, 1) });
            // 概率递增池两个条目都用真实 PVF id：RollProgressReward 可能抽中任一条，
            // fake id 会让邮件附件构造失败。ActionTypeParams[1] 是进度重置数量，
            // 设 3（> 池大小）保证首次抽取后进度不自动重置，可断言落库。
            ChestFiles[ProgressChestItemId] = BuildChest(
                new[] { Reward(rewardItemId, 1), Reward(rewardItemId, 1) },
                actionTypeName: "[increase chance lottery]",
                actionTypeParams: new[] { 0, 3, 0 });
        }

        private static PvfLib.BoosterRewardEntry Reward(int itemId, int count)
            => new PvfLib.BoosterRewardEntry
            {
                RewardKind = "upgradable legacy",
                ItemId = itemId,
                Weight = 10000,
                Count = count,
            };

        private static PvfLib.StackableItemFile BuildChest(
            PvfLib.BoosterRewardEntry[] rewards,
            string actionTypeName = null,
            int[] actionTypeParams = null)
        {
            var file = new PvfLib.StackableItemFile
            {
                StackableType = "[upgradable legacy]",
            };
            if (actionTypeName != null)
                file.ActionTypeName = actionTypeName;
            if (actionTypeParams != null)
                file.ActionTypeParams.AddRange(actionTypeParams);
            foreach (var reward in rewards)
                file.UpgradableLegacyRewards.Add(reward);
            return file;
        }

        private static LotteryItemOpenService BuildOpenService(GameDatabase database)
            => new LotteryItemOpenService(
                database.ConnectionString,
                new LotteryItemDefinitionProvider(TryLoadChest),
                new LotteryDoubleRewardPolicy(
                    new DailyResetService(database),
                    database.ConnectionString));

        private static PvfLib.StackableItemFile TryLoadChest(int itemTemplateId)
            => ChestFiles.TryGetValue(itemTemplateId, out var file) ? file : null;

        private static void SeedOwner(
            GameDatabase database,
            int accountId,
            int characterId,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES ($aid, $name, '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES ($cid, $aid, $name, 86);";
                command.Parameters.AddWithValue("$aid", accountId);
                command.Parameters.AddWithValue("$cid", characterId);
                command.Parameters.AddWithValue("$name", name);
                command.ExecuteNonQuery();
            }
        }

        private static InventoryLease LoadAndRegister(
            GameDatabase database,
            Guid sessionId,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return InventoryContext.Register(sessionId, characterId, inventory);
            }
        }

        private static InventoryLease PrepareCharacter(
            GameDatabase database,
            int accountId,
            int characterId,
            string name,
            int chestItemId,
            int chestCount,
            int rewardItemId,
            byte rewardItemKind,
            short existingRewardSlot = -1,
            short emptySlot = -1)
        {
            SeedOwner(database, accountId, characterId, name);
            var lease = LoadAndRegister(
                database,
                Guid.NewGuid(),
                accountId,
                characterId);

            var seeded = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "selftest-lottery-overflow-seed",
                (connection, transaction) =>
                {
                    var ok = lease.Inventory.SetItem(
                        InventoryListType.Main,
                        MainStart,
                        new ItemCore
                        {
                            ItemKind = ItemCore.KindConsumable,
                            ItemId = chestItemId,
                            Count = chestCount,
                        });
                    for (var slot = (short)(MainStart + 1); slot <= MainEnd; slot++)
                    {
                        if (slot == emptySlot)
                            continue;

                        var filler = slot == existingRewardSlot
                            ? new ItemCore
                            {
                                ItemKind = rewardItemKind,
                                ItemId = rewardItemId,
                                Count = 1,
                            }
                            : new ItemCore
                            {
                                ItemKind = ItemCore.KindConsumable,
                                ItemId = FillerItemId,
                                Count = 1,
                            };
                        ok &= lease.Inventory.SetItem(InventoryListType.Main, slot, filler);
                    }

                    lease.Inventory.SetMainVirtualCount(GoldSlot, SeedGold);
                    return ok;
                });
            if (!seeded)
                throw new InvalidOperationException("lottery overflow seed commit failed");
            return lease;
        }

        private static bool TryOpenChest(
            GameDatabase database,
            InventoryLease lease,
            int accountId,
            int chestItemId,
            out LotteryOpenResult result)
        {
            var service = BuildOpenService(database);
            var slotIndex = FindChestSlot(lease.Inventory, chestItemId);
            if (slotIndex < 0)
            {
                result = null;
                return false;
            }

            lock (lease.SyncRoot)
            {
                if (!service.TryCreateReservation(
                        lease.Inventory,
                        accountId,
                        slotIndex,
                        LotteryOpenPlan.ConfirmedRegular(),
                        out var reservation))
                {
                    result = null;
                    return false;
                }

                return service.TryOpen(
                    lease,
                    accountId,
                    reservation,
                    new MailboxInventoryOverflowRewardSink(
                        new MailboxService(new MailboxRepository(database))),
                    out result);
            }
        }

        private static short FindChestSlot(InventoryService inventory, int chestItemId)
        {
            for (var slot = MainStart; slot <= MainEnd; slot++)
            {
                var core = inventory.GetItem(InventoryListType.Main, slot);
                if (core != null && core.ItemId == chestItemId)
                    return slot;
            }

            return -1;
        }

        private static long CountRows(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static long CountMails(GameDatabase database, int characterId)
        {
            using var connection = database.OpenConnection();
            return CountRows(
                connection,
                "SELECT COUNT(*) FROM mailbox_messages " +
                "WHERE receiver_character_id = " + characterId + ";");
        }

        private static long CountAttachments(
            GameDatabase database,
            int characterId,
            int itemTemplateId)
        {
            using var connection = database.OpenConnection();
            return CountRows(
                connection,
                "SELECT COUNT(*) FROM mailbox_attachments a " +
                "JOIN mailbox_messages m ON a.message_id = m.message_id " +
                "WHERE m.receiver_character_id = " + characterId +
                " AND a.item_template_id = " + itemTemplateId + ";");
        }

        private static bool TryGetLatestMailColumn(
            GameDatabase database,
            int characterId,
            string column,
            out string value)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT " + column + " FROM mailbox_messages " +
                "WHERE receiver_character_id = " + characterId + " " +
                "ORDER BY message_id DESC LIMIT 1;";
            value = command.ExecuteScalar() as string;
            return value != null;
        }

        private static void ReleaseLease(int characterId, InventoryLease lease)
        {
            if (lease == null)
                return;

            InventoryPersistenceService.SaveDirty(lease);
            InventoryContext.Unregister(lease.SessionId, characterId);
        }

        private static void Check(string label, bool success, ref int failures)
        {
            Console.WriteLine($"  [{(success ? "PASS" : "FAIL")}] {label}");
            if (!success)
                failures++;
        }
    }
}
