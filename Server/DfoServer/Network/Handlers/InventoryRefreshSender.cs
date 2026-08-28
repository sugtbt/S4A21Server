using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class InventoryRefreshSender
    {
        private const string ProtocolName = "GameProtocol";

        private readonly SqliteSelectCharacterDataSource _dataSource;
        private readonly ICharacterRepository _characterRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly IGameDatabase _database;

        public InventoryRefreshSender(
            SqliteSelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository,
            IGameDatabase database = null)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _database = database ?? GameDatabase.CreateDefault();
            _honorLevel = new HonorLevelSyncService(characterRepository, _database);
            _subtype0Repository = new SqliteSubtype0FieldsRepository(_database);
        }

        public async Task SendNoti2AppearanceUpdate(EnhancedClientSession session)
        {
            var noti2Body = AppearanceService.UpdateAndBroadcast(
                session.Player,
                _characterRepository,
                _database);
            FileLogger.Log($"[{ProtocolName}] NOTI 2 appearance update: {session.Player.AppearanceEntries.Length} entries, body={noti2Body.Length}B");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, noti2Body));
        }

        public async Task SendUserInfoSubtype1Refresh(
            EnhancedClientSession session,
            string logTag)
        {
            try
            {
                var player = session?.Player;
                if (player == null || player.CharacterId <= 0)
                    return;

                var characterId = player.CharacterId;
                var record = _characterRepository.GetById(characterId);
                var addition = new SqliteSubtype1Repository(_database).Load(characterId);
                if (record == null || addition == null)
                    return;

                var accountId = session.Account?.AccountId ?? record.AccountId;
                var accountCharacters = _characterRepository.ListByAccount(accountId);
                var honor = _honorLevel.LoadSummary(accountId, accountCharacters);
                var skills = new SqliteCharacterProgressRepository(_database)
                    .LoadSkills(characterId);

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.USERINFO,
                    UserInfoBroadcastService.BuildSubtype1Body(
                        record,
                        addition,
                        accountCharacters,
                        honor,
                        skills)));

                FileLogger.Log(
                    $"[{ProtocolName}] {logTag ?? "USERINFO1_REFRESH"}: "
                    + $"cid={characterId} auraSkinFlag={addition.AuraSkinFlag}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] {logTag ?? "USERINFO1_REFRESH"} failed: "
                    + ex.Message);
            }
        }

        public void ReloadSubtype0Tail(EnhancedClientSession session)
        {
            if (session?.Player == null)
                return;

            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            var tail = _subtype0Repository.Load(cid)
                ?? session.Player.Subtype0Tail
                ?? new UserInfoMinimumTailSnapshot();
            HonorLevelDataProvider.ApplyToSubtype0Tail(tail, _honorLevel.LoadSummary(aid));
            session.Player.Subtype0Tail = tail;
        }

        public async Task SendCreatureItemListRefresh(EnhancedClientSession session)
        {
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var list = LoadCreatureItemListSnapshot(session, cid);
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)(list?.Entries.Count ?? 0));
            if (list != null)
            {
                foreach (var entry in list.Entries)
                    CreatureListBodyBuilder.WriteCreatureEntry(writer, entry);
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0069, writer.ToArray()));
        }

        public Task SendHonorLevelInfoRefresh(EnhancedClientSession session, string reason)
        {
            return _honorLevel.SendInfoAsync(session, ProtocolName, reason);
        }

        // 婚礼回放三包（WEDDING_INFO -> WEDDING_CHARAC -> COUPLE_ROOM），
        // 包体与选角序列末尾（NewCharacterInitSequence）一致，COUPLE_ROOM 用 occurrence 1 的包体。
        // 注意 COUPLE_ROOM 不可省略：实机验证左下角图标由 WEDDING_INFO 驱动，
        // 但面板结婚属性是客户端按 COUPLE_ROOM 的婚家家具列表计算的，只发前两个会导致图标在、属性丢。
        // 代价是客户端收到 COUPLE_ROOM 会重建结婚房间界面，动画从头重播一次（纯视觉，无实际影响）。
        // 静态入口：换宠物、进本、回城等没有 InventoryRefreshSender 实例的路径也可直接调用。
        internal static async Task SendWeddingReplayRefresh(EnhancedClientSession session)
        {
            if (session == null)
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.WEDDING_INFO,
                WeddingInfoBodyBuilder.BuildBody()));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.WEDDING_CHARAC,
                WeddingCharacCmdBodyBuilder.BuildBody()));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.COUPLE_ROOM,
                CoupleRoomBodyBuilder.BuildBody()));
        }

        public async Task SendItemListRefresh(EnhancedClientSession session, params InventoryListType[] listTypes)
        {
            var (cid, aid) = SessionOwnerResolver.Resolve(session);

            foreach (var listType in listTypes.Distinct().Select(MapToNotiListType).Distinct())
            {
                var itemBody = ItemListPacketBuilder.BuildBody(cid, aid, listType);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x000D, itemBody));
            }
        }

        internal static Task SendEpicPieceInfo(
            EnhancedClientSession session,
            int pieceId,
            int value)
        {
            if (session == null || pieceId <= 0)
                return Task.CompletedTask;

            return SendEpicPieceInfo(
                session,
                new[] { new ItemValueEntrySnapshot { ItemId = pieceId, Value = Math.Max(0, value) } },
                1);
        }

        internal static Task SendEpicPieceInfo(
            EnhancedClientSession session,
            IReadOnlyList<ItemValueEntrySnapshot> items,
            byte mode)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.EPIC_BOOK_INFO,
                A21UsableCount0465BodyBuilder.Build(items, mode)));
        }

        internal static async Task<bool> TrySendOwnedItemListRefresh(
            EnhancedClientSession session,
            InventoryLease expectedLease,
            InventoryListType listType,
            Func<bool> projectionGuard)
        {
            var player = session?.Player;
            if (player == null
                || expectedLease == null
                || projectionGuard == null
                || !projectionGuard()
                || !InventoryContext.IsCurrentLease(
                    expectedLease,
                    session.SessionId,
                    player.CharacterId))
            {
                return false;
            }

            byte[] body;
            lock (expectedLease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        expectedLease,
                        session.SessionId,
                        player.CharacterId)
                    || !projectionGuard())
                {
                    return false;
                }

                body = ItemListPacketBuilder.BuildItemSpaceListBody(
                    expectedLease.Inventory,
                    listType);
            }

            if (!InventoryContext.IsCurrentLease(
                    expectedLease,
                    session.SessionId,
                    player.CharacterId)
                || !projectionGuard())
            {
                return false;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000D,
                body));
            return true;
        }

        public Task SendUpdateItemList(EnhancedClientSession session, InventoryListType itemSpace, short slotIndex)
        {
            return SendUpdateItemList(session, itemSpace, new[] { slotIndex });
        }

        public Task SendGoldUpdate(EnhancedClientSession session)
        {
            return SendUpdateItemList(session, InventoryListType.Main, InventoryService.MainVirtualCurrencySlotStart);
        }

        public Task SendGoldUpdate(EnhancedClientSession session, int updatedGold)
        {
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            if (InventoryContext.TryGetLease(cid, out var lease)
                && lease.IsOwnedBy(session.SessionId))
            {
                lock (lease.SyncRoot)
                    lease.Inventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart,
                        Math.Max(0, updatedGold));
            }

            return SendGoldUpdate(session);
        }

        public async Task SendUpdateItemList(EnhancedClientSession session, InventoryListType itemSpace, IEnumerable<short> slotIndexes)
        {
            await SendOnlineUpdateItemList(session, itemSpace, slotIndexes);
        }

        public Task SendEmptyUpdateItemList(EnhancedClientSession session, InventoryListType itemSpace, short slotIndex)
        {
            if (session == null || !IsNewItemListUpdateSpace(itemSpace))
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildEmptyUpdates(itemSpace, new[] { slotIndex })));
        }

        public async Task SendSortItemLockSlotRefresh(EnhancedClientSession session, InventoryListType listType, short slotIndex)
        {
            await SendUpdateItemList(session, listType, slotIndex);
        }

        public async Task SendSortItemLockRefresh(EnhancedClientSession session, InventoryListType listType)
        {
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var refreshListType = MapToSortLockListType(listType);
            var locks = LoadOnlineSortItemLocks(session, cid, refreshListType);
            foreach (var entry in locks)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(entry)));
        }

        public async Task SendAllSortItemLockRefresh(EnhancedClientSession session)
        {
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var locks = LoadOnlineSortItemLocks(session, cid, null);
            foreach (var entry in locks)
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02CA, SortItemLockBuilder.BuildLock(entry)));
        }

        public async Task SendEquipmentItemLockListRefresh(EnhancedClientSession session, InventoryListType listType)
        {
            if (!IsEquipmentItemLockListType(listType))
                return;

            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var locks = LoadOnlineEquipmentItemLocks(session, cid, listType);
            LogEquipmentItemLockList("ITEM_LOCK_LIST_REFRESH", locks);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00FB,
                EquipmentItemLockBuilder.BuildLockList(locks)));
        }

        public async Task SendAllEquipmentItemLockListRefresh(EnhancedClientSession session)
        {
            var (cid, _) = SessionOwnerResolver.Resolve(session);
            var locks = LoadOnlineEquipmentItemLocks(session, cid, null);
            LogEquipmentItemLockList("ITEM_LOCK_LIST_ALL", locks);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00FB,
                EquipmentItemLockBuilder.BuildLockList(locks)));
        }

        private static IReadOnlyList<SortItemLockEntry> LoadOnlineSortItemLocks(
            EnhancedClientSession session,
            int characterId,
            InventoryListType? listType)
        {
            if (!InventoryContext.TryGetLease(characterId, out var lease) || !lease.IsOwnedBy(session.SessionId))
                return Array.Empty<SortItemLockEntry>();

            lock (lease.SyncRoot)
                return InventoryLockService.LoadSortItemLocks(lease.Inventory, listType);
        }

        private static IReadOnlyList<EquipmentItemLockEntry> LoadOnlineEquipmentItemLocks(
            EnhancedClientSession session,
            int characterId,
            InventoryListType? listType)
        {
            if (!InventoryContext.TryGetLease(characterId, out var lease) || !lease.IsOwnedBy(session.SessionId))
                return Array.Empty<EquipmentItemLockEntry>();

            lock (lease.SyncRoot)
                return InventoryLockService.LoadEquipmentItemLocks(lease.Inventory, listType);
        }

        internal static InventoryListType MapToSortLockListType(InventoryListType listType)
        {
            return MapToNotiListType(listType);
        }

        internal static Task SendOnlineUpdateItemList(
            EnhancedClientSession session,
            InventoryListType itemSpace,
            short slotIndex)
        {
            return SendOnlineUpdateItemList(session, itemSpace, new[] { slotIndex });
        }

        internal static async Task SendOnlineUpdateItemList(
            EnhancedClientSession session,
            InventoryListType itemSpace,
            IEnumerable<short> slotIndexes)
        {
            if (session == null || slotIndexes == null || !IsNewItemListUpdateSpace(itemSpace))
                return;

            var slots = slotIndexes.Distinct().ToList();
            if (slots.Count == 0)
                return;

            var (cid, aid) = SessionOwnerResolver.Resolve(session);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000E,
                ItemListUpdateBuilder.BuildUpdateBody(cid, aid, itemSpace, slots)));
        }

        internal static Task SendOnlineUpdateItemList(
            ISessionPacketSender sender,
            InventoryListType itemSpace,
            short slotIndex)
        {
            return SendOnlineUpdateItemList(sender, itemSpace, new[] { slotIndex });
        }

        internal static async Task SendOnlineUpdateItemList(
            ISessionPacketSender sender,
            InventoryListType itemSpace,
            IEnumerable<short> slotIndexes)
        {
            if (sender == null || slotIndexes == null || !IsNewItemListUpdateSpace(itemSpace))
                return;

            var slots = slotIndexes.Distinct().ToList();
            if (slots.Count == 0)
                return;

            await sender.SendNotiAsync(
                0x000E,
                ItemListUpdateBuilder.BuildUpdateBody(sender.CharacterId, sender.AccountId, itemSpace, slots));
        }

        internal static InventoryListType MapToNotiListType(InventoryListType moveListType)
        {
            if (moveListType == InventoryListType.Equipment)
                return InventoryListType.Avatar;
            return moveListType;
        }

        private static bool IsEquipmentItemLockListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.Pet
                || listType == InventoryListType.GuildMedal;
        }

        private static bool IsNewItemListUpdateSpace(InventoryListType itemSpace)
        {
            return itemSpace == InventoryListType.Main
                || itemSpace == InventoryListType.PersonalCargo
                || itemSpace == InventoryListType.AccountCargo
                || itemSpace == InventoryListType.QuickSlot
                || itemSpace == InventoryListType.Avatar
                || itemSpace == InventoryListType.Equipment
                || itemSpace == InventoryListType.Pet
                || itemSpace == InventoryListType.GuildMedal;
        }

        private CreatureItemListSnapshot LoadCreatureItemListSnapshot(EnhancedClientSession session, int characterId)
        {
            if (session != null
                && InventoryContext.TryGetLease(characterId, out var lease)
                && lease.IsOwnedBy(session.SessionId))
            {
                lock (lease.SyncRoot)
                    return PetInventoryAccessor.BuildCreatureItemListSnapshot(lease.Inventory);
            }

            return _dataSource.LoadCreatureItemListSnapshot(characterId);
        }

        internal static void LogEquipmentItemLockList(string tag, IReadOnlyList<EquipmentItemLockEntry> locks)
        {
            var builder = new StringBuilder();
            builder.Append($"[{ProtocolName}] {tag}: count={locks?.Count ?? 0}");
            if (locks != null)
            {
                foreach (var item in locks)
                    builder.Append($" ({item.ListType},{item.SlotIndex},state={item.State},remain={item.RemainingSeconds})");
            }

            FileLogger.Log(builder.ToString());
        }
    }
}
