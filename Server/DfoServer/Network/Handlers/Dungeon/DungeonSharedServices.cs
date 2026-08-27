using System;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Progression;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Dungeon composition root. Runtime behavior belongs to the exposed
    // application services/projectors, not to this object.
    internal sealed class DungeonSharedServices
    {
        internal const string ProtocolLogName = "GameProtocol";

        internal string ConnectionString { get; }
        internal IGameDatabase Database { get; }
        internal SqliteSelectCharacterDataSource SelectCharacterDataSource { get; }
        internal IRentalTimeProvider RentalTimeProvider { get; }
        internal InventoryRefreshSender InventoryRefresh { get; }
        internal IMercenaryRestrictionService MercenaryRestrictions { get; }

        internal Game.ReviveCoin.ReviveCoinService ReviveCoin { get; }
        internal DeathTowerCoordinator DeathTower { get; }
        internal Game.Quests.QuestDropService QuestDrops { get; }
        internal Game.Quests.DailyChallengeService DailyChallenges { get; }
        internal Game.Dungeon.DungeonItemAcquisitionService ItemAcquisition { get; }
        internal DungeonPersistentMechanismCoordinator PersistentMechanisms { get; }
        internal SqliteCharacterRepository CharacterRepository { get; }
        internal SqliteSubtype1Repository Subtype1Repository { get; }
        internal SqliteCharacterStateRepository CharacterStateRepository { get; }
        internal Game.Dungeon.DungeonDifficultyPermissionService
            DungeonDifficultyPermissions { get; }
        internal SqliteCharacterProgressRepository ProgressRepository { get; }
        internal SqliteSubtype0FieldsRepository Subtype0FieldsRepository { get; }
        internal HonorLevelSyncService HonorLevel { get; }
        internal AccountExperienceProgressService AccountExperience { get; }
        internal GrowthCapsuleSyncService GrowthCapsuleSync { get; }
        internal CharacterExperienceService CharacterExperience { get; }
        internal Game.Dungeon.TowerOfDespairProgressService TowerOfDespairProgress { get; }
        internal Game.Party.PartyManager PartyManager { get; }
        internal Game.Raid.RaidManager RaidManager { get; }
        internal Game.Session.ISessionDirectory Sessions { get; }
        internal CardRewardCoordinator CardRewards { get; }
        internal Game.Dungeon.DropService Drops { get; }
        internal Game.Dungeon.DungeonEntryAdmissionApplicationService
            EntryAdmission { get; }
        internal Game.Dungeon.DungeonEntryLimitService EntryLimits { get; }
        internal DungeonAdmissionRejectSender AdmissionRejects { get; }
        internal DungeonProgressNotificationProjector ProgressNotifications { get; }
        internal DungeonTownReturnCoordinator TownReturn { get; }
        internal Game.Dungeon.DungeonPersistentEffectApplicationService PersistentEffects { get; }
        internal Game.Dungeon.DungeonInstanceRegistry InstanceRegistry { get; }
        internal Game.Dungeon.Tournament.TournamentDungeonApplicationService
            Tournaments { get; }
        internal Game.Dungeon.BloodAltar.BloodAltarDungeonApplicationService
            BloodAltars { get; }
        internal Game.Dungeon.BloodAltar.BloodAltarRewardPlanningService
            BloodAltarRewardPlanner { get; }

        internal DungeonSharedServices(
            Game.ReviveCoin.ReviveCoinService reviveCoin,
            SqliteCharacterRepository characterRepository,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            IRentalTimeProvider rentalTimeProvider,
            string connectionString,
            InventoryRefreshSender inventoryRefresh,
            Game.Party.PartyManager partyManager = null,
            Game.Session.ISessionDirectory sessions = null,
            Game.Quests.QuestDropService questDropService = null,
            AccountExperienceProgressService accountExperience = null,
            IMercenaryRestrictionService mercenaryRestrictions = null,
            Game.Dungeon.DungeonPersistentEffectApplicationService persistentEffects = null,
            Game.Dungeon.DungeonInstanceRegistry instanceRegistry = null,
            Game.Raid.RaidManager raidManager = null,
            IGameDatabase database = null)
        {
            ReviveCoin = reviveCoin
                ?? throw new ArgumentNullException(nameof(reviveCoin));
            CharacterRepository = characterRepository
                ?? throw new ArgumentNullException(nameof(characterRepository));
            Database = database ?? GameDatabase.CreateDefault();
            ConnectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : Database.ConnectionString;
            PartyManager = partyManager;
            RaidManager = raidManager;
            Sessions = sessions;
            SelectCharacterDataSource = selectCharacterDataSource
                ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            InventoryRefresh = inventoryRefresh;
            MercenaryRestrictions = mercenaryRestrictions;
            RentalTimeProvider = rentalTimeProvider
                ?? SystemRentalTimeProvider.Instance;

            Drops = new Game.Dungeon.DropService();
            ItemAcquisition = new Game.Dungeon.DungeonItemAcquisitionService(Drops);
            QuestDrops = questDropService ?? new Game.Quests.QuestDropService(
                inventoryRefresh,
                ConnectionString,
                rollDrop: null,
                itemAcquisition: ItemAcquisition,
                database: Database);
            DailyChallenges = new Game.Quests.DailyChallengeService(
                ConnectionString,
                new Game.DailyReset.DailyResetService(Database));
            Subtype1Repository = new SqliteSubtype1Repository(
                Database);
            CharacterStateRepository = new SqliteCharacterStateRepository(
                Database);
            DungeonDifficultyPermissions =
                new Game.Dungeon.DungeonDifficultyPermissionService(
                    Database);
            ProgressRepository = new SqliteCharacterProgressRepository(
                Database);
            Subtype0FieldsRepository = new SqliteSubtype0FieldsRepository(
                Database);
            HonorLevel = new HonorLevelSyncService(
                CharacterRepository,
                Database);
            AccountExperience = accountExperience
                ?? new AccountExperienceProgressService(
                    CharacterRepository,
                    Database);
            GrowthCapsuleSync = new GrowthCapsuleSyncService(
                CharacterRepository,
                Database);
            CharacterExperience = new CharacterExperienceService(
                AccountExperience,
                Database);
            ProgressNotifications = new DungeonProgressNotificationProjector(
                ConnectionString,
                CharacterRepository,
                Subtype1Repository,
                ProgressRepository,
                Subtype0FieldsRepository,
                HonorLevel,
                AccountExperience,
                Sessions);
            PersistentEffects = persistentEffects
                ?? new Game.Dungeon.DungeonPersistentEffectApplicationService(
                    ConnectionString,
                    database: Database);
            InstanceRegistry = instanceRegistry
                ?? new Game.Dungeon.DungeonInstanceRegistry(
                    ClockService.Instance);
            TownReturn = new DungeonTownReturnCoordinator(
                InstanceRegistry,
                ProgressNotifications,
                Sessions);
            var entryCost = new Game.Dungeon.DungeonEntryCostService(Database);
            EntryAdmission =
                new Game.Dungeon.DungeonEntryAdmissionApplicationService(
                    entryCost);
            EntryLimits = new Game.Dungeon.DungeonEntryLimitService(Database);
            Tournaments =
                new Game.Dungeon.Tournament
                    .TournamentDungeonApplicationService();
            BloodAltars =
                new Game.Dungeon.BloodAltar
                    .BloodAltarDungeonApplicationService();
            BloodAltarRewardPlanner =
                new Game.Dungeon.BloodAltar
                    .BloodAltarRewardPlanningService();

            PersistentMechanisms = new DungeonPersistentMechanismCoordinator(
                CharacterStateRepository);
            DeathTower = new DeathTowerCoordinator(
                ConnectionString,
                sendExpGrantNotification: (session, settlement) =>
                    ProgressNotifications.SendExpGrantNotificationAsync(
                        session,
                        settlement?.ExperienceGrant,
                        "DEATH_TOWER_SETTLEMENT",
                        reloadMissingAccountProgress: true),
                accountExperience: AccountExperience,
                sendInDungeonLevelUpFollowups:
                    ProgressNotifications.SendInDungeonLevelUpFollowups,
                inventoryRefresh: inventoryRefresh,
                instanceRegistry: InstanceRegistry,
                townReturn: TownReturn,
                sessionDirectory: Sessions);
            TowerOfDespairProgress =
                new Game.Dungeon.TowerOfDespairProgressService(
                    new Game.Dungeon.TowerOfDespairProgressRepository(
                        Database));
            CardRewards = new CardRewardCoordinator(
                new Game.Dungeon.CardRewardService(PersistentEffects));
            AdmissionRejects = new DungeonAdmissionRejectSender();
        }
    }
}
