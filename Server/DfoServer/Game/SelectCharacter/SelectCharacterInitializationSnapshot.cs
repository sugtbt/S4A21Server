using System.Collections.Generic;
using DfoServer.Game.TitleBook;

namespace DfoServer.Game.SelectCharacter
{
    public sealed class SelectCharacterInitializationSnapshot
    {
        public const int DailyChallengeClaimFlagCount = 5;

        public ExpertJobInfoSnapshot ExpertJobInfo { get; set; } = new ExpertJobInfoSnapshot();

        public ItemLockListSnapshot ItemLockList { get; set; } = new ItemLockListSnapshot();

        public byte PcRoomPlayTimeState { get; set; }

        // 0x00B1: 复活币当日已领取标记(character_daily_reset bit0)
        public byte ShopCoinEventFlag { get; set; }

        // 0x0331: 金币携带/拍卖额同步扩充档位，客户端支持 0-4。
        public byte GoldLimitUpgradeLevel { get; set; }

        public ChampionBreakSystemSnapshot ChampionBreakSystem { get; set; } = new ChampionBreakSystemSnapshot();

        
        public List<byte> GrowthWeaponStageIds { get; } = new List<byte>();

        
        public List<PvpMissionEntrySnapshot> PvpMissions { get; } = new List<PvpMissionEntrySnapshot>();

        
        public List<DungeonPermissionEntrySnapshot> DungeonPermissions { get; } = new List<DungeonPermissionEntrySnapshot>();

        public List<int> QuestNotifyIds { get; } = new List<int>();

        public byte HotkeyKeyType { get; set; }

        public List<ushort> HotkeyConfigSlots { get; } = new List<ushort>();

        
        
        public byte[] MainGameOptionBlob { get; set; }

        public byte[] QuickchatBank0 { get; set; }

        public byte[] QuickchatBank1 { get; set; }

        public byte[] CharacterOptionBlob { get; set; }

        
        
        
        public uint CharacInvisibleFalgsPayloadLen { get; set; }

        public List<CharacInvisibleFalgEntrySnapshot> CharacInvisibleFalgs { get; } = new List<CharacInvisibleFalgEntrySnapshot>();

        
        

        
        // Legacy seed field retained for character_init_flags compatibility. It is not
        // the first uint32 of DAILY_CHALLENGE (0x0286).
        public uint RacingDungeonCurrentEnterCount { get; set; }

        public uint DailyChallengeCharacterLevel { get; set; } = 1;

        public List<RacingDungeonGroupSnapshot> RacingDungeonGroups { get; } = new List<RacingDungeonGroupSnapshot>();

        public byte[] DailyChallengeRewardClaimFlags { get; set; } =
            new byte[DailyChallengeClaimFlagCount];

        public List<uint> RacingDungeonTailIds { get; } = new List<uint>();


        public List<ItemStateEntrySnapshot> CooltimeItemStates { get; } = new List<ItemStateEntrySnapshot>();

        public List<ItemStateEntrySnapshot> EffectItemStates { get; } = new List<ItemStateEntrySnapshot>();

        public List<ItemValueEntrySnapshot> UsableCountItems { get; } = new List<ItemValueEntrySnapshot>();

        public List<ItemValueEntrySnapshot> EpicPieceItems { get; } = new List<ItemValueEntrySnapshot>();

        public AchievementCompleteSnapshot AchievementComplete { get; set; } = new AchievementCompleteSnapshot();

        public Unknown730Snapshot Unknown730 { get; set; } = new Unknown730Snapshot();

        public List<TitleBookCategorySnapshot> TitleBookCategories { get; } = new List<TitleBookCategorySnapshot>();

        public List<Unknown725Snapshot> Unknown725Packets { get; } = new List<Unknown725Snapshot>();

        public SkillInfoSnapshot SkillInfo { get; set; } = new SkillInfoSnapshot();

        public List<byte[]> DarkKnightComboSkillInfoBodies { get; } = new List<byte[]>();

        public CreatureItemListSnapshot CreatureItemList { get; set; } = new CreatureItemListSnapshot();

        public List<SelectCharacterUserInfoPacketSnapshot> UserInfoPackets { get; } = new List<SelectCharacterUserInfoPacketSnapshot>();

        
        public List<SkillPointSlotEntrySnapshot> SkillPointSlots { get; } = new List<SkillPointSlotEntrySnapshot>();


        public RentalInfoSnapshot RentalInfo { get; set; } = new RentalInfoSnapshot();


        public byte CubeType { get; set; }
        public byte CubeGrade { get; set; } = 0xff;

        
        public ushort LuckyStar { get; set; }

        
        public ushort FatigueAccelValue { get; set; }
        public byte FatigueAccelState { get; set; }

        
        public int AckCharCreatedTime { get; set; }
        public ushort AckUniqueId { get; set; }
        public List<AckPremiumEntrySnapshot> AckPremiums { get; } = new List<AckPremiumEntrySnapshot>();
        public int AckCera { get; set; }
        public int AckTokenCera { get; set; }
        public int AckHappyTokenCera { get; set; }
        public AckQuestShopEntrySnapshot[] AckQuestShopEntries { get; set; }
        public byte AckCharSlotIndex { get; set; }
        public byte AckTutorialSkipable { get; set; } = 0;
        public ushort AckFatigueBattery { get; set; }
        public ushort AckFatigueGrownUpBuff { get; set; }
        public byte AckTradePunishFlag { get; set; }
        public ushort AckExtraField86JP { get; set; }

        // A21 NOTI_PREMIUM_SERVICE (0x032F): 登录时投影魔王契约八项服务。
        // 不能只依赖客户端主动查询；A21 客户端进号后不会固定发送 CMD 0x036F。
        public ushort PremiumServiceType { get; set; }

        public byte[] PremiumServiceData { get; set; }

        public UserInfoAdditionSnapshot UserInfoAddition { get; set; }
    }

    public sealed class AckPremiumEntrySnapshot
    {
        public byte PremiumType { get; set; }
        public byte[] EndTime { get; set; }
    }

    public sealed class AckQuestShopEntrySnapshot
    {
        public ushort QuestId { get; set; }
        public uint QuestValue { get; set; }
    }
}
