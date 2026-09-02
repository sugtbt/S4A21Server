using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.KnightShield;
using DfoServer.Game.Mercenary;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        // 背包操作直连共享 store; 门面只保留选角初始化本职(称号簿/成就/水晶契约/全量快照/宠物快照)
        private readonly ExperienceItemUseService _experienceItemUseService;
        private readonly SqliteSelectCharacterDataSource _sqliteSelectCharacterDataSource;
        private readonly ICharacterRepository _characterRepository;
        private readonly InventoryRefreshSender _refresh;
        private readonly ExperienceItemNotificationService _experienceItemNotifications;
        private readonly Func<byte[], Task> _broadcastGamePacket;
        private readonly IMercenaryRestrictionService _mercenaryRestrictions;
        private readonly IExpertJobStateRepository _expertJobStates;
        private readonly ExpertJobPersistenceService _expertJobPersistence;
        private readonly ExpertJobOperationCoordinator _expertJobOperations;
        private readonly MonsterCardBindService _monsterCardBindService;
        private readonly MonsterCardUpgradeService _monsterCardUpgradeService;
        private readonly IGameDatabase _database;
        private readonly IInventoryOverflowRewardSink _overflowRewardSink;
        private readonly MailboxService _mailboxService;
        private readonly KnightShieldService _knightShieldService;

        public string ProtocolName => "GameProtocol";

        internal InventoryHandler(
            ExperienceItemUseService experienceItemUseService,
            SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource,
            ICharacterRepository characterRepository,
            InventoryRefreshSender refreshSender,
            ExperienceItemNotificationService experienceItemNotifications,
            IExpertJobStateRepository expertJobStates,
            ExpertJobPersistenceService expertJobPersistence,
            ExpertJobOperationCoordinator expertJobOperations,
            Func<byte[], Task> broadcastGamePacket = null,
            IMercenaryRestrictionService mercenaryRestrictions = null,
            IGameDatabase database = null,
            IInventoryOverflowRewardSink overflowRewardSink = null,
            MailboxService mailboxService = null,
            KnightShieldService knightShieldService = null)
        {
            _experienceItemUseService = experienceItemUseService
                ?? throw new ArgumentNullException(nameof(experienceItemUseService));
            _sqliteSelectCharacterDataSource = sqliteSelectCharacterDataSource ?? throw new ArgumentNullException(nameof(sqliteSelectCharacterDataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _refresh = refreshSender ?? throw new ArgumentNullException(nameof(refreshSender));
            _experienceItemNotifications = experienceItemNotifications
                ?? throw new ArgumentNullException(nameof(experienceItemNotifications));
            _broadcastGamePacket = broadcastGamePacket;
            _mercenaryRestrictions = mercenaryRestrictions;
            _expertJobStates = expertJobStates
                ?? throw new ArgumentNullException(nameof(expertJobStates));
            _expertJobPersistence = expertJobPersistence
                ?? throw new ArgumentNullException(nameof(expertJobPersistence));
            _expertJobOperations = expertJobOperations
                ?? throw new ArgumentNullException(nameof(expertJobOperations));
            _monsterCardBindService = new MonsterCardBindService();
            _monsterCardUpgradeService = new MonsterCardUpgradeService();
            _database = database ?? GameDatabase.CreateDefault();
            _overflowRewardSink = overflowRewardSink
                ?? RejectingInventoryOverflowRewardSink.Instance;
            _mailboxService = mailboxService;
            _knightShieldService = knightShieldService;
        }

        public static (int characterId, int accountId) ResolveOwner(EnhancedClientSession session)
            => SessionOwnerResolver.Resolve(session);

        private static Task SendUsableCountLimitUpdateAsync(
            EnhancedClientSession session,
            UsableCountLimitState state)
        {
            if (session == null || state == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x021E,
                UsableCountLimitPacketBuilder.BuildUpdateBody(state)));
        }

        private async Task BroadcastItemNotice(
            EnhancedClientSession session,
            string operation,
            Func<ushort, byte[]> buildBody,
            string details)
        {
            if (_broadcastGamePacket == null || buildBody == null)
                return;

            try
            {
                var userUniqueId = session?.Player?.UserId ?? 0;
                if (userUniqueId == 0 && session?.Player?.CharacterId > 0)
                    userUniqueId = (ushort)session.Player.CharacterId;

                await _broadcastGamePacket(GamePacketEnvelopeBuilder.Build(
                    0x00, 0x0056, buildBody(userUniqueId)));
                FileLogger.Log($"[{ProtocolName}] {operation}: notice broadcast type=0x0056 uniqueId={userUniqueId} {details}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] {operation}: notice broadcast failed: {ex.Message}");
            }
        }

        public static bool TryParseDeleteOrSellRequest(byte[] body, out InventoryListType listType, out short slotIndex, out short itemCount)
        {
            listType = InventoryListType.Main;
            slotIndex = 0;
            itemCount = 0;

            if (body == null || body.Length < 4)
                return false;

            if (body.Length >= 5 && Enum.IsDefined(typeof(InventoryListType), (byte)body[0]))
            {
                listType = (InventoryListType)body[0];
                slotIndex = BitConverter.ToInt16(body, 1);
                itemCount = BitConverter.ToInt16(body, 3);
                return true;
            }

            slotIndex = BitConverter.ToInt16(body, 0);
            itemCount = BitConverter.ToInt16(body, 2);
            return true;
        }

        internal static List<PackageGrantedItem> ToBoosterPopupGrantedItemsForSelfTest(BoosterUseResult result)
        {
            return ToBoosterPopupGrantedItems(result);
        }

        internal static List<PackageGrantedItem> ToAggregatedBoosterGrantedItemsForSelfTest(BoosterUseResult result)
        {
            return ToPackageGrantedItems(result);
        }

        private static List<PackageGrantedItem> ToBoosterPopupGrantedItems(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            AddPopupItems(items, result.DisplayRewards);
            AddPopupItems(items, result.DoubleRewards);
            return items.Count > 0 ? items : ToPackageGrantedItems(result);
        }

        private static void AddPopupItems(List<PackageGrantedItem> target, IEnumerable<PackageGrantedItem> source)
        {
            if (target == null || source == null)
                return;

            foreach (var reward in source)
            {
                if (reward == null || reward.ItemTemplateId <= 0 || reward.DisplayCount <= 0)
                    continue;

                target.Add(new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.DisplayCount <= 0 ? 1 : reward.DisplayCount,
                    Durability = reward.Durability,
                    Attr = reward.Attr,
                    ExpireTime = reward.ExpireTime,
                });
            }
        }

        private static List<PackageGrantedItem> ToPackageGrantedItems(BoosterUseResult result)
        {
            var items = new List<PackageGrantedItem>();
            if (result == null)
                return items;

            foreach (var reward in result.Rewards)
            {
                items.Add(new PackageGrantedItem
                {
                    ListType = reward.ListType,
                    SlotIndex = reward.SlotIndex,
                    ItemTemplateId = reward.ItemTemplateId,
                    DisplayCount = reward.GrantedCount <= 0 ? 1 : reward.GrantedCount,
                    Durability = reward.Durability,
                    Attr = reward.Attr,
                    ExpireTime = reward.ExpireTime,
                });
            }

            return items;
        }
    }
}
