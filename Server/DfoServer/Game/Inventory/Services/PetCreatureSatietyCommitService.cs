using System;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureSatietyCommitService
    {
        internal static bool TryCommitDungeonElapsed(
            InventoryLease lease,
            DateTime startUtc,
            DateTime endUtc,
            out PetCreatureSatietyUpdate update)
        {
            update = PetCreatureSatietyUpdate.Noop(
                lease?.CharacterId ?? 0);
            if (lease?.Inventory == null || startUtc == DateTime.MinValue)
                return false;

            if (!TryPreview(
                    lease,
                    inventory => PetCreatureSatietyService.PreviewDungeonElapsed(
                        inventory,
                        startUtc,
                        endUtc),
                    out update))
                return false;
            if (!update.StateChanged)
                return true;

            var committedUpdate = update;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "persist-pet-dungeon-elapsed",
                (connection, transaction) =>
                {
                    committedUpdate = PetCreatureSatietyService
                        .ApplyDungeonElapsedForCommit(
                            lease.Inventory,
                            startUtc,
                            endUtc);
                    return true;
                });
            if (!committed)
                return false;

            update = committedUpdate;
            return true;
        }

        internal static bool TryCommitTownElapsed(
            InventoryLease lease,
            DateTime startUtc,
            DateTime endUtc,
            out PetCreatureSatietyUpdate update)
        {
            update = PetCreatureSatietyUpdate.Noop(
                lease?.CharacterId ?? 0);
            if (lease?.Inventory == null || startUtc == DateTime.MinValue)
                return false;

            if (!TryPreview(
                    lease,
                    inventory => PetCreatureSatietyService.PreviewTownElapsed(
                        inventory,
                        startUtc,
                        endUtc),
                    out update))
                return false;
            if (!update.StateChanged)
                return true;

            var committedUpdate = update;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "persist-pet-town-elapsed",
                (connection, transaction) =>
                {
                    committedUpdate = PetCreatureSatietyService.ApplyTownElapsed(
                        lease.Inventory,
                        startUtc,
                        endUtc);
                    return true;
                });
            if (!committed)
                return false;

            update = committedUpdate;
            return true;
        }

        internal static bool TryCommitDungeonDeath(
            InventoryLease lease,
            DateTime startUtc,
            DateTime endUtc,
            out PetCreatureSatietyUpdate update)
        {
            update = PetCreatureSatietyUpdate.Noop(
                lease?.CharacterId ?? 0);
            if (lease?.Inventory == null || startUtc == DateTime.MinValue)
                return false;

            if (!TryPreview(
                    lease,
                    inventory => PetCreatureSatietyService.PreviewDungeonDeath(
                        inventory,
                        startUtc,
                        endUtc),
                    out update))
                return false;
            if (!update.StateChanged)
                return true;

            var committedUpdate = update;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "check-pet-dungeon-death",
                (connection, transaction) =>
                {
                    committedUpdate = PetCreatureSatietyService
                        .ApplyDungeonDeathIfExpired(
                            lease.Inventory,
                            startUtc,
                            endUtc);
                    return true;
                });
            if (!committed)
                return false;

            update = committedUpdate;
            return true;
        }

        internal static bool TryCommitRevival(
            InventoryLease lease,
            out PetCreatureRevivalUpdate update)
        {
            update = PetCreatureRevivalUpdate.Noop(
                lease?.CharacterId ?? 0);
            if (lease?.Inventory == null)
                return false;

            if (!TryPreview(
                    lease,
                    PetCreatureSatietyService.PreviewRevival,
                    out update))
                return false;
            if (!update.Revived)
                return true;

            var committedUpdate = update;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "revive-pet-creature",
                (connection, transaction) =>
                {
                    committedUpdate = PetCreatureSatietyService
                        .ReviveEquippedCreatureIfDead(lease.Inventory);
                    return true;
                });
            if (!committed)
                return false;

            update = committedUpdate;
            return true;
        }

        private static bool TryPreview<T>(
            InventoryLease lease,
            Func<InventoryService, T> preview,
            out T result)
        {
            result = default(T);
            if (lease?.Inventory == null
                || preview == null
                || !InventoryContext.IsCurrentLease(
                    lease,
                    lease.SessionId,
                    lease.CharacterId))
            {
                return false;
            }

            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        lease.SessionId,
                        lease.CharacterId))
                {
                    return false;
                }

                result = preview(lease.Inventory);
                return true;
            }
        }
    }
}
