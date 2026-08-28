using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers.Pets;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class PetCreatureRuntimeSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== PET_CREATURE_RUNTIME selftest ===");
            var failures = 0;

            VerifyTownPreviewAndApply(ref failures);
            VerifyRevivalPreviewAndApply(ref failures);
            VerifyNoopCommitSkipsDatabase(ref failures);
            Check(
                "failed zero-delay death commit uses positive retry backoff",
                PetCreatureRuntimeService.DeathCommitRetryDelay >= TimeSpan.FromSeconds(1),
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "PET_CREATURE_RUNTIME selftest passed."
                    : $"PET_CREATURE_RUNTIME selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyTownPreviewAndApply(ref int failures)
        {
            var inventory = CreateEquippedCreatureInventory(
                characterId: 909001,
                accountId: 909000,
                creatureKey: 7001,
                stomach: 50,
                database: null);
            var detail = inventory.CreatureDetails.GetDetail(7001);
            var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

            var noopPreview = PetCreatureSatietyService.PreviewTownElapsed(
                inventory,
                start,
                start.AddSeconds(359));
            Check(
                "town preview keeps sub-threshold recovery out of dirty state",
                !noopPreview.StateChanged
                && detail.Stomach == 50
                && inventory.CreatureDetails.DirtyDetailUids.Count == 0,
                ref failures);

            var changedPreview = PetCreatureSatietyService.PreviewTownElapsed(
                inventory,
                start,
                start.AddSeconds(360));
            Check(
                "town preview reports threshold change without mutating detail",
                changedPreview.StateChanged
                && changedPreview.Before == 50
                && changedPreview.After == 51
                && detail.Stomach == 50
                && inventory.CreatureDetails.DirtyDetailUids.Count == 0,
                ref failures);

            var applied = PetCreatureSatietyService.ApplyTownElapsed(
                inventory,
                start,
                start.AddSeconds(360));
            Check(
                "town apply mutates and marks detail only after visible change",
                applied.StateChanged
                && detail.Stomach == 51
                && inventory.CreatureDetails.DirtyDetailUids.Contains(7001),
                ref failures);
        }

        private static void VerifyRevivalPreviewAndApply(ref int failures)
        {
            var inventory = CreateEquippedCreatureInventory(
                characterId: 909011,
                accountId: 909010,
                creatureKey: 7011,
                stomach: 0,
                database: null);
            var detail = inventory.CreatureDetails.GetDetail(7011);

            var preview = PetCreatureSatietyService.PreviewRevival(inventory);
            Check(
                "revival preview does not mutate a dead creature",
                preview.Revived
                && preview.Before == 0
                && preview.After == 1
                && detail.Stomach == 0
                && inventory.CreatureDetails.DirtyDetailUids.Count == 0,
                ref failures);

            var applied = PetCreatureSatietyService.ReviveEquippedCreatureIfDead(inventory);
            Check(
                "revival apply marks the creature detail dirty",
                applied.Revived
                && detail.Stomach == 1
                && inventory.CreatureDetails.DirtyDetailUids.Contains(7011),
                ref failures);
        }

        private static void VerifyNoopCommitSkipsDatabase(ref int failures)
        {
            var database = new ThrowingGameDatabase();
            var inventory = CreateEquippedCreatureInventory(
                characterId: 909021,
                accountId: 909020,
                creatureKey: 7021,
                stomach: 100,
                database);
            var sessionId = Guid.NewGuid();
            var lease = InventoryContext.Register(sessionId, inventory);
            try
            {
                var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
                var committed = PetCreatureSatietyCommitService.TryCommitTownElapsed(
                    lease,
                    start,
                    start.AddMinutes(30),
                    out var update);
                Check(
                    "no-op town recovery does not open a SQLite connection",
                    committed
                    && !update.StateChanged
                    && database.OpenConnectionCalls == 0,
                    ref failures);

                committed = PetCreatureSatietyCommitService.TryCommitDungeonElapsed(
                    lease,
                    start,
                    start,
                    out update);
                Check(
                    "no-op dungeon elapsed does not open a SQLite connection",
                    committed
                    && !update.StateChanged
                    && database.OpenConnectionCalls == 0,
                    ref failures);

                committed = PetCreatureSatietyCommitService.TryCommitDungeonDeath(
                    lease,
                    start,
                    start,
                    out update);
                Check(
                    "no-op dungeon death check does not open a SQLite connection",
                    committed
                    && !update.StateChanged
                    && database.OpenConnectionCalls == 0,
                    ref failures);

                var revivalCommitted = PetCreatureSatietyCommitService.TryCommitRevival(
                    lease,
                    out var revivalUpdate);
                Check(
                    "no-op revival does not open a SQLite connection",
                    revivalCommitted
                    && !revivalUpdate.Revived
                    && database.OpenConnectionCalls == 0,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, inventory.CharacterId);
            }
        }

        private static InventoryService CreateEquippedCreatureInventory(
            int characterId,
            int accountId,
            int creatureKey,
            byte stomach,
            IGameDatabase database)
        {
            var inventory = new InventoryService(characterId, accountId, database);
            inventory.AttachItem(
                InventoryListType.Equipment,
                PetInventoryLayout.CreatureEquipSlot,
                new ItemCore
                {
                    ItemKind = ItemCore.KindCreature,
                    ItemId = 63000,
                    Value = creatureKey,
                });
            inventory.CreatureDetails.Attach(new CreatureDetail
            {
                Uid = creatureKey,
                Stomach = stomach,
                FieldAfterValue32 = 1,
            });
            inventory.ClearDirtyState();
            return inventory;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private sealed class ThrowingGameDatabase : IGameDatabase
        {
            public int OpenConnectionCalls { get; private set; }
            public string DatabasePath => string.Empty;
            public string SchemaFilePath => string.Empty;
            public string ConnectionString => "Data Source=unused.db";

            public SqliteConnection OpenConnection()
            {
                OpenConnectionCalls++;
                throw new InvalidOperationException("no-op commit opened the database");
            }

            public T Read<T>(Func<SqliteConnection, T> action) =>
                throw new NotSupportedException();

            public T Write<T>(
                Func<SqliteConnection, SqliteTransaction, T> action,
                bool immediate = true) =>
                throw new NotSupportedException();

            public void Write(
                Action<SqliteConnection, SqliteTransaction> action,
                bool immediate = true) =>
                throw new NotSupportedException();
        }
    }
}
