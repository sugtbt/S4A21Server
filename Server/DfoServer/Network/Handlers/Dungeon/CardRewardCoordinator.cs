using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal readonly struct CardRewardEplpDecision
    {
        internal CardRewardEplpDecision(byte state, byte option)
        {
            State = state;
            Option = option;
            Ready = true;
        }

        internal bool Ready { get; }
        internal byte State { get; }
        internal byte Option { get; }
        internal bool IsCommitted => Ready && State == 1;
    }

    internal sealed class CardRewardCoordinator
    {
        private readonly CardRewardService _application;
        private readonly ICardRewardNotificationSender _sender;
        private readonly ISessionDirectory _sessions;
        private readonly IGameDatabase _database;

        internal CardRewardCoordinator(
            CardRewardService application = null,
            ICardRewardNotificationSender sender = null,
            ISessionDirectory sessions = null,
            IGameDatabase database = null)
        {
            _application = application ?? new CardRewardService();
            _sender = sender ?? new CardRewardNotificationSender();
            _sessions = sessions;
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
                    await SendPartyLayoutAsync(session, run);
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
                if (cardType == 1
                    && (!TryGetOwnedInventory(session, out var paymentLease)
                        || !_application.CanPayPaidCard(paymentLease, run)))
                {
                    await SendPartyCardInfoAsync(session, run);
                    return;
                }

                var side = cardType == 0
                    ? CardRewardSide.Free
                    : CardRewardSide.Paid;
                try
                {
                    var selectedCardIndex =
                        await TrySelectAvailableCardAndProjectAsync(
                            session,
                            run,
                            side,
                            requestedCardIndex: cardIndex);
                    if (selectedCardIndex == 0xFF)
                    {
                        return;
                    }
                    if (side == CardRewardSide.Free)
                        DungeonRunLifecycle.CancelAutoFlip(session);
                }
                catch (Exception ex)
                {
                    if (side == CardRewardSide.Free
                        && session.Player.IsCurrentDungeonRun(runIdentity))
                    {
                        StartDelayedAutoFlip(session, delayMs: 4000);
                    }
                    FileLogger.Log(
                        $"[CardRewardCoordinator] card-info projection failed: " +
                        $"cid={session.Player.CharacterId} side={side} " +
                        $"slot={cardIndex} error={ex.Message}");
                    return;
                }
                if (!session.Player.IsCurrentDungeonRun(runIdentity))
                {
                    await ClearSelectedCardAsync(
                        run,
                        side,
                        cardIndex);
                    return;
                }
                await DeliverCardRewards(session, run, side);
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
                await SendPartyLayoutAsync(session, run);
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

        internal async Task<CardRewardEplpDecision> PrepareEplpCommand(
            EnhancedClientSession session,
            byte[] body)
        {
            if (body == null || body.Length < 2)
                return default;
            var state = body[0];
            var option = body[1];
            var run = session.Player.CurrentRun;
            var identity = run?.CaptureIdentity() ?? default;
            if (run == null)
            {
                DungeonRunLifecycle.CancelAutoFlip(session);
                return new CardRewardEplpDecision(state, option);
            }

            await run.Settlement.CardProjectionGate.WaitAsync();
            try
            {
                if (!session.Player.IsCurrentDungeonRun(identity))
                    return default;
                if (run.SettlementState
                        == DungeonSettlementState.ResultShown
                    && run.CardRewards != null)
                {
                    DungeonRunLifecycle.CancelAutoFlip(session);
                    await SendPartyLayoutAsync(session, run);
                    if (!session.Player.IsCurrentDungeonRun(identity)
                        || !run.TryMarkCardsRevealed())
                    {
                        return default;
                    }
                    StartDelayedAutoFlip(session, 4000);
                    return default;
                }

                DungeonRunLifecycle.CancelAutoFlip(session);
                return new CardRewardEplpDecision(state, option);
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
            if (!session.Player.IsCurrentDungeonRun(identity))
            {
                return;
            }
            byte autoCardIndex;
            try
            {
                autoCardIndex = await TrySelectAvailableCardAndProjectAsync(
                    session,
                    run,
                    CardRewardSide.Free,
                    requestedCardIndex: null);
            }
            catch (Exception ex)
            {
                if (session.Player.IsCurrentDungeonRun(identity))
                    StartDelayedAutoFlip(session, delayMs: 4000);
                FileLogger.Log(
                    $"[CardRewardCoordinator] auto card-info projection failed: " +
                    $"cid={session.Player.CharacterId} error={ex.Message}");
                return;
            }
            if (autoCardIndex == 0xFF)
                return;
            if (session.Player.IsCurrentDungeonRun(identity))
                await DeliverCardRewards(session, run, CardRewardSide.Free);
            else
            {
                await ClearSelectedCardAsync(
                    run,
                    CardRewardSide.Free,
                    autoCardIndex);
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
                await RestoreDeliveryAfterFailureAsync(
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
                await RestoreDeliveryAfterFailureAsync(
                    session,
                    run,
                    identity,
                    side);
                await SendPartyCardInfoAsync(session, run);
            }
        }

        private async Task RestoreDeliveryAfterFailureAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            CardRewardSide side)
        {
            await ClearSelectedCardAsync(run, side);
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
                await SendPartyLayoutAsync(session, run);
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

        internal Task SendExitAsync(
            EnhancedClientSession session,
            byte state,
            byte option)
            => _sender.SendExitAsync(session, state, option);

        private Task SendPartyLayoutAsync(
            EnhancedClientSession session,
            DungeonRun run)
            => SendPartyProjectionAsync(session, run, layout: true);

        private Task SendPartyCardInfoAsync(
            EnhancedClientSession session,
            DungeonRun run)
            => SendPartyProjectionAsync(session, run, layout: false);

        private async Task<byte> TrySelectAvailableCardAndProjectAsync(
            EnhancedClientSession owner,
            DungeonRun run,
            CardRewardSide side,
            byte? requestedCardIndex)
        {
            await run.Instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                var roster = CaptureCardRewardRoster(run);
                var first = requestedCardIndex ?? (byte)0;
                var last = requestedCardIndex ?? (byte)3;
                for (var cardIndex = first;
                     cardIndex <= last;
                     cardIndex++)
                {
                    if (IsCardPositionOccupied(
                            run,
                            roster,
                            side,
                            cardIndex))
                    {
                        continue;
                    }
                    if (!CardRewardRules.TrySelectCardSlot(
                            run,
                            side == CardRewardSide.Free
                                ? (byte)0
                                : (byte)1,
                            cardIndex))
                    {
                        continue;
                    }

                    try
                    {
                        var projection = BuildPartyProjection(run, roster);
                        await SendCardInfoProjectionLockedAsync(
                            owner,
                            roster,
                            projection);
                        return cardIndex;
                    }
                    catch
                    {
                        CardRewardRules.ClearSelectedSlot(
                            run,
                            side,
                            cardIndex);
                        throw;
                    }
                }
                return 0xFF;
            }
            finally
            {
                run.Instance.CardRewardProjectionGate.Release();
            }
        }

        private async Task ClearSelectedCardAsync(
            DungeonRun run,
            CardRewardSide side,
            int cardIndex)
        {
            if (run?.Instance == null)
                return;
            await run.Instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                CardRewardRules.ClearSelectedSlot(run, side, cardIndex);
            }
            finally
            {
                run.Instance.CardRewardProjectionGate.Release();
            }
        }

        private async Task ClearSelectedCardAsync(
            DungeonRun run,
            CardRewardSide side)
        {
            if (run?.Instance == null)
                return;
            await run.Instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                CardRewardRules.ClearSelectedSlot(run, side);
            }
            finally
            {
                run.Instance.CardRewardProjectionGate.Release();
            }
        }

        private static IReadOnlyList<DungeonParticipantRosterEntry>
            CaptureCardRewardRoster(DungeonRun run)
            => run.Instance.ParticipantEffects.GetRoster(
                run.GetSettlementSourceEventId(),
                DungeonParticipantEffectAudience.Instance);

        internal static bool IsCardPositionOccupied(
            DungeonRun sourceRun,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            CardRewardSide side,
            int cardIndex)
        {
            if (cardIndex < 0
                || cardIndex >= Game.Party.PartyConstants.MaxMembers)
            {
                return true;
            }

            if (roster == null || roster.Count == 0)
                return IsRunCardPositionOccupied(sourceRun, side, cardIndex);
            foreach (var participant in roster)
            {
                if (IsRunCardPositionOccupied(
                        participant?.Run,
                        side,
                        cardIndex))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsRunCardPositionOccupied(
            DungeonRun run,
            CardRewardSide side,
            int cardIndex)
        {
            if (run == null)
                return false;
            lock (run.SyncRoot)
            {
                var slots = side == CardRewardSide.Free
                    ? run.FreeCardSlots
                    : run.PaidCardSlots;
                return slots == null
                    || cardIndex >= slots.Length
                    || slots[cardIndex] != 0xFF;
            }
        }

        private async Task SendPartyProjectionAsync(
            EnhancedClientSession owner,
            DungeonRun run,
            bool layout)
        {
            if (owner?.Player == null || run?.Instance == null)
                return;

            await run.Instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                var roster = CaptureCardRewardRoster(run);
                var projection = BuildPartyProjection(run, roster);
                if (layout)
                    await _sender.SendLayoutAsync(owner, projection);
                else
                    await SendCardInfoProjectionLockedAsync(
                        owner,
                        roster,
                        projection);
            }
            finally
            {
                run.Instance.CardRewardProjectionGate.Release();
            }
        }

        private async Task SendCardInfoProjectionLockedAsync(
            EnhancedClientSession owner,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            CardRewardPartyProjection projection)
        {
            await _sender.SendCardInfoAsync(owner, projection);
            if (_sessions == null || roster == null)
                return;
            foreach (var participant in roster)
            {
                if (participant.CharacterId == owner.Player.CharacterId
                    || !_sessions.TryGet(
                        participant.CharacterId,
                        out var peer)
                    || peer?.Player == null
                    || peer.ListenerPort != owner.ListenerPort
                    || peer.TcpClient == null
                    || !peer.TcpClient.Connected
                    || !peer.Player.IsCurrentDungeonRun(
                        participant.RunIdentity))
                {
                    continue;
                }

                try
                {
                    await _sender.SendCardInfoAsync(peer, projection);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[CardRewardCoordinator] peer projection failed: " +
                        $"owner={owner.Player.CharacterId} " +
                        $"peer={participant.CharacterId} " +
                        $"error={ex.Message}");
                }
            }
        }

        internal static CardRewardPartyProjection BuildPartyProjection(
            DungeonRun sourceRun,
            IReadOnlyList<DungeonParticipantRosterEntry> roster)
        {
            if (sourceRun == null)
                throw new ArgumentNullException(nameof(sourceRun));

            var eligibility = new short[
                CardRewardPartyProjection.WireSlotCount];
            var freeSelectors = new byte[eligibility.Length];
            var paidSelectors = new byte[eligibility.Length];
            var paidGold = new int[eligibility.Length];
            var paidItemIds = new int[eligibility.Length];
            var paidItemCounts = new int[eligibility.Length];
            Array.Fill(eligibility, (short)-1);
            Array.Fill(freeSelectors, (byte)0xFF);
            Array.Fill(paidSelectors, (byte)0xFF);

            var assignedPartySlots = new bool[
                Game.Party.PartyConstants.MaxMembers];
            if (roster == null || roster.Count == 0)
            {
                ProjectParticipant(
                    sourceRun,
                    eligibility,
                    freeSelectors,
                    paidSelectors,
                    paidGold,
                    paidItemIds,
                    paidItemCounts,
                    assignedPartySlots);
            }
            else
            {
                foreach (var participant in roster)
                {
                    ProjectParticipant(
                        participant?.Run,
                        eligibility,
                        freeSelectors,
                        paidSelectors,
                        paidGold,
                        paidItemIds,
                        paidItemCounts,
                        assignedPartySlots);
                }
            }

            var slots = new CardRewardPartySlotProjection[eligibility.Length];
            for (var index = 0; index < slots.Length; index++)
            {
                slots[index] = new CardRewardPartySlotProjection(
                    eligibility[index],
                    freeSelectors[index],
                    paidSelectors[index],
                    paidGold[index],
                    paidItemIds[index],
                    paidItemCounts[index]);
            }
            return new CardRewardPartyProjection(slots);
        }

        private static void ProjectParticipant(
            DungeonRun run,
            short[] eligibility,
            byte[] freeSelectors,
            byte[] paidSelectors,
            int[] paidGoldByCard,
            int[] paidItemIdsByCard,
            int[] paidItemCountsByCard,
            bool[] assignedPartySlots)
        {
            if (run == null)
                return;
            var partySlot = run.EntryPartySlotIndex;
            if (partySlot >= assignedPartySlots.Length)
                throw new InvalidOperationException(
                    $"Invalid frozen card party slot {partySlot}.");
            if (assignedPartySlots[partySlot])
                throw new InvalidOperationException(
                    $"Duplicate frozen card party slot {partySlot}.");

            byte selectedFree;
            byte selectedPaid;
            DungeonSettlementRuntime runtime;
            List<ClearRewardGenerator.CardReward> cards;
            lock (run.SyncRoot)
            {
                selectedFree = FindSelectedCardIndex(run.FreeCardSlots);
                selectedPaid = FindSelectedCardIndex(run.PaidCardSlots);
                runtime = run.SettlementRuntime;
                cards = run.CardRewards;
            }

            var paidGold = runtime?.PaidGold.GoldAmount
                ?? (cards != null
                    && cards.Count > 4
                    && cards[4].IsGold
                    ? cards[4].GoldAmount
                    : 0);
            var paidItemId = runtime?.PaidItem.ItemId
                ?? (cards != null
                    && cards.Count > 5
                    && !cards[5].IsGold
                    ? cards[5].ItemId
                    : 0);
            var paidItemCount = runtime?.PaidItem.StackCount
                ?? (cards != null
                    && cards.Count > 5
                    && !cards[5].IsGold
                    ? cards[5].StackCount
                    : 0);
            eligibility[partySlot] = 1;
            assignedPartySlots[partySlot] = true;
            if (selectedFree != 0xFF)
            {
                if (freeSelectors[selectedFree] != 0xFF)
                    throw new InvalidOperationException(
                        $"Duplicate free card selection {selectedFree}.");
                freeSelectors[selectedFree] = partySlot;
            }
            if (selectedPaid != 0xFF)
            {
                if (paidSelectors[selectedPaid] != 0xFF)
                    throw new InvalidOperationException(
                        $"Duplicate paid card selection {selectedPaid}.");
                paidSelectors[selectedPaid] = partySlot;
                paidGoldByCard[selectedPaid] = paidGold;
                paidItemIdsByCard[selectedPaid] = paidItemId;
                paidItemCountsByCard[selectedPaid] = paidItemCount;
            }
        }

        private static byte FindSelectedCardIndex(byte[] slots)
        {
            if (slots == null)
                return 0xFF;
            for (byte index = 0; index < slots.Length; index++)
            {
                if (slots[index] != 0xFF)
                    return index;
            }
            return 0xFF;
        }

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
