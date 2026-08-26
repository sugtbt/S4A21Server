using System;
using System.Collections.Generic;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    public sealed class InventoryCharacterLifecycleService
    {
        internal const short RentalMainSlotStart = InventoryService.MainSlotStart;
        internal const short RentalMainSlotEnd = 64;

        private readonly string _connectionString;
        private readonly IRentalTimeProvider _timeProvider;

        internal InventoryCharacterLifecycleService(
            string databasePath,
            string schemaFilePath,
            IRentalTimeProvider timeProvider = null)
            : this(new GameDatabase(databasePath, schemaFilePath), timeProvider)
        {
        }

        internal InventoryCharacterLifecycleService(
            IGameDatabase database,
            IRentalTimeProvider timeProvider = null)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
            _timeProvider = timeProvider ?? SystemRentalTimeProvider.Instance;
        }

        internal void EnsureContainerState(int characterId, int accountId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                int count;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM character_container_state WHERE character_id = @characterId";
                    command.Parameters.AddWithValue("@characterId", characterId);
                    count = Convert.ToInt32(command.ExecuteScalar());
                }

                using (var transaction = connection.BeginTransaction())
                {
                    if (count <= 0)
                    {
                        InventoryContainerStateRepository.UpsertCharacterContainerState(
                            connection,
                            transaction,
                            characterId,
                            InventoryListType.Main,
                            (ushort)ItemSlotBoundService.MainExpandStageFull);
                        InventoryContainerStateRepository.UpsertCharacterContainerState(
                            connection,
                            transaction,
                            characterId,
                            InventoryListType.Avatar,
                            0);
                        InventoryContainerStateRepository.UpsertCharacterContainerState(
                            connection,
                            transaction,
                            characterId,
                            InventoryListType.PersonalCargo,
                            CargoModel.DefaultCapacity);
                        InventoryContainerStateRepository.UpsertCharacterContainerState(
                            connection,
                            transaction,
                            characterId,
                            InventoryListType.GuildMedal,
                            0);
                    }

                    EnsureMainVirtualCurrencySlots(connection, transaction, characterId);
                    transaction.Commit();
                }
            }
        }

        internal int DeleteExpiredNameTagState(int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var now = _timeProvider.UtcNowUnixSeconds();
                    var count = DeleteExpiredNameTagState(
                        connection,
                        transaction,
                        characterId,
                        now);
                    if (count > 0)
                        transaction.Commit();

                    return count;
                }
            }
        }

        internal RentalInfoSnapshot RebuildRentalInfoFromInventory(
            int characterId,
            int accountId,
            RentalInfoSnapshot storedRentalInfo)
        {
            var rebuilt = new RentalInfoSnapshot();
            if (storedRentalInfo != null)
                rebuilt.RentalId = storedRentalInfo.RentalId;

            if (characterId <= 0)
                return rebuilt;

            var now = _timeProvider.UtcNowUnixSeconds();
            var shopIdByInventoryId = BuildRentalShopIndex(storedRentalInfo);
            var shopIdByExpireTime = BuildRentalShopExpireIndex(storedRentalInfo);
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                foreach (var rental in LoadActiveRentalItemsFromNewItems(connection, characterId, now))
                {
                    if (!TryResolveRentalShopId(
                            shopIdByInventoryId,
                            shopIdByExpireTime,
                            rental.itemTemplateId,
                            rental.expireTime,
                            out var shopId))
                        continue;

                    rebuilt.UpsertItem(
                        shopId,
                        unchecked((uint)rental.itemTemplateId),
                        unchecked((uint)rental.expireTime));
                }
            }

            return rebuilt;
        }

        internal void SeedNewCharacterEquipment(int characterId, int accountId, (short slot, int itemId)[] equipment)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    InventoryNewCharacterSeedService.SeedInitialEquipment(
                        connection,
                        transaction,
                        characterId,
                        equipment);
                    transaction.Commit();
                }
            }
        }

        private static void EnsureMainVirtualCurrencySlots(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            for (short slotIndex = InventoryService.MainVirtualCurrencySlotStart;
                 slotIndex <= InventoryService.MainVirtualCurrencySlotEnd;
                 slotIndex++)
            {
                var core = new ItemCore
                {
                    ItemKind = ItemCore.KindSpecialMaterial,
                    ItemId = slotIndex,
                    Count = 0,
                };
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT OR IGNORE INTO character_inventory_items (
    character_id, list_type, slot_index, item_core, created_at, updated_at
) VALUES (
    @characterId, @listType, @slotIndex, @itemCore, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
);";
                    command.Parameters.AddWithValue("@characterId", characterId);
                    command.Parameters.AddWithValue("@listType", (int)InventoryListType.Main);
                    command.Parameters.AddWithValue("@slotIndex", slotIndex);
                    command.Parameters.AddWithValue("@itemCore", core.ToBytes());
                    command.ExecuteNonQuery();
                }
            }
        }

        private static Dictionary<uint, uint> BuildRentalShopIndex(RentalInfoSnapshot storedRentalInfo)
        {
            var map = new Dictionary<uint, uint>();
            if (storedRentalInfo == null)
                return map;

            foreach (var item in storedRentalInfo.Items)
            {
                if (item == null || item.ItemId == 0 || item.InventoryTemplateId == 0)
                    continue;

                map[item.InventoryTemplateId] = item.ItemId;
            }

            return map;
        }

        private static Dictionary<uint, uint> BuildRentalShopExpireIndex(RentalInfoSnapshot storedRentalInfo)
        {
            var map = new Dictionary<uint, uint>();
            if (storedRentalInfo == null)
                return map;

            foreach (var item in storedRentalInfo.Items)
            {
                if (item == null || item.ItemId == 0 || item.ExpireTime == 0)
                    continue;

                map[item.ExpireTime] = item.ItemId;
            }

            return map;
        }

        private static bool TryResolveRentalShopId(
            Dictionary<uint, uint> shopIdByInventoryId,
            Dictionary<uint, uint> shopIdByExpireTime,
            int inventoryTemplateId,
            int expireTime,
            out uint shopId)
        {
            shopId = 0;
            if (!RentalWeaponInventoryMapper.IsValidInventoryTemplate(inventoryTemplateId))
                return false;

            var inventoryId = unchecked((uint)inventoryTemplateId);
            if (shopIdByInventoryId.TryGetValue(inventoryId, out shopId) && shopId != 0)
                return true;

            var expireKey = unchecked((uint)expireTime);
            if (shopIdByExpireTime.TryGetValue(expireKey, out shopId) && shopId != 0)
                return true;

            shopId = inventoryId;
            return true;
        }

        private static List<(InventoryListType listType, short slotIndex, int itemTemplateId, int expireTime)>
            LoadActiveRentalItemsFromNewItems(SqliteConnection connection, int characterId, uint now)
        {
            var result = new List<(InventoryListType listType, short slotIndex, int itemTemplateId, int expireTime)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT list_type, slot_index, item_core
FROM character_inventory_items
WHERE character_id = @characterId
  AND list_type IN (@mainList, @equipmentList)
ORDER BY list_type, slot_index;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@mainList", (int)InventoryListType.Main);
                command.Parameters.AddWithValue("@equipmentList", (int)InventoryListType.Equipment);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var listType = (InventoryListType)reader.GetInt32(0);
                        var slotIndex = Convert.ToInt16(reader.GetInt32(1));
                        if (listType == InventoryListType.Main
                            && (slotIndex < RentalMainSlotStart || slotIndex > RentalMainSlotEnd))
                            continue;

                        var core = ReadItemCore(reader[2]);
                        if (core == null
                            || core.ExpireTime <= now
                            || !RentalWeaponInventoryMapper.IsValidInventoryTemplate(core.ItemId))
                            continue;

                        result.Add((listType, slotIndex, core.ItemId, core.ExpireTime));
                    }
                }
            }

            return result;
        }

        private static int DeleteExpiredNameTagState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            uint now)
        {
            if (!NameTagStateRepository.ClearExpired(
                    connection,
                    transaction,
                    characterId,
                    now))
                return 0;

            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                    lease.Inventory.NameTag.Clear();
            }

            FileLogger.Log($"[NameTagExpire] CLEAR name tag state char={characterId}");
            return 1;
        }

        private static ItemCore ReadItemCore(object value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            var data = value as byte[];
            if (data == null || data.Length < ItemCore.Size)
                return null;

            return ItemCore.FromBytes(data);
        }
    }
}
