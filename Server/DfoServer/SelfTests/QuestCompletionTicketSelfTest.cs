using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.TitleBook;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class QuestCompletionTicketSelfTest
    {
        private const int SideTicketItemId = 10157196;
        private const int EpicTicketItemId = 10006007;
        private const int AchievementTicketItemId = 10006087;
        private const short TicketSlot = 16;
        private const int CharacterLevel = 20;
        private const int AchievementCharacterLevel = 10;

        public static int Run()
        {
            Console.WriteLine("=== QUEST_COMPLETION_TICKET selftest ===");
            var failures = 0;

            VerifyPvfDefinitions(ref failures);
            VerifyLevelHelpers(ref failures);
            VerifyAwakeningTicketMatching(ref failures);
            VerifyAchievementTicketLevelFilter(ref failures);
            VerifyGradeTicket(SideTicketItemId, "side", "epic", ref failures);
            VerifyGradeTicket(EpicTicketItemId, "epic", "side", ref failures);

            Console.WriteLine(failures == 0
                ? "QUEST_COMPLETION_TICKET selftest passed"
                : $"QUEST_COMPLETION_TICKET selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyPvfDefinitions(ref int failures)
        {
            var side = StackableItemProvider.Load(SideTicketItemId);
            var epic = StackableItemProvider.Load(EpicTicketItemId);
            Check(
                "side quest ticket PVF action/icon/level is parsed",
                StackableItemProvider.NormalizeType(side?.ActionTypeName) == "[quest clear]"
                    && QuestCompletionTicketService.ResolveIconFrame(side.Icon) == 1639
                    && side.MinimumLevel == 5
                    && side.MaximumLevel == 86,
                ref failures);
            Check(
                "epic quest ticket PVF action/icon/level is parsed",
                StackableItemProvider.NormalizeType(epic?.ActionTypeName) == "[quest clear]"
                    && QuestCompletionTicketService.ResolveIconFrame(epic.Icon) == 1288
                    && epic.MinimumLevel == 0
                    && epic.MaximumLevel == 84,
                ref failures);
        }

        private static void VerifyAchievementTicketLevelFilter(ref int failures)
        {
            if (!TryFindAchievementLevelPair(
                    AchievementCharacterLevel,
                    out var eligibleQuestId,
                    out var futureQuestId))
            {
                Check(
                    "achievement ticket PVF preconditions have level samples",
                    false,
                    ref failures);
                return;
            }

            const int accountId = 9711801;
            const int characterId = 9711802;
            var sessionId = Guid.NewGuid();
            var tempDbPath = BuildTempDatabasePath("achievement");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(
                    tempDbPath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(
                    database,
                    accountId,
                    characterId,
                    AchievementCharacterLevel,
                    "quest-ticket-achievement");
                lease = RegisterInventoryWithTicket(
                    database,
                    sessionId,
                    characterId,
                    accountId,
                    AchievementTicketItemId);

                var service = new QuestCompletionTicketService(
                    database.ConnectionString);
                QuestCompletionTicketUseResult result;
                lock (lease.SyncRoot)
                {
                    result = service.UseBySlot(new QuestCompletionTicketUseRequest
                    {
                        SessionId = sessionId,
                        CharacterId = characterId,
                        AccountId = accountId,
                        Lease = lease,
                        ListType = InventoryListType.Main,
                        SlotIndex = TicketSlot,
                        ExpectedItemTemplateId = AchievementTicketItemId,
                    });
                }

                using (var connection = database.OpenConnection())
                {
                    var flags = QuestRepository.LoadClearedFlags(
                        connection,
                        null,
                        characterId);
                    Check(
                        "achievement ticket keeps future-level titlebook quests uncleared",
                        result.Success
                            && result.CompletedQuestIds.Contains(eligibleQuestId)
                            && flags.ContainsKey(eligibleQuestId)
                            && !flags.ContainsKey(futureQuestId)
                            && ReadMainItemCount(
                                database,
                                characterId,
                                accountId,
                                TicketSlot) == 0,
                        ref failures);
                }
            }
            finally
            {
                ReleaseLease(sessionId, characterId, lease);
                TryDeleteDatabase(tempDbPath);
            }
        }

        private static void VerifyAwakeningTicketMatching(ref int failures)
        {
            Check(
                "first awakening ticket matches awakening-reward quests only",
                QuestCompletionTicketService.IsFirstAwakeningRewardQuest(
                    new QuestFile
                    {
                        RewardType = "[awakening type]",
                        RewardIntData = "1",
                    })
                && !QuestCompletionTicketService.IsFirstAwakeningRewardQuest(
                    new QuestFile
                    {
                        RewardType = "[awakening type]",
                        RewardIntData = "2",
                    })
                && !QuestCompletionTicketService.IsFirstAwakeningRewardQuest(
                    new QuestFile
                    {
                        RewardType = "[item]",
                        RewardIntData = "1",
                    })
                && !QuestCompletionTicketService.IsFirstAwakeningRewardQuest(
                    new QuestFile
                    {
                        RewardType = "[awakening type]",
                        RewardIntData = "1 1",
                    }),
                ref failures);

            // Quest 2680 (第一次自我觉醒) carries no [job change quest] tag;
            // its awakening flows through the [awakening type] reward only.
            var selfAwakening = QuestData.GetQuestFile(2680);
            Check(
                "PVF self-awakening quest 2680 matches the first awakening ticket",
                selfAwakening != null
                    && selfAwakening.JobChangeQuestValue == 0
                    && QuestCompletionTicketService
                        .IsFirstAwakeningRewardQuest(selfAwakening),
                ref failures);

            var secondSelfAwakening = QuestData.GetQuestFile(2681);
            Check(
                "PVF self-awakening quest 2681 stays on the second awakening ticket",
                secondSelfAwakening != null
                    && secondSelfAwakening.JobChangeQuestValue == 3
                    && !QuestCompletionTicketService
                        .IsFirstAwakeningRewardQuest(secondSelfAwakening),
                ref failures);
        }

        private static void VerifyLevelHelpers(ref int failures)
        {
            var quest = new QuestFile { Level = new[] { 30, 99 } };
            var stackable = new StackableItemFile
            {
                MinimumLevel = 10,
                MaximumLevel = 20,
            };

            Check(
                "quest min level blocks higher-level targets",
                !QuestCompletionTicketService.IsQuestMinimumLevelSatisfied(
                    quest,
                    29)
                && QuestCompletionTicketService.IsQuestMinimumLevelSatisfied(
                    quest,
                    30),
                ref failures);
            Check(
                "ticket usable level respects stackable min/max",
                !QuestCompletionTicketService.IsUsableByLevel(stackable, 9)
                && QuestCompletionTicketService.IsUsableByLevel(stackable, 10)
                && QuestCompletionTicketService.IsUsableByLevel(stackable, 20)
                && !QuestCompletionTicketService.IsUsableByLevel(stackable, 21),
                ref failures);
            Check(
                "play only quest type is excluded from grade tickets",
                QuestCompletionTicketService.IsPlayOnlyQuest(
                    new QuestFile { Type = "[play only]" })
                && QuestCompletionTicketService.IsPlayOnlyQuest(
                    new QuestFile { Type = "play only" })
                && !QuestCompletionTicketService.IsPlayOnlyQuest(
                    new QuestFile { Type = "[clear map]" }),
                ref failures);
        }

        private static void VerifyGradeTicket(
            int ticketItemId,
            string targetGrade,
            string otherGrade,
            ref int failures)
        {
            var eligibleQuestId = FindQuestByGrade(
                targetGrade,
                quest => QuestCompletionTicketService
                    .IsQuestMinimumLevelSatisfied(quest, CharacterLevel)
                    && !QuestCompletionTicketService.IsPlayOnlyQuest(quest));
            var futureQuestId = FindQuestByGrade(
                targetGrade,
                quest => !QuestCompletionTicketService
                    .IsQuestMinimumLevelSatisfied(quest, CharacterLevel)
                    && !QuestCompletionTicketService.IsPlayOnlyQuest(quest));
            var otherQuestId = FindQuestByGrade(
                otherGrade,
                quest => QuestCompletionTicketService
                    .IsQuestMinimumLevelSatisfied(quest, CharacterLevel));
            var playOnlyQuestId = FindQuestByGrade(
                targetGrade,
                quest => QuestCompletionTicketService
                    .IsQuestMinimumLevelSatisfied(quest, CharacterLevel)
                    && QuestCompletionTicketService.IsPlayOnlyQuest(quest));
            var requiresPlayOnlySample = string.Equals(
                targetGrade,
                "side",
                StringComparison.OrdinalIgnoreCase);

            Check(
                $"{targetGrade} ticket PVF preconditions have sample quests",
                eligibleQuestId != 0
                    && futureQuestId != 0
                    && otherQuestId != 0
                    && (!requiresPlayOnlySample || playOnlyQuestId != 0),
                ref failures);
            if (eligibleQuestId == 0
                || futureQuestId == 0
                || otherQuestId == 0
                || (requiresPlayOnlySample && playOnlyQuestId == 0))
            {
                return;
            }

            var accountId = 9710000 + ticketItemId % 1000;
            var characterId = accountId + 1;
            var sessionId = Guid.NewGuid();
            var tempDbPath = BuildTempDatabasePath(targetGrade);
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(
                    tempDbPath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(
                    database,
                    accountId,
                    characterId,
                    CharacterLevel,
                    "quest-ticket-" + targetGrade);
                lease = RegisterInventoryWithTicket(
                    database,
                    sessionId,
                    characterId,
                    accountId,
                    ticketItemId);

                var service = new QuestCompletionTicketService(
                    database.ConnectionString);
                QuestCompletionTicketUseResult result;
                lock (lease.SyncRoot)
                {
                    result = service.UseBySlot(new QuestCompletionTicketUseRequest
                    {
                        SessionId = sessionId,
                        CharacterId = characterId,
                        AccountId = accountId,
                        Lease = lease,
                        ListType = InventoryListType.Main,
                        SlotIndex = TicketSlot,
                        ExpectedItemTemplateId = ticketItemId,
                    });
                }

                using (var connection = database.OpenConnection())
                {
                    var flags = QuestRepository.LoadClearedFlags(
                        connection,
                        null,
                        characterId);
                    Check(
                        $"{targetGrade} ticket clears only eligible grade without rewards",
                        result.Success
                            && result.FinishResults.Count == 0
                            && result.AchievementResults.Count == 0
                            && result.CompletedQuestIds.Contains(eligibleQuestId)
                            && flags.ContainsKey(eligibleQuestId)
                            && !flags.ContainsKey(futureQuestId)
                            && !flags.ContainsKey(otherQuestId)
                            && (playOnlyQuestId == 0
                                || !flags.ContainsKey(playOnlyQuestId))
                            && ReadMainItemCount(
                                database,
                                characterId,
                                accountId,
                                TicketSlot) == 0,
                        ref failures);
                }
            }
            finally
            {
                ReleaseLease(sessionId, characterId, lease);
                TryDeleteDatabase(tempDbPath);
            }
        }

        private static ushort FindQuestByGrade(
            string grade,
            Func<QuestFile, bool> predicate)
        {
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                if (questId <= 0 || questId > ushort.MaxValue)
                    continue;

                var quest = QuestData.GetQuestFile(questId);
                if (quest == null
                    || QuestData.NormalizeQuestTag(quest.Grade) != grade)
                {
                    continue;
                }

                if (predicate(quest))
                    return (ushort)questId;
            }

            return 0;
        }

        private static bool TryFindAchievementLevelPair(
            int characterLevel,
            out ushort eligibleQuestId,
            out ushort futureQuestId)
        {
            eligibleQuestId = 0;
            futureQuestId = 0;
            var provider = TitleBookStaticDataProvider.LoadDefault();
            foreach (var questId in provider.GetGeneralAchievementQuestIds())
            {
                var quest = QuestData.GetQuestFile(questId);
                if (!IsCompletableTitleAchievement(questId, quest))
                    continue;

                if (eligibleQuestId == 0
                    && QuestCompletionTicketService
                        .IsQuestMinimumLevelSatisfied(quest, characterLevel))
                {
                    eligibleQuestId = questId;
                }
                else if (futureQuestId == 0
                    && !QuestCompletionTicketService
                        .IsQuestMinimumLevelSatisfied(quest, characterLevel))
                {
                    futureQuestId = questId;
                }

                if (eligibleQuestId != 0 && futureQuestId != 0)
                    return true;
            }

            return false;
        }

        private static bool IsCompletableTitleAchievement(
            ushort questId,
            QuestFile quest)
        {
            return quest != null
                && QuestData.NormalizeQuestTag(quest.Grade) == "achievement"
                && QuestData.NormalizeQuestTag(quest.RewardType) == "title"
                && QuestData.TryResolveCompletionDefinition(
                    questId,
                    out _,
                    out _);
        }

        private static void SeedCharacter(
            GameDatabase database,
            int accountId,
            int characterId,
            int level,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @name, '');
INSERT INTO characters (character_id, account_id, name, job, level)
VALUES (@cid, @aid, @name, 0, @level);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@level", level);
                command.ExecuteNonQuery();
            }
        }

        private static InventoryLease RegisterInventoryWithTicket(
            GameDatabase database,
            Guid sessionId,
            int characterId,
            int accountId,
            int ticketItemId)
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
                TicketSlot,
                new ItemCore
                {
                    ItemKind = ItemCore.KindConsumable,
                    ItemId = ticketItemId,
                    Count = 1,
                });

            var lease = InventoryContext.Register(
                sessionId,
                characterId,
                inventory);
            if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "selftest-seed-quest-ticket"))
            {
                throw new InvalidOperationException(
                    "failed to persist quest ticket selftest item");
            }

            return lease;
        }

        private static int ReadMainItemCount(
            GameDatabase database,
            int characterId,
            int accountId,
            short slotIndex)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                var item = inventory.GetItem(InventoryListType.Main, slotIndex);
                return item != null && item.ItemId > 0 ? item.Count : 0;
            }
        }

        private static string BuildTempDatabasePath(string suffix)
        {
            var fileName = "s4a21-quest-ticket-" + suffix + "-"
                + Guid.NewGuid().ToString("N") + ".db";
            return Path.Combine(Path.GetTempPath(), fileName);
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

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            if (condition)
            {
                Console.WriteLine("[PASS] " + name);
                return;
            }

            failures++;
            Console.WriteLine("[FAIL] " + name);
        }
    }
}
