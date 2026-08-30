using DfoServer.Game.Premium;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class PremiumContractProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== PREMIUM_CONTRACT_PROTOCOL selftest ===");
            var failures = 0;

            Check(
                "A21 premium query uses CMD 0x036F",
                (ushort)CmdPacketTypeA21.PREMIUM_SERVICE == 0x036F,
                ref failures);
            Check(
                "A21 premium state uses NOTI 0x032F",
                (ushort)NotiPacketTypeA21.PREMIUM_SERVICE == 0x032F,
                ref failures);
            Check(
                "A21 contract activation uses CERA_SPECIALITEM 0x0042",
                (ushort)NotiPacketTypeA21.CERA_SPECIALITEM == 0x0042,
                ref failures);
            Check(
                "Devil contract storage slots remain isolated from PVF premium types",
                DevilContractCatalog.SlotPremiumTypeBase == 580
                && DevilContractCatalog.SlotCount == 8
                && DevilContractCatalog.SlotToPremiumType(6) == 586,
                ref failures);

            var body = PremiumService.BuildPremiumServiceStateBody(
                PremiumService.DefaultServiceType,
                new byte[74]);
            Check(
                "A21 premium state body is status + type + 74-byte data",
                body.Length == 77
                && body[0] == 1
                && BitConverter.ToUInt16(body, 1) == PremiumService.DefaultServiceType,
                ref failures);

            var sequence = NewCharacterInitSequence.Build();
            Check(
                "character init proactively sends A21 premium service state",
                sequence.Any(packet =>
                    packet.Kind == SelectCharacterPacketTemplateKind.Raw
                    && packet.Command == 0x00
                    && packet.Type == (ushort)NotiPacketTypeA21.PREMIUM_SERVICE),
                ref failures);

            var initData = new byte[74];
            initData[6] = 0x78;
            var initSnapshot = new SelectCharacterDataSnapshot
            {
                InitializationSnapshot = new SelectCharacterInitializationSnapshot
                {
                    PremiumServiceType = PremiumService.DefaultServiceType,
                    PremiumServiceData = initData,
                },
            };
            var initBuilder = new PremiumServiceInitBodyBuilder();
            Check(
                "premium init builder uses A21 NOTI body layout",
                initBuilder.TryBuild(initSnapshot, 0, out var initBody)
                && initBody.Length == 77
                && initBody[0] == 1
                && BitConverter.ToUInt16(initBody, 1) == PremiumService.DefaultServiceType
                && initBody[3 + 6] == 0x78,
                ref failures);

            failures += RunDailyUsageChecks();
            failures += RunQuestAssistantChecks();
            failures += RunQuestAssistantGiftChecks();

            Console.WriteLine(
                failures == 0
                    ? "PREMIUM_CONTRACT_PROTOCOL selftest passed."
                    : $"PREMIUM_CONTRACT_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static int RunQuestAssistantGiftChecks()
        {
            var failures = 0;
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "dfo-premium-gifts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var database = new GameDatabase(
                    Path.Combine(tempDirectory, "inventory.db"),
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "Sqlite",
                        "item_schema.sql"));
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id) VALUES (7, 'premium-gift-test');
INSERT INTO characters(character_id, account_id, name)
VALUES (11, 7, 'premium-gift-character');
INSERT INTO account_premiums(account_id, premium_type, end_time)
VALUES (7, @premiumType, @expire);";
                    command.Parameters.AddWithValue(
                        "@premiumType",
                        DevilContractCatalog.SlotToPremiumType(
                            DevilContractUsagePolicy.QuestAssistantSlot));
                    command.Parameters.AddWithValue(
                        "@expire",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
                    command.ExecuteNonQuery();
                }

                var dailyReset = new DailyResetService(database);
                var mailbox = new MailboxService(
                    new MailboxRepository(database));
                var service = new QuestAssistantGiftService(
                    database,
                    dailyReset,
                    mailbox);
                var first = service.TryDeliver(11, 7);
                var second = service.TryDeliver(11, 7);
                var inbox = mailbox.LoadInbox(11, 10);
                var giftItemIds = inbox
                    .SelectMany(mail => mail.Attachments)
                    .Select(attachment => attachment.ItemTemplateId)
                    .OrderBy(itemId => itemId)
                    .ToArray();
                Check(
                    "quest assistant sends one daily system mail with four APC gifts",
                    first.Success
                    && first.MessageId > 0
                    && second.SkippedAsAlreadyDelivered
                    && inbox.Count == 1
                    && giftItemIds.SequenceEqual(new[]
                    {
                        2681922,
                        2681923,
                        2681924,
                        2681925,
                    }),
                    ref failures);
                Check(
                    "quest assistant gift delivery records the daily claim",
                    dailyReset.GetCounter(
                        11,
                        QuestAssistantGiftService.DailyCounterKey) == 1,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[info] quest assistant gift check failed: " + ex.Message);
                failures++;
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch
                {
                }
            }
            return failures;
        }

        private static int RunQuestAssistantChecks()
        {
            var failures = 0;
            var candidate = new QuestDropCandidate
            {
                Count = 2,
                DropRate = 100,
                MaxStack = 10,
                SeekingRequiredCount = 10,
            };
            Check(
                "quest assistant adds the conservative 50-percent item bonus",
                QuestAssistantDropPolicy.ApplyBonus(
                    candidate,
                    currentHeld: 0,
                    baseCount: 2,
                    rollPercent: () => 99) == 3,
                ref failures);
            Check(
                "quest assistant uses stochastic rounding for one base item",
                QuestAssistantDropPolicy.ApplyBonus(
                    candidate,
                    currentHeld: 0,
                    baseCount: 1,
                    rollPercent: () => 49) == 2
                && QuestAssistantDropPolicy.ApplyBonus(
                    candidate,
                    currentHeld: 0,
                    baseCount: 1,
                    rollPercent: () => 50) == 1,
                ref failures);
            Check(
                "quest assistant bonus cannot exceed the quest held limit",
                QuestAssistantDropPolicy.ApplyBonus(
                    candidate,
                    currentHeld: 9,
                    baseCount: 2,
                    rollPercent: () => 0) == 1,
                ref failures);
            return failures;
        }

        private static int RunDailyUsageChecks()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"dfo-premium-contract-{Guid.NewGuid():N}.db");
            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                }.ToString();
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
CREATE TABLE account_premiums (
    account_id INTEGER NOT NULL,
    premium_type INTEGER NOT NULL,
    end_time INTEGER NOT NULL,
    PRIMARY KEY (account_id, premium_type)
);
CREATE TABLE character_daily_reset (
    character_id INTEGER PRIMARY KEY,
    day_id INTEGER NOT NULL DEFAULT 0,
    week_id INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE character_daily_counters (
    character_id INTEGER NOT NULL,
    counter_key TEXT NOT NULL,
    period TEXT NOT NULL,
    value INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (character_id, counter_key)
);";
                        command.ExecuteNonQuery();
                        command.CommandText = @"
INSERT INTO account_premiums(account_id, premium_type, end_time)
VALUES (7, @goldCardType, @expire);";
                        command.Parameters.AddWithValue(
                            "@goldCardType",
                            DevilContractCatalog.SlotToPremiumType(
                                DevilContractUsagePolicy.GoldCardSlot));
                        command.Parameters.AddWithValue(
                            "@expire",
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
                        command.ExecuteNonQuery();
                    }
                }

                var database = GameDatabase.AttachInitialized(connectionString);
                var usagePolicy = new DevilContractUsagePolicy(
                    database,
                    new DailyResetService(database));
                Check(
                    "active gold-card contract starts with an available daily use",
                    usagePolicy.HasAvailableBenefit(
                        characterId: 11,
                        accountId: 7,
                        slotIndex: DevilContractUsagePolicy.GoldCardSlot),
                    ref failures);

                for (var index = 0;
                     index < DevilContractUsagePolicy.GoldCardDailyLimit;
                     index++)
                {
                    var consumed = database.Write(
                        (connection, transaction) => usagePolicy.TryConsume(
                            connection,
                            transaction,
                            characterId: 11,
                            accountId: 7,
                            slotIndex: DevilContractUsagePolicy.GoldCardSlot));
                    Check(
                        $"gold-card daily use {index + 1} is accepted",
                        consumed,
                        ref failures);
                }

                var rejectedAtLimit = database.Write(
                    (connection, transaction) => usagePolicy.TryConsume(
                        connection,
                        transaction,
                        characterId: 11,
                        accountId: 7,
                        slotIndex: DevilContractUsagePolicy.GoldCardSlot));
                var usage = usagePolicy.BuildPremiumServiceUsage(11);
                Check(
                    "gold-card daily limit rejects use 11 and reports used count 10",
                    !rejectedAtLimit
                    && usage.TryGetValue(
                        DevilContractUsagePolicy.GoldCardSlot,
                        out var goldCardUsed)
                    && goldCardUsed
                        == DevilContractUsagePolicy.GoldCardDailyLimit,
                    ref failures);

                var serviceData = PremiumService.BuildPremiumServiceData(
                    connectionString,
                    accountId: 7,
                    usage);
                Check(
                    "premium state projects the gold-card daily used count",
                    BitConverter.ToInt32(serviceData, 10)
                        == DevilContractUsagePolicy.GoldCardDailyLimit,
                    ref failures);
            }
            finally
            {
                try
                {
                    if (File.Exists(databasePath))
                        File.Delete(databasePath);
                }
                catch
                {
                }
            }

            return failures;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
