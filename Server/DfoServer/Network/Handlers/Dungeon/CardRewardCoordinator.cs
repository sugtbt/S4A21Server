using System;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class CardRewardCoordinator
    {
        private readonly CardRewardService _application;
        private readonly ICardRewardNotificationSender _sender;
        private readonly IGameDatabase _database;

        internal CardRewardCoordinator(
            CardRewardService application = null,
            ICardRewardNotificationSender sender = null,
            IGameDatabase database = null)
        {
            _application = application ?? new CardRewardService();
            _sender = sender ?? new CardRewardNotificationSender();
            _database = database;
        }

        internal void ScheduleAutoFlow(
            EnhancedClientSession session,
            int layoutDelayMs,
            int autoFlipDelayMs)
        {
            var run = session.Player.CurrentRun;
            if (run == null)
                return;
            layoutDelayMs = Math.Max(0, layoutDelayMs);
            autoFlipDelayMs = Math.Max(0, autoFlipDelayMs);
            lock (run.SyncRoot)
                run.Settlement.CardAutoFlipDelayMs = autoFlipDelayMs;
            var identity = run.CaptureIdentity();
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(layoutDelayMs);
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.SettlementCardAutoFlow,
                deadlineUtc,
                RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleLayoutTimer(
                session,
                run,
                identity,
                deadlineUtc,
                ticket,
                autoFlipDelayMs,
                "Settlement");
        }

        internal void StartDelayedAutoFlip(
            EnhancedClientSession session,
            int delayMs)
        {
            var run = session.Player.CurrentRun;
            if (run == null)
                return;
            delayMs = Math.Max(0, delayMs);
            lock (run.SyncRoot)
                run.Settlement.CardAutoFlipDelayMs = delayMs;
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.SettlementCardAutoFlow,
                deadlineUtc,
                RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleAutoFlipTimer(
                session,
                run,
                run.CaptureIdentity(),
                deadlineUtc,
                ticket,
                "Standalone");
        }

        internal bool RecoverTimer(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return false;

            var settlementState = run.SettlementState;
            if (run.CardRewards == null
                || settlementState == DungeonSettlementState.Completed
                || (settlementState == DungeonSettlementState.CardsRevealed
                    && (run.FreeCardRewardDelivered
                        || CardRewardRules.IsCommitted(
                            run,
                            CardRewardSide.Free))))
            {
                run.Timers.Cancel(
                    DungeonRunTimerKeys.SettlementCardAutoFlow);
                return false;
            }

            if (!run.Timers.TryResume(
                    DungeonRunTimerKeys.SettlementCardAutoFlow,
                    out var ticket,
                    out var deadlineUtc))
            {
                return false;
            }

            var identity = run.CaptureIdentity();
            if (settlementState == DungeonSettlementState.ResultShown)
            {
                int autoFlipDelayMs;
                lock (run.SyncRoot)
                    autoFlipDelayMs = run.Settlement.CardAutoFlipDelayMs;
                ScheduleLayoutTimer(
                    session,
                    run,
                    identity,
                    deadlineUtc,
                    ticket,
                    Math.Max(0, autoFlipDelayMs),
                    "Rejoin");
                return true;
            }

            if (settlementState == DungeonSettlementState.CardsRevealed)
            {
                ScheduleAutoFlipTimer(
                    session,
                    run,
                    identity,
                    deadlineUtc,
                    ticket,
                    "Rejoin");
                return true;
            }

            run.Timers.Cancel(DungeonRunTimerKeys.SettlementCardAutoFlow);
            return false;
        }

        internal async Task HandleSelectCard(
            EnhancedClientSession session,
            byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null || body == null || body.Length < 2)
                return;
            var runIdentity = run.CaptureIdentity();
            var requestedLayout = run.SettlementState
                == DungeonSettlementState.ResultShown;
            var cardType = body[0];
            var cardIndex = body[1];

            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!session.Player.IsCurrentDungeonRun(runIdentity))
                    return;

                if (requestedLayout)
                {
                    if (run.SettlementState
                        != DungeonSettlementState.ResultShown)
                        return;
                    DungeonRunLifecycle.CancelAutoFlip(session);
                    await _sender.SendLayoutAsync(session);
                    if (!session.Player.IsCurrentDungeonRun(runIdentity)
                        || !run.TryMarkCardsRevealed())
                    {
                        return;
                    }
                    StartDelayedAutoFlip(session, 4000);
                    return;
                }

                if (run.SettlementState
                        != DungeonSettlementState.CardsRevealed
                    || cardType > 1
                    || cardIndex > 3)
                {
                    return;
                }
                if (cardType == 0)
                    DungeonRunLifecycle.CancelAutoFlip(session);

                if (cardType == 1
                    && cardIndex == 0
                    && (!TryGetOwnedInventory(session, out var paymentLease)
                        || !_application.CanPayPaidCard(paymentLease, run)))
                {
                    await _sender.SendCardInfoAsync(session, run);
                    return;
                }

                if (!CardRewardRules.TrySelectCardSlot(run, cardType, cardIndex))
                    return;

                var side = cardType == 0
                    ? CardRewardSide.Free
                    : CardRewardSide.Paid;
                try
                {
                    await _sender.SendCardInfoAsync(session, run);
                }
                catch (Exception ex)
                {
                    RestoreSelectionAfterNotificationFailure(
                        session,
                        run,
                        runIdentity,
                        side,
                        cardIndex);
                    FileLogger.Log(
                        $"[CardRewardCoordinator] card-info projection failed: " +
                        $"cid={session.Player.CharacterId} side={side} " +
                        $"slot={cardIndex} error={ex.Message}");
                    return;
                }
                if (!session.Player.IsCurrentDungeonRun(runIdentity))
                {
                    CardRewardRules.ClearSelectedSlot(
                        run,
                        side,
                        cardIndex);
                    return;
                }
                if (cardIndex == 0)
                {
                    await DeliverCardRewards(
                        session,
                        run,
                        side);
                }
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }
        }

        internal async Task HandleCardStartRequest(
            EnhancedClientSession session)
        {
            var run = session.Player.CurrentRun;
            if (run == null
                || run.SettlementState
                    != DungeonSettlementState.ResultShown)
                return;
            var identity = run.CaptureIdentity();
            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!session.Player.IsCurrentDungeonRun(identity)
                    || run.SettlementState
                        != DungeonSettlementState.ResultShown)
                {
                    return;
                }
                DungeonRunLifecycle.CancelAutoFlip(session);
                await _sender.SendLayoutAsync(session);
                if (!session.Player.IsCurrentDungeonRun(identity)
                    || !run.TryMarkCardsRevealed())
                {
                    return;
                }
                StartDelayedAutoFlip(session, 4000);
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }
        }

        internal async Task<bool> HandleEplpCommand(
            EnhancedClientSession session,
            byte[] body)
        {
            if (body == null || body.Length < 2)
                return false;
            var state = body[0];
            var option = body[1];
            var run = session.Player.CurrentRun;
            var identity = run?.CaptureIdentity() ?? default;
            if (run == null)
            {
                DungeonRunLifecycle.CancelAutoFlip(session);
                await _sender.SendExitAsync(session, state, option);
                return state == 1;
            }

            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return false;
                if (run.SettlementState
                        == DungeonSettlementState.ResultShown
                    && run.CardRewards != null)
                {
                    DungeonRunLifecycle.CancelAutoFlip(session);
                    await _sender.SendLayoutAsync(session);
                    if (!session.Player.IsCurrentDungeonRun(identity)
                        || !run.TryMarkCardsRevealed())
                    {
                        return false;
                    }
                    StartDelayedAutoFlip(session, 4000);
                    return false;
                }

                DungeonRunLifecycle.CancelAutoFlip(session);
                await _sender.SendExitAsync(session, state, option);
                return state == 1;
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }
        }

        private async Task AutoFlipFreeCard(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var identity = run?.CaptureIdentity() ?? default;
            if (!session.Player.IsCurrentDungeonRun(identity)
                || !CardRewardRules.TrySelectCardSlot(run, 0, 0))
            {
                return;
            }
            if (!session.Player.IsCurrentDungeonRun(identity))
            {
                CardRewardRules.ClearSelectedSlot(
                    run,
                    CardRewardSide.Free,
                    cardIndex: 0);
                return;
            }
            try
            {
                await _sender.SendCardInfoAsync(session, run);
            }
            catch (Exception ex)
            {
                RestoreSelectionAfterNotificationFailure(
                    session,
                    run,
                    identity,
                    CardRewardSide.Free,
                    cardIndex: 0);
                FileLogger.Log(
                    $"[CardRewardCoordinator] auto card-info projection failed: " +
                    $"cid={session.Player.CharacterId} error={ex.Message}");
                return;
            }
            if (session.Player.IsCurrentDungeonRun(identity))
                await DeliverCardRewards(session, run, CardRewardSide.Free);
            else
            {
                CardRewardRules.ClearSelectedSlot(
                    run,
                    CardRewardSide.Free,
                    cardIndex: 0);
            }
        }

        private async Task DeliverCardRewards(
            EnhancedClientSession session,
            DungeonRun run,
            CardRewardSide side)
        {
            var identity = run.CaptureIdentity();
            if (!TryGetOwnedInventory(session, out var lease))
            {
                RestoreDeliveryAfterFailure(
                    session,
                    run,
                    identity,
                    side);
                FileLogger.Log(
                    $"[CardRewardCoordinator] online inventory missing " +
                    $"cid={session.Player.CharacterId} side={side}");
                return;
            }
            var result = _application.Deliver(
                session.Player.CharacterId,
                lease,
                run,
                side);
            if (result.Committed
                && session.Player.IsCurrentDungeonRun(identity))
            {
                try
                {
                    await _sender.SendItemUpdatesAsync(
                        session,
                        result.Changes);
                    if (result.ConsumedGoldCardContractUse)
                    {
                        var database = _database ?? lease.Inventory.Database;
                        await PremiumService.SendPremiumServiceRefresh(
                            session,
                            session.Account?.AccountId ?? 0,
                            database);
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[CardRewardCoordinator] item update projection failed " +
                        $"after commit: cid={session.Player.CharacterId} " +
                        $"side={side} error={ex.Message}");
                }
            }
            else if (!result.Committed
                && session.Player.IsCurrentDungeonRun(identity))
            {
                RestoreDeliveryAfterFailure(
                    session,
                    run,
                    identity,
                    side);
                await _sender.SendCardInfoAsync(session, run);
            }
        }

        private void RestoreDeliveryAfterFailure(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            CardRewardSide side)
        {
            CardRewardRules.ClearSelectedSlot(run, side);
            if (side == CardRewardSide.Free
                && session.Player.IsCurrentDungeonRun(identity)
                && run.SettlementState
                    == DungeonSettlementState.CardsRevealed)
            {
                StartDelayedAutoFlip(session, delayMs: 4000);
            }
        }

        private void RestoreSelectionAfterNotificationFailure(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            CardRewardSide side,
            int cardIndex)
        {
            CardRewardRules.ClearSelectedSlot(
                run,
                side,
                cardIndex);
            if (side == CardRewardSide.Free
                && session.Player.IsCurrentDungeonRun(identity)
                && run.SettlementState
                    == DungeonSettlementState.CardsRevealed)
            {
                StartDelayedAutoFlip(session, delayMs: 4000);
            }
        }

        private void ScheduleLayoutTimer(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            DateTime deadlineUtc,
            RunTimerTicket ticket,
            int autoFlipDelayMs,
            string source)
        {
            if (!IsAutoFlipTimerCurrent(session, run, identity, ticket))
                return;
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                BuildAutoFlipTimerName(session, run, ticket),
                deadlineUtc,
                async _ => await OnLayoutTimerElapsedAsync(
                    session,
                    run,
                    identity,
                    ticket,
                    autoFlipDelayMs,
                    source));
            run.Timers.Attach(ticket, handle);
        }

        private void ScheduleAutoFlipTimer(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            DateTime deadlineUtc,
            RunTimerTicket ticket,
            string source)
        {
            if (!IsAutoFlipTimerCurrent(session, run, identity, ticket))
                return;
            var handle = ClockService.Instance.ScheduleOneShotAsync(
                BuildAutoFlipTimerName(session, run, ticket),
                deadlineUtc,
                async _ => await OnAutoFlipTimerElapsedAsync(
                    session,
                    run,
                    identity,
                    ticket,
                    source));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnLayoutTimerElapsedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket,
            int autoFlipDelayMs,
            string source)
        {
            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!IsAutoFlipTimerCurrent(session, run, identity, ticket))
                {
                    return;
                }
                if (run.SettlementState
                    != DungeonSettlementState.ResultShown)
                {
                    run.Timers.TryComplete(ticket);
                    return;
                }
                FileLogger.Log(
                    $"[CardRewardCoordinator] {source} auto-layout timer fired");
                await _sender.SendLayoutAsync(session);
                if (!IsAutoFlipTimerCurrent(session, run, identity, ticket)
                    || !run.TryMarkCardsRevealed())
                {
                    return;
                }
                var deadlineUtc = DateTime.UtcNow.AddMilliseconds(
                    Math.Max(0, autoFlipDelayMs));
                var nextTicket = run.Timers.Begin(
                    DungeonRunTimerKeys.SettlementCardAutoFlow,
                    deadlineUtc,
                    RunTimerDetachPolicy.SuspendUntilResume);
                ScheduleAutoFlipTimer(
                    session,
                    run,
                    identity,
                    deadlineUtc,
                    nextTicket,
                    "Auto-flow");
            }
            finally
            {
                run.Settlement.CardProjectionGate.Release();
            }
        }

        private async Task OnAutoFlipTimerElapsedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket,
            string source)
        {
            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!IsAutoFlipTimerCurrent(session, run, identity, ticket))
                {
                    return;
                }
                if (run.SettlementState
                    != DungeonSettlementState.CardsRevealed)
                {
                    run.Timers.TryComplete(ticket);
                    return;
                }
                FileLogger.Log(
                    $"[CardRewardCoordinator] {source} auto-flip timer fired");
                await AutoFlipFreeCard(session, run);
            }
            finally
            {
                run.Timers.TryComplete(ticket);
                run.Settlement.CardProjectionGate.Release();
            }
        }

        private static bool IsAutoFlipTimerCurrent(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket)
            => session?.Player != null
               && session.Player.IsCurrentDungeonRun(identity)
               && run.Matches(identity)
               && run.Timers.IsCurrent(ticket);

        private static bool TryGetOwnedInventory(
            EnhancedClientSession session,
            out InventoryLease lease)
        {
            lease = null;
            var characterId = session?.Player?.CharacterId ?? 0;
            return characterId > 0
                && InventoryContext.TryGetLease(characterId, out lease)
                && lease.IsOwnedBy(session.SessionId);
        }

        private static string BuildAutoFlipTimerName(
            EnhancedClientSession session,
            DungeonRun run,
            RunTimerTicket ticket)
            => "dungeon-card:" + session.SessionId.ToString("N")
               + ":" + run.RunId
               + ":" + ticket.Generation;
    }
}
