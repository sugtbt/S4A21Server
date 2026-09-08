using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal enum CardRewardSide
    {
        Free,
        Paid,
    }

    internal static class CardRewardRules
    {
        internal static bool TrySelectCardSlot(
            DungeonRun run,
            byte cardType,
            byte cardIndex)
        {
            if (run == null || cardType > 1 || cardIndex > 3)
                return false;
            lock (run.SyncRoot)
            {
                if (run.SettlementState
                        != DungeonSettlementState.CardsRevealed
                    || run.CardRewards == null)
                    return false;
                if (cardType == 1 && !HasPaidCardReward(run.CardRewards))
                    return false;
                var slots = cardType == 0
                    ? run.FreeCardSlots
                    : run.PaidCardSlots;
                if (slots == null
                    || cardIndex >= slots.Length
                    || slots[cardIndex] != 0xFF
                    || Array.Exists(slots, value => value != 0xFF))
                {
                    return false;
                }
                slots[cardIndex] = 0x00;
                run.CardFlipCount++;
                return true;
            }
        }

        internal static bool TryReserveDelivery(
            DungeonRun run,
            CardRewardSide side,
            out List<ClearRewardGenerator.CardReward> cards,
            out DungeonEffectReservation reservation)
        {
            cards = null;
            reservation = default;
            if (run == null)
                return false;
            lock (run.SyncRoot)
            {
                cards = run.CardRewards;
                if (cards == null
                    || (side == CardRewardSide.Paid
                        && !HasPaidCardReward(cards)))
                {
                    return false;
                }
            }
            return run.Effects.TryReserve(GetEffectId(run, side), out reservation);
        }

        internal static DungeonEffectId GetEffectId(
            DungeonRun run,
            CardRewardSide side)
            => new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                side == CardRewardSide.Free
                    ? "card-reward-free"
                    : "card-reward-paid",
                DungeonEffectScope.Player,
                run.RunId);

        internal static bool IsCommitted(
            DungeonRun run,
            CardRewardSide side)
            => run != null
               && run.Effects.GetState(GetEffectId(run, side))
               == DungeonEffectState.Committed;

        internal static void ProjectDelivery(
            DungeonRun run,
            CardRewardSide side)
        {
            lock (run.SyncRoot)
            {
                if (side == CardRewardSide.Free)
                    run.FreeCardRewardDelivered = true;
                else
                    run.PaidCardRewardDelivered = true;
            }
        }

        internal static void ClearSelectedSlot(
            DungeonRun run,
            CardRewardSide side)
        {
            if (run == null)
                return;
            lock (run.SyncRoot)
            {
                var slots = side == CardRewardSide.Free
                    ? run.FreeCardSlots
                    : run.PaidCardSlots;
                if (slots == null)
                    return;
                for (var index = 0; index < slots.Length; index++)
                {
                    if (slots[index] == 0xFF)
                        continue;
                    slots[index] = 0xFF;
                    if (run.CardFlipCount > 0)
                        run.CardFlipCount--;
                    return;
                }
            }
        }

        internal static void ClearSelectedSlot(
            DungeonRun run,
            CardRewardSide side,
            int cardIndex)
        {
            lock (run.SyncRoot)
            {
                var slots = side == CardRewardSide.Free
                    ? run.FreeCardSlots
                    : run.PaidCardSlots;
                if (slots == null
                    || cardIndex < 0
                    || cardIndex >= slots.Length
                    || slots[cardIndex] == 0xFF)
                {
                    return;
                }
                slots[cardIndex] = 0xFF;
                if (run.CardFlipCount > 0)
                    run.CardFlipCount--;
            }
        }

        internal static bool HasPaidCardReward(
            IReadOnlyList<ClearRewardGenerator.CardReward> cards)
            => cards != null
               && cards.Count > 5
               && !cards[5].IsGold
               && cards[5].ItemId > 0
               && cards[5].StackCount > 0;

        internal static void CompleteSettlementIfFinished(DungeonRun run)
        {
            if (run == null)
                return;
            var freeDone = IsCommitted(run, CardRewardSide.Free);
            var paidDone = IsCommitted(run, CardRewardSide.Paid);
            var finished = false;
            lock (run.SyncRoot)
            {
                var cards = run.CardRewards;
                if (cards == null)
                    return;
                var hasPaid = HasPaidCardReward(cards);
                run.FreeCardRewardDelivered = freeDone;
                run.PaidCardRewardDelivered = paidDone;
                finished = freeDone && (!hasPaid || paidDone);
                if (finished)
                    run.CardRewards = null;
            }
            if (finished)
                run.TryCompleteSettlement();
        }

        internal static int GetPaidGoldCost(DungeonRun run)
        {
            if (run == null)
                return 0;
            lock (run.SyncRoot)
                return Math.Max(0, run.PaidCardCost);
        }
    }
}
