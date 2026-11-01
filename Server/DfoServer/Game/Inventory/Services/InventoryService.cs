using System;
using System.Collections.Generic;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Currency;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.TitleBook;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryMainItemConsumeResult
    {
        public bool Success { get; set; }

        public short SlotIndex { get; set; } = -1;

        public int ConsumedCount { get; set; }

        public int RemainingCount { get; set; }

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal sealed class InventoryService
    {
        public const short MainVirtualCurrencySlotStart = 0;
        public const short MainVirtualCurrencySlotEnd = 2;
        public const short MainSlotStart = 3;
        public const short MainSlotEnd = 351;
        public const short MainReservedSlotStart = 352;
        public const short MainReservedSlotEnd = 353;
        public const short MainVirtualCubeSlotStart = 354;
        public const short MainVirtualCubeSlotEnd = 359;
        public const short MainVirtualSoulSlotStart = 360;
        public const short MainVirtualSoulSlotEnd = 364;
        public const int MainSlotCount = MainSlotEnd + 1;

        public const short BodySlotStart = 0;
        public const short BodySlotEnd = (short)EquipmentType.GuildMedal;
        public const int BodySlotCount = BodySlotEnd - BodySlotStart + 1;

        public const short AvatarSlotStart = 0;
        public const short AvatarSlotEnd = 209;
        public const int AvatarSlotCount = AvatarSlotEnd - AvatarSlotStart + 1;

        public const short CreatureSlotStart = 0;
        public const short CreatureSlotEnd = 239;
        public const int CreatureSlotCount = CreatureSlotEnd - CreatureSlotStart + 1;

        public const short GuildMedalSlotStart = 0;
        public const short GuildMedalSlotEnd = 97;
        public const int GuildMedalSlotCount = GuildMedalSlotEnd - GuildMedalSlotStart + 1;

        private readonly ItemCore[] _main = new ItemCore[MainSlotCount];
        private readonly ItemCore[] _body = new ItemCore[BodySlotCount];
        private readonly ItemCore[] _avatar = new ItemCore[AvatarSlotCount];
        private readonly ItemCore[] _creature = new ItemCore[CreatureSlotCount];
        private readonly ItemCore[] _guildMedal = new ItemCore[GuildMedalSlotCount];
        private readonly Dictionary<short, VirtualCountItem> _mainVirtualCounts =
            new Dictionary<short, VirtualCountItem>();
        private readonly HashSet<short> _dirtyMainVirtualSlots = new HashSet<short>();
        private readonly Dictionary<InventoryListType, HashSet<short>> _dirtySlots =
            new Dictionary<InventoryListType, HashSet<short>>();
        private readonly Dictionary<InventoryListType, ushort> _listParams =
            new Dictionary<InventoryListType, ushort>();
        private readonly HashSet<InventoryListType> _dirtyListParams = new HashSet<InventoryListType>();
        private int _pendingHappyTokenCeraGrant;
        private int _pendingTokenCeraGrant;

        private readonly List<(InventoryListType FromListType, short FromSlotIndex,
            InventoryListType ToListType, short ToSlotIndex)> _pendingGrowthMigrations
            = new List<(InventoryListType, short, InventoryListType, short)>();

        public InventoryService(int characterId, int accountId)
            : this(characterId, accountId, database: null)
        {
        }

        internal InventoryService(
            int characterId,
            int accountId,
            IGameDatabase database)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId), "在线背包需要有效的角色 ID。");

            CharacterId = characterId;
            AccountId = accountId;
            Database = database;
            Cargo = new CargoModel(characterId, accountId);
            AccountCargo = new AccountCargoModel(accountId);
            AvatarDetails = new AvatarDetailManager(database, () => InventoryContext.CanPersistModel(this), () => PersistenceSyncRoot);
            CreatureDetails = new CreatureDetailManager(database, () => InventoryContext.CanPersistModel(this), () => PersistenceSyncRoot);
            CreatureDetails.BindCharacter(characterId);
            InitEmptySlots();
            InitDefaultMainVirtualCounts();
            InitDefaultListParams();
        }

        public int CharacterId { get; }

        public int AccountId { get; }

        internal IGameDatabase Database { get; }

        // A failed/unknown transaction may leave this object ahead of the DB.
        // This latch is permanent: recovery installs a different loaded model.
        private int _persistenceUntrusted;
        private int _wasContextManaged;
        internal bool WasContextManaged => System.Threading.Volatile.Read(ref _wasContextManaged) != 0;
        internal void MarkContextManaged() => System.Threading.Interlocked.Exchange(ref _wasContextManaged, 1);
        private object _persistenceSyncRoot = new object();
        internal object PersistenceSyncRoot => System.Threading.Volatile.Read(ref _persistenceSyncRoot);
        internal void BindPersistenceSyncRoot(object syncRoot)
            => System.Threading.Volatile.Write(ref _persistenceSyncRoot, syncRoot ?? throw new ArgumentNullException(nameof(syncRoot)));
        internal bool IsPersistenceTrusted => System.Threading.Volatile.Read(ref _persistenceUntrusted) == 0;
        internal void MarkPersistenceUntrusted(string reason)
        {
            lock (PersistenceSyncRoot) System.Threading.Interlocked.Exchange(ref _persistenceUntrusted, 1);
        }

        public CargoModel Cargo { get; }

        public AccountCargoModel AccountCargo { get; }

        public AvatarDetailManager AvatarDetails { get; }

        public CreatureDetailManager CreatureDetails { get; }

        public EquipmentItemLockManager EquipmentLocks { get; } = new EquipmentItemLockManager();

        public NameTagState NameTag { get; } = new NameTagState();

        public TitleBookModel TitleBook { get; } = new TitleBookModel();

        public AchievementModel Achievements { get; } = new AchievementModel();

        public CollectBoxModel CollectBox { get; } = new CollectBoxModel();

        public EpicPieceBookModel EpicPieces { get; } = new EpicPieceBookModel();

        public InventoryItemStateBook ItemStates { get; } = new InventoryItemStateBook();

        public IReadOnlyCollection<InventoryListType> DirtyListTypes => _dirtySlots.Keys;

        public IReadOnlyCollection<short> DirtyMainVirtualCountSlots => _dirtyMainVirtualSlots;

        public IReadOnlyCollection<InventoryListType> DirtyListParams => _dirtyListParams;

        public int PendingHappyTokenCeraGrant => _pendingHappyTokenCeraGrant;

        /// <summary>代币券 queued for this commit; flushed with accounts.token_cera.</summary>
        public int PendingTokenCeraGrant => _pendingTokenCeraGrant;

        public byte AuraSkinFlag { get; private set; }

        public bool IsAuraSkinSlotOpened => AuraSkinFlag != 0;

        public static InventoryService LoadFromDb(SqliteConnection connection, int characterId, int accountId)
        {
            return LoadFromDb(
                connection,
                characterId,
                accountId,
                database: null);
        }

        internal static InventoryService LoadFromDb(
            SqliteConnection connection,
            int characterId,
            int accountId,
            IGameDatabase database)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            var inventory = new InventoryService(
                characterId,
                accountId,
                database);
            inventory.LoadContainerStates(connection);
            inventory.LoadAccountCargoState(connection);
            inventory.AvatarDetails.LoadForCharacter(connection, characterId);
            inventory.CreatureDetails.LoadForCharacter(connection, characterId);
            inventory.EquipmentLocks.LoadForCharacter(connection, characterId);
            inventory.LoadNameTagState(connection);
            inventory.LoadTitleBook(connection);
            inventory.Achievements.LoadForCharacter(connection, characterId);
            CollectBoxProgressRepository.LoadModel(connection, null, characterId, inventory.CollectBox);
            inventory.LoadAuraSkinFlag(connection);
            CharacterItemStateRepository.LoadInto(connection, characterId, inventory.ItemStates);

            foreach (var item in InventoryItemRepository.LoadCharacterItems(connection, characterId))
                inventory.AttachItem(item);

            foreach (var item in InventoryItemRepository.LoadAccountCargoItems(connection, accountId))
                inventory.AttachItem(item);

            inventory.LoadMainVirtualCounts(connection);
            inventory.LoadEpicPieceBook(connection);
            inventory.ClearDirtyState();
            return inventory;
        }

        public ushort GetListParam16(InventoryListType listType)
        {
            if (listType == InventoryListType.AccountCargo)
                return AccountCargo.SelectionKey;
            if (listType == InventoryListType.PersonalCargo)
                return Cargo.Capacity;

            if (_listParams.TryGetValue(listType, out var value))
                return NormalizeListParam(listType, value);

            return GetDefaultListParam(listType);
        }

        public void SetListParam16(InventoryListType listType, ushort value)
        {
            var normalized = NormalizeListParam(listType, value);
            if (listType == InventoryListType.AccountCargo)
            {
                if (AccountCargo.SelectionKey == normalized)
                    return;

                AccountCargo.SelectionKey = normalized;
                _dirtyListParams.Add(listType);
                return;
            }

            if (_listParams.TryGetValue(listType, out var oldValue) && oldValue == normalized)
                return;

            _listParams[listType] = normalized;
            if (listType == InventoryListType.PersonalCargo)
                Cargo.Capacity = normalized;
            _dirtyListParams.Add(listType);
        }

        public ItemCore GetItem(InventoryListType listType, short slotIndex)
        {
            return TryGetItem(listType, slotIndex, out var core) ? core : null;
        }

        public bool TryGetItem(InventoryListType listType, short slotIndex, out ItemCore core)
        {
            core = null;
            switch (listType)
            {
                case InventoryListType.PersonalCargo:
                    core = Cargo.GetItem(slotIndex);
                    return core != null;
                case InventoryListType.AccountCargo:
                    core = AccountCargo.GetItem(slotIndex);
                    return core != null;
            }

            if (!TryGetArray(listType, out var items)
                || !TryGetArrayIndex(listType, slotIndex, items.Length, out var index))
                return false;

            core = NormalizeItem(items[index]);
            return core != null;
        }

        public IReadOnlyList<KeyValuePair<short, ItemCore>> GetItems(InventoryListType listType)
        {
            switch (listType)
            {
                case InventoryListType.PersonalCargo:
                    return Cargo.GetItems();
                case InventoryListType.AccountCargo:
                    return AccountCargo.GetItems();
            }

            var result = new List<KeyValuePair<short, ItemCore>>();
            if (!TryGetArray(listType, out var items))
                return result;

            for (var index = 0; index < items.Length; index++)
            {
                var slotIndex = (short)index;
                if (!TryGetArrayIndex(listType, slotIndex, items.Length, out _))
                    continue;

                var core = NormalizeItem(items[index]);
                if (core != null)
                    result.Add(new KeyValuePair<short, ItemCore>(slotIndex, core));
            }

            return result;
        }

        public IReadOnlyList<VirtualCountItem> GetMainVirtualCounts()
        {
            var result = new List<VirtualCountItem>();
            foreach (var pair in _mainVirtualCounts)
                result.Add(pair.Value.Copy());

            result.Sort((left, right) => left.SlotIndex.CompareTo(right.SlotIndex));
            return result;
        }

        public VirtualCountItem GetMainVirtualCount(short slotIndex)
        {
            return TryGetMainVirtualCount(slotIndex, out var item) ? item : null;
        }

        public bool TryGetMainVirtualCount(short slotIndex, out VirtualCountItem item)
        {
            item = null;
            if (!_mainVirtualCounts.TryGetValue(slotIndex, out var value))
                return false;

            item = value.Copy();
            return true;
        }

        public void AttachMainVirtualCount(short slotIndex, int itemId, int count)
        {
            if (!TryResolveMainVirtualItemId(slotIndex, out var fixedItemId))
                return;

            _mainVirtualCounts[slotIndex] = new VirtualCountItem
            {
                SlotIndex = slotIndex,
                ItemId = fixedItemId,
                Count = NormalizeVirtualCount(count),
            };
        }

        public bool SetMainVirtualCount(short slotIndex, int count)
        {
            if (!TryResolveMainVirtualItemId(slotIndex, out var itemId))
                return false;

            return SetMainVirtualCount(slotIndex, itemId, count);
        }

        public bool SetMainVirtualCount(short slotIndex, int itemId, int count)
        {
            if (!TryResolveMainVirtualItemId(slotIndex, out var fixedItemId))
                return false;

            var normalizedCount = NormalizeVirtualCount(count);
            if (_mainVirtualCounts.TryGetValue(slotIndex, out var current)
                && current.ItemId == fixedItemId
                && current.Count == normalizedCount)
                return true;

            _mainVirtualCounts[slotIndex] = new VirtualCountItem
            {
                SlotIndex = slotIndex,
                ItemId = fixedItemId,
                Count = normalizedCount,
            };
            _dirtyMainVirtualSlots.Add(slotIndex);
            return true;
        }

        public void SetAuraSkinFlag(byte auraSkinFlag)
        {
            AuraSkinFlag = auraSkinFlag != 0 ? (byte)1 : (byte)0;
        }

        public int CountMainItem(int itemId)
        {
            if (itemId < 0)
                return 0;

            if (TryResolveMainVirtualSlotByItemId(itemId, out var virtualSlot, out _))
                return GetMainVirtualCount(virtualSlot)?.Count ?? 0;

            long total = 0;
            foreach (var pair in GetItems(InventoryListType.Main))
            {
                var item = pair.Value;
                if (item == null || item.ItemId != itemId)
                    continue;

                total += InventoryStackRuleService.IsStackable(item)
                    ? Math.Max(0, item.Count)
                    : 1;
                if (total >= int.MaxValue)
                    return int.MaxValue;
            }

            return (int)total;
        }

        public bool TryConsumeMainItem(
            int itemId,
            int count,
            out InventoryMainItemConsumeResult result)
        {
            result = new InventoryMainItemConsumeResult();
            if (itemId < 0 || count <= 0)
                return false;

            if (TryResolveMainVirtualSlotByItemId(itemId, out var virtualSlot, out var virtualItemId))
                return TryConsumeMainVirtualItem(virtualSlot, virtualItemId, count, result);

            foreach (var pair in GetItems(InventoryListType.Main))
            {
                var slotIndex = pair.Key;
                var item = pair.Value;
                if (item == null || item.ItemId != itemId)
                    continue;

                if (!InventoryStackRuleService.IsStackable(item) && count != 1)
                    return false;

                if (!InventoryDeleteService.TryDecreaseStack(
                        this,
                        InventoryListType.Main,
                        slotIndex,
                        count,
                        out var deleteResult)
                    || !deleteResult.Success)
                    return false;

                result.Success = true;
                result.SlotIndex = slotIndex;
                result.ConsumedCount = deleteResult.DeletedCount;
                result.RemainingCount = deleteResult.RemainingCount;
                result.Changes.AddRange(deleteResult.Changes);
                return true;
            }

            return false;
        }

        public bool TryGrantGold(int amount, int carryLimit, out int grantedCount, out int finalCount)
        {
            grantedCount = 0;
            finalCount = GetMainVirtualCount(MainVirtualCurrencySlotStart)?.Count ?? 0;
            if (amount <= 0)
                return true;

            var limit = Math.Max(0, carryLimit);
            var current = finalCount;
            var target = current >= limit
                ? current
                : (int)Math.Min(limit, (long)current + amount);

            grantedCount = Math.Max(0, target - current);
            finalCount = target;
            return SetMainVirtualCount(MainVirtualCurrencySlotStart, 0, target);
        }

        public void AttachItem(InventoryItem item)
        {
            if (item == null || item.Core == null)
                return;

            AttachItem(item.ListType, item.SlotIndex, item.Core);
        }

        public void AttachItem(InventoryListType listType, short slotIndex, ItemCore core)
        {
            core = NormalizeItem(core);
            if (core == null)
                return;

            switch (listType)
            {
                case InventoryListType.PersonalCargo:
                    Cargo.AttachItem(slotIndex, core);
                    return;
                case InventoryListType.AccountCargo:
                    AccountCargo.AttachItem(slotIndex, core);
                    return;
            }

            if (!TryGetArray(listType, out var items)
                || !TryGetArrayIndex(listType, slotIndex, items.Length, out var index))
                return;

            items[index] = core;
        }

        public bool SetItem(InventoryListType listType, short slotIndex, ItemCore core)
        {
            core = NormalizeItem(core);
            if (core == null)
                return RemoveItem(listType, slotIndex);

            switch (listType)
            {
                case InventoryListType.PersonalCargo:
                    if (!Cargo.SetItem(slotIndex, core))
                        return false;
                    MarkDirty(listType, slotIndex);
                    return true;
                case InventoryListType.AccountCargo:
                    if (!AccountCargo.SetItem(slotIndex, core))
                        return false;
                    MarkDirty(listType, slotIndex);
                    return true;
            }

            if (!TryGetArray(listType, out var items)
                || !TryGetArrayIndex(listType, slotIndex, items.Length, out var index))
                return false;

            items[index] = core;
            MarkDirty(listType, slotIndex);
            return true;
        }

        public bool RemoveItem(InventoryListType listType, short slotIndex)
        {
            switch (listType)
            {
                case InventoryListType.PersonalCargo:
                    if (!Cargo.RemoveItem(slotIndex))
                        return false;
                    MarkDirty(listType, slotIndex);
                    return true;
                case InventoryListType.AccountCargo:
                    if (!AccountCargo.RemoveItem(slotIndex))
                        return false;
                    MarkDirty(listType, slotIndex);
                    return true;
            }

            if (!TryGetArray(listType, out var items)
                || !TryGetArrayIndex(listType, slotIndex, items.Length, out var index)
                || NormalizeItem(items[index]) == null)
                return false;

            items[index].Init();
            MarkDirty(listType, slotIndex);
            return true;
        }

        public void MarkDirty(InventoryListType listType, short slotIndex)
        {
            if (listType == InventoryListType.Main)
            {
                if (IsVirtualMainSlot(slotIndex))
                {
                    _dirtyMainVirtualSlots.Add(slotIndex);
                    return;
                }

                if (IsReservedMainSlot(slotIndex))
                    return;
            }

            if (!_dirtySlots.TryGetValue(listType, out var slots))
            {
                slots = new HashSet<short>();
                _dirtySlots[listType] = slots;
            }

            slots.Add(slotIndex);
        }

        public IReadOnlyCollection<short> GetDirtySlots(InventoryListType listType)
        {
            if (!_dirtySlots.TryGetValue(listType, out var slots))
                return Array.Empty<short>();

            return new List<short>(slots);
        }

        public bool TryQueueHappyTokenCeraGrant(int count)
        {
            if (count <= 0)
                return false;

            var updated = (long)_pendingHappyTokenCeraGrant + count;
            if (updated > int.MaxValue)
                return false;

            _pendingHappyTokenCeraGrant = (int)updated;
            return true;
        }

        internal void RestorePendingHappyTokenCeraGrant(int count)
        {
            _pendingHappyTokenCeraGrant = Math.Max(0, count);
        }

        /// <summary>Queues 代币券 for the next commit (accounts.token_cera).</summary>
        public bool TryQueueTokenCeraGrant(int count)
        {
            if (count <= 0)
                return false;

            var updated = (long)_pendingTokenCeraGrant + count;
            if (updated > int.MaxValue)
                return false;

            _pendingTokenCeraGrant = (int)updated;
            return true;
        }

        internal void RestorePendingTokenCeraGrant(int count)
        {
            _pendingTokenCeraGrant = Math.Max(0, count);
        }

        public void ClearDirtyState()
        {
            _dirtySlots.Clear();
            _dirtyMainVirtualSlots.Clear();
            _dirtyListParams.Clear();
            _pendingHappyTokenCeraGrant = 0;
            _pendingTokenCeraGrant = 0;
            _pendingGrowthMigrations.Clear();
            AvatarDetails.ClearDirtyState();
            CreatureDetails.ClearDirtyState();
            Cargo.ClearDirtyState();
            AccountCargo.ClearDirtyState();
            TitleBook.ClearDirtyState();
            Achievements.ClearDirtyState();
            CollectBox.ClearDirtyState();
            EpicPieces.ClearDirtyState();
            ItemStates.ClearDirtyState();
        }

        // 跨列表移动(背包<->穿戴)后 item_uid 会变，龙跃武器成长值必须跟着
        // 同一件物品迁移。移动服务在内存变更时登记映射，持久化落库后由
        // InventoryPersistenceService 在同一事务内执行迁移。
        internal void RecordGrowthMigration(
            InventoryListType fromListType,
            short fromSlotIndex,
            InventoryListType toListType,
            short toSlotIndex)
        {
            _pendingGrowthMigrations.Add(
                (fromListType, fromSlotIndex, toListType, toSlotIndex));
        }

        internal IReadOnlyList<(InventoryListType FromListType, short FromSlotIndex,
            InventoryListType ToListType, short ToSlotIndex)> PendingGrowthMigrations
            => _pendingGrowthMigrations;

        public static bool IsVirtualMainSlot(short slotIndex)
        {
            return slotIndex >= MainVirtualCurrencySlotStart && slotIndex <= MainVirtualCurrencySlotEnd
                || slotIndex >= MainVirtualCubeSlotStart && slotIndex <= MainVirtualCubeSlotEnd
                || slotIndex >= MainVirtualSoulSlotStart && slotIndex <= MainVirtualSoulSlotEnd;
        }

        public static bool IsReservedMainSlot(short slotIndex)
        {
            return slotIndex >= MainReservedSlotStart && slotIndex <= MainReservedSlotEnd;
        }

        private void LoadContainerStates(SqliteConnection connection)
        {
            foreach (var pair in InventoryContainerStateRepository.LoadCharacterContainerState(connection, CharacterId))
            {
                if (pair.Key == InventoryListType.AccountCargo)
                    continue;

                _listParams[pair.Key] = NormalizeListParam(pair.Key, pair.Value);
                if (pair.Key == InventoryListType.PersonalCargo)
                    Cargo.Capacity = pair.Value;
            }
        }

        private void LoadAccountCargoState(SqliteConnection connection)
        {
            var state = InventoryContainerStateRepository.LoadAccountCargoState(connection, AccountId);
            AccountCargo.SelectionKey = state.SelectionKey;
            AccountCargo.Money = state.Value32;
        }

        private void LoadMainVirtualCounts(SqliteConnection connection)
        {
            foreach (var item in InventoryMainVirtualCountRepository.LoadCurrencySlots(connection, CharacterId))
                AttachMainVirtualCount(item.SlotIndex, item.ItemId, item.Count);

            foreach (var cube in CurrencyService.LoadCubeFragments(connection, null, AccountId))
                AttachMainVirtualCount((short)cube.Slot, cube.ItemId, cube.Count);

            foreach (var soul in CurrencyService.LoadSoulWarehouseCounts(connection, null, AccountId))
                AttachMainVirtualCount((short)soul.Slot, soul.ItemId, soul.Count);
        }

        private void LoadEpicPieceBook(SqliteConnection connection)
        {
            EpicPieces.LoadFromBlob(EpicPieceBookRepository.LoadBlob(connection, null, AccountId));
        }

        private void LoadTitleBook(SqliteConnection connection)
        {
            var titleBook = CharacterTitleBookRepository.LoadModel(connection, CharacterId);
            foreach (var item in titleBook.GetItems())
                TitleBook.AttachItem(item.Key.Category, item.Key.SlotIndex, item.Value);
        }

        private void LoadNameTagState(SqliteConnection connection)
        {
            var state = NameTagStateRepository.Load(connection, CharacterId);
            if (state.IsActive())
                NameTag.Set(state.ItemId, state.ExpireTime);
            else
                NameTag.Clear();
        }

        private void LoadAuraSkinFlag(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT aura_skin_flag FROM characters WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                var value = command.ExecuteScalar();
                AuraSkinFlag = value == null || value == DBNull.Value
                    ? (byte)0
                    : (Convert.ToInt32(value) != 0 ? (byte)1 : (byte)0);
            }
        }

        private bool TryGetArray(InventoryListType listType, out ItemCore[] items)
        {
            switch (listType)
            {
                case InventoryListType.Main:
                    items = _main;
                    return true;
                case InventoryListType.Equipment:
                    items = _body;
                    return true;
                case InventoryListType.Avatar:
                    items = _avatar;
                    return true;
                case InventoryListType.Pet:
                    items = _creature;
                    return true;
                case InventoryListType.GuildMedal:
                    items = _guildMedal;
                    return true;
                default:
                    items = null;
                    return false;
            }
        }

        private void InitEmptySlots()
        {
            InitEmptySlots(_main, MainSlotStart, MainSlotEnd);
            InitEmptySlots(_body);
            InitEmptySlots(_avatar);
            InitEmptySlots(_creature);
            InitEmptySlots(_guildMedal);
        }

        private static void InitEmptySlots(ItemCore[] items, short slotStart, short slotEnd)
        {
            for (var index = slotStart; index <= slotEnd && index < items.Length; index++)
                items[index] = new ItemCore();
        }

        private static void InitEmptySlots(ItemCore[] items)
        {
            for (var index = 0; index < items.Length; index++)
                items[index] = new ItemCore();
        }

        private void InitDefaultMainVirtualCounts()
        {
            _mainVirtualCounts.Clear();
            for (short slotIndex = MainVirtualCurrencySlotStart; slotIndex <= MainVirtualCurrencySlotEnd; slotIndex++)
                AttachMainVirtualCount(slotIndex, slotIndex, 0);

            for (short slotIndex = MainVirtualCubeSlotStart; slotIndex <= MainVirtualCubeSlotEnd; slotIndex++)
            {
                if (TryResolveMainVirtualItemId(slotIndex, out var itemId))
                    AttachMainVirtualCount(slotIndex, itemId, 0);
            }

            for (short slotIndex = MainVirtualSoulSlotStart; slotIndex <= MainVirtualSoulSlotEnd; slotIndex++)
            {
                if (TryResolveMainVirtualItemId(slotIndex, out var itemId))
                    AttachMainVirtualCount(slotIndex, itemId, 0);
            }
        }

        private void InitDefaultListParams()
        {
            _listParams[InventoryListType.Main] = (ushort)ItemSlotBoundService.MainExpandStageFull;
            _listParams[InventoryListType.Avatar] = 0;
            _listParams[InventoryListType.PersonalCargo] = CargoModel.DefaultCapacity;
            _listParams[InventoryListType.Pet] = 0;
            _listParams[InventoryListType.Equipment] = 0;
            _listParams[InventoryListType.GuildMedal] = 0;
        }

        private static bool TryGetArrayIndex(InventoryListType listType, short slotIndex, int slotCount, out int index)
        {
            if (listType == InventoryListType.Equipment)
                slotIndex = (short)EquipmentTypeInfo.ToA21AppearanceSlot(slotIndex);

            index = slotIndex;
            if (index < 0 || index >= slotCount)
                return false;

            if (listType != InventoryListType.Main)
                return true;

            return slotIndex >= MainSlotStart && slotIndex <= MainSlotEnd;
        }

        private static ushort NormalizeListParam(InventoryListType listType, ushort value)
        {
            switch (listType)
            {
                case InventoryListType.PersonalCargo:
                    return CargoModel.NormalizeCapacity(value);
                case InventoryListType.AccountCargo:
                    return AccountCargoModel.NormalizeSelectionKey(value);
                default:
                    return value;
            }
        }

        private static ushort GetDefaultListParam(InventoryListType listType)
        {
            switch (listType)
            {
                case InventoryListType.Main:
                    return (ushort)ItemSlotBoundService.MainExpandStageFull;
                case InventoryListType.PersonalCargo:
                    return CargoModel.DefaultCapacity;
                default:
                    return 0;
            }
        }

        private static ItemCore NormalizeItem(ItemCore core)
        {
            return core != null && !core.IsEmpty ? core : null;
        }

        private static int NormalizeVirtualCount(int count)
        {
            return count < 0 ? 0 : count;
        }

        public static bool TryResolveMainVirtualItemId(short slotIndex, out int itemId)
        {
            if (slotIndex >= MainVirtualCurrencySlotStart && slotIndex <= MainVirtualCurrencySlotEnd)
            {
                itemId = slotIndex;
                return true;
            }

            itemId = CurrencyService.GetAccountWarehouseItemIdFromSlot(slotIndex);
            return itemId > 0;
        }

        public static bool TryResolveMainVirtualSlotByItemId(int itemId, out short slotIndex, out int fixedItemId)
        {
            slotIndex = -1;
            fixedItemId = 0;

            if (itemId == 0 || itemId == 2)
            {
                slotIndex = (short)itemId;
                fixedItemId = itemId;
                return true;
            }

            if (itemId == ReviveCoinService.ItemId)
            {
                slotIndex = ReviveCoinService.WalletSlot;
                fixedItemId = ReviveCoinService.ItemId;
                return true;
            }

            if (!CurrencyService.IsAccountWarehouseItem(itemId))
                return false;

            var accountSlot = CurrencyService.GetAccountWarehouseSlot(itemId);
            if (!CurrencyService.IsAccountWarehouseSlot(accountSlot))
                return false;

            slotIndex = (short)accountSlot;
            fixedItemId = itemId;
            return true;
        }

        private bool TryConsumeMainVirtualItem(
            short slotIndex,
            int fixedItemId,
            int count,
            InventoryMainItemConsumeResult result)
        {
            var current = GetMainVirtualCount(slotIndex);
            if (current == null || current.Count < count)
                return false;

            var remaining = current.Count - count;
            if (!SetMainVirtualCount(slotIndex, fixedItemId, remaining))
                return false;

            result.Success = true;
            result.SlotIndex = slotIndex;
            result.ConsumedCount = count;
            result.RemainingCount = remaining;
            result.Changes.AddSlot(InventoryListType.Main, slotIndex);
            return true;
        }
    }
}
