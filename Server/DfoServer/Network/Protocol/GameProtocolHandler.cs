using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.KnightShield;
using DfoServer.Game.Lottery;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Mercenary;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Pets;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network
{
    public class GameProtocolHandler : BaseProtocolHandler, IDisposable
    {
        private readonly LoginHandler _loginHandler;
        private readonly CharacterSelectHandler _characterSelectHandler;
        private readonly GrowupChangeHandler _growupChangeHandler;
        private readonly CharacterSessionLifecycleCoordinator
            _characterSessionLifecycle;
        private readonly InventoryHandler _inventoryHandler;
        private readonly LotteryItemHandler _lotteryItemHandler;
        private readonly KnightShieldHandler _knightShieldHandler;
        private readonly TownHandler _townHandler;
        private readonly DungeonHandler _dungeonHandler;
        private readonly SecretShopHandler _secretShopHandler;
        private readonly StaminaHandler _staminaHandler;
        private readonly SkillHandler _skillHandler;
        private readonly SettingsHandler _settingsHandler;
        private readonly CeraShopHandler _ceraShopHandler;
        private readonly LuckyStarHandler _luckyStarHandler;
        private readonly RentalHandler _rentalHandler;
        private readonly MailboxHandler _mailboxHandler;
        private readonly CollectionBoxHandler _collectionBoxHandler;
        private readonly ShopCoinEventHandler _shopCoinEventHandler;
        private readonly InventoryRefreshSender _inventoryRefreshSender;
        private readonly PetCreatureHandler _petCreatureHandler;
        private readonly MercenaryHandler _mercenaryHandler;
        private readonly MercenaryExpeditionHandler _mercenaryExpeditionHandler;
        private readonly GrowthCapsuleHandler _growthCapsuleHandler;
        private readonly GoldLimitHandler _goldLimitHandler;
        private readonly CraneMiniGameHandler _craneMiniGameHandler;
        private readonly EventJoustHandler _eventJoustHandler;
        private readonly ExpertJobStoreHandler _expertJobStoreHandler;
        private readonly ExpertJobExtractionHandler _expertJobExtractionHandler;
        private readonly ExpertJobCompoundHandler _expertJobCompoundHandler;
        private readonly ExpertJobGiveupHandler _expertJobGiveupHandler;
        private readonly EnchanterHandler _enchanterHandler;
        // 组队与城镇/副本共享同一个 PartyManager 实例: 副本 fan-out 与跟随退出都要看到同一份队伍状态。
        private readonly Game.Party.PartyManager _partyManager;
        private readonly DungeonInstanceRegistry _dungeonInstances;
        private readonly PartyHandler _partyHandler;
        private readonly RaidHandler _raidHandler;
        private readonly ChatHandler _chatHandler;
        private readonly Handlers.Dungeon.DungeonRejoinCoordinator
            _dungeonRejoin;
        private readonly CharacterTransitionCoordinator _characterTransitions;
        private readonly PvpChannelInfoHandler _pvpChannelInfoHandler;
        private readonly PvpRoomHandler _pvpRoomHandler;
        private readonly IGameDatabase _database;
        private readonly GameProtocolWorldDependencies _worldDependencies;
        private readonly bool _ownsWorldDependencies;
        private readonly GameProtocolSocialHandlers _socialHandlers;
        private readonly GameCommandRegistry _cmdDispatch;

        public override string ProtocolName => "GameProtocol";

        public GameProtocolHandler(
            ISessionDirectory sessionDirectory,
            Func<byte[], Task> broadcastGamePacket = null,
            PartyUdpRelay udpRelay = null,
            PartyUdpRelay pvpUdpRelay = null,
            IGameDatabase database = null)
            : this(
                sessionDirectory,
                broadcastGamePacket,
                udpRelay,
                pvpUdpRelay,
                ServerRuntimeBuilder.CreateGameProtocolCoreDependencies(
                    database ?? GameDatabase.CreateDefault()),
                inventory: null,
                world: null,
                characterInventoryHandlers: null,
                expertJobHandlers: null,
                townDungeonHandlers: null,
                socialHandlers: null,
                featureHandlers: null,
                characterSessionLifecycle: null)
        {
        }

        internal GameProtocolHandler(
            ISessionDirectory sessionDirectory,
            Func<byte[], Task> broadcastGamePacket,
            PartyUdpRelay udpRelay,
            PartyUdpRelay pvpUdpRelay,
            GameProtocolCoreDependencies core,
            GameProtocolInventoryDependencies inventory,
            GameProtocolWorldDependencies world,
            GameProtocolCharacterInventoryHandlers characterInventoryHandlers,
            GameProtocolExpertJobHandlers expertJobHandlers,
            GameProtocolTownDungeonHandlers townDungeonHandlers,
            GameProtocolSocialHandlers socialHandlers,
            GameProtocolFeatureHandlers featureHandlers,
            CharacterSessionLifecycleCoordinator characterSessionLifecycle)
        {
            if (core == null)
                throw new ArgumentNullException(nameof(core));
            inventory ??= ServerRuntimeBuilder
                .CreateGameProtocolInventoryDependencies(core);
            _ownsWorldDependencies = world == null;
            world ??= ServerRuntimeBuilder.CreateGameProtocolWorldDependencies(
                core,
                sessionDirectory);
            characterInventoryHandlers ??= ServerRuntimeBuilder
                .CreateGameProtocolCharacterInventoryHandlers(
                    core,
                    inventory,
                    world,
                    broadcastGamePacket);
            expertJobHandlers ??= ServerRuntimeBuilder
                .CreateGameProtocolExpertJobHandlers(core, inventory, world);
            townDungeonHandlers ??= ServerRuntimeBuilder
                .CreateGameProtocolTownDungeonHandlers(core, inventory, world);
            socialHandlers ??= ServerRuntimeBuilder
                .CreateGameProtocolSocialHandlers(
                    core,
                    world,
                    townDungeonHandlers,
                    udpRelay,
                    pvpUdpRelay);
            featureHandlers ??= ServerRuntimeBuilder
                .CreateGameProtocolFeatureHandlers(
                    core,
                    inventory,
                    world,
                    townDungeonHandlers,
                    broadcastGamePacket);
            characterSessionLifecycle ??= ServerRuntimeBuilder
                .CreateCharacterSessionLifecycleCoordinator(
                    core,
                    inventory,
                    world,
                    characterInventoryHandlers,
                    expertJobHandlers,
                    townDungeonHandlers,
                    socialHandlers,
                    featureHandlers);
            _worldDependencies = world;
            _socialHandlers = socialHandlers;

            var database = core.Database;
            _database = database;

            var characterRepository = core.CharacterRepository;
            var sqliteSelectCharacterDataSource = core.SelectCharacterDataSource;

            _characterTransitions = world.CharacterTransitions;
            _dungeonInstances = world.DungeonInstances;
            _loginHandler = characterInventoryHandlers.Login;
            _characterSelectHandler = characterInventoryHandlers.CharacterSelect;
            _inventoryRefreshSender = inventory.InventoryRefreshSender;
            _growupChangeHandler = new GrowupChangeHandler(
                new GrowupChangeApplicationService(),
                _inventoryRefreshSender);
            _knightShieldHandler = characterInventoryHandlers.KnightShield;
            _inventoryHandler = characterInventoryHandlers.Inventory;
            _lotteryItemHandler = featureHandlers.LotteryItem;
            _petCreatureHandler = featureHandlers.PetCreature;
            // 组队与城镇/副本共享同一个 PartyManager 实例: 跟随退出/副本 fan-out 都要看到同一份队伍状态。
            _partyManager = world.PartyManager;
            _expertJobStoreHandler = expertJobHandlers.Store;
            _enchanterHandler = expertJobHandlers.Enchanter;
            _expertJobExtractionHandler = expertJobHandlers.Extraction;
            _expertJobCompoundHandler = expertJobHandlers.Compound;
            _expertJobGiveupHandler = expertJobHandlers.Giveup;
            _townHandler = townDungeonHandlers.Town;
            _dungeonHandler = townDungeonHandlers.Dungeon;
            _secretShopHandler = featureHandlers.SecretShop;
            _staminaHandler = featureHandlers.Stamina;
            _settingsHandler = featureHandlers.Settings;
            _ceraShopHandler = featureHandlers.CeraShop;
            _skillHandler = featureHandlers.Skill;
            _luckyStarHandler = featureHandlers.LuckyStar;
            _rentalHandler = featureHandlers.Rental;
            _mercenaryExpeditionHandler = featureHandlers.MercenaryExpedition;
            _mailboxHandler = featureHandlers.Mailbox;
            _collectionBoxHandler = featureHandlers.CollectionBox;
            _shopCoinEventHandler = featureHandlers.ShopCoinEvent;
            _mercenaryHandler = featureHandlers.Mercenary;
            _partyHandler = socialHandlers.Party;
            _raidHandler = socialHandlers.Raid;
            _chatHandler = socialHandlers.Chat;
            _dungeonRejoin = socialHandlers.DungeonRejoin;
            _growthCapsuleHandler = featureHandlers.GrowthCapsule;
            _goldLimitHandler = featureHandlers.GoldLimit;
            _craneMiniGameHandler = featureHandlers.CraneMiniGame;
            _eventJoustHandler = featureHandlers.EventJoust;
            _pvpChannelInfoHandler = socialHandlers.PvpChannelInfo;
            _pvpRoomHandler = socialHandlers.PvpRoom;
            _characterSessionLifecycle = characterSessionLifecycle;

            _cmdDispatch = new GameCommandRegistry();
            _cmdDispatch.RegisterGroup("login", RegisterLoginHandlers);
            _cmdDispatch.RegisterGroup("character", RegisterCharacterHandlers);
            _cmdDispatch.RegisterGroup("inventory", RegisterInventoryHandlers);
            _cmdDispatch.RegisterGroup("pet", RegisterPetHandlers);
            _cmdDispatch.RegisterGroup("sort-item-lock", RegisterSortItemLockHandlers);
            _cmdDispatch.RegisterGroup("equipment-item-lock", RegisterEquipmentItemLockHandlers);
            _cmdDispatch.RegisterGroup("equipment-socket", RegisterEquipmentSocketHandlers);
            _cmdDispatch.RegisterGroup("equipment-emblem", RegisterEquipmentEmblemHandlers);
            _cmdDispatch.RegisterGroup("avatar-socket", RegisterAvatarSocketHandlers);
            _cmdDispatch.RegisterGroup("avatar-emblem", RegisterAvatarEmblemHandlers);
            _cmdDispatch.RegisterGroup("dungeon", RegisterDungeonHandlers);
            _cmdDispatch.RegisterGroup("skill", RegisterSkillHandlers);
            _cmdDispatch.RegisterGroup("town", RegisterTownHandlers);
            _cmdDispatch.RegisterGroup("settings", RegisterSettingsHandlers);
            _cmdDispatch.RegisterGroup("quest", RegisterQuestHandlers);
            _cmdDispatch.RegisterGroup("mailbox", RegisterMailboxHandlers);
            _cmdDispatch.RegisterGroup("collection-box", RegisterCollectionBoxHandlers);
            _cmdDispatch.RegisterGroup("mercenary", RegisterMercenaryHandlers);
            _cmdDispatch.RegisterGroup("party-chat", RegisterPartyHandlers);
            _cmdDispatch.RegisterGroup("raid-lottery-overflow", RegisterRaidHandlers);
            _cmdDispatch.RegisterGroup(
                "udp-endpoint",
                d => d[(ushort)CmdPacketType.SET_UDP_IP_PORT] =
                    HandleSetUdpEndpoint);
            _cmdDispatch.RegisterGroup("expert-job", RegisterExpertJobHandlers);
            _cmdDispatch.RegisterGroup("misc-pvp", RegisterMiscHandlers);
            _cmdDispatch.RegisterGroup(
                "shop-coin-event",
                d => d[0x00CF] = _shopCoinEventHandler.HandleShopCoinEvent);
            _cmdDispatch.RegisterGroup("friend", RegisterFriendHandlers);
            _cmdDispatch.RegisterGroup("event-joust", RegisterEventJoustHandlers);
        }

        public void Dispose()
        {
            _socialHandlers.Dispose();
            if (_ownsWorldDependencies)
                _worldDependencies.Dispose();
        }

        public override Task OnClientConnected(
            EnhancedClientSession session)
        {
            return _characterSessionLifecycle.HandleConnectedAsync(session);
        }

        public override async Task OnClientDisconnected(
            EnhancedClientSession session)
        {
            _raidHandler.ClearSession(session.SessionId);
            await _characterSessionLifecycle.HandleDisconnectedAsync(session);
        }

        public override async Task OnPacketReceived(EnhancedClientSession session, FlexiblePacket packet)
        {
            var header = packet.GetHeader<GamePacketHeader>();
            var body = packet.BodyData;

            PacketFileLogger.Log("RECV", packet.GetBytes());

            try
            {
                await OnPacketReceived_86JP(session, header, body);
            }
            catch (Exception ex)
            {
                FileLogger.Log(ex.ToString());
                throw;
            }
        }

        public async Task OnPacketReceived_86JP(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!_characterSessionLifecycle.CanDispatch(session, header))
                return;

            if (header.cmd == 0)
            {

            }

            if (header.cmd == 1)
            {
                if (_cmdDispatch.TryGetValue(header.type, out var handler))
                    await handler(session, header, body);
                else
                    FileLogger.Log($"[GameProtocol] Unhandled CMD type=0x{header.type:X4} body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            }
        }

        #region CMD Dispatch Registration

        private void RegisterLoginHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x0001] = _loginHandler.Handle_ENUM_CMDPACKET_LOGIN;
            d[0x04DD] = _loginHandler.Handle_ENUM_CMDPACKET_CHECK_USER_CONNECTION;
        }

        private void RegisterCharacterHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x0004] =
                _characterSessionLifecycle.HandleSelectCharacterAsync;
            d[0x0005] = _characterSelectHandler.Handle_ENUM_CMDPACKET_CREATE_CHARACTER;
            d[0x0006] = _characterSelectHandler.Handle_ENUM_CMDPACKET_DELETE_CHARACTER;
            d[0x0007] = _characterSessionLifecycle
                .HandleReturnSelectCharacterAsync;
            d[0x0008] = _characterSelectHandler.Handle_ENUM_CMDPACKET_GET_USERINFO;
            d[0x01A8] = _characterSelectHandler
                .Handle_ENUM_CMDPACKET_OTHER_USER_TITLE_BOOK_LIST;
            d[(ushort)CmdPacketTypeA21.RE_GROWUP_CHANGE] =
                _growupChangeHandler.Handle;
            d[0x0009] = _staminaHandler.Handle_ENUM_CMDPACKET_RECOVER_STAMINA;
            d[0x02B5] = _characterSelectHandler.Handle_ENUM_CMDPACKET_CHECK_DOUBLE_CHARACTER_NAME;
            d[0x0127] = _characterSelectHandler.Handle_CHANGE_CHARAC_SLOT;
        }

        private void RegisterPartyHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketType.SEND_MESSAGE] =
                _chatHandler.Handle_SEND_MESSAGE;
            d[0x000C] = _partyHandler.Handle_SET_PARTY_INFO;        // 12 创建/更新队伍
            d[0x000D] = async (s, h, b) =>
            {
                var userId = s?.Player?.UserId ?? (ushort)0;
                var wasInParty = userId != 0
                    && _partyManager.GetPartyByUser(userId) != null;
                await _partyHandler.Handle_LEAVE_PARTY(s, h, b);
                if (wasInParty && _partyManager.GetPartyByUser(userId) == null)
                    await _raidHandler.HandleNormalPartyLeftAsync(userId);
            };                                                      // 13 leave party
            d[0x000E] = _partyHandler.Handle_WALKOUT_PARTY_MEMBER;  // 14 踢人
            d[0x000A] = _partyHandler.Handle_REQUEST_PEER;          // 10 右键同屏玩家→组队/交易邀请(按uid)→给目标发 SC 0x0007 弹框
            d[0x000B] = _partyHandler.Handle_RES_PEER;              // 11 被邀请者应答(body=邀请者uid+reqType)→组队并广播 PARTY_INFO
            // 419 creates a chat/1:1 conversation; party invites use 0x000A/0x000B.
            d[0x01A3] = _chatHandler.Handle_CREATE_GROUP;
            d[(ushort)CmdPacketType.ONE_TO_ONE_CHAT_STATE] =
                _chatHandler.Handle_ONE_TO_ONE_CHAT_STATE;
            d[0x00A6] = _partyHandler.Handle_CALL_PARTY_MEMBER_REALTIME_INFO;  // 166 请求成员实时信息(HP%)
            d[0x0079] = _partyHandler.Handle_CHANGE_HOST;           // 121 委托队长(body=1字节槽位)
            // P2P 上报类: df 只喂统计计数器, 不回包不转发。收下即忽略, 消掉 Unhandled 日志。
            d[(ushort)CmdPacketType.P2P_HOLE_PUNCHING_SUCCESS_RATE] = (s, h, b) => Task.CompletedTask;
            d[0x0061] = (s, h, b) => Task.CompletedTask;            // PEER_CONNECT_RESULT
            d[0x0031] = (s, h, b) => Task.CompletedTask;            // REPORT_BAD_P2P_USER
            d[0x01DF] = (s, h, b) => Task.CompletedTask;            // P2P_STATISTICS
        }

        private void RegisterRaidHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketType.CREATE_RAID] = _raidHandler.HandleCreateRaid;
            d[(ushort)CmdPacketType.RAID_ENTRY_COST_INFO] = _raidHandler.HandleEntryCostInfo;
            d[(ushort)CmdPacketType.RAID_BUFF_SYSTEM] = _raidHandler.HandleRaidBuffSystem;
            d[(ushort)CmdPacketType.RAID_MONSTER_HP] = _raidHandler.HandleRaidMonsterHp;
            d[(ushort)CmdPacketType.LEAVE_RAID] = _raidHandler.HandleLeaveRaid;
            d[(ushort)CmdPacketType.START_RAID] = _raidHandler.HandleStartRaid;
            d[(ushort)CmdPacketType.RAID_MOVIE_SKIP] = _raidHandler.HandleRaidMovieSkip;
            d[(ushort)CmdPacketType.SELECT_RAID_REWARD_CARD] = _raidHandler.HandleSelectRaidRewardCard;
            d[(ushort)CmdPacketType.RAID_DO_BEHAVIOR] = _raidHandler.HandleRaidDoBehavior;
            d[(ushort)CmdPacketType.RAID_SET_SYMBOL] = _raidHandler.HandleRaidSetSymbol;
            d[(ushort)CmdPacketType.RAID_MANAGER_WORK] = _raidHandler.HandleRaidManagerWork;
            d[(ushort)CmdPacketType.MODIFY_RAID_INFO] = _raidHandler.HandleModifyRaidInfo;
            d[0x00D9] = async (s, h, b) =>
            {
                if (await _raidHandler.TryHandleCreatePopupClose(s, h, b))
                    return;
                await _lotteryItemHandler.HandleOverflowInfo(s, h, b);
            };
        }
        private async Task HandleSetUdpEndpoint(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var previous = session?.Player?.ReportedUdpEndpoint;
            await _partyHandler.Handle_SET_UDP_IP_PORT(session, header, body);
            var current = session?.Player?.ReportedUdpEndpoint;
            if (current != null && !ReferenceEquals(previous, current))
                await _pvpRoomHandler.HandleReportedUdpEndpointChanged(session);
        }

        // A21 sends SECURITY_STATUS during dungeon loading. The response is
        // a fixed six-byte zero body; it does not alter gameplay state.
        private Task HandleSecurityStatus(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.SECURITY_STATUS,
                new byte[6]));
        }

        private void RegisterInventoryHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x0012] = async (s, h, b) =>
            {
                if (await _dungeonHandler.TryHandleDeathTowerDeleteItem(s, h, b))
                    return;
                await _inventoryHandler.Handle_ENUM_CMDPACKET_DELETE_ITEM(s, h, b);
            };                                                                    //18
            d[0x0013] = async (s, h, b) =>
            {
                if (await _dungeonHandler.TryHandleDeathTowerMoveItem(s, h, b))
                    return;
                if (await _knightShieldHandler.TryHandleMoveItemSpace(s, h, b))
                    return;
                await _inventoryHandler.Handle_ENUM_CMDPACKET_MOVE_ITEMSPACE(s, h, b);
            };                                                                    //19
            d[0x0014] = async (s, h, b) =>
            {
                if (await _dungeonHandler.TryHandleDeathTowerSortItem(s, h, b))
                    return;
                await _inventoryHandler.Handle_ENUM_CMDPACKET_SORT_ITEM(s, h, b);
            };                                                                    //20
            d[0x0015] = _inventoryHandler.Handle_ENUM_CMDPACKET_BUY_ITEM;          //21
            d[0x02CC] = _inventoryHandler.Handle_ENUM_CMDPACKET_SHOP_PURCHASE_COUNT;//716
            d[0x0016] = _inventoryHandler.Handle_ENUM_CMDPACKET_SELL_ITEM;         //22
            d[0x0017] = _inventoryHandler.Handle_ENUM_CMDPACKET_REPAIR_EQUIPMENT;  //23 装备修理
            d[(ushort)CmdPacketTypeA21.DECREASE_DURABILITY] =
                _inventoryHandler.Handle_ENUM_CMDPACKET_DECREASE_DURABILITY;        //48 装备耐久扣减
            d[0x0019] = _inventoryHandler.Handle_ENUM_CMDPACKET_COMPOUND_ITEM;     //25 compound item / recipe
            d[0x001A] = _inventoryHandler.Handle_ENUM_CMDPACKET_DISJOINT_ITEM;     //26 系统分解
            d[0x00CA] = _inventoryHandler.Handle_DISJOINT_AVATAR;                  //202 时装分解
            d[0x001B] = _lotteryItemHandler.HandleUseLotteryItem;                 //27
            d[(ushort)CmdPacketType.INCREASE_STATUS] = async (s, h, b) =>
            {
                var prevLevel = s.Player?.Level;
                await _inventoryHandler.Handle_ENUM_CMDPACKET_INCREASE_STATUS(s, h, b);
                // 经验道具(INCREASE_STATUS)可能升级：前后比较 Level，
                // 升级则向把 self 加为好友的人重推好友列表（节点数据，不分频道）。
                if (s.Player != null && s.Player.Level != prevLevel)
                    await UnitedFriendSystem.NotifyFriendListInfoChanged(
                        s, _worldDependencies.Sessions);
            };
            d[(ushort)CmdPacketTypeA21.REQUEST_EVENT_SERVER_LEVEL_UP] =
                _inventoryHandler.Handle_REQUEST_EVENT_SERVER_LEVEL_UP;
            d[0x00CC] = _inventoryHandler.Handle_ENUM_CMDPACKET_PURIFY_ITEM;
            d[0x00CD] = _inventoryHandler.Handle_ENUM_CMDPACKET_INVEST_ITEM_AMPLIFY_OPTION;
            d[0x00D0] = _inventoryHandler.Handle_OPEN_MAGIC_BOX_SINGLE;
            d[(ushort)CmdPacketType.INCREASE_CHANCE_LOTTERY_RESET] =
                _lotteryItemHandler.HandleIncreaseChanceLotteryReset;
            d[(ushort)CmdPacketType.CRANE_START_USE] = _craneMiniGameHandler.HandleStartUse;
            d[(ushort)CmdPacketType.CRANE_PICKUP] = _craneMiniGameHandler.HandlePickup;
            d[0x0050] = _inventoryHandler.Handle_ENUM_CMDPACKET_UPGRADE_ITEM;      //80
            d[(ushort)CmdPacketType.UPGRADE_ITEM_SEPARATE] = _inventoryHandler.Handle_UPGRADE_ITEM_SEPARATE;
            d[0x0051] = _inventoryHandler.Handle_ENUM_CMDPACKET_RESET_ITEM_ATTR;   //81 装备品级调整箱(万花镜)
            d[0x00A0] = _inventoryHandler.Handle_OPEN_SELECTABLE_PACKAGE;
            d[(ushort)CmdPacketType.UPGRADE_CHRONICLE] = _inventoryHandler.Handle_UPGRADE_CHRONICLE;
            d[(ushort)CmdPacketType.ENCHANT_3RD_CHRONICLE_ITEM] = _inventoryHandler.Handle_ENCHANT_3RD_CHRONICLE_ITEM;
            d[0x0110] = _inventoryHandler.Handle_ENUM_CMDPACKET_ENCHANT_BY_BEAD;   //272
            d[(ushort)CmdPacketType.USE_GEM] = _inventoryHandler.Handle_USE_GEM;   //826 守护珠镶嵌
            d[0x0191] = _inventoryHandler.Handle_UNSEAL_RANDOM_OPTION;             //401
            d[0x0197] = _inventoryHandler.Handle_REGENERATION_RANDOM_OPTION;       //407 equipment compound
            d[(ushort)CmdPacketTypeA21.TITLE_BOOK_PUT] = _inventoryHandler.Handle_TITLE_BOOK;
            d[0x01B6] = _inventoryHandler.Handle_CHANGE_RANDOM_OPTION;             //438
            d[(ushort)CmdPacketTypeA21.TITLE_BOOK_GET] = _inventoryHandler.Handle_TITLE_BOOK;
            d[0x019E] = _inventoryHandler.Handle_ENUM_CMDPACKET_MONSTERCARD_BIND;  //414 monster card synthesis
            d[0x025C] = _inventoryHandler.Handle_UPGRADE_CARD;                     //604 monster card upgrade
            d[0x0207] = _inventoryHandler.Handle_OPEN_AVATAR_PACKAGE;
            d[0x0218] = _inventoryHandler.Handle_USE_BOOSTER_ITEM;
            d[(ushort)CmdPacketTypeA21.USE_RIGHT_OF_CHANGE_GROW_TYPE] =
                _inventoryHandler.Handle_USE_RIGHT_OF_CHANGE_GROW_TYPE;
            d[(ushort)CmdPacketTypeA21.CARGO_TRANSPORT_ITEM] =
                _inventoryHandler.Handle_CARGO_TRANSPORT_ITEM;
            d[(ushort)CmdPacketTypeA21.EPIC_BOOK_MAKE_ITEM] =
                _inventoryHandler.Handle_EPIC_BOOK_MAKE_ITEM;
            d[(ushort)CmdPacketTypeA21.OPEN_AURA_SKIN_SLOT] =
                _inventoryHandler.Handle_OPEN_AURA_SKIN_SLOT;
            d[0x0239] = _inventoryHandler.Handle_SET_CLONE_TITLE;                  //569
            d[(ushort)CmdPacketType.USE_RANDOMBOX_ITEM_EXPAND] = _inventoryHandler.Handle_OPEN_MAGIC_BOX;
            d[0x0063] = _inventoryHandler.Handle_COMPOUND_AVATAR;                  //99 合并装扮(时装合成)
            d[0x0100] = _inventoryHandler.Handle_COMPOUND_EMBLEM;                  //256 徽章合成
            d[(ushort)CmdPacketType.BIND_PLUS] = _inventoryHandler.Handle_COMPOUND_AVATAR_SET;              // 8件高级装扮100%合成稀有装扮(克隆装扮合成器)
            d[(ushort)CmdPacketType.ADD_EQUIPMENT_EFFECT] = _inventoryHandler.Handle_ADD_EQUIPMENT_EFFECT;  // 武器特效符文添加
            d[0x0131] = _inventoryHandler.Handle_CREATE_ACCOUNT_CARGO;               //305 开通金库
            d[0x0132] = _inventoryHandler.Handle_UPGRADE_ACCOUNT_CARGO;             //306 扩容金库
            d[0x0133] = _inventoryHandler.Handle_DEPOSIT_MONEY;                    //307 金库存金币
            d[0x0134] = _inventoryHandler.Handle_WITHDRAW_MONEY;                   //308 金库取金币
            d[0x0198] = _inventoryHandler.Handle_UPGRADE_CARGO;                    //408 扩容个人仓库
            d[0x01CC] = _inventoryHandler.Handle_AVATAR_OPTION_CHANGE;             //460 时装属性调整箱
            d[(ushort)CmdPacketTypeA21.USE_DYE] = _inventoryHandler.Handle_USE_DYE; //499 时装染色剂
            d[(ushort)CmdPacketType.USE_LIMIT_CUBE] =
                _inventoryHandler.Handle_USE_LIMIT_CUBE;
            d[(ushort)CmdPacketType.USE_TITLE_CHANGE_ITEM] =
                _inventoryHandler.Handle_USE_TITLE_CHANGE_ITEM;
            d[KnightShieldDeckBodyBuilder.ChangeDeckCommandType] = _knightShieldHandler.HandleChangeDeckInfo;
        }

        private void RegisterPetHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            GameCommandHandler useStackable = async (s, h, b) =>
            {
                if (await _dungeonHandler.TryHandleDeathTowerUseStackable(s, h, b))
                    return;
                if (await _inventoryHandler.TryHandleDungeonUseStackable(s, h, b))
                    return;
                if (await _petCreatureHandler.TryHandleUseStackable(s, h, b))
                    return;

                await _inventoryHandler.Handle_ENUM_CMDPACKET_USE_STACKABLE(s, h, b);
            };
            d[0x002C] = useStackable;
            d[(ushort)CmdPacketTypeA21.USE_STACKABLE_ACTION] = useStackable;
            d[0x0064] = _petCreatureHandler.HandleRenameCreature;
            d[0x0066] = _petCreatureHandler.HandleHatchCreatureEgg;
            d[0x007A] = _petCreatureHandler.HandleCreatureScriptMessage;
            d[0x00AD] = _petCreatureHandler.HandleHatchCreatureEgg;
            d[0x00AE] = _petCreatureHandler.HandleRequestHatchedCreature;
            d[0x01E0] = _petCreatureHandler.HandleVerifyCreatureQuest;
        }

        private void RegisterSortItemLockHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x02CA] = _inventoryHandler.Handle_ENUM_CMDPACKET_TOGGLE_SORT_ITEM_LOCK;
            d[0x02CB] = _inventoryHandler.Handle_ENUM_CMDPACKET_UNLOCK_SORT_ITEM_LOCK;
        }

        private void RegisterEquipmentItemLockHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x010B] = _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_LOCK;
            d[0x010C] = _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK;
            d[0x010D] = _inventoryHandler.Handle_ENUM_CMDPACKET_REQUEST_ITEM_UNLOCK_CANCEL;
        }

        private void RegisterEquipmentSocketHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketType.ADD_EQUIPMENT_SOCKET] = _inventoryHandler.Handle_EQUIPMENT_SOCKET_OPEN;
        }

        private void RegisterEquipmentEmblemHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketType.USE_EMBLEM_FOR_EQUIPMENT] = _inventoryHandler.Handle_EQUIPMENT_EMBLEM_ATTACH;
        }

        private void RegisterAvatarSocketHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x00CE] = _inventoryHandler.Handle_AVATAR_SOCKET_OPEN;
        }

        private void RegisterAvatarEmblemHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x00C9] = _inventoryHandler.Handle_AVATAR_EMBLEM_ATTACH;
        }

        private async Task HandleRaidAwareSetPlayResult(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            var dungeonId = run?.DungeonId ?? 0;
            var wasCleared = run?.Phase == DungeonRunPhase.Cleared;
            await _dungeonHandler.Handle_SET_PLAY_RESULT(session, header, body);
            if (wasCleared
                && run != null
                && run.Phase == DungeonRunPhase.ResultShown)
            {
                if (RaidHandler.IsAntonRaidDungeon(dungeonId))
                {
                    Handlers.Dungeon.DungeonRunLifecycle.CancelAutoFlip(session);
                    run.CardRewards = null;
                    run.Phase = DungeonRunPhase.CardsRevealed;
                    FileLogger.Log(
                        $"[GameProtocol] RAID_DUNGEON_SETTLEMENT " +
                        $"normal-card-flow suppressed dungeon={dungeonId}");
                }
                await _raidHandler.HandleDungeonClearedAsync(session, dungeonId);
            }
        }

        private async Task HandleRaidAwareDungeonExit(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            Func<EnhancedClientSession, GamePacketHeader, byte[], Task> handler,
            string reason)
        {
            var run = session?.Player?.CurrentRun;
            var dungeonId = run?.DungeonId ?? 0;
            await handler(session, header, body);
            if (run != null && session?.Player?.CurrentRun == null)
                await _raidHandler.HandleDungeonAbortedAsync(
                    session,
                    dungeonId,
                    reason);
        }

        private async Task HandleRaidAwareFinishLoading(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            await _townHandler.Handle_ENUM_CMDPACKET_FINISH_LOADING(
                session,
                header,
                body);
            await _raidHandler.HandleDungeonLoadedAsync(session);
        }

        private async Task HandleRaidAwareCharacterDeath(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var dungeonId = session?.Player?.CurrentRun?.DungeonId ?? 0;
            await _dungeonHandler.Handle_ENUM_CMDPACKET_DIE_CHARACTER(
                session,
                header,
                body);
            if (RaidHandler.IsAntonRaidDungeon(dungeonId))
                await _raidHandler.HandleDungeonCharacterDeathAsync(
                    session,
                    dungeonId);
        }

        private async Task HandleRaidAwareUseCoin(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var dungeonId = session?.Player?.CurrentRun?.DungeonId ?? 0;
            var targetUserId = body != null && body.Length >= sizeof(ushort)
                ? BitConverter.ToUInt16(body, 0)
                : session?.Player?.UserId ?? (ushort)0;
            var revived = await _dungeonHandler.HandleUseCoinWithResultAsync(
                session,
                header,
                body);
            if (revived && RaidHandler.IsAntonRaidDungeon(dungeonId))
            {
                await _raidHandler.HandleDungeonCharacterReviveAsync(
                    session,
                    dungeonId,
                    targetUserId);
            }
        }
        private void RegisterDungeonHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x000F] = _dungeonHandler.Handle_ENUM_CMDPACKET_ENTER_SELECT_DUNGEON;
            d[0x0010] = _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_DUNGEON;
            d[(ushort)CmdPacketTypeA21.REQUEST_CIRCLE_ENTER] =
                _dungeonHandler.Handle_ENUM_CMDPACKET_REQUEST_CIRCLE_ENTER;
            d[(ushort)CmdPacketTypeA21.DIE_MONSTER] = _dungeonHandler.Handle_ENUM_CMDPACKET_DIE_MONSTER;
            d[0x0028] = HandleRaidAwareCharacterDeath;       //40
            d[0x0029] = HandleRaidAwareUseCoin;
            d[(ushort)CmdPacketTypeA21.GET_ITEM] = _dungeonHandler.Handle_ENUM_CMDPACKET_GET_ITEM;
            d[0x002D] = _dungeonHandler.Handle_ENUM_CMDPACKET_MOVE_MAP;
            d[0x002E] = HandleRaidAwareSetPlayResult;                    //46
            d[0x002F] = _dungeonHandler.Handle_ENUM_CMDPACKET_DROP_ITEM;
            d[0x0045] = _dungeonHandler.Handle_CARD_START_REQUEST;
            d[0x0047] = _dungeonHandler.Handle_ENUM_CMDPACKET_SELECT_CARD;
            d[0x0048] = (s, h, b) => HandleRaidAwareDungeonExit(s, h, b, _dungeonHandler.Handle_ENUM_CMDPACKET_EPLP_COMMAND, "eplp");
            d[0x0075] = _dungeonHandler.Handle_BOSS_DIE_CHECK;
            d[0x007B] = (s, h, b) => HandleRaidAwareDungeonExit(s, h, b, _dungeonHandler.Handle_ENUM_CMDPACKET_DEATH_RESPAWN, "death-respawn");       //123
            d[0x008F] = _dungeonHandler.Handle_ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG; //143
            d[0x00BF] = _dungeonHandler.Handle_ENUM_CMDPACKET_DUNGEON_EVENT_STORY_PAUSE; //191
            d[0x0128] = _secretShopHandler.HandleBuyRequest;
            d[0x0129] = _secretShopHandler.HandleOpenClose;
            d[0x013C] = _dungeonHandler.HandleDungeonMechanismCommand;
            d[0x01E4] = _dungeonHandler.Handle_ENUM_CMDPACKET_TUTORIAL_LEVEL_UP;   //484
            d[0x0211] = _dungeonHandler.HandleDungeonMechanismCommand;
            d[0x0253] = _dungeonHandler.HandleDungeonMechanismCommand;
            d[0x026B] = _dungeonHandler.HandleDungeonMechanismCommand;
            d[0x026D] = _dungeonHandler.HandleDungeonMechanismCommand;
            d[0x0270] = _dungeonHandler.HandleDungeonMechanismCommand;
            d[(ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT_STATE] =
                _dungeonHandler.HandleDungeonMechanismCommand;
            d[(ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT] =
                _dungeonHandler.HandleDungeonMechanismCommand;
            d[(ushort)CmdPacketType.BLOOD_ROUND_UI_PREPARE_FINISH_] =
                _dungeonHandler.HandleDungeonMechanismCommand;
            d[(ushort)CmdPacketType.DIE_BLOOD_MONSTER] =
                _dungeonHandler.HandleDungeonMechanismCommand;
            d[(ushort)CmdPacketType.SELECT_ULTIMATE_DIFFICULTY] =
                _dungeonHandler.HandleDungeonMechanismCommand;
            d[(ushort)CmdPacketTypeA21.PREMIUM_SERVICE] = (session, header, body) =>
                PremiumQueryHandler.Handle_PREMIUM_SERVICE(
                    session,
                    header,
                    body,
                    _database);                                                   //786
            d[(ushort)CmdPacketType.VERY_DIFFICULT_HELL_PARTY] = _dungeonHandler.Handle_ENUM_CMDPACKET_GORGEOUS_CHALLENGE_TOGGLE;
            d[(ushort)CmdPacketType.BREAK_TRAP_RESULT] = _dungeonHandler.HandleDungeonMechanismCommand;
            d[0x009F] = _dungeonHandler.Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD; // 159
            d[(ushort)CmdPacketType.REJOIN_DUNGEON] = _dungeonRejoin.HandleRejoinAsync;
            d[(ushort)CmdPacketType.CANCEL_REJOIN_DUNGEON] = _dungeonRejoin.HandleCancelAsync;
        }

        private void RegisterSkillHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x001C] = _skillHandler.Handle_CHANGE_SKILLSLOT;                     //28
            d[0x001D] = _skillHandler.Handle_BUY_SKILL;                            //29
            d[0x0104] = _skillHandler.Handle_CHANGE_ANOTHER_SKILL_TREE;            //260
            d[0x014B] = _skillHandler.Handle_CHANGE_SKILL_COMMAND;                 //331
            d[0x014C] = _skillHandler.Handle_RESET_ALL_SKILL_COMMANDS;             //332
            d[0x01EC] = _skillHandler.Handle_SKILL_INIT;                           //492
            d[0x01FD] = _skillHandler.Handle_COMBO_SKILL_INFO;                     //509
            d[0x01FF] = _skillHandler.Handle_COMBO_SKILL_EXTENSION_QUICK_SLOT_RESET; //511
        }

        private void RegisterTownHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x0023] = _townHandler.Handle_ENUM_CMDPACKET_SET_USER_POSITION;
            d[0x0024] = async (s, h, b) =>
            {
                if (b != null && b.Length >= 6)
                    await _expertJobStoreHandler.CloseSessionAsync(s, includeOwner: true);
                await _townHandler.Handle_ENUM_CMDPACKET_SET_USER_AREA(s, h, b);
                await _expertJobStoreHandler.SendAreaStoresToAsync(s);
            };
            d[(ushort)CmdPacketTypeA21.FINISH_LOADING] = HandleRaidAwareFinishLoading;
            d[0x002A] = (s, h, b) => HandleRaidAwareDungeonExit(s, h, b, _townHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME, "giveup");
            d[0x0084] = (s, h, b) => HandleRaidAwareDungeonExit(s, h, b, _townHandler.Handle_ENUM_CMDPACKET_GIVEUP_GAME, "back-to-village");
            d[0x00ED] = _townHandler.Handle_ENUM_CMDPACKET_TELEPORT;
            d[(ushort)CmdPacketTypeA21.GET_PCROOM_TIME_POINT_ITEM] =
                _townHandler.Handle_ENUM_CMDPACKET_GET_PCROOM_TIME_POINT_ITEM;
            d[(ushort)CmdPacketType.PARTY_TELEPORT] =
                _townHandler.Handle_ENUM_CMDPACKET_PARTY_TELEPORT;
        }

        private void RegisterSettingsHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x00C5] = _settingsHandler.Handle_SAVE_GAME_OPTION_1;
            d[0x00C6] = (s, h, b) => { _settingsHandler.Handle_SAVE_GAME_OPTION_2(s, h, b); return Task.CompletedTask; };
            d[0x0170] = (s, h, b) => { _settingsHandler.Handle_SAVE_QUICKCHAT(s, h, b); return Task.CompletedTask; };
            d[0x00FE] = _settingsHandler.Handle_CHANGE_EMOTION;
            d[0x01C0] = (s, h, b) => { _settingsHandler.Handle_SAVE_CHARACTER_OPTION(s, h, b); return Task.CompletedTask; };
        }

        private void RegisterQuestHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketType.IMAGE_COMMUNICATION_EQUIPMENT_USE] =
                async (s, h, b) =>
                {
                    if (s.GameSession != null)
                    {
                        await s.GameSession.QuestManager
                            .HandleImageCommunicationEquipmentUseAsync(
                                h.type,
                                b);
                    }
                };
            d[0x001F] = async (s, h, b) => //31
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleAcceptQuestAsync(
                        h.type,
                        b,
                        s.SessionId);
            };
            d[0x0020] = async (s, h, b) => //32
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleGiveupQuestAsync(
                        h.type,
                        b,
                        s.SessionId);
            };
            d[0x0021] = async (s, h, b) => //33
            {
                if (s.GameSession != null)
                {
                    var sourceRun = s.Player?.CurrentRun;
                    var sourceEvent = sourceRun != null
                        ? DungeonEventEnvelope.Create(
                            sourceRun,
                            s.Player.CharacterId,
                            "client quest set-trigger")
                        : null;
                    var result = await s.GameSession.QuestManager
                        .HandleSetTriggerAsync(h.type, b, s.SessionId);
                    await _dungeonHandler.HandleQuestSetTriggerResultAsync(
                        s,
                        result,
                        sourceEvent);
                }
            };
            d[0x0022] = async (s, h, b) => //34
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleFinishQuestAsync(
                        h.type,
                        b,
                        s.SessionId);
            };
            d[(ushort)CmdPacketTypeA21.SCENARIO_MODE_CLEAR_QUEST] = async (s, h, b) =>
            {
                if (s.GameSession != null)
                    await s.GameSession.QuestManager.HandleScenarioModeClearQuestAsync(
                        h.type,
                        b,
                        s.SessionId);
            };
            d[0x01FB] = (s, h, b) =>
            {
                s.GameSession?.QuestManager.HandleSaveQuestNotify(b);
                return Task.CompletedTask;
            };
            d[(ushort)CmdPacketTypeA21.DAILY_CHALLENGE_REWARD] = async (s, h, b) =>
            {
                if (s.GameSession == null)
                    return;

                var result = s.GameSession.QuestManager.HandleDailyChallengeReward(
                    s.SessionId,
                    b);
                await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    h.type,
                    DailyChallengeRewardAckBuilder.Build(result)));

                if (result?.GrantedReward == true && result.Changes.HasChanges)
                {
                    var slotsByList = new Dictionary<InventoryListType, List<short>>();
                    foreach (var change in result.Changes.Slots)
                    {
                        if (!slotsByList.TryGetValue(change.ListType, out var slots))
                        {
                            slots = new List<short>();
                            slotsByList[change.ListType] = slots;
                        }
                        slots.Add(change.SlotIndex);
                    }

                    foreach (var pair in slotsByList)
                        await _inventoryRefreshSender.SendUpdateItemList(s, pair.Key, pair.Value);
                }

                if (result?.Snapshot != null)
                {
                    await s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.DAILY_CHALLENGE,
                        DailyChallengeBodyBuilder.Build(result.Snapshot)));
                }

                FileLogger.Log(
                    $"[GameProtocol] DAILY_CHALLENGE_REWARD cid={s.Player?.CharacterId ?? 0} "
                    + $"group={result?.GroupIndex ?? -1} status={result?.Status.ToString() ?? "null"} "
                    + $"item={result?.ItemId ?? 0} count={result?.ItemCount ?? 0}");
            };
        }

        private void RegisterMailboxHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x005E] = _mailboxHandler.HandleSendMailbox;
            d[0x005F] = _mailboxHandler.HandleClaimMailbox;
            d[0x0060] = _mailboxHandler.HandleOpenMailbox;
            d[0x0086] = _mailboxHandler.HandleChangeLetterStatMailbox;
            d[0x013B] = _mailboxHandler.HandleSendMultiMailbox;
            d[0x0144] = _mailboxHandler.HandleQueryCharacterInfoMailbox;
        }

        private void RegisterCollectionBoxHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketType.SELECT_COLLECTBOX] = _collectionBoxHandler.HandleQueryCollectionBox;
            d[(ushort)CmdPacketType.ADD_COLLECTBOX_ITEM] = _collectionBoxHandler.HandleInsertCollectBoxItem;
            d[(ushort)CmdPacketType.REMOVE_COLLECTBOX_ITEM] = _collectionBoxHandler.HandleRemoveCollectBoxItem;
        }

        private void RegisterMercenaryHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketTypeA21.MERCENARY_RETURN] = _mercenaryExpeditionHandler.HandleReturn;
            d[(ushort)CmdPacketTypeA21.MERCENARY_INFO] = _mercenaryExpeditionHandler.HandleInfo;
            d[(ushort)CmdPacketTypeA21.MERCENARY_COMPETITION] = _mercenaryExpeditionHandler.HandleCompetition;
            d[(ushort)CmdPacketTypeA21.REQUEST_CHARAC_SKILL_INFO] = _mercenaryHandler.HandleMercenaryRequest;
            d[(ushort)CmdPacketTypeA21.SELECT_STRIKER] = _mercenaryHandler.HandleMercenaryRequest;
        }

        // 好友：0x0122 ADD / 0x0123 DELETE（UnitedFriendSystem 聚合处理器）。
        // 处理器需会话目录（推在线好友通知/组列表），取 world 依赖绑定的同目录。
        private void RegisterFriendHandlers(
            GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketTypeA21.ADD_UNITED_SERVER_FRIEND] =
                (s, h, b) => UnitedFriendSystem.HandleAddUnitedServerFriend(
                    s, h, b, _worldDependencies.Sessions);
            d[(ushort)CmdPacketTypeA21.DELETE_UNITED_SERVER_FRIEND] =
                (s, h, b) => UnitedFriendSystem.HandleDeleteUnitedServerFriend(
                    s, h, b, _worldDependencies.Sessions);
        }

        private void RegisterEventJoustHandlers(
            GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketTypeA21.JOUST_INFO] =
                _eventJoustHandler.HandleInfoAsync;
            d[(ushort)CmdPacketTypeA21.JOUST_BETTING] =
                _eventJoustHandler.HandleBettingAsync;
            d[(ushort)CmdPacketTypeA21.JOUST_MATCH_HISTORY] =
                _eventJoustHandler.HandleMatchHistoryAsync;
        }

        private void RegisterMiscHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[0x0003] = (s, h, b) =>
                s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0003, CommonPacketBodyBuilder.BuildSuccessAck()));
            d[0x0040] = _ceraShopHandler.HandleCeraShopPurchase;                   //64
            d[(ushort)CmdPacketType.GEN_CERATICKET] = _ceraShopHandler.HandleGenCeraTicket;
            d[(ushort)CmdPacketTypeA21.ACHIEVEMENT_TRIGGER] = _inventoryHandler.Handle_ACHIEVEMENT_TRIGGER;
            d[0x01DE] = _dungeonHandler.HandleDungeonSceneUniqueIdReport;           //478
            d[0x02A8] = (s, h, b) =>
                s.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02A8, new byte[] { 0x00, 0x00 }));
            d[RentalHandler.CommandType] = _rentalHandler.HandleRentWeapon;
            d[(ushort)CmdPacketTypeA21.CHARGE_RENTPOINT] =
                _luckyStarHandler.HandleShopPurchasePacket;
            d[(ushort)CmdPacketType.GET_EXPAND_EXP_GAGE_REWARD] = _growthCapsuleHandler.HandleClaimAsync;
            d[(ushort)CmdPacketType.UPGRADE_CARRY_GOLD] = _goldLimitHandler.HandleUpgradeAsync;
            d[PvpChannelInfoHandler.CommandType] =
                _pvpChannelInfoHandler.HandlePvpChannelInfo;
            d[PvpRoomHandler.MakeRoomCommandType] =
                _pvpRoomHandler.HandleMakeRoom;
            d[PvpRoomHandler.EnterRoomCommandType] =
                _pvpRoomHandler.HandleEnterRoom;
            d[PvpRoomHandler.SetSeatStateCommandType] =
                _pvpRoomHandler.HandleSetSeatState;
            d[PvpRoomHandler.SetReadyStateCommandType] =
                _pvpRoomHandler.HandleSetReadyState;
            d[PvpRoomHandler.SetTeamModeCommandType] =
                _pvpRoomHandler.HandleSetTeamMode;
            d[PvpRoomHandler.DiePvpCharacterCommandType] =
                _pvpRoomHandler.HandleDiePvpCharacter;
            d[PvpRoomHandler.PvpTimeOutCommandType] =
                _pvpRoomHandler.HandlePvpTimeOut;
            d[PvpRoomHandler.EndPvpResultCommandType] =
                _pvpRoomHandler.HandleEndPvpResult;
            d[PvpRoomHandler.PvpRankResponseCommandType] =
                _pvpRoomHandler.HandlePvpRankResponse;
            d[PvpRoomHandler.CompleteLoadPvpCommandType] =
                _pvpRoomHandler.HandleCompleteLoadPvp;
            d[PvpRoomHandler.ConnectP2pPvpCommandType] =
                _pvpRoomHandler.HandleConnectP2pPvp;
            d[PvpRoomHandler.PvpRequestFightCommandType] =
                _pvpRoomHandler.HandlePvpRequestFight;
            d[(ushort)CmdPacketType.SECURITY_STATUS] = HandleSecurityStatus;
        }

        private void RegisterExpertJobHandlers(GameCommandRegistry.GameCommandRegistrationGroup d)
        {
            d[(ushort)CmdPacketType.GIVEUP_EXPERT_JOB] = _expertJobGiveupHandler.Handle;
            d[(ushort)CmdPacketType.CREATE_EXPERT_JOB_STORE] = _expertJobStoreHandler.HandleCreate;
            d[(ushort)CmdPacketType.ENTER_EXPERT_JOB_STORE] = _expertJobStoreHandler.HandleEnter;
            d[(ushort)CmdPacketType.CLOSE_EXPERT_JOB_STORE] = _expertJobStoreHandler.HandleClose;
            d[(ushort)CmdPacketType.REPAIR_DISJOINT_MACHINE] = _expertJobStoreHandler.HandleRepair;
            d[(ushort)CmdPacketType.UPGRADE_DISJOINT_MACHINE] = _expertJobStoreHandler.HandleUpgrade;
            d[(ushort)CmdPacketType.REQUEST_DISJOINT_ITEM] = HandleSharedDisjointOrHellParty;
            d[(ushort)CmdPacketType.EXPERT_EXTRACTION] = _expertJobExtractionHandler.Handle;
            d[(ushort)CmdPacketType.REPAIR_EXPERT_JOB_STORE] = _enchanterHandler.HandleRepair;
            d[(ushort)CmdPacketType.USE_ENCHANT_STORE] = _expertJobStoreHandler.HandleEnchant;
            d[(ushort)CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB] =
                _expertJobCompoundHandler.Handle;
        }

        private Task HandleSharedDisjointOrHellParty(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            return _expertJobStoreHandler.HasEnteredStore(session)
                ? _expertJobStoreHandler.HandleDisjoint(session, header, body)
                : _dungeonHandler.Handle_ENUM_CMDPACKET_HELLPARTY_START(session, header, body);
        }

        #endregion
    }
}
