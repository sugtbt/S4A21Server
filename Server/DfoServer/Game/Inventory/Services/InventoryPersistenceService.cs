using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Currency;
using DfoServer.Game.TitleBook;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryPersistenceService
    {
        private const string ClockTickName = "inventory:save-dirty";
        private static readonly object RegisterSync = new object();
        private static bool _clockRegistered;
        private static int _savingAll;

        public static void RegisterClock(ClockService clock)
        {
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));

            lock (RegisterSync)
            {
                if (_clockRegistered)
                    return;

                _clockRegistered = true;
                clock.RegisterMinuteTick(ClockTickName, _ => SaveAllDirty());
            }
        }

        public static void SaveAllDirty()
        {
            if (Interlocked.Exchange(ref _savingAll, 1) != 0)
                return;

            try
            {
                foreach (var lease in InventoryContext.GetLeasesSnapshot())
                    SaveDirty(lease);
            }
            finally
            {
                Interlocked.Exchange(ref _savingAll, 0);
            }
        }

        public static bool SaveDirty(InventoryLease lease)
        {
            if (lease == null || lease.Inventory == null)
                return false;

            try
            {
                lock (lease.SyncRoot)
                {
                    if (!InventoryContext.CanPersistLease(lease)) return false;
                    var inventory = lease.Inventory;
                    if (!HasDirtyData(inventory))
                        return true;

                    var database = inventory.Database
                        ?? GameDatabase.CreateDefault();
                    using (var connection = database.OpenConnection())
                    {
                        using (var transaction = connection.BeginTransaction())
                        {
                            if (!SaveDirtyInTransaction(connection, transaction, lease)) return false;
                            transaction.Commit();
                        }
                    }

                    inventory.ClearDirtyState();
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] SaveDirty failed cid={lease.CharacterId} aid={lease.AccountId}: {ex.Message}");
                return false;
            }
        }

        internal static bool SaveDirtyAndLoadWallet(InventoryLease lease, out WalletSnapshot wallet)
        {
            wallet = null;
            if (lease == null || lease.Inventory == null)
                return false;

            try
            {
                lock (lease.SyncRoot)
                {
                    if (!InventoryContext.CanPersistLease(lease)) return false;
                    var inventory = lease.Inventory;
                    var hasDirtyData = HasDirtyData(inventory);
                    var database = inventory.Database
                        ?? GameDatabase.CreateDefault();
                    using (var connection = database.OpenConnection())
                    {
                        using (var transaction = connection.BeginTransaction())
                        {
                            if (hasDirtyData
                                && !SaveDirtyInTransaction(connection, transaction, lease))
                                return false;

                            wallet = CurrencyService.LoadWallet(
                                connection,
                                transaction,
                                inventory.CharacterId);
                            transaction.Commit();
                        }
                    }

                    if (hasDirtyData)
                        inventory.ClearDirtyState();
                }

                return wallet != null;
            }
            catch (Exception ex)
            {
                wallet = null;
                FileLogger.Log($"[InventoryPersistence] SaveDirtyAndLoadWallet failed cid={lease.CharacterId} aid={lease.AccountId}: {ex.Message}");
                return false;
            }
        }

        internal static bool SaveDirtyInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryLease lease)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (lease == null || lease.Inventory == null)
                return false;

            if (!ReferenceEquals(transaction.Connection, connection))
                throw new ArgumentException("Inventory persistence requires the matching transaction.");
            if (!InventoryContext.CanPersistLease(lease)) return false;

            var inventory = lease.Inventory;
            if (!HasDirtyData(inventory))
                return true;

            SaveDirtyItemAudits(connection, transaction, lease, inventory);
            SaveDirtyMainVirtualCountAudits(connection, transaction, lease, inventory);
            // 跨列表移动会改变 item_uid，落库前先记住旧 uid，落库后再迁移成长值。
            var growthMigrations = CaptureGrowthMigrationOldUids(connection, transaction, inventory);
            SaveDirtyItems(connection, transaction, inventory, growthMigrations);
            ApplyGrowthMigrations(connection, transaction, inventory, growthMigrations);
            SaveDirtyAvatarDetails(connection, transaction, inventory);
            SaveDirtyCreatureDetails(connection, transaction, inventory);
            SaveDirtyTitleBook(connection, transaction, inventory);
            SaveDirtyAchievements(connection, transaction, inventory);
            SaveDirtyCollectBox(connection, transaction, inventory);
            SaveDirtyEpicPieceBook(connection, transaction, inventory);
            SaveDirtyItemStates(connection, transaction, inventory);
            SaveDirtyMainVirtualCounts(connection, transaction, inventory);
            SaveDirtyContainerStates(connection, transaction, inventory);
            SavePendingAccountCurrencyGrants(connection, transaction, inventory);
            return true;
        }

        public static bool SaveAvatarDetailImmediately(
            AvatarDetail detail,
            IGameDatabase database = null)
        {
            if (detail == null || detail.AvatarUid <= 0)
                return false;

            try
            {
                database ??= GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        AvatarDetailRepository.Upsert(connection, transaction, detail);
                        transaction.Commit();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] avatar detail save failed avatarUid={detail.AvatarUid}: {ex.Message}");
                return false;
            }
        }

        public static bool DeleteAvatarDetailImmediately(
            long avatarUid,
            IGameDatabase database = null)
        {
            if (avatarUid <= 0)
                return false;

            try
            {
                database ??= GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        AvatarDetailRepository.Delete(connection, transaction, avatarUid);
                        transaction.Commit();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] avatar detail delete failed avatarUid={avatarUid}: {ex.Message}");
                return false;
            }
        }

        public static bool DeleteCreatureDetailImmediately(
            int characterId,
            int creatureKey,
            IGameDatabase database = null)
        {
            if (characterId <= 0 || creatureKey <= 0)
                return false;

            try
            {
                database ??= GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        CreatureDetailRepository.Delete(connection, transaction, characterId, creatureKey);
                        transaction.Commit();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] creature detail delete failed cid={characterId} uid={creatureKey}: {ex.Message}");
                return false;
            }
        }

        public static bool SaveCreatureDetailImmediately(
            int characterId,
            CreatureDetail detail,
            IGameDatabase database = null)
        {
            if (characterId <= 0 || detail == null || detail.Uid <= 0)
                return false;

            try
            {
                database ??= GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        var updated = CreatureDetailRepository.Upsert(connection, transaction, characterId, detail);
                        transaction.Commit();
                        return updated;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryPersistence] creature detail save failed cid={characterId} uid={detail.Uid}: {ex.Message}");
                return false;
            }
        }

        private static bool HasDirtyData(InventoryService inventory)
        {
            if (inventory.PendingHappyTokenCeraGrant > 0 || inventory.PendingTokenCeraGrant > 0)
                return true;
            if (inventory.DirtyMainVirtualCountSlots.Count > 0 || inventory.DirtyListParams.Count > 0)
                return true;
            if (inventory.AvatarDetails.DirtyDetailUids.Count > 0
                || inventory.AvatarDetails.DeletedDetailUids.Count > 0
                || inventory.CreatureDetails.DirtyDetailUids.Count > 0
                || inventory.CreatureDetails.DeletedDetailUids.Count > 0)
                return true;
            if (inventory.Cargo.DirtySlots.Count > 0
                || inventory.Cargo.IsStateDirty
                || inventory.AccountCargo.DirtySlots.Count > 0
                || inventory.AccountCargo.IsStateDirty)
                return true;
            if (inventory.TitleBook.HasDirtySlots
                || inventory.Achievements.DirtyQuestIds.Count > 0)
                return true;
            if (inventory.CollectBox.HasDirtySlots)
                return true;
            if (inventory.EpicPieces.IsDirty)
                return true;
            if (inventory.ItemStates.IsDirty)
                return true;

            foreach (var _ in inventory.DirtyListTypes)
                return true;

            return false;
        }

        private static void SavePendingAccountCurrencyGrants(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            if (inventory.PendingTokenCeraGrant > 0)
            {
                // 代币券 vouchers (PVF trade_point.stk) become account currency here,
                // inside the same transaction that saves the bag.
                CurrencyService.GrantTokenCera(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    inventory.PendingTokenCeraGrant);
            }

            if (inventory.PendingHappyTokenCeraGrant <= 0)
                return;

            CurrencyService.GrantHappyTokenCera(
                connection,
                transaction,
                inventory.CharacterId,
                inventory.PendingHappyTokenCeraGrant);
        }

        private static void SaveDirtyItems(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            List<(long OldItemUid, InventoryListType FromListType, short FromSlotIndex, InventoryListType ToListType, short ToSlotIndex)> growthMigrations)
        {
            // 跨列表移动时源槽会被清空，此时成长值属于"待迁移"而非"随物品删除"，
            // 必须保留原行交给 ApplyGrowthMigrations 搬到新 uid。
            var migratingSourceSlots = new HashSet<(InventoryListType, short)>();
            if (growthMigrations != null)
            {
                foreach (var migration in growthMigrations)
                    migratingSourceSlots.Add((migration.FromListType, migration.FromSlotIndex));
            }

            foreach (var pair in CollectDirtyItemSlots(inventory))
                foreach (var slotIndex in pair.Value)
                    SaveSlot(
                        connection,
                        transaction,
                        inventory,
                        pair.Key,
                        slotIndex,
                        migratingSourceSlots.Contains((pair.Key, slotIndex)));
        }

        // 跨列表移动(背包<->穿戴)后 item_uid 会变。龙跃武器的成长值按
        // (character_id, item_uid) 关联，必须跟着同一件物品迁移，否则穿戴一次
        // 就会丢掉已累积的成长值。
        // 落库前记下旧 uid，落库后查到新 uid 再迁移，整个过程与物品落库同事务。
        private static List<(long OldItemUid, InventoryListType FromListType, short FromSlotIndex, InventoryListType ToListType, short ToSlotIndex)>
            CaptureGrowthMigrationOldUids(
                SqliteConnection connection,
                SqliteTransaction transaction,
                InventoryService inventory)
        {
            var result = new List<(long, InventoryListType, short, InventoryListType, short)>();
            var migrations = inventory.PendingGrowthMigrations;
            if (migrations == null || migrations.Count == 0)
                return result;

            // 落库前先把"源槽里的物品模板 ID"记下来。交换之后两个槽的 uid 会
            // 互指，落库后再判断哪条成长值属于哪件物品就分不清了，必须提前记录。
            foreach (var migration in migrations)
            {
                var oldItemUid = InventoryItemRepository.LoadCharacterSlotUid(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    migration.FromListType,
                    migration.FromSlotIndex);
                if (oldItemUid <= 0)
                    continue;

                var record = EquipmentGrowthRepository.LoadGrowth(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    oldItemUid);
                if (record == null)
                    continue;

                result.Add((
                    oldItemUid,
                    migration.FromListType,
                    migration.FromSlotIndex,
                    migration.ToListType,
                    migration.ToSlotIndex));
            }

            return result;
        }

        private static void ApplyGrowthMigrations(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            List<(long OldItemUid, InventoryListType FromListType, short FromSlotIndex, InventoryListType ToListType, short ToSlotIndex)> migrations)
        {
            if (migrations == null || migrations.Count == 0)
                return;

            // 交换时两个方向同时存在，直接逐条 UPDATE OR REPLACE 会互相顶掉
            // (A->B 之后 B 的行已被占用)。先把待迁移的值读进内存，删掉旧行，
            // 再按新 uid 写回。
            // 进入这里的迁移已在 Capture 阶段过滤为"源槽确实挂了成长值"的那些，
            // 交换中普通装备那一侧不会被收录，所以不会凭空造出成长值。
            var pending = new Dictionary<long, (int ItemId, int GrowthValue, int Stage)>();
            var consumedOldUids = new List<long>();
            foreach (var migration in migrations)
            {
                var record = EquipmentGrowthRepository.LoadGrowth(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    migration.OldItemUid);
                if (record == null)
                    continue;

                var newItemUid = InventoryItemRepository.LoadCharacterSlotUid(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    migration.ToListType,
                    migration.ToSlotIndex);
                if (newItemUid <= 0 || newItemUid == migration.OldItemUid)
                    continue;

                pending[newItemUid] = (record.ItemId, record.GrowthValue, record.Stage);
                consumedOldUids.Add(migration.OldItemUid);
            }

            if (pending.Count == 0)
                return;

            foreach (var oldItemUid in consumedOldUids)
                EquipmentGrowthRepository.DeleteForItemUid(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    oldItemUid);

            foreach (var pair in pending)
                EquipmentGrowthRepository.WriteGrowthSnapshot(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    pair.Key,
                    pair.Value.ItemId,
                    pair.Value.GrowthValue,
                    pair.Value.Stage);
        }

        private static void SaveDirtyItemAudits(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryLease lease,
            InventoryService inventory)
        {
            foreach (var pair in CollectDirtyItemSlots(inventory))
            {
                var listType = pair.Key;
                foreach (var slotIndex in pair.Value)
                {
                    if (listType == InventoryListType.Main
                        && (InventoryService.IsVirtualMainSlot(slotIndex)
                            || InventoryService.IsReservedMainSlot(slotIndex)))
                        continue;

                    var before = InventoryAuditRepository.LoadPersistedSlotCore(
                        connection,
                        transaction,
                        inventory,
                        listType,
                        slotIndex);
                    var after = inventory.GetItem(listType, slotIndex);
                    var auditEvent = InventoryAuditEvent.FromSlotChange(
                        lease.SessionId,
                        inventory,
                        listType,
                        slotIndex,
                        before,
                        after);
                    InventoryAuditRepository.Insert(connection, transaction, auditEvent);
                }
            }
        }

        private static void SaveDirtyMainVirtualCountAudits(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryLease lease,
            InventoryService inventory)
        {
            foreach (var slotIndex in inventory.DirtyMainVirtualCountSlots)
            {
                var item = inventory.GetMainVirtualCount(slotIndex);
                if (item == null)
                    continue;

                var beforeCount = InventoryAuditRepository.LoadPersistedVirtualCount(
                    connection,
                    transaction,
                    inventory,
                    slotIndex);
                var auditEvent = InventoryAuditEvent.FromVirtualCountChange(
                    lease.SessionId,
                    inventory,
                    slotIndex,
                    item.ItemId,
                    beforeCount,
                    item.Count);
                InventoryAuditRepository.Insert(connection, transaction, auditEvent);
            }
        }

        private static Dictionary<InventoryListType, HashSet<short>> CollectDirtyItemSlots(InventoryService inventory)
        {
            var listTypes = new HashSet<InventoryListType>(inventory.DirtyListTypes);
            if (inventory.Cargo.DirtySlots.Count > 0)
                listTypes.Add(InventoryListType.PersonalCargo);
            if (inventory.AccountCargo.DirtySlots.Count > 0)
                listTypes.Add(InventoryListType.AccountCargo);

            var result = new Dictionary<InventoryListType, HashSet<short>>();
            foreach (var listType in listTypes)
            {
                var slots = new HashSet<short>(inventory.GetDirtySlots(listType));
                if (listType == InventoryListType.PersonalCargo)
                    slots.UnionWith(inventory.Cargo.DirtySlots);
                else if (listType == InventoryListType.AccountCargo)
                    slots.UnionWith(inventory.AccountCargo.DirtySlots);

                result[listType] = slots;
            }

            return result;
        }

        private static void SaveSlot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            bool growthMigratesOut)
        {
            if (listType == InventoryListType.Main
                && (InventoryService.IsVirtualMainSlot(slotIndex)
                    || InventoryService.IsReservedMainSlot(slotIndex)))
                return;

            var core = inventory.GetItem(listType, slotIndex);
            if (listType == InventoryListType.AccountCargo)
            {
                if (core == null)
                    InventoryItemRepository.DeleteAccountCargoSlot(connection, transaction, inventory.AccountId, slotIndex);
                else
                    InventoryItemRepository.UpsertAccountCargoSlot(connection, transaction, inventory.AccountId, inventory.CharacterId, slotIndex, core);
                return;
            }

            if (core == null)
            {
                // 该槽的成长值要迁到新槽时不能删，否则迁移无行可搬。
                DeleteSlotWithGrowth(
                    connection,
                    transaction,
                    inventory,
                    listType,
                    slotIndex,
                    deleteGrowth: !growthMigratesOut);
                return;
            }

            InventoryItemRepository.UpsertCharacterSlot(
                connection,
                transaction,
                inventory.CharacterId,
                listType,
                slotIndex,
                core);
        }

        // 物品被删除时连同它的龙跃成长值一起清掉：成长值属于装备实例，
        // 实例没了就不该留下孤儿行（否则换武器后会残留永远指不到物品的记录）。
        private static void DeleteSlotWithGrowth(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            bool deleteGrowth)
        {
            var itemUid = InventoryItemRepository.LoadCharacterSlotUid(
                connection,
                transaction,
                inventory.CharacterId,
                listType,
                slotIndex);
            if (deleteGrowth && itemUid > 0)
            {
                EquipmentGrowthRepository.DeleteForItemUid(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    itemUid);
            }

            InventoryItemRepository.DeleteCharacterSlot(
                connection,
                transaction,
                inventory.CharacterId,
                listType,
                slotIndex);
        }

        private static void SaveDirtyAvatarDetails(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var avatarUid in inventory.AvatarDetails.DeletedDetailUids)
                AvatarDetailRepository.Delete(connection, transaction, avatarUid);

            foreach (var detail in inventory.AvatarDetails.GetDirtyDetails())
                AvatarDetailRepository.Upsert(connection, transaction, detail);
        }

        private static void SaveDirtyCreatureDetails(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var creatureKey in inventory.CreatureDetails.DeletedDetailUids)
                CreatureDetailRepository.Delete(connection, transaction, inventory.CharacterId, creatureKey);

            foreach (var detail in inventory.CreatureDetails.GetDirtyDetails())
                CreatureDetailRepository.Upsert(connection, transaction, inventory.CharacterId, detail);
        }

        private static void SaveDirtyTitleBook(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var item in inventory.TitleBook.GetDirtyItems())
            {
                CharacterTitleBookRepository.SaveSlot(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    item.Key.Category,
                    item.Key.SlotIndex,
                    item.Value);
            }
        }

        private static void SaveDirtyAchievements(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var entry in inventory.Achievements.GetDirtyEntries())
                CharacterAchievementRepository.UpsertEntry(connection, transaction, inventory.CharacterId, entry);
        }

        private static void SaveDirtyCollectBox(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var slot in inventory.CollectBox.GetDirtySlots())
            {
                CollectBoxProgressRepository.SaveSlot(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    slot.BoxIndex,
                    slot.SlotIndex,
                    slot.ItemId);
            }
        }

        private static void SaveDirtyEpicPieceBook(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            if (!inventory.EpicPieces.IsDirty)
                return;

            EpicPieceBookRepository.SaveBlob(
                connection,
                transaction,
                inventory.AccountId,
                inventory.EpicPieces.ToBlob());
        }

        private static void SaveDirtyItemStates(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            if (!inventory.ItemStates.IsDirty)
                return;

            CharacterItemStateRepository.SaveAll(
                connection,
                transaction,
                inventory.CharacterId,
                inventory.ItemStates);
        }

        private static void SaveDirtyMainVirtualCounts(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var slotIndex in inventory.DirtyMainVirtualCountSlots)
            {
                var item = inventory.GetMainVirtualCount(slotIndex);
                if (item == null)
                    continue;

                if (slotIndex >= InventoryService.MainVirtualCurrencySlotStart
                    && slotIndex <= InventoryService.MainVirtualCurrencySlotEnd)
                {
                    InventoryMainVirtualCountRepository.UpsertCurrencySlot(
                        connection,
                        transaction,
                        inventory.CharacterId,
                        slotIndex,
                        item.Count);
                    continue;
                }

                if (slotIndex >= InventoryService.MainVirtualCubeSlotStart
                    && slotIndex <= InventoryService.MainVirtualCubeSlotEnd)
                {
                    CurrencyService.SetCubeFragmentCount(
                        connection,
                        transaction,
                        inventory.AccountId,
                        item.ItemId,
                        item.Count);
                    continue;
                }

                if (slotIndex >= InventoryService.MainVirtualSoulSlotStart
                    && slotIndex <= InventoryService.MainVirtualSoulSlotEnd)
                {
                    CurrencyService.SetSoulWarehouseCount(
                        connection,
                        transaction,
                        inventory.AccountId,
                        item.ItemId,
                        item.Count);
                }
            }
        }

        private static void SaveDirtyContainerStates(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            foreach (var listType in inventory.DirtyListParams)
            {
                if (listType == InventoryListType.AccountCargo
                    || listType == InventoryListType.PersonalCargo)
                    continue;

                InventoryContainerStateRepository.UpsertCharacterContainerState(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    listType,
                    inventory.GetListParam16(listType));
            }

            if (inventory.Cargo.IsStateDirty
                || inventory.DirtyListParams.Contains(InventoryListType.PersonalCargo))
            {
                InventoryContainerStateRepository.UpsertCharacterContainerState(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    InventoryListType.PersonalCargo,
                    inventory.Cargo.Capacity);
            }

            if (inventory.DirtyListParams.Contains(InventoryListType.AccountCargo)
                || inventory.AccountCargo.IsStateDirty
                || inventory.AccountCargo.DirtySlots.Count > 0)
            {
                InventoryContainerStateRepository.UpsertAccountCargoState(
                    connection,
                    transaction,
                    inventory.AccountId,
                    inventory.AccountCargo.SelectionKey,
                    inventory.AccountCargo.Money,
                    (ushort)inventory.AccountCargo.GetItems().Count);
            }
        }
    }
}
