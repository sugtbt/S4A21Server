using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Dungeon
{
    internal sealed class CardRewardDeliveryResult
    {
        internal static CardRewardDeliveryResult NotCommitted { get; } =
            new CardRewardDeliveryResult(
                false,
                Array.Empty<InventorySlotMutation>());

        internal CardRewardDeliveryResult(
            bool committed,
            IReadOnlyList<InventorySlotMutation> changes,
            bool consumedGoldCardContractUse = false)
        {
            Committed = committed;
            Changes = changes ?? Array.Empty<InventorySlotMutation>();
            ConsumedGoldCardContractUse = consumedGoldCardContractUse;
        }

        internal bool Committed { get; }
        internal IReadOnlyList<InventorySlotMutation> Changes { get; }
        internal bool ConsumedGoldCardContractUse { get; }
    }

    // Card reward application service. Durable inventory mutation and effect
    // result are committed by DungeonPersistentEffectApplicationService in the
    // same SQLite transaction; this class only owns the run-local projection.
    internal sealed class CardRewardService
    {
        private readonly DungeonPersistentEffectApplicationService
            _persistentEffects;
        private readonly Action _afterDurableCommit;

        internal CardRewardService(
            DungeonPersistentEffectApplicationService persistentEffects = null,
            Action afterDurableCommit = null)
        {
            _persistentEffects = persistentEffects;
            _afterDurableCommit = afterDurableCommit;
        }

        internal bool CanPayPaidCard(InventoryLease lease, DungeonRun run)
        {
            var cost = CardRewardRules.GetPaidGoldCost(run);
            if (cost <= 0)
                return true;
            if (lease == null)
                return false;
            lock (lease.SyncRoot)
                return lease.Inventory.CountMainItem(0) >= cost;
        }

        internal CardRewardDeliveryResult Deliver(
            int characterId,
            InventoryLease lease,
            DungeonRun run,
            CardRewardSide side)
        {
            if (characterId <= 0
                || lease == null
                || lease.CharacterId != characterId
                || run == null)
            {
                return CardRewardDeliveryResult.NotCommitted;
            }
            if (!InventoryContext.IsCurrentLease(
                    lease,
                    lease.SessionId,
                    characterId))
            {
                return CardRewardDeliveryResult.NotCommitted;
            }
            if (!CardRewardRules.TryReserveDelivery(
                    run,
                    side,
                    out var cards,
                    out var reservation))
            {
                return CardRewardDeliveryResult.NotCommitted;
            }

            var durableCommitted = false;
            CardRewardPersistentCommitResult durableResult = null;
            try
            {
                var persistentEffects = ResolvePersistentEffects(lease);
                if (!persistentEffects.TryApplyCardReward(
                        reservation.EffectId,
                        lease,
                        lease.SessionId,
                        side,
                        CardRewardRules.GetPaidGoldCost(run),
                        side == CardRewardSide.Paid
                            && run.PaidCardUsesDevilContract,
                        cards,
                        out durableResult,
                        out var error))
                {
                    FileLogger.Log(
                        $"[CardRewardService] {side} durable commit failed: " +
                        (error ?? "unknown"));
                    return FailDelivery(run, side, reservation);
                }

                durableCommitted = true;
                _afterDurableCommit?.Invoke();
                ProjectDurableCommit(run, side, reservation);
                FileLogger.Log(
                    $"[CardRewardService] {side} rewards committed: " +
                    $"{durableResult?.Changes.Count ?? 0} entries");
                return new CardRewardDeliveryResult(
                    true,
                    durableResult?.Changes,
                    durableResult?.ConsumedGoldCardContractUse == true);
            }
            catch (Exception ex)
            {
                if (durableCommitted)
                {
                    ProjectDurableCommit(run, side, reservation);
                    FileLogger.Log(
                        $"[CardRewardService] {side} recovered committed " +
                        $"effect after local checkpoint failure: {ex.Message}");
                    return new CardRewardDeliveryResult(
                        true,
                        durableResult?.Changes,
                        durableResult?.ConsumedGoldCardContractUse == true);
                }

                run.Effects.TryFail(reservation);
                CardRewardRules.ClearSelectedSlot(run, side);
                FileLogger.Log(
                    $"[CardRewardService] {side} delivery failed: {ex.Message}");
                return CardRewardDeliveryResult.NotCommitted;
            }
        }

        private DungeonPersistentEffectApplicationService
            ResolvePersistentEffects(InventoryLease lease)
        {
            if (_persistentEffects != null)
                return _persistentEffects;
            var database = lease?.Inventory?.Database
                ?? throw new InvalidOperationException(
                    "Card reward inventory has no database ownership.");
            return new DungeonPersistentEffectApplicationService(
                database.ConnectionString,
                database: database);
        }

        private static void ProjectDurableCommit(
            DungeonRun run,
            CardRewardSide side,
            DungeonEffectReservation reservation)
        {
            if (!run.Effects.TryCommit(reservation))
                run.Effects.ProjectCommitted(reservation.EffectId);
            CardRewardRules.ProjectDelivery(run, side);
            CardRewardRules.CompleteSettlementIfFinished(run);
        }

        private static CardRewardDeliveryResult FailDelivery(
            DungeonRun run,
            CardRewardSide side,
            DungeonEffectReservation reservation)
        {
            run.Effects.TryFail(reservation);
            CardRewardRules.ClearSelectedSlot(run, side);
            return CardRewardDeliveryResult.NotCommitted;
        }
    }

    internal sealed class CardRewardInventoryMutationSnapshot
    {
        private readonly Dictionary<(InventoryListType, short), ItemCore>
            _items = new Dictionary<(InventoryListType, short), ItemCore>();
        private readonly Dictionary<short, int> _virtualCounts =
            new Dictionary<short, int>();

        internal static CardRewardInventoryMutationSnapshot Capture(
            InventoryService inventory,
            InventoryRewardGrantBatchPlan plan,
            bool includeGold)
        {
            var snapshot = new CardRewardInventoryMutationSnapshot();
            if (includeGold)
            {
                snapshot.CaptureVirtual(
                    inventory,
                    InventoryService.MainVirtualCurrencySlotStart);
            }

            foreach (var entry in plan.Entries)
            {
                if (entry.Kind == InventoryRewardGrantKind.MainVirtualCount)
                {
                    snapshot.CaptureVirtual(inventory, entry.SlotIndex);
                    continue;
                }
                if (entry.Kind != InventoryRewardGrantKind.InventoryItem)
                    continue;

                var key = (entry.ListType, entry.SlotIndex);
                if (!snapshot._items.ContainsKey(key))
                {
                    snapshot._items[key] = inventory.TryGetItem(
                        entry.ListType,
                        entry.SlotIndex,
                        out var item)
                        ? item.Copy()
                        : null;
                }
            }
            return snapshot;
        }

        internal void Restore(
            InventoryService inventory,
            InventoryRewardGrantBatchPlan plan)
        {
            if (inventory == null)
                return;

            if (plan != null)
            {
                foreach (var entry in plan.Entries)
                {
                    if (entry.Kind == InventoryRewardGrantKind.InventoryItem
                        && entry.CreateResult != null)
                    {
                        InventoryCreateService.DetachCreatedDetails(
                            inventory,
                            entry.CreateResult);
                    }
                }
            }

            foreach (var pair in _items)
            {
                if (pair.Value == null)
                    inventory.RemoveItem(pair.Key.Item1, pair.Key.Item2);
                else
                {
                    inventory.SetItem(
                        pair.Key.Item1,
                        pair.Key.Item2,
                        pair.Value.Copy());
                }
            }
            foreach (var pair in _virtualCounts)
                inventory.SetMainVirtualCount(pair.Key, pair.Value);
        }

        private void CaptureVirtual(
            InventoryService inventory,
            short slotIndex)
        {
            if (_virtualCounts.ContainsKey(slotIndex))
                return;
            _virtualCounts[slotIndex] =
                inventory.GetMainVirtualCount(slotIndex)?.Count ?? 0;
        }
    }
}
