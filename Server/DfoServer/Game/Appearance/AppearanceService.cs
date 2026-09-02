using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.KnightShield;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Appearance
{
    public static class AppearanceService
    {
        public static byte[] UpdateAndBroadcast(
            PlayerContext player,
            ICharacterRepository characterRepository,
            IGameDatabase database = null)
        {
            var updated = LoadOnlineAppearanceFromInventory(
                player.CharacterId,
                player.Job,
                player.GrowType,
                database: database);

            player.AppearanceEntries = updated;
            characterRepository.UpdateAppearance(player.CharacterId, updated);

            return BuildNoti2Body(player, database);
        }

        public static byte[] RefreshPlayerAppearance(
            PlayerContext player,
            ICharacterRepository characterRepository,
            KnightShieldDeckSnapshot knightShieldDeck,
            IGameDatabase database = null)
        {
            if (player == null)
                return Array.Empty<byte>();

            var updated = LoadOnlineAppearanceFromInventory(
                player.CharacterId,
                player.Job,
                player.GrowType,
                knightShieldDeck,
                database);
            player.AppearanceEntries = updated;
            if (characterRepository != null && player.CharacterId > 0)
                characterRepository.UpdateAppearance(player.CharacterId, updated);

            return BuildNoti2Body(player, database);
        }

        public static byte[] SetCloneTitleAndBroadcast(
            PlayerContext player,
            ICharacterRepository characterRepository,
            int characterId,
            int cloneTitleItemId,
            IGameDatabase database = null)
        {
            SaveCloneTitleItemId(characterId, cloneTitleItemId, database);
            var tail = player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
            tail.CloneTitleItemId = (uint)(cloneTitleItemId > 0 ? cloneTitleItemId : 0);
            player.Subtype0Tail = tail;
            var updated = LoadOnlineAppearanceFromInventory(
                characterId,
                player.Job,
                player.GrowType,
                database: database);

            player.AppearanceEntries = updated;
            characterRepository.UpdateAppearance(characterId, updated);

            return BuildNoti2Body(player, database);
        }

        public static void PersistCloneTitle(
            int characterId,
            int cloneTitleItemId,
            IGameDatabase database = null)
        {
            SaveCloneTitleItemId(characterId, cloneTitleItemId, database);
        }

        public static byte[] SetRuntimeCloneTitleAndBuildNoti2(
            PlayerContext player,
            int cloneTitleItemId,
            IGameDatabase database = null)
        {
            if (player == null)
                return Array.Empty<byte>();

            if (player.CharacterId > 0)
            {
                SaveCloneTitleItemId(player.CharacterId, cloneTitleItemId, database);
                var tail = player.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
                tail.CloneTitleItemId = (uint)(cloneTitleItemId > 0 ? cloneTitleItemId : 0);
                player.Subtype0Tail = tail;
                player.AppearanceEntries = LoadOnlineAppearanceFromInventory(
                    player.CharacterId,
                    player.Job,
                    player.GrowType,
                    database: database);
            }

            return BuildNoti2Body(player, database);
        }

        public static byte[] BuildCloneTitleAckBody(int cloneTitleItemId, byte state = 0, byte suppressMessage = 0)
        {
            var ack = new byte[5];
            ack[0] = 0x01;
            BitConverter.GetBytes(cloneTitleItemId).CopyTo(ack, 1);
            return ack;
        }

        public static CharacterAppearanceEntry[] LoadOnlineAppearanceFromInventory(
            int characterId,
            byte job,
            int growType,
            KnightShieldDeckSnapshot knightShieldDeck = null,
            IGameDatabase database = null)
        {
            var projectionBuilder = new Noti2InventoryProjectionBuilder();
            CharacterAppearanceEntry[] entries;
            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                    entries = projectionBuilder.BuildAppearanceEntries(lease.Inventory);

                var projected = ApplyKnightShieldAppearance(
                    entries,
                    characterId,
                    job,
                    growType,
                    knightShieldDeck,
                    database ?? lease.Inventory.Database);
                if (projected.Length > 0)
                    return projected;

                return LoadCharacterAppearanceFromDb(
                    new CharacterRecord
                    {
                        CharacterId = characterId,
                        Job = job,
                        GrowType = (byte)growType,
                    },
                    database ?? GameDatabase.CreateDefault());
            }

            return LoadCharacterAppearanceFromDb(
                new CharacterRecord
                {
                    CharacterId = characterId,
                    Job = job,
                    GrowType = (byte)growType,
                },
                database ?? GameDatabase.CreateDefault());
        }

        internal static CharacterAppearanceEntry[] DropStaleFashionFromAppearanceBlob(
            CharacterAppearanceEntry[] blob)
        {
            if (blob == null || blob.Length == 0)
                return Array.Empty<CharacterAppearanceEntry>();

            var result = new List<CharacterAppearanceEntry>(blob.Length);
            foreach (var entry in blob)
            {
                if (entry == null || entry.DisplayItemId <= 0)
                    continue;
                if (entry.Slot <= (byte)EquipmentType.AuroraIllusionAvatar)
                    continue;
                result.Add(entry);
            }

            return result.ToArray();
        }

        public static CharacterAppearanceEntry[] LoadCharacterAppearanceFromDb(
            CharacterRecord character,
            IGameDatabase database = null)
        {
            if (character == null || character.CharacterId <= 0)
                return Array.Empty<CharacterAppearanceEntry>();

            database ??= GameDatabase.CreateDefault();
            using (var connection = database.OpenConnection())
            {
                var equippedItems = InventoryItemRepository.LoadEquippedItems(connection, character.CharacterId);
                var avatarDetails = AvatarDetailRepository.LoadForCharacter(connection, character.CharacterId);
                var projectionBuilder = new Noti2InventoryProjectionBuilder();
                var entries = projectionBuilder.BuildAppearanceEntries(equippedItems, avatarDetails);
                KnightShieldDeckSnapshot deck = null;
                if (KnightShieldDataProvider.IsEligibleCharacter(character.Job))
                {
                    deck = KnightShieldDeckRepository
                        .FromConnectionString(database.ConnectionString)
                        .Load(character.CharacterId);
                }
                return ApplyKnightShieldAppearance(
                    entries,
                    character.CharacterId,
                    character.Job,
                    character.GrowType,
                    deck,
                    database);
            }
        }

        public static Dictionary<int, CharacterAppearanceEntry[]> LoadRosterAppearancesFromDb(
            int accountId,
            IReadOnlyList<CharacterRecord> characters,
            IGameDatabase database = null)
        {
            var result = new Dictionary<int, CharacterAppearanceEntry[]>();
            if (accountId <= 0)
                return result;
            characters = characters ?? Array.Empty<CharacterRecord>();

            database ??= GameDatabase.CreateDefault();
            using (var connection = database.OpenConnection())
            {
                var equippedItems = InventoryItemRepository.LoadEquippedItemsByAccount(connection, accountId);
                var avatarDetails = AvatarDetailRepository.LoadForAccount(connection, accountId);
                var grouped = new Dictionary<int, List<InventoryItem>>();
                foreach (var item in equippedItems)
                {
                    if (item == null || !item.CharacterId.HasValue || item.CharacterId.Value <= 0)
                        continue;

                    if (!grouped.TryGetValue(item.CharacterId.Value, out var list))
                    {
                        list = new List<InventoryItem>();
                        grouped[item.CharacterId.Value] = list;
                    }

                    list.Add(item);
                }

                var projectionBuilder = new Noti2InventoryProjectionBuilder();
                var knightShieldRepository = KnightShieldDeckRepository.FromConnectionString(database.ConnectionString);
                foreach (var character in characters)
                {
                    if (character == null || character.CharacterId <= 0)
                        continue;

                    grouped.TryGetValue(character.CharacterId, out var characterItems);
                    var entries = characterItems != null
                        ? projectionBuilder.BuildAppearanceEntries(characterItems, avatarDetails)
                        : Array.Empty<CharacterAppearanceEntry>();
                    var deck = KnightShieldDataProvider.IsEligibleCharacter(character.Job)
                        ? knightShieldRepository.Load(character.CharacterId)
                        : null;
                    result[character.CharacterId] = ApplyKnightShieldAppearance(
                        entries,
                        character.CharacterId,
                        character.Job,
                        character.GrowType,
                        deck,
                        database);
                }
            }

            return result;
        }

        private static CharacterAppearanceEntry[] ApplyKnightShieldAppearance(
            CharacterAppearanceEntry[] entries,
            int characterId,
            byte job,
            int growType,
            KnightShieldDeckSnapshot knightShieldDeck,
            IGameDatabase database)
        {
            if (!KnightShieldDataProvider.IsEligibleCharacter(job))
                return entries ?? Array.Empty<CharacterAppearanceEntry>();

            var deck = knightShieldDeck
                ?? new KnightShieldDeckRepository(database ?? GameDatabase.CreateDefault())
                    .Load(characterId);
            return KnightShieldAppearanceSynchronizer.Apply(entries, job, growType, deck);
        }

        internal static CharacterAppearanceEntry[] BuildFromEquippedEntries(
            IEnumerable<EquippedEntrySnapshot> equippedEntries)
        {
            var result = new List<CharacterAppearanceEntry>();
            if (equippedEntries == null)
                return result.ToArray();

            foreach (var entry in equippedEntries)
            {
                if (entry == null)
                    continue;

                // 外观列表的 itemId 保持真实穿戴模板；替换称号动画还会由 subtype0 tail 首字段刷新。
                // A21 副武器外观槽是 EquipmentType.SupportWeapon（24）；守护者主盾走同一标准装备槽。
                if (!EquipmentTypeInfo.IsA21RosterAppearanceSlot(entry.Slot))
                    continue;
                var core = entry.Core;
                if (core == null || core.ItemId == 0)
                    continue;

                int displayItemId = core.ItemId;

                result.Add(new CharacterAppearanceEntry(
                    (byte)entry.Slot,
                    displayItemId,
                    4,
                    BuildAppearanceExpansionData(core),
                    BuildAppearanceState(core),
                    0,
                    0u,
                    core.EnchantUpgradeCount));
            }

            return result.ToArray();
        }

        internal static byte BuildAppearanceState(ItemCore core)
        {
            if (core == null)
                return 0;

            var upgrade = core.Attr & 0x1F;
            return unchecked((byte)(upgrade * 2 + (core.AmplifyType != 0 ? 1 : 0)));
        }

        private static byte[] BuildAppearanceExpansionData(ItemCore core)
        {
            return new byte[4];
        }

        public static int LoadCloneTitleItemId(
            int characterId,
            IGameDatabase database = null)
        {
            if (characterId <= 0)
                return 0;

            database ??= GameDatabase.CreateDefault();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT clone_title_item_id FROM characters WHERE character_id = @cid LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
            }
        }

        public static void SaveCloneTitleItemId(
            int characterId,
            int cloneTitleItemId,
            IGameDatabase database = null)
        {
            if (characterId <= 0)
                return;

            database ??= GameDatabase.CreateDefault();
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE characters
SET clone_title_item_id = @cloneTitleItemId,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cloneTitleItemId", cloneTitleItemId > 0 ? cloneTitleItemId : 0);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        public static byte[] BuildNoti2Body(
            PlayerContext player,
            IGameDatabase database = null)
        {
            var record = new CharacterRecord
            {
                CharacterId = player.CharacterId,
                Name = player.Name,
                Job = player.Job,
                GrowType = player.GrowType,
                Level = player.Level,
                UserState = player.UserState,
                Appearance = player.AppearanceEntries,
                Subtype0Tail = player.Subtype0Tail,
            };

            if (record.Subtype0Tail == null && player.CharacterId > 0)
            {
                record.Subtype0Tail = new SqliteSubtype0FieldsRepository(
                        database ?? GameDatabase.CreateDefault())
                    .Load(player.CharacterId);
                player.Subtype0Tail = record.Subtype0Tail;
            }

            if (record.Subtype0Tail == null)
                record.Subtype0Tail = new UserInfoMinimumTailSnapshot();

            return UserInfoSubtype0Builder.BuildNotificationBody(record);
        }
    }
}
