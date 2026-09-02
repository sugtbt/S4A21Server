using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.KnightShield;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    public static class A21KnightShieldDeckSelfTest
    {
        private const int AccountId = 9751;
        private const int CharacterId = 9752;
        private const int ElvenShield = 113370003;
        private const int ElvenSpare = 113370004;
        private const int ChaosShield = 113370025;
        private const int SharedLevelShield = 113370033;
        private const int ChaosOpenQuest = 12782;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== A21_KNIGHT_SHIELD_DECK selftest ===");

            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-a21-knight-shield-deck-selftest-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(database);
                var repository = new KnightShieldDeckRepository(database);
                var service = new KnightShieldService(repository);
                var character = new CharacterRecord
                {
                    CharacterId = CharacterId,
                    AccountId = AccountId,
                    Job = KnightShieldDataProvider.GuardianJob,
                    GrowType = 1,
                    Level = 99,
                };

                RunProtocolChecks();
                RunCatalogAndQuestChecks();
                RunServiceChecks(repository, service, character);
                RunProjectionChecks(character);
            }
            finally
            {
                TryDelete(databasePath);
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");
            }

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void RunProtocolChecks()
        {
            var deck = new KnightShieldDeckSnapshot(new[]
            {
                ElvenShield,
                ElvenSpare,
                0,
                SharedLevelShield,
                0,
            });
            var body = KnightShieldDeckBodyBuilder.BuildDeck(deck);
            Check(
                "SEND_DECK_INFO writes five int32 slots",
                body.Length == KnightShieldDeckBodyBuilder.DeckBodyLength
                && BitConverter.ToInt32(body, 0) == ElvenShield
                && BitConverter.ToInt32(body, 4) == ElvenSpare
                && BitConverter.ToInt32(body, 8) == 0
                && BitConverter.ToInt32(body, 12) == SharedLevelShield
                && BitConverter.ToInt32(body, 16) == 0);

            var ack = KnightShieldDeckBodyBuilder.BuildChangeDeckAck(deck);
            Check(
                "shield deck protocol bodies keep five authoritative slots",
                ack.Length == KnightShieldDeckBodyBuilder.ChangeDeckAckLength
                && ack[KnightShieldDeckBodyBuilder.ChangeDeckAckStatusOffset] == 1
                && ack[KnightShieldDeckBodyBuilder.ChangeDeckAckReservedOffset] == 0
                && BitConverter.ToInt32(ack, KnightShieldDeckBodyBuilder.ChangeDeckAckSlotsOffset)
                    == ElvenShield
                && body.Length == KnightShieldDeckBodyBuilder.DeckBodyLength
                && BitConverter.ToInt32(body, 0) == ElvenShield
                && BitConverter.ToInt32(body, 4) == ElvenSpare
                && BitConverter.ToInt32(body, 12) == SharedLevelShield);
        }

        private static void RunCatalogAndQuestChecks()
        {
            var catalogOk = KnightShieldDataProvider.IsCatalogShield(1, ElvenShield)
                && !KnightShieldDataProvider.IsCatalogShield(2, ElvenShield)
                && KnightShieldDataProvider.IsCatalogShield(1, SharedLevelShield)
                && KnightShieldDataProvider.IsCatalogShield(2, SharedLevelShield);

            var questOk = KnightShieldDataProvider.TryGetCatalogEntry(
                    1,
                    ElvenShield,
                    out var questEntry)
                && questEntry.UnlockKind == KnightShieldUnlockKind.Quest
                && !KnightShieldDataProvider.IsCatalogShieldUnlocked(
                    KnightShieldDataProvider.GuardianJob,
                    1,
                    ElvenShield,
                    99,
                    new HashSet<int>())
                && KnightShieldDataProvider.IsCatalogShieldUnlocked(
                    KnightShieldDataProvider.GuardianJob,
                    1,
                    ElvenShield,
                    99,
                    new HashSet<int> { questEntry.ClearQuestId });

            var levelOk = KnightShieldDataProvider.TryGetCatalogEntry(
                    1,
                    SharedLevelShield,
                    out var levelEntry)
                && levelEntry.UnlockKind == KnightShieldUnlockKind.Level
                && !KnightShieldDataProvider.IsCatalogShieldUnlocked(
                    KnightShieldDataProvider.GuardianJob,
                    1,
                    SharedLevelShield,
                    levelEntry.RequiredLevel - 1,
                    new HashSet<int>())
                && KnightShieldDataProvider.IsCatalogShieldUnlocked(
                    KnightShieldDataProvider.GuardianJob,
                    1,
                    SharedLevelShield,
                    levelEntry.RequiredLevel,
                    new HashSet<int>());

            Check(
                "catalog unlocks use PVF gates and real quest completion",
                catalogOk && questOk && levelOk
                && !ParseQuestIds(QuestListBodyBuilder.BuildBody(
                    55,
                    KnightShieldDataProvider.GuardianJob,
                    2,
                    new Dictionary<int, int>()))
                    .Contains(ChaosOpenQuest));
        }

        private static void RunServiceChecks(
            KnightShieldDeckRepository repository,
            KnightShieldService service,
            CharacterRecord character)
        {
            var clearIds = GetClearIds(1, ElvenShield, ElvenSpare);
            var initiallyEmpty = Matches(repository.Load(CharacterId), 0, 0, 0, 0, 0);
            var mutations = service.TryEquipMain(
                    character,
                    ElvenShield,
                    out _,
                    out _,
                    clearIds)
                && service.TryEquipSlot(
                    character,
                    1,
                    ElvenSpare,
                    out var equipped,
                    out _,
                    clearIds)
                && Matches(equipped, ElvenShield, ElvenSpare, 0, 0, 0)
                && service.TryMoveDeckSlot(
                    character,
                    0,
                    1,
                    out var swapped,
                    out _)
                && Matches(swapped, ElvenSpare, ElvenShield, 0, 0, 0)
                && Matches(repository.Load(CharacterId), ElvenSpare, ElvenShield, 0, 0, 0);

            var rejects = !service.TryEquipMain(
                    character,
                    ElvenShield,
                    out _,
                    out _,
                    new HashSet<int>())
                && !service.TryEquipMain(
                    character,
                    ChaosShield,
                    out _,
                    out _,
                    GetClearIds(2, ChaosShield));

            repository.Save(CharacterId, new KnightShieldDeckSnapshot(new[]
            {
                ElvenShield,
                ElvenSpare,
                0,
                0,
                0,
            }));
            Check(
                "deck service persists, validates, and reconciles slot 24",
                initiallyEmpty
                && mutations
                && rejects
                && Matches(
                    service.ReconcileOnSelect(character, ElvenSpare, clearIds),
                    ElvenSpare,
                    0,
                    0,
                    0,
                    0));
        }

        private static void RunProjectionChecks(CharacterRecord character)
        {
            var deck = new KnightShieldDeckSnapshot(new[] { ElvenShield, 0, 0, 0, 0 });
            var appearance = KnightShieldAppearanceSynchronizer.Apply(
                Array.Empty<CharacterAppearanceEntry>(),
                character.Job,
                character.GrowType,
                deck);
            var existingCore = ItemCore.Create(ItemCore.KindEquipment, ElvenShield);
            existingCore.Attr = 7;
            var addition = new UserInfoAdditionSnapshot();
            addition.EquippedEntries.Add(new EquippedEntrySnapshot
            {
                Slot = (short)EquipmentType.SupportWeapon,
                Core = existingCore,
            });
            KnightShieldEquipmentSnapshotSynchronizer.Apply(
                character.Job,
                character.GrowType,
                addition,
                deck);
            Check(
                "projection uses slot 24 without replacing ItemCore",
                EquipmentTypeInfo.IsA21RosterAppearanceSlot((short)EquipmentType.SupportWeapon)
                && appearance.Length == 1
                && appearance[0].Slot == (byte)EquipmentType.SupportWeapon
                && appearance[0].DisplayItemId == ElvenShield
                && addition.EquippedEntries.Count == 1
                && ReferenceEquals(addition.EquippedEntries[0].Core, existingCore)
                && addition.EquippedEntries[0].Core.Attr == 7);
        }

        private static HashSet<int> GetClearIds(int growType, params int[] itemIds)
        {
            var result = new HashSet<int>();
            foreach (var itemId in itemIds)
            {
                if (KnightShieldDataProvider.TryGetCatalogEntry(growType, itemId, out var entry)
                    && entry.ClearQuestId > 0)
                    result.Add(entry.ClearQuestId);
            }
            return result;
        }

        private static HashSet<int> ParseQuestIds(byte[] body)
        {
            var result = new HashSet<int>();
            if (body == null || body.Length < 3)
                return result;
            var count = BitConverter.ToUInt16(body, 1);
            if (body.Length < 3 + (count * 2))
                return result;
            for (var index = 0; index < count; index++)
                result.Add(BitConverter.ToUInt16(body, 3 + (index * 2)));
            return result;
        }

        private static bool Matches(KnightShieldDeckSnapshot snapshot, params int[] expected)
        {
            if (snapshot == null || expected == null || expected.Length != KnightShieldDeckSnapshot.SlotCount)
                return false;
            for (var index = 0; index < expected.Length; index++)
            {
                if (snapshot.GetShieldItemId(index) != expected[index])
                    return false;
            }
            return true;
        }

        private static void SeedCharacter(GameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, 'a21-knight-shield-deck-selftest', '');
INSERT INTO characters(character_id, account_id, name, job, grow_type, level)
VALUES(@cid, @aid, 'a21-knight-shield-deck-selftest', 12, 1, 99);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.ExecuteNonQuery();
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
