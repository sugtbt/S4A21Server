using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.Tournament;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal enum TournamentDeathDeadlineAction : byte
    {
        ReturnToTown = 0,
        PresentTournamentRewards = 1,
        Ignore = 2,
    }

    internal readonly struct TournamentDeathDeadlineResolution
    {
        internal TournamentDeathDeadlineResolution(
            TournamentDeathDeadlineAction action,
            byte completedRounds,
            bool newlyEliminated)
        {
            Action = action;
            CompletedRounds = completedRounds;
            NewlyEliminated = newlyEliminated;
        }

        internal TournamentDeathDeadlineAction Action { get; }
        internal byte CompletedRounds { get; }
        internal bool NewlyEliminated { get; }
    }

    // Network/application coordinator for tournament-specific commands and
    // projections. Domain state remains in DungeonInstance/DungeonRun.
    internal sealed class TournamentDungeonCoordinator
    {
        private const int RewardSelectionTimeoutMilliseconds = 3000;

        private readonly DungeonSharedServices _services;
        private readonly DungeonSettlementHandler _settlement;
        private readonly TournamentDungeonApplicationService _application;

        internal TournamentDungeonCoordinator(
            DungeonSharedServices services,
            DungeonSettlementHandler settlement)
        {
            _services = services
                ?? throw new ArgumentNullException(nameof(services));
            _settlement = settlement
                ?? throw new ArgumentNullException(nameof(settlement));
            _application = services.Tournaments;
        }

        internal bool CanAcceptActorDeath(
            DungeonRun run,
            Guid sourceEventId,
            ushort sequenceId)
            => _application.CanAcceptActorDeath(
                run,
                sourceEventId,
                sequenceId);

        internal TournamentDeathDeadlineResolution ResolveDeathDeadline(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity)
        {
            if (session?.Player == null
                || run == null
                || !run.Matches(identity)
                || !session.Player.IsCurrentDungeonRun(identity))
            {
                return new TournamentDeathDeadlineResolution(
                    TournamentDeathDeadlineAction.Ignore,
                    completedRounds: 0,
                    newlyEliminated: false);
            }
            if (!_application.IsTournamentRun(run))
            {
                return new TournamentDeathDeadlineResolution(
                    TournamentDeathDeadlineAction.ReturnToTown,
                    completedRounds: 0,
                    newlyEliminated: false);
            }
            if (run.RunState != DungeonRunState.Active)
            {
                return new TournamentDeathDeadlineResolution(
                    TournamentDeathDeadlineAction.Ignore,
                    completedRounds: 0,
                    newlyEliminated: false);
            }

            var transition = _application.ApplyElimination(run);
            if (!transition.Handled
                && !_application.IsTournamentTerminated(run))
            {
                return new TournamentDeathDeadlineResolution(
                    TournamentDeathDeadlineAction.Ignore,
                    transition.CompletedRounds,
                    newlyEliminated: false);
            }

            return new TournamentDeathDeadlineResolution(
                TournamentDeathDeadlineAction.PresentTournamentRewards,
                transition.CompletedRounds,
                transition.Accepted);
        }

        internal async Task<TournamentActorDeathTransition>
            OnActorDeathAsync(
                EnhancedClientSession session,
                DungeonRun run,
                DungeonActorDeathFact death)
        {
            if (session?.Player == null
                || run == null
                || death == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                return default;
            }

            var transition = _application.ApplyActorDeath(run, death);
            if (transition.Disposition
                == TournamentActorDeathDisposition.Duplicate)
            {
                return transition;
            }
            if (!transition.Accepted)
            {
                FileLogger.Log(
                    $"[Tournament] canonical death rejected: " +
                    $"cid={session.Player.CharacterId} seq={death.SequenceId} " +
                    $"event={death.SourceEventId:N}");
                return transition;
            }

            if (transition.RoundCompleted)
            {
                FileLogger.Log(
                    $"[Tournament] round complete: " +
                    $"cid={session.Player.CharacterId} " +
                    $"round={transition.CompletedRound} " +
                    $"next={transition.CurrentRound} " +
                    $"complete={transition.TournamentCompleted}");
            }

            if (transition.TournamentCompleted)
                await EnsureParticipantRewardsAsync(
                    session,
                    run,
                    forceProjection: false);
            return transition;
        }

        internal async Task EnsureParticipantRewardsAsync(
            EnhancedClientSession session,
            DungeonRun run,
            bool forceProjection)
        {
            if (session?.Player == null
                || run == null
                || !_application.IsTournamentTerminated(run)
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                return;
            }

            var partySlot = ResolvePartySlot(session);
            _application.TryCreateParticipantRewards(
                run,
                partySlot,
                session.Player.CharacterId,
                out var rewards);
            if (rewards == null)
                return;

            if (!rewards.ExperienceDelivered)
                await TryDeliverExperienceAsync(session, run, rewards);
            if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return;

            var presentationEffectId =
                _application.GetRewardPresentationEffectId(run);
            if (forceProjection)
            {
                await SendClearRewardAsync(session, run, rewards);
            }
            else if (run.Effects.GetState(presentationEffectId)
                    != DungeonEffectState.Committed
                && run.Effects.TryReserve(
                    presentationEffectId,
                    out var presentationReservation))
            {
                try
                {
                    await SendClearRewardAsync(session, run, rewards);
                    if (!run.Effects.TryCommit(presentationReservation))
                    {
                        throw new InvalidOperationException(
                            "tournament reward presentation reservation was lost");
                    }
                }
                catch
                {
                    run.Effects.TryFail(presentationReservation);
                    throw;
                }
            }
        }

        internal async Task HandleRewardSelectStateAsync(
            EnhancedClientSession session,
            TournamentRewardSelectStateDungeonCommand command,
            DungeonEventEnvelope source)
        {
            var run = session?.Player?.CurrentRun;
            var rewards = _application.GetParticipantRewards(run);
            if (!IsCommandCurrent(session, run, source)
                || command == null
                || rewards == null
                || !_application.IsTournamentTerminated(run)
                || run.RunState != DungeonRunState.Active)
            {
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT_STATE,
                TournamentPacketBuilder.BuildTournamentRewardSelectState(
                    rewards)));
            FileLogger.Log(
                $"[Tournament] reward selection state sent: " +
                $"cid={session.Player.CharacterId} " +
                $"partyCount={rewards.PartyCount} " +
                $"partySlot={rewards.LocalPartySlot}");
            StartRewardSelectionTimer(session, run, rewards);
        }

        internal async Task HandleRewardSelectAsync(
            EnhancedClientSession session,
            TournamentRewardSelectDungeonCommand command,
            DungeonEventEnvelope source)
        {
            var run = session?.Player?.CurrentRun;
            var rewards = _application.GetParticipantRewards(run);
            if (!IsCommandCurrent(session, run, source)
                || command == null
                || rewards == null
                || !_application.IsTournamentTerminated(run)
                || run.RunState != DungeonRunState.Active)
            {
                return;
            }

            await ApplyTournamentRewardSelectionAsync(
                session,
                run,
                command.CardType,
                command.CardIndex,
                run.CaptureIdentity(),
                "client");
        }

        internal async Task RecoverAsync(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || !_application.IsTournamentTerminated(run))
                return;

            await EnsureParticipantRewardsAsync(
                session,
                run,
                forceProjection: true);
            if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return;

            var rewards = _application.GetParticipantRewards(run);
            if (rewards == null || run.RunState != DungeonRunState.Active)
                return;
            if (AreAllParticipantRewardsComplete(run))
            {
                await CompleteTournamentResultAsync(session, run);
                return;
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT_STATE,
                TournamentPacketBuilder.BuildTournamentRewardSelectState(
                    rewards)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT,
                TournamentPacketBuilder.BuildTournamentRewardSelection(
                    rewards)));
            ResumeRewardSelectionTimer(session, run, rewards);
        }

        private async Task ApplyTournamentRewardSelectionAsync(
            EnhancedClientSession session,
            DungeonRun run,
            byte cardType,
            byte cardIndex,
            DungeonRunIdentity identity,
            string source)
        {
            var rewards = _application.GetParticipantRewards(run);
            if (session?.Player == null
                || run == null
                || rewards == null
                || !_application.IsTournamentTerminated(run)
                || run.RunState != DungeonRunState.Active
                || !session.Player.IsCurrentDungeonRun(identity))
            {
                return;
            }

            var reserved = _application.TryReserveReward(
                run,
                cardType,
                cardIndex,
                out var reward,
                out var reservation);
            InventoryMutationSet changes = null;
            var failureReason = string.Empty;
            if (reserved)
            {
                if (!TryGetOwnedInventory(session, out var lease)
                    || !_application.TryDeliverReward(
                        lease,
                        reward,
                        out changes,
                        out failureReason)
                    || !_application.TryCommitReward(
                        run,
                        cardType,
                        cardIndex,
                        reservation))
                {
                    _application.FailReward(
                        run,
                        cardType,
                        cardIndex,
                        reservation);
                    reserved = false;
                }
            }

            if (reserved
                && session.Player.IsCurrentDungeonRun(identity)
                && _services.InventoryRefresh != null
                && changes != null)
            {
                foreach (var slot in changes.Slots)
                {
                    await _services.InventoryRefresh.SendUpdateItemList(
                        session,
                        slot.ListType,
                        slot.SlotIndex);
                    if (!session.Player.IsCurrentDungeonRun(identity))
                        return;
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.TOURNAMENT_REWARD_SELECT,
                TournamentPacketBuilder.BuildTournamentRewardSelection(
                    rewards)));
            if (!session.Player.IsCurrentDungeonRun(identity))
                return;

            if (!reserved)
            {
                FileLogger.Log(
                    $"[Tournament] reward selection ignored: " +
                    $"source={source} cid={session.Player.CharacterId} " +
                    $"type={cardType} index={cardIndex} " +
                    $"reason={failureReason}");
                return;
            }

            FileLogger.Log(
                $"[Tournament] reward delivered: " +
                $"source={source} cid={session.Player.CharacterId} " +
                $"type={cardType} index={cardIndex} " +
                $"gold={(reward.IsGold ? reward.GoldAmount : 0)} " +
                $"item={(reward.IsGold ? 0 : reward.ItemId)}");
            if (!AreAllParticipantRewardsComplete(run))
                return;

            run.Timers.Cancel(
                DungeonRunTimerKeys.TournamentRewardAutoSelect);
            await CompleteTournamentResultAsync(session, run);
        }

        private void StartRewardSelectionTimer(
            EnhancedClientSession session,
            DungeonRun run,
            TournamentParticipantRewardState rewards)
        {
            if (!IsRewardSelectionTimerApplicable(session, run, rewards))
                return;

            if (run.Timers.TryGetCurrentTicket(
                    DungeonRunTimerKeys.TournamentRewardAutoSelect,
                    out _))
            {
                return;
            }
            if (run.Timers.TryGetSnapshot(
                    DungeonRunTimerKeys.TournamentRewardAutoSelect,
                    out var previous)
                && previous.HasDeadline
                && previous.IsSuspended)
            {
                return;
            }

            var identity = run.CaptureIdentity();
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(
                RewardSelectionTimeoutMilliseconds);
            var ticket = run.Timers.Begin(
                DungeonRunTimerKeys.TournamentRewardAutoSelect,
                deadlineUtc,
                RunTimerDetachPolicy.SuspendUntilResume);
            ScheduleRewardSelectionTimer(
                session,
                run,
                identity,
                deadlineUtc,
                ticket);
            FileLogger.Log(
                $"[Tournament] reward auto-select scheduled: " +
                $"cid={session.Player.CharacterId} run={run.RunId}/" +
                $"{run.RunGeneration} deadline={deadlineUtc:O} " +
                $"generation={ticket.Generation}");
        }

        private void ResumeRewardSelectionTimer(
            EnhancedClientSession session,
            DungeonRun run,
            TournamentParticipantRewardState rewards)
        {
            if (!IsRewardSelectionTimerApplicable(session, run, rewards))
                return;
            if (!run.Timers.TryResume(
                    DungeonRunTimerKeys.TournamentRewardAutoSelect,
                    out var ticket,
                    out var deadlineUtc))
            {
                return;
            }

            ScheduleRewardSelectionTimer(
                session,
                run,
                run.CaptureIdentity(),
                deadlineUtc,
                ticket);
            FileLogger.Log(
                $"[Tournament] reward auto-select resumed: " +
                $"cid={session.Player.CharacterId} run={run.RunId}/" +
                $"{run.RunGeneration} deadline={deadlineUtc:O} " +
                $"generation={ticket.Generation}");
        }

        private void ScheduleRewardSelectionTimer(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            DateTime deadlineUtc,
            RunTimerTicket ticket)
        {
            if (!IsRewardSelectionTimerCurrent(session, run, identity, ticket))
                return;

            var handle = ClockService.Instance.ScheduleOneShotAsync(
                "tournament-card:" + session.SessionId.ToString("N")
                    + ":" + run.RunId + ":" + ticket.Generation,
                deadlineUtc,
                async _ => await OnRewardSelectionTimerElapsedAsync(
                    session,
                    run,
                    identity,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnRewardSelectionTimerElapsedAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket)
        {
            try
            {
                if (!IsRewardSelectionTimerCurrent(
                        session,
                        run,
                        identity,
                        ticket))
                {
                    return;
                }

                var rewards = _application.GetParticipantRewards(run);
                if (rewards == null)
                    return;

                for (byte cardType = 0;
                    cardType < TournamentParticipantRewardState.CardTypeCount;
                    cardType++)
                {
                    if (!rewards.IsCardTypeEnabled(cardType)
                        || rewards.IsCardTypeSelectionComplete(cardType)
                        || !IsRewardSelectionTimerCurrent(
                            session,
                            run,
                            identity,
                            ticket))
                    {
                        continue;
                    }

                    await ApplyTournamentRewardSelectionAsync(
                        session,
                        run,
                        cardType,
                        cardIndex: 0,
                        identity: identity,
                        source: "timer");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Tournament] reward auto-select failed: " +
                    $"run={run?.RunId ?? 0}/{run?.RunGeneration ?? 0} " +
                    $"generation={ticket.Generation} error={ex.Message}");
            }
            finally
            {
                run?.Timers.TryComplete(ticket);
            }
        }

        private bool IsRewardSelectionTimerApplicable(
            EnhancedClientSession session,
            DungeonRun run,
            TournamentParticipantRewardState rewards)
            => session?.Player != null
               && run != null
               && rewards != null
               && !rewards.IsSelectionComplete
               && _application.IsTournamentTerminated(run)
               && run.RunState == DungeonRunState.Active
               && session.Player.IsCurrentDungeonRun(run.CaptureIdentity());

        private static bool IsRewardSelectionTimerCurrent(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            RunTimerTicket ticket)
            => session?.Player != null
               && run != null
               && run.Matches(identity)
               && run.Timers.IsCurrent(ticket)
               && session.Player.IsCurrentDungeonRun(identity);

        private Task SubmitClearIntentAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (!_application.TryCreateClearIntent(
                    run,
                    session?.Player?.CharacterId ?? 0,
                    out var intent))
            {
                return Task.CompletedTask;
            }

            return _settlement.SubmitClearIntentAsync(session, intent);
        }

        private Task CompleteTournamentResultAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (!_application.IsTournamentChampion(run))
            {
                var rewards = _application.GetParticipantRewards(run);
                FileLogger.Log(
                    $"[Tournament] elimination settlement complete: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"run={run?.RunId ?? 0}/{run?.RunGeneration ?? 0} " +
                    $"rounds={rewards?.CompletedRounds ?? 0}");
                return Task.CompletedTask;
            }

            return SubmitClearIntentAsync(session, run);
        }

        private async Task TryDeliverExperienceAsync(
            EnhancedClientSession session,
            DungeonRun run,
            TournamentParticipantRewardState rewards)
        {
            if (!_application.TryReserveExperience(run, out var reservedState))
                return;

            ExperienceGrantResult grant = null;
            var persistenceFailed = false;
            try
            {
                if (reservedState.RewardExperience > 0)
                {
                    if (!InventoryContext.TryGetLease(
                            session.Player.CharacterId,
                            out var lease)
                        || !lease.IsOwnedBy(session.SessionId)
                        || !CharacterExperienceCommitService.TryCommitTournamentExperience(
                            lease,
                            session.Player,
                            session.Account?.AccountId ?? 0,
                            reservedState.RewardExperience,
                            _services.CharacterExperience,
                            out grant,
                            out persistenceFailed))
                    {
                        throw new InvalidOperationException(
                            $"tournament experience commit failed "
                            + $"persistenceFailed={persistenceFailed}");
                    }
                }
                if (!reservedState.TryMarkExperienceDelivered())
                    throw new InvalidOperationException(
                        "tournament experience reservation was lost");
            }
            catch
            {
                reservedState.TryRollbackExperience();
                throw;
            }

            // Grant(OnAnyChange) and the delivery checkpoint are already
            // committed. A transport failure must not make EXP grantable
            // again; rejoin projects the authoritative character state.
            await _services.ProgressNotifications
                .SendExpGrantNotificationAsync(
                    session,
                    grant,
                    "TOURNAMENT_CLEAR_REWARD");
            if (grant?.LeveledUp == true)
            {
                await _services.ProgressNotifications
                    .SendInDungeonLevelUpFollowups(session);
            }
        }

        private async Task SendClearRewardAsync(
            EnhancedClientSession session,
            DungeonRun run,
            TournamentParticipantRewardState rewards)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.TOURNAMENT_CLEAR_REWARD,
                TournamentPacketBuilder.BuildTournamentClearReward(rewards)));
            FileLogger.Log(
                $"[Tournament] clear reward projected: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"rounds={rewards.CompletedRounds} " +
                $"champion={rewards.CompletedAllRounds} " +
                $"exp={rewards.RewardExperience}");
        }

        private bool AreAllParticipantRewardsComplete(DungeonRun run)
        {
            var roster = _services.InstanceRegistry
                .CaptureInstanceParticipantRoster(run.Instance.Identity);
            if (roster.Count == 0)
            {
                return _application.GetParticipantRewards(run)
                    ?.IsSelectionComplete == true;
            }

            foreach (var participant in roster)
            {
                if (_application.GetParticipantRewards(participant.Run)
                    ?.IsSelectionComplete != true)
                {
                    return false;
                }
            }
            return true;
        }

        private int ResolvePartySlot(EnhancedClientSession session)
        {
            var party = _services.PartyManager?.GetPartyByUser(
                session.Player.UserId);
            if (party == null)
                return 0;
            foreach (var member in party.MembersBySlot())
            {
                if (member.UserId == session.Player.UserId)
                    return member.SlotIndex;
            }
            return 0;
        }

        private static bool TryGetOwnedInventory(
            EnhancedClientSession session,
            out InventoryLease lease)
        {
            lease = null;
            var characterId = session?.Player?.CharacterId ?? 0;
            return characterId > 0
                && InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    characterId,
                    out lease);
        }

        private static bool IsCommandCurrent(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonEventEnvelope source)
            => session?.Player != null
               && run != null
               && source != null
               && run.Matches(source.RunIdentity)
               && session.Player.IsCurrentDungeonRun(run.CaptureIdentity());
    }
}
