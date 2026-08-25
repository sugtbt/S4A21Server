using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events;
using DfoServer.Game.Events.Joust;
using DfoServer.Game.Inventory;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.KnightShield;
using DfoServer.Game.Lottery;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Party;
using DfoServer.Game.Raid;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Pets;
using System;
using System.Threading.Tasks;

namespace DfoServer.Infrastructure
{
    // 正式运行时组合根。当前首批先统一数据库与协议处理器入口，
    // 后续按业务模块把 GameProtocolHandler 内的对象装配逐步迁到这里。
    public sealed class ServerRuntimeBuilder : IDisposable
    {
        private GameProtocolCoreDependencies _gameProtocolCore;
        private GameProtocolInventoryDependencies _gameProtocolInventory;
        private GameProtocolWorldDependencies _gameProtocolWorld;
        private GameProtocolCharacterInventoryHandlers
            _gameProtocolCharacterInventoryHandlers;
        private GameProtocolExpertJobHandlers _gameProtocolExpertJobHandlers;
        private GameProtocolTownDungeonHandlers _gameProtocolTownDungeonHandlers;
        private GameProtocolSocialHandlers _gameProtocolSocialHandlers;
        private GameProtocolFeatureHandlers _gameProtocolFeatureHandlers;
        private CharacterSessionLifecycleCoordinator
            _characterSessionLifecycle;
        private PartyUdpRelay _boundUdpRelay;
        private PartyUdpRelay _boundPvpUdpRelay;
        private bool _gameProtocolBuilt;
        private ISessionDirectory _boundSessionDirectory;
        private bool _disposed;

        public ServerRuntimeBuilder(IGameDatabase database)
        {
            Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public IGameDatabase Database { get; }

        public static ServerRuntimeBuilder CreateDefault()
        {
            return new ServerRuntimeBuilder(GameDatabase.CreateDefault());
        }

        public GameProtocolHandler BuildGameProtocolHandler(
            ISessionDirectory sessionDirectory,
            Func<byte[], Task> broadcastGamePacket = null,
            PartyUdpRelay udpRelay = null,
            PartyUdpRelay pvpUdpRelay = null)
        {
            ThrowIfDisposed();
            if (sessionDirectory == null)
                throw new ArgumentNullException(nameof(sessionDirectory));
            if (_gameProtocolBuilt)
            {
                throw new InvalidOperationException(
                    "ServerRuntimeBuilder can build only one game protocol runtime.");
            }

            var core = GetOrCreateGameProtocolCoreDependencies();
            var inventory = GetOrCreateGameProtocolInventoryDependencies(core);
            var world = GetOrCreateGameProtocolWorldDependencies(
                sessionDirectory,
                core);
            var characterInventoryHandlers =
                GetOrCreateGameProtocolCharacterInventoryHandlers(
                    core,
                    inventory,
                    world,
                    broadcastGamePacket);
            var expertJobHandlers = GetOrCreateGameProtocolExpertJobHandlers(
                core,
                inventory,
                world);
            var townDungeonHandlers = GetOrCreateGameProtocolTownDungeonHandlers(
                core,
                inventory,
                world);
            var socialHandlers = GetOrCreateGameProtocolSocialHandlers(
                core,
                world,
                townDungeonHandlers,
                udpRelay,
                pvpUdpRelay);
            var featureHandlers = GetOrCreateGameProtocolFeatureHandlers(
                core,
                inventory,
                world,
                townDungeonHandlers,
                broadcastGamePacket);
            var characterSessionLifecycle =
                GetOrCreateCharacterSessionLifecycleCoordinator(
                    core,
                    inventory,
                    world,
                    characterInventoryHandlers,
                    expertJobHandlers,
                    townDungeonHandlers,
                    socialHandlers,
                    featureHandlers);
            var handler = new GameProtocolHandler(
                sessionDirectory,
                broadcastGamePacket,
                udpRelay,
                pvpUdpRelay,
                core,
                inventory,
                world,
                characterInventoryHandlers,
                expertJobHandlers,
                townDungeonHandlers,
                socialHandlers,
                featureHandlers,
                characterSessionLifecycle);
            _gameProtocolBuilt = true;
            return handler;
        }

        internal GameProtocolCoreDependencies GetOrCreateGameProtocolCoreDependencies()
        {
            ThrowIfDisposed();
            if (_gameProtocolCore != null)
                return _gameProtocolCore;

            var characterRepository = new SqliteCharacterRepository(Database);
            var accountRepository = new SqliteAccountRepository(Database);
            var rentalTimeProvider = SystemRentalTimeProvider.Instance;
            var dailyResetService = new DailyResetService(Database);
            var dungeonPersistentEffects =
                new DungeonPersistentEffectApplicationService(
                    Database.ConnectionString,
                    database: Database);
            var inventoryLifecycle = new InventoryCharacterLifecycleService(
                Database,
                rentalTimeProvider);
            var experienceItemUseService = new ExperienceItemUseService(
                Database,
                rentalTimeProvider);
            var selectCharacterDataSource = new SqliteSelectCharacterDataSource(
                Database,
                characterRepository,
                inventoryLifecycle,
                rentalTimeProvider,
                dailyResetService);
            var getUserInfoTemplate = new SqliteUserInfoBlobRepository(Database)
                .LoadGetUserInfoTemplate();
            var eventManager = new EventManager(Database);
            eventManager.Initialize();

            _gameProtocolCore = new GameProtocolCoreDependencies(
                Database,
                accountRepository,
                characterRepository,
                rentalTimeProvider,
                dailyResetService,
                dungeonPersistentEffects,
                experienceItemUseService,
                selectCharacterDataSource,
                getUserInfoTemplate,
                eventManager);
            return _gameProtocolCore;
        }

        internal static GameProtocolCoreDependencies CreateGameProtocolCoreDependencies(
            IGameDatabase database)
        {
            return new ServerRuntimeBuilder(database)
                .GetOrCreateGameProtocolCoreDependencies();
        }

        internal GameProtocolInventoryDependencies GetOrCreateGameProtocolInventoryDependencies(
            GameProtocolCoreDependencies core = null)
        {
            ThrowIfDisposed();
            if (_gameProtocolInventory != null)
                return _gameProtocolInventory;

            core ??= GetOrCreateGameProtocolCoreDependencies();
            _gameProtocolInventory = CreateGameProtocolInventoryDependencies(core);
            return _gameProtocolInventory;
        }

        internal static GameProtocolInventoryDependencies CreateGameProtocolInventoryDependencies(
            GameProtocolCoreDependencies core)
        {
            if (core == null)
                throw new ArgumentNullException(nameof(core));

            var inventoryRefreshSender = new InventoryRefreshSender(
                core.SelectCharacterDataSource,
                core.CharacterRepository,
                core.Database);
            var knightShieldService = new KnightShieldService(
                new KnightShieldDeckRepository(core.Database));
            var expertJobStateRepository =
                new SqliteExpertJobStateRepository(core.Database);
            var mailboxService = new MailboxService(
                new MailboxRepository(core.Database));

            var dependencies = new GameProtocolInventoryDependencies(
                inventoryRefreshSender,
                knightShieldService,
                new ExperienceItemNotificationService(
                    core.CharacterRepository,
                    core.Database),
                expertJobStateRepository,
                new ExpertJobPersistenceService(core.Database),
                new ExpertJobStoreRuntimeService(),
                new ExpertJobOperationCoordinator(),
                new SqliteSubtype0FieldsRepository(core.Database),
                new HonorLevelSyncService(
                    core.CharacterRepository,
                    core.Database),
                mailboxService,
                new MailboxInventoryOverflowRewardSink(mailboxService));
            core.DungeonPersistentEffects.BindOverflowRewardSink(
                dependencies.OverflowRewardSink);
            return dependencies;
        }

        internal GameProtocolWorldDependencies GetOrCreateGameProtocolWorldDependencies(
            ISessionDirectory sessionDirectory,
            GameProtocolCoreDependencies core = null)
        {
            ThrowIfDisposed();
            if (sessionDirectory == null)
                throw new ArgumentNullException(nameof(sessionDirectory));

            if (_gameProtocolWorld != null)
            {
                if (!ReferenceEquals(_boundSessionDirectory, sessionDirectory))
                {
                    throw new InvalidOperationException(
                        "ServerRuntimeBuilder is already bound to another session directory.");
                }
                return _gameProtocolWorld;
            }

            core ??= GetOrCreateGameProtocolCoreDependencies();
            _boundSessionDirectory = sessionDirectory;
            _gameProtocolWorld = CreateGameProtocolWorldDependencies(
                core,
                sessionDirectory);
            return _gameProtocolWorld;
        }

        internal static GameProtocolWorldDependencies CreateGameProtocolWorldDependencies(
            GameProtocolCoreDependencies core,
            ISessionDirectory sessionDirectory)
        {
            if (core == null)
                throw new ArgumentNullException(nameof(core));
            if (sessionDirectory == null)
                throw new ArgumentNullException(nameof(sessionDirectory));

            var mercenaryRepository = new MercenaryRepository(core.Database);
            return new GameProtocolWorldDependencies(
                sessionDirectory,
                new CharacterTransitionCoordinator(sessionDirectory),
                new DungeonInstanceRegistry(ClockService.Instance),
                new PartyManager(),
                new RaidManager(),
                mercenaryRepository,
                new MercenaryRestrictionService(mercenaryRepository));
        }

        internal GameProtocolCharacterInventoryHandlers
            GetOrCreateGameProtocolCharacterInventoryHandlers(
                GameProtocolCoreDependencies core = null,
                GameProtocolInventoryDependencies inventory = null,
                GameProtocolWorldDependencies world = null,
                Func<byte[], Task> broadcastGamePacket = null)
        {
            ThrowIfDisposed();
            if (_gameProtocolCharacterInventoryHandlers != null)
                return _gameProtocolCharacterInventoryHandlers;

            core ??= GetOrCreateGameProtocolCoreDependencies();
            inventory ??= GetOrCreateGameProtocolInventoryDependencies(core);
            if (world == null)
            {
                if (_boundSessionDirectory == null)
                {
                    throw new InvalidOperationException(
                        "World dependencies must be bound before creating handlers.");
                }
                world = GetOrCreateGameProtocolWorldDependencies(
                    _boundSessionDirectory,
                    core);
            }

            _gameProtocolCharacterInventoryHandlers =
                CreateGameProtocolCharacterInventoryHandlers(
                    core,
                    inventory,
                    world,
                    broadcastGamePacket);
            return _gameProtocolCharacterInventoryHandlers;
        }

        internal static GameProtocolCharacterInventoryHandlers
            CreateGameProtocolCharacterInventoryHandlers(
                GameProtocolCoreDependencies core,
                GameProtocolInventoryDependencies inventory,
                GameProtocolWorldDependencies world,
                Func<byte[], Task> broadcastGamePacket)
        {
            if (core == null) throw new ArgumentNullException(nameof(core));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (world == null) throw new ArgumentNullException(nameof(world));

            return new GameProtocolCharacterInventoryHandlers(
                new LoginHandler(
                    core.AccountRepository,
                    core.CharacterRepository,
                    core.Database),
                new CharacterSelectHandler(
                    core.DungeonPersistentEffects,
                    core.SelectCharacterDataSource,
                    core.CharacterRepository,
                    core.GetUserInfoTemplate,
                    world.Sessions,
                    world.DungeonInstances,
                    world.MercenaryRestrictions,
                    core.Database,
                    core.DailyResetService),
                new InventoryHandler(
                    core.ExperienceItemUseService,
                    core.SelectCharacterDataSource,
                    core.CharacterRepository,
                    inventory.InventoryRefreshSender,
                    inventory.ExperienceItemNotifications,
                    inventory.ExpertJobStateRepository,
                    inventory.ExpertJobPersistence,
                    inventory.ExpertJobOperations,
                    broadcastGamePacket,
                    world.MercenaryRestrictions,
                    core.Database,
                    inventory.OverflowRewardSink,
                    inventory.MailboxService),
                new KnightShieldHandler(
                    inventory.KnightShieldService,
                    core.CharacterRepository,
                    core.Database));
        }

        internal GameProtocolExpertJobHandlers
            GetOrCreateGameProtocolExpertJobHandlers(
                GameProtocolCoreDependencies core = null,
                GameProtocolInventoryDependencies inventory = null,
                GameProtocolWorldDependencies world = null)
        {
            ThrowIfDisposed();
            if (_gameProtocolExpertJobHandlers != null)
                return _gameProtocolExpertJobHandlers;

            core ??= GetOrCreateGameProtocolCoreDependencies();
            inventory ??= GetOrCreateGameProtocolInventoryDependencies(core);
            if (world == null)
            {
                if (_boundSessionDirectory == null)
                {
                    throw new InvalidOperationException(
                        "World dependencies must be bound before creating handlers.");
                }
                world = GetOrCreateGameProtocolWorldDependencies(
                    _boundSessionDirectory,
                    core);
            }

            _gameProtocolExpertJobHandlers =
                CreateGameProtocolExpertJobHandlers(core, inventory, world);
            return _gameProtocolExpertJobHandlers;
        }

        internal static GameProtocolExpertJobHandlers
            CreateGameProtocolExpertJobHandlers(
                GameProtocolCoreDependencies core,
                GameProtocolInventoryDependencies inventory,
                GameProtocolWorldDependencies world)
        {
            if (core == null) throw new ArgumentNullException(nameof(core));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (world == null) throw new ArgumentNullException(nameof(world));

            var stores = inventory.ExpertJobStores;
            var states = inventory.ExpertJobStateRepository;
            var persistence = inventory.ExpertJobPersistence;
            var operations = inventory.ExpertJobOperations;
            var refresh = inventory.InventoryRefreshSender;

            return new GameProtocolExpertJobHandlers(
                new ExpertJobStoreHandler(
                    stores,
                    new ExpertJobStorePlacementValidator(),
                    world.PartyManager,
                    world.Sessions,
                    states,
                    states,
                    core.CharacterRepository,
                    inventory.Subtype0Repository,
                    inventory.HonorLevel,
                    persistence,
                    operations,
                    refresh),
                new ExpertJobExtractionHandler(
                    states,
                    core.CharacterRepository,
                    inventory.Subtype0Repository,
                    inventory.HonorLevel,
                    persistence,
                    refresh,
                    operations),
                new ExpertJobCompoundHandler(
                    stores,
                    states,
                    core.CharacterRepository,
                    inventory.Subtype0Repository,
                    inventory.HonorLevel,
                    persistence,
                    refresh,
                    operations),
                new ExpertJobGiveupHandler(
                    stores,
                    new ExpertJobGiveupApplicationService(
                        core.Database,
                        states),
                    new ExpertJobGiveupNotificationProjector(
                        core.CharacterRepository,
                        inventory.Subtype0Repository,
                        inventory.HonorLevel,
                        core.SelectCharacterDataSource,
                        refresh),
                    operations),
                new EnchanterHandler(
                    stores,
                    states,
                    persistence,
                    operations));
        }

        internal GameProtocolTownDungeonHandlers
            GetOrCreateGameProtocolTownDungeonHandlers(
                GameProtocolCoreDependencies core = null,
                GameProtocolInventoryDependencies inventory = null,
                GameProtocolWorldDependencies world = null)
        {
            ThrowIfDisposed();
            if (_gameProtocolTownDungeonHandlers != null)
                return _gameProtocolTownDungeonHandlers;

            core ??= GetOrCreateGameProtocolCoreDependencies();
            inventory ??= GetOrCreateGameProtocolInventoryDependencies(core);
            if (world == null)
            {
                if (_boundSessionDirectory == null)
                {
                    throw new InvalidOperationException(
                        "World dependencies must be bound before creating handlers.");
                }
                world = GetOrCreateGameProtocolWorldDependencies(
                    _boundSessionDirectory,
                    core);
            }

            _gameProtocolTownDungeonHandlers =
                CreateGameProtocolTownDungeonHandlers(core, inventory, world);
            return _gameProtocolTownDungeonHandlers;
        }

        internal static GameProtocolTownDungeonHandlers
            CreateGameProtocolTownDungeonHandlers(
                GameProtocolCoreDependencies core,
                GameProtocolInventoryDependencies inventory,
                GameProtocolWorldDependencies world)
        {
            if (core == null) throw new ArgumentNullException(nameof(core));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (world == null) throw new ArgumentNullException(nameof(world));

            var reviveCoin = new Game.ReviveCoin.ReviveCoinService(
                core.DailyResetService);
            return new GameProtocolTownDungeonHandlers(
                reviveCoin,
                new TownHandler(
                    core.CharacterRepository,
                    core.SelectCharacterDataSource,
                    world.PartyManager,
                    world.Sessions,
                    inventory.InventoryRefreshSender,
                    world.DungeonInstances,
                    world.RaidManager,
                    core.Database),
                new DungeonHandler(
                    core.DungeonPersistentEffects,
                    reviveCoin,
                    core.CharacterRepository,
                    core.SelectCharacterDataSource,
                    core.RentalTimeProvider,
                    core.Database.ConnectionString,
                    inventory.InventoryRefreshSender,
                    world.PartyManager,
                    world.Sessions,
                    mercenaryRestrictions: world.MercenaryRestrictions,
                    instanceRegistry: world.DungeonInstances,
                    raidManager: world.RaidManager,
                    database: core.Database));
        }

        internal GameProtocolSocialHandlers GetOrCreateGameProtocolSocialHandlers(
            GameProtocolCoreDependencies core = null,
            GameProtocolWorldDependencies world = null,
            GameProtocolTownDungeonHandlers townDungeon = null,
            PartyUdpRelay udpRelay = null,
            PartyUdpRelay pvpUdpRelay = null)
        {
            ThrowIfDisposed();
            if (_gameProtocolSocialHandlers != null)
            {
                if (!ReferenceEquals(_boundUdpRelay, udpRelay)
                    || !ReferenceEquals(_boundPvpUdpRelay, pvpUdpRelay))
                {
                    throw new InvalidOperationException(
                        "Social handlers are already bound to another UDP relay set.");
                }
                return _gameProtocolSocialHandlers;
            }

            core ??= GetOrCreateGameProtocolCoreDependencies();
            if (world == null)
            {
                if (_boundSessionDirectory == null)
                {
                    throw new InvalidOperationException(
                        "World dependencies must be bound before creating handlers.");
                }
                world = GetOrCreateGameProtocolWorldDependencies(
                    _boundSessionDirectory,
                    core);
            }
            townDungeon ??= GetOrCreateGameProtocolTownDungeonHandlers(
                core,
                GetOrCreateGameProtocolInventoryDependencies(core),
                world);

            _boundUdpRelay = udpRelay;
            _boundPvpUdpRelay = pvpUdpRelay;
            _gameProtocolSocialHandlers = CreateGameProtocolSocialHandlers(
                core,
                world,
                townDungeon,
                udpRelay,
                pvpUdpRelay);
            return _gameProtocolSocialHandlers;
        }

        internal static GameProtocolSocialHandlers CreateGameProtocolSocialHandlers(
            GameProtocolCoreDependencies core,
            GameProtocolWorldDependencies world,
            GameProtocolTownDungeonHandlers townDungeon,
            PartyUdpRelay udpRelay,
            PartyUdpRelay pvpUdpRelay)
        {
            if (core == null) throw new ArgumentNullException(nameof(core));
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (townDungeon == null)
                throw new ArgumentNullException(nameof(townDungeon));

            var party = new PartyHandler(
                world.PartyManager,
                core.CharacterRepository,
                world.Sessions,
                udpRelay,
                characterTransitions: world.CharacterTransitions,
                database: core.Database);
            var raid = new RaidHandler(
                core.CharacterRepository,
                world.Sessions,
                world.RaidManager);
            var chat = new ChatHandler(
                world.Sessions,
                world.PartyManager);
            var dungeonRejoin =
                new Network.Handlers.Dungeon.DungeonRejoinCoordinator(
                    world.DungeonInstances,
                    world.CharacterTransitions,
                    party.TryRestoreDungeonParticipantAsync,
                    party.RollbackDungeonParticipantRestoreAsync,
                    townDungeon.Town.NotifyLeaveAsync,
                    recoverParticipantEffects: townDungeon.Dungeon
                        .RecoverDungeonParticipantEffectsAsync);
            var pvpRoom = new PvpRoomHandler(
                world.Sessions,
                townDungeon.Town.BuildFullUserInfoPacket,
                world.CharacterTransitions,
                pvpUdpRelay: pvpUdpRelay,
                database: core.Database);
            party.AttachPvpRoomHandler(pvpRoom);

            return new GameProtocolSocialHandlers(
                party,
                raid,
                chat,
                dungeonRejoin,
                new PvpChannelInfoHandler(),
                pvpRoom);
        }

        internal GameProtocolFeatureHandlers GetOrCreateGameProtocolFeatureHandlers(
            GameProtocolCoreDependencies core = null,
            GameProtocolInventoryDependencies inventory = null,
            GameProtocolWorldDependencies world = null,
            GameProtocolTownDungeonHandlers townDungeon = null,
            Func<byte[], Task> broadcastGamePacket = null)
        {
            ThrowIfDisposed();
            if (_gameProtocolFeatureHandlers != null)
                return _gameProtocolFeatureHandlers;

            core ??= GetOrCreateGameProtocolCoreDependencies();
            inventory ??= GetOrCreateGameProtocolInventoryDependencies(core);
            if (world == null)
            {
                if (_boundSessionDirectory == null)
                {
                    throw new InvalidOperationException(
                        "World dependencies must be bound before creating handlers.");
                }
                world = GetOrCreateGameProtocolWorldDependencies(
                    _boundSessionDirectory,
                    core);
            }
            townDungeon ??= GetOrCreateGameProtocolTownDungeonHandlers(
                core,
                inventory,
                world);

            _gameProtocolFeatureHandlers = CreateGameProtocolFeatureHandlers(
                core,
                inventory,
                world,
                townDungeon,
                broadcastGamePacket);
            return _gameProtocolFeatureHandlers;
        }

        internal static GameProtocolFeatureHandlers CreateGameProtocolFeatureHandlers(
            GameProtocolCoreDependencies core,
            GameProtocolInventoryDependencies inventory,
            GameProtocolWorldDependencies world,
            GameProtocolTownDungeonHandlers townDungeon,
            Func<byte[], Task> broadcastGamePacket)
        {
            if (core == null) throw new ArgumentNullException(nameof(core));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (townDungeon == null)
                throw new ArgumentNullException(nameof(townDungeon));

            var lotteryDoubleRewardPolicy = new LotteryDoubleRewardPolicy(
                core.DailyResetService,
                core.Database.ConnectionString);
            var lotteryItem = new LotteryItemHandler(
                new LotteryItemOpenService(
                    core.Database.ConnectionString,
                    new LotteryItemDefinitionProvider(),
                    lotteryDoubleRewardPolicy),
                new LotteryOpenPlanner(lotteryDoubleRewardPolicy),
                new LotteryOpenSessionCoordinator(),
                new LotteryItemResponseSender(
                    lotteryDoubleRewardPolicy,
                    inventory.InventoryRefreshSender,
                    core.Database.ConnectionString,
                    broadcastGamePacket),
                inventory.OverflowRewardSink);

            var mercenaryService = new MercenaryService(
                world.MercenaryRepository,
                core.CharacterRepository,
                new MercenaryAvatarBonusTierProvider(core.Database),
                mailDelivery: new MailboxMercenaryMailDelivery(
                    inventory.MailboxService));
            mercenaryService.RegisterDeliveryClock(ClockService.Instance);
            PetCreatureRuntimeService.EnsureClockRegistered();
            var joustService = new JoustService(
                core.Database,
                inventory.MailboxService);
            joustService.Initialize();
            joustService.RegisterClock(ClockService.Instance);
            var eventJoustHandler = new EventJoustHandler(
                joustService,
                inventory.InventoryRefreshSender,
                world.Sessions);
            eventJoustHandler.RegisterClock(ClockService.Instance);

            return new GameProtocolFeatureHandlers(
                lotteryItem,
                new PetCreatureHandler(
                    core.SelectCharacterDataSource,
                    inventory.InventoryRefreshSender),
                new SecretShopHandler(inventory.InventoryRefreshSender),
                new StaminaHandler(
                    inventory.InventoryRefreshSender,
                    core.Database),
                new SettingsHandler(world.Sessions, core.Database),
                new CeraShopHandler(
                    core.SelectCharacterDataSource,
                    inventory.InventoryRefreshSender,
                    core.Database,
                    inventory.OverflowRewardSink),
                new SkillHandler(
                    core.CharacterRepository,
                    inventory.InventoryRefreshSender,
                    core.Database,
                    core.SelectCharacterDataSource),
                new LuckyStarHandler(
                    core.SelectCharacterDataSource,
                    core.RentalTimeProvider,
                    inventory.InventoryRefreshSender,
                    core.Database),
                new RentalHandler(
                    core.SelectCharacterDataSource,
                    core.RentalTimeProvider,
                    inventory.InventoryRefreshSender,
                    core.Database),
                new MercenaryExpeditionHandler(mercenaryService),
                new MailboxHandler(
                    core.CharacterRepository,
                    inventory.MailboxService,
                    world.Sessions,
                    inventory.InventoryRefreshSender),
                new CollectionBoxHandler(inventory.InventoryRefreshSender),
                new ShopCoinEventHandler(
                    townDungeon.ReviveCoin,
                    inventory.InventoryRefreshSender),
                new MercenaryHandler(
                    core.CharacterRepository,
                    core.Database),
                new GrowthCapsuleHandler(
                    inventory.InventoryRefreshSender,
                    core.CharacterRepository,
                    core.Database),
                new GoldLimitHandler(
                    new Game.Currency.CharacterGoldLimitRepository(
                        core.Database),
                    inventory.InventoryRefreshSender),
                new CraneMiniGameHandler(
                    inventory.InventoryRefreshSender,
                    inventory.OverflowRewardSink),
                eventJoustHandler);
        }

        internal CharacterSessionLifecycleCoordinator
            GetOrCreateCharacterSessionLifecycleCoordinator(
                GameProtocolCoreDependencies core = null,
                GameProtocolInventoryDependencies inventory = null,
                GameProtocolWorldDependencies world = null,
                GameProtocolCharacterInventoryHandlers
                    characterInventoryHandlers = null,
                GameProtocolExpertJobHandlers expertJobHandlers = null,
                GameProtocolTownDungeonHandlers townDungeonHandlers = null,
                GameProtocolSocialHandlers socialHandlers = null,
                GameProtocolFeatureHandlers featureHandlers = null)
        {
            ThrowIfDisposed();
            if (_characterSessionLifecycle != null)
                return _characterSessionLifecycle;

            core ??= GetOrCreateGameProtocolCoreDependencies();
            inventory ??= GetOrCreateGameProtocolInventoryDependencies(core);
            if (world == null)
            {
                if (_boundSessionDirectory == null)
                {
                    throw new InvalidOperationException(
                        "World dependencies must be bound before creating lifecycle coordination.");
                }
                world = GetOrCreateGameProtocolWorldDependencies(
                    _boundSessionDirectory,
                    core);
            }
            characterInventoryHandlers ??=
                GetOrCreateGameProtocolCharacterInventoryHandlers(
                    core,
                    inventory,
                    world);
            expertJobHandlers ??= GetOrCreateGameProtocolExpertJobHandlers(
                core,
                inventory,
                world);
            townDungeonHandlers ??= GetOrCreateGameProtocolTownDungeonHandlers(
                core,
                inventory,
                world);
            socialHandlers ??= GetOrCreateGameProtocolSocialHandlers(
                core,
                world,
                townDungeonHandlers,
                _boundUdpRelay,
                _boundPvpUdpRelay);
            featureHandlers ??= GetOrCreateGameProtocolFeatureHandlers(
                core,
                inventory,
                world,
                townDungeonHandlers);

            _characterSessionLifecycle =
                CreateCharacterSessionLifecycleCoordinator(
                    core,
                    inventory,
                    world,
                    characterInventoryHandlers,
                    expertJobHandlers,
                    townDungeonHandlers,
                    socialHandlers,
                    featureHandlers);
            return _characterSessionLifecycle;
        }

        internal static CharacterSessionLifecycleCoordinator
            CreateCharacterSessionLifecycleCoordinator(
                GameProtocolCoreDependencies core,
                GameProtocolInventoryDependencies inventory,
                GameProtocolWorldDependencies world,
                GameProtocolCharacterInventoryHandlers
                    characterInventoryHandlers,
                GameProtocolExpertJobHandlers expertJobHandlers,
                GameProtocolTownDungeonHandlers townDungeonHandlers,
                GameProtocolSocialHandlers socialHandlers,
                GameProtocolFeatureHandlers featureHandlers)
        {
            if (core == null) throw new ArgumentNullException(nameof(core));
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (characterInventoryHandlers == null)
                throw new ArgumentNullException(nameof(characterInventoryHandlers));
            if (expertJobHandlers == null)
                throw new ArgumentNullException(nameof(expertJobHandlers));
            if (townDungeonHandlers == null)
                throw new ArgumentNullException(nameof(townDungeonHandlers));
            if (socialHandlers == null)
                throw new ArgumentNullException(nameof(socialHandlers));
            if (featureHandlers == null)
                throw new ArgumentNullException(nameof(featureHandlers));

            return new CharacterSessionLifecycleCoordinator(
                characterInventoryHandlers.Login,
                characterInventoryHandlers.CharacterSelect,
                core.CharacterRepository,
                core.SelectCharacterDataSource,
                world.Sessions,
                world.CharacterTransitions,
                expertJobHandlers.Store,
                townDungeonHandlers.Town,
                world.DungeonInstances,
                socialHandlers.DungeonRejoin,
                featureHandlers.LotteryItem,
                featureHandlers.CraneMiniGame,
                featureHandlers.EventJoust,
                socialHandlers.PvpRoom,
                inventory.InventoryRefreshSender,
                core.Database,
                core.DailyResetService);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _gameProtocolSocialHandlers?.Dispose();
            _gameProtocolWorld?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ServerRuntimeBuilder));
        }
    }
}
