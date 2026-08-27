using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public sealed class InitPacketBuilderRegistry
    {
        private readonly Dictionary<ushort, IInitPacketBuilder> _builders = new Dictionary<ushort, IInitPacketBuilder>();
        private readonly Dictionary<ushort, IInitCmdPacketBuilder> _cmdBuilders = new Dictionary<ushort, IInitCmdPacketBuilder>();
        private readonly IGameDatabase _database;

        public InitPacketBuilderRegistry()
            : this(GameDatabase.CreateDefault(), null)
        {
        }

        public InitPacketBuilderRegistry(IGameDatabase database)
            : this(database, null)
        {
        }

        // sessions 供 UnitedServerFriendInfoBodyBuilder 按 CharacterId 反查 self 会话，
        // 组真实好友列表（见 GenericBuilders.cs 该类注释）；null 时好友 init 走空态兜底。
        public InitPacketBuilderRegistry(
            IGameDatabase database,
            ISessionDirectory sessions)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            var collectBoxProgressRepository = new CollectBoxProgressRepository(
                _database);

            Register(new SkillInfoBodyBuilder());              
            Register(new DarkKnightComboSkillInfoBodyBuilder());
            Register(new QuestListBodyBuilder());              
            Register(new UserInfoBodyBuilder());
            Register(new UserStateInitBodyBuilder());
            Register(new SimpleByteBodyBuilder(0x00CA, _ => 0));
            Register(new A21UsableCount0465BodyBuilder());
            Register(new A21UsableCount021EBodyBuilder());
            Register(new CreatureListBodyBuilder());           
            Register(new ExpertJobInfoBodyBuilder());          
            Register(new ItemLockListBodyBuilder());           
            Register(new EmptyPartyInfoBodyBuilder());
            Register(new ItemStateListBodyBuilder(0x00AC));
            Register(new ItemStateListBodyBuilder(0x00AE));
            Register(new AchievementListBodyBuilder());    
            Register(new TitleBookListBodyBuilder());   
            Register(new StoryBookInfoBodyBuilder());
            Register(new ChampionBreakSystemBodyBuilder());    
            Register(new DailyScheduleBodyBuilder());             
            Register(new BuyRestrictItemListBodyBuilder());             

            
            Register(new SimpleByteBodyBuilder(0x00B1, s => s.ShopCoinEventFlag));  // 复活币当日领取标记(character_daily_reset)
            Register(new SimpleByteBodyBuilder(0x01A8, s => s.PcRoomPlayTimeState));
            Register(new SimpleByteBodyBuilder(0x0331, s => s.GoldLimitUpgradeLevel));
            Register(new SimpleByteBodyBuilder(
                (ushort)NotiPacketTypeA21.UPGRADE_CARRY_GOLD,
                s => s.GoldLimitUpgradeLevel));

            
            Register(new EnterGameWorldCompleteBodyBuilder());

            
            Register(new BossTowerBodyBuilder());                                       
            Register(new MailboxBodyBuilder(_database));                                
            Register(new GrowthWeaponBodyBuilder());                                     
            Register(new ShowEffectBodyBuilder());                                       
            Register(new PvpMissionBodyBuilder());                                       
            Register(new DungeonPermissionBodyBuilder());                                

            
            Register(new EventInfoBodyBuilder(_database));
            Register(new HotkeyConfigBodyBuilder());                                    
            Register(new CharacterOptionBodyBuilder());

            
            Register(new GameOptionBodyBuilder());                                      
            Register(new ClearQuestListBodyBuilder());                            
            Register(new DailyChallengeBodyBuilder());                                   

            
            Register(new SkillPointSlotBodyBuilder());                                  
            Register(new CollectionBoxBodyBuilder(collectBoxProgressRepository));
            Register(new RentalInfoBodyBuilder());                                      
            Register(new LotteryBufferBodyBuilder(_database));                          
            Register(new CubeInfoBodyBuilder());                                        
            Register(new FatigueAccelBodyBuilder());                                    

            
            
            
            Register(new UserPositionBodyBuilder());
            Register(new CeraBodyBuilder());
            Register(new PetCreatureWelcomeMessageBodyBuilder());
            Register(new UnitedServerFriendInfoBodyBuilder(sessions));
            Register(new WeddingInfoBodyBuilder());
            Register(new DimensionGateEntranceInfoBodyBuilder(_database));
            Register(new StrikerSupportTagCharacterBodyBuilder(_database));
            RegisterCmd(new MercenaryInfoCmdBodyBuilder(_database));
            RegisterCmd(new WeddingCharacCmdBodyBuilder());
        }

        public bool TryBuild(ushort notiType, SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            if (notiType == (ushort)NotiPacketTypeA21.COUPLE_ROOM && occurrenceIndex == 1)
            {
                body = CoupleRoomBodyBuilder.BuildBody();
                return true;
            }

            if (_builders.TryGetValue(notiType, out var builder))
                return builder.TryBuild(snapshot, occurrenceIndex, out body);
            body = null;
            return false;
        }

        public bool TryBuildCmd(ushort cmdType, SelectCharacterDataSnapshot snapshot, out byte[] body)
        {
            if (_cmdBuilders.TryGetValue(cmdType, out var cmdBuilder))
                return cmdBuilder.TryBuild(snapshot, out body);

            if (cmdType == 0x0004)
            {
                if (SelectCharacterAckBodyBuilder.TryBuild(
                        snapshot,
                        _database.ConnectionString,
                        out body))
                    return true;
            }
            body = null;
            return false;
        }

        private void Register(IInitPacketBuilder builder)
        {
            _builders[builder.NotiType] = builder;
        }

        private void RegisterCmd(IInitCmdPacketBuilder builder)
        {
            _cmdBuilders[builder.CmdType] = builder;
        }
    }
}
