using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Events.DailyAttendanceAnytime;
using DfoServer.Game.Events.RecommendedDungeons;
using DfoServer.Game.Events.TotalAttendance;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.KnightShield;
using DfoServer.Game.Mailbox;
using DfoServer.Network.Handlers;
using System;

namespace DfoServer.Infrastructure
{
    // 库存、外观通知和专家职业共同使用的进程级依赖簇。
    internal sealed class GameProtocolInventoryDependencies
    {
        internal GameProtocolInventoryDependencies(
            InventoryRefreshSender inventoryRefreshSender,
            KnightShieldService knightShieldService,
            ExperienceItemNotificationService experienceItemNotifications,
            SqliteExpertJobStateRepository expertJobStateRepository,
            ExpertJobPersistenceService expertJobPersistence,
            ExpertJobStoreRuntimeService expertJobStores,
            ExpertJobOperationCoordinator expertJobOperations,
            SqliteSubtype0FieldsRepository subtype0Repository,
            HonorLevelSyncService honorLevel,
            MailboxService mailboxService,
            RecommendDungeonClearStatsService recommendDungeonClears,
            DailyAttendanceAnytimeService dailyAttendanceAnytime,
            TotalAttendanceService totalAttendance,
            MailboxInventoryOverflowRewardSink overflowRewardSink)
        {
            InventoryRefreshSender = inventoryRefreshSender
                ?? throw new ArgumentNullException(nameof(inventoryRefreshSender));
            KnightShieldService = knightShieldService
                ?? throw new ArgumentNullException(nameof(knightShieldService));
            ExperienceItemNotifications = experienceItemNotifications
                ?? throw new ArgumentNullException(nameof(experienceItemNotifications));
            ExpertJobStateRepository = expertJobStateRepository
                ?? throw new ArgumentNullException(nameof(expertJobStateRepository));
            ExpertJobPersistence = expertJobPersistence
                ?? throw new ArgumentNullException(nameof(expertJobPersistence));
            ExpertJobStores = expertJobStores
                ?? throw new ArgumentNullException(nameof(expertJobStores));
            ExpertJobOperations = expertJobOperations
                ?? throw new ArgumentNullException(nameof(expertJobOperations));
            Subtype0Repository = subtype0Repository
                ?? throw new ArgumentNullException(nameof(subtype0Repository));
            HonorLevel = honorLevel
                ?? throw new ArgumentNullException(nameof(honorLevel));
            MailboxService = mailboxService
                ?? throw new ArgumentNullException(nameof(mailboxService));
            RecommendDungeonClears = recommendDungeonClears
                ?? throw new ArgumentNullException(
                    nameof(recommendDungeonClears));
            DailyAttendanceAnytime = dailyAttendanceAnytime
                ?? throw new ArgumentNullException(
                    nameof(dailyAttendanceAnytime));
            TotalAttendance = totalAttendance
                ?? throw new ArgumentNullException(nameof(totalAttendance));
            OverflowRewardSink = overflowRewardSink
                ?? throw new ArgumentNullException(nameof(overflowRewardSink));
        }

        internal InventoryRefreshSender InventoryRefreshSender { get; }

        internal KnightShieldService KnightShieldService { get; }

        internal ExperienceItemNotificationService ExperienceItemNotifications { get; }

        internal SqliteExpertJobStateRepository ExpertJobStateRepository { get; }

        internal ExpertJobPersistenceService ExpertJobPersistence { get; }

        internal ExpertJobStoreRuntimeService ExpertJobStores { get; }

        internal ExpertJobOperationCoordinator ExpertJobOperations { get; }

        internal SqliteSubtype0FieldsRepository Subtype0Repository { get; }

        internal HonorLevelSyncService HonorLevel { get; }

        internal MailboxService MailboxService { get; }

        internal RecommendDungeonClearStatsService RecommendDungeonClears { get; }

        internal DailyAttendanceAnytimeService DailyAttendanceAnytime { get; }

        internal TotalAttendanceService TotalAttendance { get; }

        internal MailboxInventoryOverflowRewardSink OverflowRewardSink { get; }
    }
}
