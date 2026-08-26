using System;
using System.IO;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class ItemStateSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== ITEM_STATE selftest ===");
            var failures = 0;

            VerifySchemaMigration(ref failures);
            VerifyOnlineCacheAndProjection(ref failures);
            VerifyPvfLifecycleParsing(ref failures);
            VerifyLifecycleRules(ref failures);

            Console.WriteLine(failures == 0
                ? "ITEM_STATE selftest passed"
                : $"ITEM_STATE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifySchemaMigration(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_item_state_migration_{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    Check(
                        "new schema creates character_item_states",
                        TableExists(connection, "character_item_states")
                        && !TableExists(connection, "character_item_values")
                        && SqliteMigrations.ReadVersion(connection) == SqliteMigrations.CurrentVersion,
                        ref failures);

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
DROP TABLE character_item_states;
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES (70001, 'item-state-migration-account', '');
INSERT INTO characters(character_id, account_id, name, job)
VALUES (70002, 70001, 'item-state-migration-character', 0);
CREATE TABLE character_item_values (
    character_id INTEGER NOT NULL,
    list_kind TEXT NOT NULL,
    sort_order INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    value INTEGER NOT NULL,
    PRIMARY KEY (character_id, list_kind, sort_order)
);
INSERT INTO character_item_values(character_id, list_kind, sort_order, item_id, value)
VALUES
    (70002, 'cooltime', 0, 1001, 1700000010),
    (70002, 'cooltime', 1, 1001, 1700000020),
    (70002, 'effect', 0, 1002, 1700000030),
    (70002, 'unknown', 0, 1003, 1700000040);
UPDATE schema_metadata SET schema_version = 6 WHERE singleton_id = 1;
PRAGMA user_version = 6;";
                        command.ExecuteNonQuery();
                    }

                    SqliteMigrations.Apply(connection);
                    Check(
                        "v6 character_item_values migrates to current character_item_states",
                        SqliteMigrations.ReadVersion(connection) == SqliteMigrations.CurrentVersion
                        && TableExists(connection, "character_item_states")
                        && !TableExists(connection, "character_item_values")
                        && CountRows(connection, "character_item_states") == 2
                        && ReadExpireTime(connection, 70002, ItemStateKinds.Cooltime, 1001) == 1700000020
                        && ReadExpireTime(connection, 70002, ItemStateKinds.Effect, 1002) == 1700000030,
                        ref failures);
                }
            }
            finally
            {
                TryDelete(tempDbPath);
            }
        }

        private static void VerifyOnlineCacheAndProjection(ref int failures)
        {
            const int accountId = 71001;
            const int characterId = 71002;
            const long now = 1700000100;
            var sessionId = Guid.NewGuid();
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_item_state_online_{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                SeedAccount(database, accountId, "item-state-account");
                SeedCharacter(database, characterId, accountId, "item-state-character");
                InsertItemState(database, characterId, ItemStateKinds.Cooltime, 2001, (int)now + 30);
                InsertItemState(database, characterId, ItemStateKinds.Cooltime, 2002, (int)now - 1);
                InsertItemState(database, characterId, ItemStateKinds.Effect, 2003, (int)now + 60);

                InventoryLease lease = null;
                try
                {
                    using (var connection = database.OpenConnection())
                    {
                        var inventory = InventoryService.LoadFromDb(
                            connection,
                            characterId,
                            accountId,
                            database);
                        lease = InventoryContext.Register(sessionId, characterId, inventory);
                    }

                    var snapshot = new SelectCharacterInitializationSnapshot();
                    SqliteSelectCharacterDataSource.ApplyOnlineItemStates(
                        characterId,
                        snapshot,
                        now);

                    Check(
                        "login projection sends only active item states",
                        snapshot.CooltimeItemStates.Count == 1
                        && snapshot.CooltimeItemStates[0].ItemId == 2001
                        && snapshot.CooltimeItemStates[0].ExpireTime == 30
                        && snapshot.EffectItemStates.Count == 1
                        && snapshot.EffectItemStates[0].ItemId == 2003
                        && snapshot.EffectItemStates[0].ExpireTime == 60,
                        ref failures);

                    var cooltimeBuilder = new ItemStateListBodyBuilder(0x00AC);
                    cooltimeBuilder.TryBuild(
                        new SelectCharacterDataSnapshot { InitializationSnapshot = snapshot },
                        0,
                        out var cooltimeBody);
                    Check(
                        "login projection packet body sends remaining seconds",
                        cooltimeBody.Length == 9
                        && cooltimeBody[0] == 1
                        && BitConverter.ToInt32(cooltimeBody, 1) == 2001
                        && BitConverter.ToInt32(cooltimeBody, 5) == 30,
                        ref failures);

                    var fallbackSnapshot = new SelectCharacterInitializationSnapshot();
                    fallbackSnapshot.CooltimeItemStates.Add(new ItemStateEntrySnapshot
                    {
                        ItemId = 3001,
                        ExpireTime = (int)now + 15,
                    });
                    fallbackSnapshot.EffectItemStates.Add(new ItemStateEntrySnapshot
                    {
                        ItemId = 3002,
                        ExpireTime = (int)now - 1,
                    });
                    SqliteSelectCharacterDataSource.ApplyOnlineItemStates(
                        90000001,
                        fallbackSnapshot,
                        now);
                    Check(
                        "loaded item state snapshot fallback sends remaining seconds",
                        fallbackSnapshot.CooltimeItemStates.Count == 1
                        && fallbackSnapshot.CooltimeItemStates[0].ItemId == 3001
                        && fallbackSnapshot.CooltimeItemStates[0].ExpireTime == 15
                        && fallbackSnapshot.EffectItemStates.Count == 0,
                        ref failures);

                    using (var connection = database.OpenConnection())
                    {
                        Check(
                            "expired item states are removed through dirty persistence",
                            ReadExpireTime(connection, characterId, ItemStateKinds.Cooltime, 2002) == 0
                            && CountRows(connection, "character_item_states") == 2,
                            ref failures);
                    }

                    lock (lease.SyncRoot)
                        lease.Inventory.ItemStates.Upsert(ItemStateKinds.Cooltime, 2004, (int)now + 120);

                    Check(
                        "dirty ItemStates save through InventoryPersistenceService",
                        InventoryPersistenceService.SaveDirty(lease),
                        ref failures);

                    using (var connection = database.OpenConnection())
                    {
                        Check(
                            "saved item state is persisted",
                            ReadExpireTime(connection, characterId, ItemStateKinds.Cooltime, 2004) == (int)now + 120,
                            ref failures);
                    }
                }
                finally
                {
                    InventoryContext.Unregister(sessionId, characterId);
                }
            }
            finally
            {
                TryDelete(tempDbPath);
            }
        }

        private static void VerifyPvfLifecycleParsing(ref int failures)
        {
            var stackable = PvfLib.StackableItemFile.Parse(@"
[effect maintenance]
[stat change duration]
    1800000 `myself`
[cooltime maintenance]
[cool time]
    10000
");

            Check(
                "stackablefile parses lifecycle tags",
                stackable.HasEffectMaintenance
                && stackable.HasCooltimeMaintenance
                && stackable.StatChangeDurationMilliseconds == 1800000
                && string.Equals(stackable.StatChangeDurationTarget, "myself", StringComparison.Ordinal)
                && stackable.CoolTime == 10000,
                ref failures);
        }

        private static void VerifyLifecycleRules(ref int failures)
        {
            const long now = 1700000200;
            const int itemId = 72001;
            var stackable = PvfLib.StackableItemFile.Parse(@"
[effect maintenance]
[stat change duration]
    1800000 `myself`
[cooltime maintenance]
[cool time]
    10000
");

            var inventory = new InventoryService(72002, 72003);
            inventory.SetItem(
                InventoryListType.Main,
                10,
                CreateStackableCore(itemId, 2, 0));
            inventory.ClearDirtyState();

            var plan = InventoryItemLifecycleService.PrepareUseWithDefinition(
                inventory,
                InventoryListType.Main,
                10,
                itemId,
                now,
                1,
                stackable);
            Check(
                "lifecycle plan writes effect and cooltime deadlines",
                plan.Success
                && plan.EffectExpireTime == now + 1800
                && plan.CooltimeExpireTime == now + 10,
                ref failures);

            InventoryItemLifecycleService.ApplyUseSuccess(inventory, plan);
            inventory.ItemStates.TryGetExpireTime(
                ItemStateKinds.Effect,
                itemId,
                out var effectExpireTime);
            inventory.ItemStates.TryGetExpireTime(
                ItemStateKinds.Cooltime,
                itemId,
                out var cooltimeExpireTime);
            Check(
                "lifecycle success updates ItemStates cache",
                effectExpireTime == now + 1800
                && cooltimeExpireTime == now + 10
                && inventory.ItemStates.IsDirty,
                ref failures);

            var activePlan = InventoryItemLifecycleService.PrepareUseWithDefinition(
                inventory,
                InventoryListType.Main,
                10,
                itemId,
                now + 5,
                1,
                stackable);
            Check(
                "active effect state rejects repeated use",
                activePlan.Status == InventoryItemLifecycleStatus.EffectActive,
                ref failures);

            var coolTimeOnly = PvfLib.StackableItemFile.Parse(@"
[cool time]
    10000
");
            inventory.ItemStates.Remove(ItemStateKinds.Effect, itemId);
            inventory.ItemStates.Remove(ItemStateKinds.Cooltime, itemId);
            var noMaintenancePlan = InventoryItemLifecycleService.PrepareUseWithDefinition(
                inventory,
                InventoryListType.Main,
                10,
                itemId,
                now,
                1,
                coolTimeOnly);
            Check(
                "cool time without maintenance does not create state deadline",
                noMaintenancePlan.Success
                && noMaintenancePlan.CooltimeExpireTime == 0
                && noMaintenancePlan.EffectExpireTime == 0,
                ref failures);

            inventory.SetItem(
                InventoryListType.Main,
                10,
                CreateStackableCore(73001, 1, (int)now - 1));
            inventory.SetItem(
                InventoryListType.Main,
                11,
                CreateStackableCore(73001, 1, (int)now + 100));
            var expiredPlan = InventoryItemLifecycleService.PrepareUseWithDefinition(
                inventory,
                InventoryListType.Main,
                10,
                73001,
                now,
                1,
                null);
            Check(
                "expired use removes only current slot",
                expiredPlan.SourceExpiredDeleted
                && inventory.GetItem(InventoryListType.Main, 10) == null
                && inventory.GetItem(InventoryListType.Main, 11)?.ItemId == 73001,
                ref failures);

            inventory.SetItem(
                InventoryListType.Main,
                12,
                CreateStackableCore(73002, 1, (int)now - 1));
            inventory.SetItem(
                InventoryListType.Main,
                13,
                CreateStackableCore(73003, 1, 0));
            var changes = new InventoryMutationSet();
            var removed = InventoryItemLifecycleService.RemoveExpiredItemsInRange(
                inventory,
                InventoryListType.Main,
                new ItemSlotRange(12, 13),
                now,
                changes);
            Check(
                "sort range cleanup removes expired items before sorting",
                removed == 1
                && changes.HasChanges
                && inventory.GetItem(InventoryListType.Main, 12) == null
                && inventory.GetItem(InventoryListType.Main, 13)?.ItemId == 73003,
                ref failures);

            Check(
                "ItemCore ExpireTime zero is treated as active",
                !InventoryItemLifecycleService.IsExpired(
                    CreateStackableCore(73004, 1, 0),
                    now),
                ref failures);

            var cleanupInventory = new InventoryService(72004, 72005);
            cleanupInventory.SetListParam16(InventoryListType.AccountCargo, 8);
            cleanupInventory.SetItem(
                InventoryListType.Main,
                14,
                CreateStackableCore(74000, 1, 0));
            cleanupInventory.SetItem(
                InventoryListType.Equipment,
                (short)EquipmentType.Weapon,
                CreateCore(ItemCore.KindEquipment, 74001, 1, (int)now - 1));
            cleanupInventory.SetItem(
                InventoryListType.PersonalCargo,
                0,
                CreateStackableCore(74002, 1, (int)now - 1));
            cleanupInventory.SetItem(
                InventoryListType.AccountCargo,
                0,
                CreateStackableCore(74003, 1, (int)now - 1));
            cleanupInventory.SetItem(
                InventoryListType.Pet,
                (short)ItemSlotBoundService.PetEquipmentSlotStart,
                CreateCore(ItemCore.KindCreatureEquipment, 74004, 1, (int)now - 1));

            cleanupInventory.SetItem(
                InventoryListType.Avatar,
                0,
                CreateCore(ItemCore.KindAvatar, 74010, 9101, (int)now - 1));
            cleanupInventory.AvatarDetails.Attach(CreateAvatarDetail(9101, 74010, (int)now + 100));
            cleanupInventory.SetItem(
                InventoryListType.Avatar,
                1,
                CreateCore(ItemCore.KindAvatar, 74011, 9102, (int)now + 100));
            cleanupInventory.AvatarDetails.Attach(CreateAvatarDetail(9102, 74011, (int)now - 1));

            cleanupInventory.SetItem(
                InventoryListType.Pet,
                0,
                CreateCore(ItemCore.KindCreature, 74020, 9201, (int)now - 1));
            cleanupInventory.CreatureDetails.Attach(CreateCreatureDetail(9201, (int)now + 100));
            cleanupInventory.SetItem(
                InventoryListType.Pet,
                1,
                CreateCore(ItemCore.KindCreature, 74021, 9202, (int)now + 100));
            cleanupInventory.CreatureDetails.Attach(CreateCreatureDetail(9202, (int)now - 1));
            cleanupInventory.ClearDirtyState();

            var cleanupChanges = new InventoryMutationSet();
            var cleanupRemoved = InventoryItemLifecycleService.RemoveExpiredItems(
                cleanupInventory,
                now,
                cleanupChanges);
            Check(
                "lifecycle cleanup removes core-expired equipment and cargo items",
                cleanupRemoved == 6
                && cleanupChanges.HasChanges
                && cleanupInventory.GetItem(InventoryListType.Main, 14)?.ItemId == 74000
                && cleanupInventory.GetItem(InventoryListType.Equipment, (short)EquipmentType.Weapon) == null
                && cleanupInventory.GetItem(InventoryListType.PersonalCargo, 0) == null
                && cleanupInventory.GetItem(InventoryListType.AccountCargo, 0) == null
                && cleanupInventory.GetItem(InventoryListType.Pet, (short)ItemSlotBoundService.PetEquipmentSlotStart) == null,
                ref failures);
            Check(
                "avatar cleanup uses avatar detail expire dates",
                cleanupInventory.GetItem(InventoryListType.Avatar, 0)?.ItemId == 74010,
                ref failures);
            Check(
                "avatar cleanup removes expired avatar slot",
                cleanupInventory.GetItem(InventoryListType.Avatar, 1) == null,
                ref failures);
            Check(
                "avatar cleanup keeps active avatar detail",
                cleanupInventory.AvatarDetails.GetDetail(9101) != null,
                ref failures);
            Check(
                "avatar cleanup removes expired avatar detail",
                cleanupInventory.AvatarDetails.GetDetail(9102) == null,
                ref failures);
            Check(
                "avatar cleanup marks one avatar detail deleted",
                cleanupInventory.AvatarDetails.DeletedDetailUids.Count == 1,
                ref failures);
            Check(
                "pet cleanup uses creature detail expire dates",
                cleanupInventory.GetItem(InventoryListType.Pet, 0)?.ItemId == 74020,
                ref failures);
            Check(
                "pet cleanup removes expired pet slot",
                cleanupInventory.GetItem(InventoryListType.Pet, 1) == null,
                ref failures);
            Check(
                "pet cleanup keeps active creature detail",
                cleanupInventory.CreatureDetails.GetDetail(9201) != null,
                ref failures);
            Check(
                "pet cleanup removes expired creature detail",
                cleanupInventory.CreatureDetails.GetDetail(9202) == null,
                ref failures);
            Check(
                "pet cleanup marks one creature detail deleted",
                cleanupInventory.CreatureDetails.DeletedDetailUids.Count == 1,
                ref failures);
        }

        private static void SeedAccount(GameDatabase database, int accountId, string mid)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@mid", mid);
                command.ExecuteNonQuery();
            }
        }

        private static void SeedCharacter(
            GameDatabase database,
            int characterId,
            int accountId,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertItemState(
            GameDatabase database,
            int characterId,
            string stateKind,
            int itemId,
            int expireTime)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO character_item_states(character_id, state_kind, item_id, expire_time)
VALUES (@cid, @kind, @itemId, @expireTime);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@kind", stateKind);
                command.Parameters.AddWithValue("@itemId", itemId);
                command.Parameters.AddWithValue("@expireTime", expireTime);
                command.ExecuteNonQuery();
            }
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type = 'table' AND name = @name;";
                command.Parameters.AddWithValue("@name", tableName);
                return Convert.ToInt32(command.ExecuteScalar()) != 0;
            }
        }

        private static int CountRows(SqliteConnection connection, string tableName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int ReadExpireTime(
            SqliteConnection connection,
            int characterId,
            string stateKind,
            int itemId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT expire_time
FROM character_item_states
WHERE character_id = @cid AND state_kind = @kind AND item_id = @itemId;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@kind", stateKind);
                command.Parameters.AddWithValue("@itemId", itemId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? 0
                    : Convert.ToInt32(value);
            }
        }

        private static ItemCore CreateStackableCore(
            int itemId,
            int count,
            int expireTime)
        {
            return new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = itemId,
                Count = count,
                Durability = 0,
                ExpireTime = expireTime,
            };
        }

        private static ItemCore CreateCore(
            byte itemKind,
            int itemId,
            int value,
            int expireTime)
        {
            return new ItemCore
            {
                ItemKind = itemKind,
                ItemId = itemId,
                Value = value,
                ExpireTime = expireTime,
            };
        }

        private static AvatarDetail CreateAvatarDetail(
            int avatarUid,
            int itemId,
            int expireDate)
        {
            return new AvatarDetail
            {
                AvatarUid = avatarUid,
                OwnerId = 72005,
                CharacterId = 72004,
                ItemId = itemId,
                ExpireDate = expireDate,
            };
        }

        private static CreatureDetail CreateCreatureDetail(
            int creatureKey,
            int expireDate)
        {
            return new CreatureDetail
            {
                Uid = creatureKey,
                Field04 = 100,
                FieldAfterValue32 = 1,
                ExpireDate = expireDate,
            };
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine($"[FAIL] {name}");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
