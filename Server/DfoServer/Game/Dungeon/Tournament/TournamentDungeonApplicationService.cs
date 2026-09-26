using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.Dungeon.Tournament
{
    // Tournament domain/application boundary. It owns PVF-derived definition
    // usage and runtime transitions; packet projection stays in Network.
    internal sealed class TournamentDungeonApplicationService
    {
        private readonly Func<InventoryLease, bool> _persistInventory;

        internal TournamentDungeonApplicationService(
            Func<InventoryLease, bool> persistInventory = null)
        {
            _persistInventory = persistInventory
                ?? InventoryPersistenceService.SaveDirty;
        }

        internal bool TryPrepareRun(
            DungeonRun run,
            int partyCount,
            Func<int, int> next,
            out TournamentDungeonDefinition definition,
            out string failureReason)
        {
            definition = null;
            failureReason = string.Empty;
            if (run == null)
            {
                failureReason = "run is missing";
                return false;
            }

            if (!TournamentDungeonDefinitionCatalog.IsTournamentDungeon(
                    run.DungeonId))
            {
                return true;
            }

            if (!TournamentDungeonDefinitionCatalog.TryResolve(
                    run.DungeonId,
                    run.MazeStartMapId,
                    out definition,
                    out failureReason))
            {
                return false;
            }

            if (!TournamentDungeonRuntimeFactory.TryCreate(
                    definition,
                    partyCount,
                    next,
                    out var runtime,
                    out failureReason))
            {
                return false;
            }

            if (!run.Instance.Mechanisms.TryAttachTournament(runtime))
            {
                failureReason =
                    "tournament runtime already belongs to another instance";
                return false;
            }

            // A tournament has its own final-round/reward gate. Ordinary boss
            // endpoint clearing must remain disabled until card selection ends.
            run.IgnoreDefaultDungeonClear = true;
            return true;
        }

        internal bool IsTournamentRun(DungeonRun run)
            => run?.Instance?.Mechanisms?.Tournament != null;

        internal bool IsTournamentMap(
            DungeonRun run,
            int mapId)
            => IsTournamentRun(run)
               && mapId > 0
               && run.Instance.Mechanisms.Tournament.Definition.MapId == mapId;

        internal bool TryProjectStartMap(
            DungeonRun run,
            DungeonData.MazeSumInfo source,
            out DungeonData.MazeSumInfo projected)
        {
            projected = source;
            var runtime = run?.Instance?.Mechanisms?.Tournament;
            if (runtime == null || source.Index != runtime.Definition.MapId)
                return false;

            projected = new DungeonData.MazeSumInfo
            {
                Index = source.Index,
                X = source.X,
                Y = source.Y,
                Monsters = CopyActors(runtime.PathActors),
                EventMonsterPositions = source.EventMonsterPositions,
                SpecialPassiveObjects = source.SpecialPassiveObjects,
                PassiveObjectCodes = source.PassiveObjectCodes,
            };
            return true;
        }

        internal bool TryBindFirstActorSequence(
            DungeonRun run,
            ushort firstActorSequence)
        {
            var runtime = run?.Instance?.Mechanisms?.Tournament;
            return runtime != null
                && runtime.TryBindFirstActorSequence(firstActorSequence);
        }

        internal bool CanAcceptActorDeath(
            DungeonRun run,
            Guid sourceEventId,
            ushort sequenceId)
        {
            var runtime = run?.Instance?.Mechanisms?.Tournament;
            return runtime == null
                || runtime.CanAcceptActorDeath(sourceEventId, sequenceId);
        }

        internal TournamentActorDeathTransition ApplyActorDeath(
            DungeonRun run,
            DungeonActorDeathFact death)
        {
            var runtime = run?.Instance?.Mechanisms?.Tournament;
            return runtime == null
                ? default
                : runtime.TryApplyActorDeath(death);
        }

        internal TournamentEliminationTransition ApplyElimination(
            DungeonRun run)
        {
            var runtime = run?.Instance?.Mechanisms?.Tournament;
            return runtime == null
                ? default
                : runtime.TryEliminate();
        }

        internal bool IsTournamentTerminated(DungeonRun run)
            => run?.Instance?.Mechanisms?.Tournament?.IsTerminated == true;

        internal bool IsTournamentChampion(DungeonRun run)
            => run?.Instance?.Mechanisms?.Tournament?.IsChampion == true;

        internal bool TryCreateParticipantRewards(
            DungeonRun run,
            int partySlot,
            int characterId,
            out TournamentParticipantRewardState state)
        {
            state = null;
            var runtime = run?.Instance?.Mechanisms?.Tournament;
            if (runtime == null || !runtime.IsTerminated)
                return false;

            var partyCount = runtime.Definition.PartyLimit;
            if (partySlot < 0 || partySlot >= partyCount)
                return false;

            lock (run.SyncRoot)
            {
                if (run.Settlement.Tournament != null)
                {
                    state = run.Settlement.Tournament;
                    return false;
                }

                // Tournament teams can field up to two human party members,
                // so reward cards roll on the same per-participant card
                // stream as ordinary settlement instead of the shared room
                // LCG, which would duplicate one member's cards for the
                // whole team.
                var lcg = new DnfLcg(
                    DropService.DeriveParticipantCardSeed(
                        run.Seed,
                        run.PartyDungeonInstanceId,
                        run.CurrentRoomInstanceId,
                        characterId));
                if (!TryCreateRewardCards(
                        run,
                        runtime,
                        lcg,
                        out var rewards))
                {
                    return false;
                }

                state = new TournamentParticipantRewardState(
                    rewards,
                    partyCount,
                    partySlot,
                    runtime.Definition.GetClearRewardExperience(
                        runtime.CompletedRounds),
                    runtime.CompletedRounds,
                    runtime.IsChampion);
                run.Settlement.Tournament = state;
                return true;
            }
        }

        internal TournamentParticipantRewardState GetParticipantRewards(
            DungeonRun run)
        {
            if (run == null)
                return null;
            lock (run.SyncRoot)
                return run.Settlement.Tournament;
        }

        internal bool TryReserveReward(
            DungeonRun run,
            byte cardType,
            byte cardIndex,
            out ClearRewardGenerator.CardReward reward,
            out DungeonEffectReservation reservation)
        {
            reward = default;
            reservation = default;
            var state = GetParticipantRewards(run);
            if (state == null
                || !state.TryReserveSelection(cardType, cardIndex, out reward))
            {
                return false;
            }

            var effectId = GetRewardEffectId(run, cardType);
            if (!run.Effects.TryReserve(effectId, out reservation))
            {
                state.TryRollbackSelection(cardType, cardIndex);
                return false;
            }

            return true;
        }

        internal bool TryCommitReward(
            DungeonRun run,
            byte cardType,
            byte cardIndex,
            DungeonEffectReservation reservation)
        {
            var state = GetParticipantRewards(run);
            if (state == null || !run.Effects.TryCommit(reservation))
                return false;

            if (!state.TryMarkDelivered(cardType, cardIndex))
            {
                FileLogger.Log(
                    $"[Tournament] reward ledger committed but state transition failed: " +
                    $"run={run.RunId} type={cardType} index={cardIndex}");
                return false;
            }

            return true;
        }

        internal void FailReward(
            DungeonRun run,
            byte cardType,
            byte cardIndex,
            DungeonEffectReservation reservation)
        {
            GetParticipantRewards(run)?.TryRollbackSelection(
                cardType,
                cardIndex);
            run?.Effects.TryFail(reservation);
        }

        internal bool TryReserveExperience(
            DungeonRun run,
            out TournamentParticipantRewardState state)
        {
            state = GetParticipantRewards(run);
            return state != null && state.TryReserveExperience();
        }

        internal bool TryCreateClearIntent(
            DungeonRun run,
            int sourcePlayerId,
            out DungeonClearIntent intent)
        {
            intent = null;
            var runtime = run?.Instance?.Mechanisms?.Tournament;
            var rewards = GetParticipantRewards(run);
            if (sourcePlayerId <= 0
                || runtime == null
                || !runtime.IsChampion
                || rewards?.IsSelectionComplete != true)
            {
                return false;
            }

            var finalActor = runtime.PathActors.Count > 0
                ? runtime.PathActors[runtime.PathActors.Count - 1]
                : default;
            var finalSequence = runtime.FirstActorSequence > 0
                    && runtime.PathActors.Count > 0
                ? (long?)runtime.FirstActorSequence
                    + runtime.PathActors.Count - 1L
                : null;
            var source = new DungeonEventEnvelope(
                run.GetSettlementSourceEventId(),
                run.CaptureIdentity(),
                run.CurrentRoomInstanceId > 0
                    ? run.CurrentRoomInstanceId
                    : (long?)null,
                sourcePlayerId,
                sourcePlayerId,
                finalSequence,
                finalActor.Code > 0 ? finalActor.Code : (int?)null,
                "tournament-reward-selection-complete",
                Environment.TickCount64);
            intent = new DungeonClearIntent(
                source,
                "tournament reward selection complete",
                finalActor.Code,
                DungeonClearPresentationKind.Tournament);
            return true;
        }

        internal DungeonEffectId GetRewardPresentationEffectId(
            DungeonRun run)
            => new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                "tournament-clear-reward",
                DungeonEffectScope.Player,
                run.RunId);

        internal DungeonEffectId GetRewardEffectId(
            DungeonRun run,
            byte cardType)
            => new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                "tournament-card-" + cardType,
                DungeonEffectScope.Player,
                run.RunId);

        internal bool TryDeliverReward(
            InventoryLease lease,
            ClearRewardGenerator.CardReward reward,
            out InventoryMutationSet changes,
            out string failureReason)
        {
            changes = new InventoryMutationSet();
            failureReason = string.Empty;
            if (IsEmptyReward(reward)
                || (reward.IsGold && reward.GoldAmount <= 0))
            {
                return true;
            }
            if (lease == null)
            {
                failureReason = "inventory lease is missing";
                return false;
            }

            var request = reward.IsGold
                ? InventoryRewardGrantRequest.Create(
                    0,
                    reward.GoldAmount,
                    ItemCreateReason.DungeonDrop)
                : InventoryRewardGrantRequest.Create(
                    reward.ItemId,
                    reward.StackCount,
                    ItemCreateReason.DungeonDrop);
            if (request.Count <= 0 || (!reward.IsGold && request.ItemTemplateId <= 0))
            {
                failureReason = "reward is empty or malformed";
                return false;
            }

            lock (lease.SyncRoot)
            {
                if (!InventoryRewardGrantService.TryPlanBatch(
                        lease.Inventory,
                        new[] { request },
                        out var plan)
                    || plan == null
                    || !plan.Success)
                {
                    failureReason =
                        $"reward plan failed: {plan?.Error.ToString() ?? "unknown"}";
                    return false;
                }

                var snapshot = InventoryMutationSnapshot.Capture(
                    lease.Inventory,
                    plan);
                if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                        lease.Inventory,
                        plan,
                        out var granted)
                    || granted == null
                    || !granted.Success)
                {
                    snapshot.Restore(lease.Inventory, plan);
                    failureReason = "reward apply failed";
                    return false;
                }

                changes.AddRange(granted.Changes);
                if (!_persistInventory(lease))
                {
                    snapshot.Restore(lease.Inventory, plan);
                    changes = new InventoryMutationSet();
                    failureReason = "reward persistence failed";
                    return false;
                }
            }

            return true;
        }

        // The common card generator owns the base amount because it already
        // accounts for canonical instance kill facts. Tournament DGN then
        // applies its own clear-reward multiplier to that one frozen amount.
        internal static int ScaleTournamentGold(
            int baseGold,
            float multiplier)
        {
            if (baseGold <= 0
                || multiplier <= 0f
                || float.IsNaN(multiplier)
                || float.IsInfinity(multiplier))
            {
                return 0;
            }

            var scaled = baseGold * (double)multiplier;
            return scaled >= int.MaxValue
                ? int.MaxValue
                : (int)Math.Floor(scaled);
        }

        private static bool TryCreateRewardCards(
            DungeonRun run,
            TournamentDungeonRuntime runtime,
            DnfLcg lcg,
            out List<ClearRewardGenerator.CardReward> rewards)
        {
            rewards = new List<ClearRewardGenerator.CardReward>(
                TournamentParticipantRewardState.CardTypeCount
                * TournamentParticipantRewardState.CardsPerType);
            var instance = run.Instance;
            var selection = instance?.Selection;
            var statistics = instance?.KillStatistics
                ?? default(DungeonKillStatistics);
            var context = new ClearRewardGenerationContext(
                runtime.Definition.BasicLevel,
                run.Difficulty,
                selection?.PartyMemberCount ?? run.EntryPartyMemberCount,
                rankBonusRate: 0.0f,
                normalKillCount: statistics.NormalKillCount,
                championKillCount: statistics.ChampionKillCount,
                bossKillCount: statistics.BossKillCount,
                visitedRoomCount: Math.Max(
                    1,
                    instance?.VisitedRoomCount ?? run.RoomStates.Count),
                totalRoomCount: Math.Max(
                    1,
                    selection?.TotalRoomCount ?? run.TotalRoomCount));
            var baseGold = ClearRewardGenerator.GenerateFreeGoldCard(
                context,
                lcg);
            var frozenGold = new ClearRewardGenerator.CardReward
            {
                IsGold = true,
                GoldAmount = ScaleTournamentGold(
                    baseGold.GoldAmount,
                    runtime.Definition.ClearRewardGoldRate),
            };

            var upperResultKey = runtime.CompletedRounds;
            if (!runtime.IsTerminated || upperResultKey > 4)
            {
                rewards.Clear();
                return false;
            }

            // The upper row uses the frozen completed-round result. Only a
            // champion receives the additional result-key 5 lower row.
            for (var index = 0;
                index < TournamentParticipantRewardState.CardsPerType;
                index++)
            {
                if (!TryCreateResultCard(
                        runtime.Definition,
                        upperResultKey,
                        frozenGold,
                        lcg,
                        out var reward))
                {
                    rewards.Clear();
                    return false;
                }
                rewards.Add(reward);
            }
            if (!runtime.IsChampion)
            {
                for (var index = 0;
                    index < TournamentParticipantRewardState.CardsPerType;
                    index++)
                {
                    rewards.Add(CreateEmptyReward());
                }
                return true;
            }

            for (var index = 0;
                index < TournamentParticipantRewardState.CardsPerType;
                index++)
            {
                if (!TryCreateResultCard(
                        runtime.Definition,
                        resultKey: 5,
                        frozenGold,
                        lcg,
                        out var reward))
                {
                    rewards.Clear();
                    return false;
                }
                rewards.Add(reward);
            }
            return true;
        }

        private static ClearRewardGenerator.CardReward CreateEmptyReward()
            => new ClearRewardGenerator.CardReward
            {
                IsGold = false,
                ItemId = -1,
                StackCount = 0,
            };

        private static bool TryCreateResultCard(
            TournamentDungeonDefinition definition,
            int resultKey,
            ClearRewardGenerator.CardReward frozenGold,
            DnfLcg lcg,
            out ClearRewardGenerator.CardReward reward)
        {
            reward = default;
            if (definition == null
                || lcg == null
                || !definition.TryGetResultCard(resultKey, out var weights)
                || weights.TotalWeight <= 0
                || weights.TotalWeight > int.MaxValue)
            {
                return false;
            }

            var roll = lcg.Next((int)weights.TotalWeight);
            if (roll < weights.GoldWeight)
            {
                reward = frozenGold;
                return true;
            }
            if (roll < weights.GoldWeight + weights.ItemWeight)
                return TryCreateRewardItem(definition, lcg, out reward);

            reward = CreateEmptyReward();
            return true;
        }

        private static bool TryCreateRewardItem(
            TournamentDungeonDefinition definition,
            DnfLcg lcg,
            out ClearRewardGenerator.CardReward reward)
        {
            reward = default;
            long totalWeight = 0;
            foreach (var candidate in definition.RewardItemRates)
                totalWeight += candidate.Weight;
            if (totalWeight <= 0 || totalWeight > int.MaxValue)
                return false;

            var roll = lcg.Next((int)totalWeight);
            long cumulative = 0;
            foreach (var candidate in definition.RewardItemRates)
            {
                cumulative += candidate.Weight;
                if (roll >= cumulative)
                    continue;

                reward = new ClearRewardGenerator.CardReward
                {
                    IsGold = false,
                    ItemId = candidate.ItemId,
                    StackCount = candidate.Count,
                };
                return true;
            }
            return false;
        }

        private static bool IsEmptyReward(
            ClearRewardGenerator.CardReward reward)
            => !reward.IsGold
               && reward.ItemId == -1
               && reward.StackCount == 0;

        private static List<DungeonData.MonsterSumInfo> CopyActors(
            IReadOnlyList<DungeonData.MonsterSumInfo> source)
        {
            var result = new List<DungeonData.MonsterSumInfo>(
                source?.Count ?? 0);
            if (source != null)
                foreach (var actor in source)
                    result.Add(actor);
            return result;
        }

        private sealed class InventoryMutationSnapshot
        {
            private readonly Dictionary<(InventoryListType, short), ItemCore>
                _items = new Dictionary<(InventoryListType, short), ItemCore>();
            private readonly Dictionary<short, int> _virtualCounts =
                new Dictionary<short, int>();

            internal static InventoryMutationSnapshot Capture(
                InventoryService inventory,
                InventoryRewardGrantBatchPlan plan)
            {
                var snapshot = new InventoryMutationSnapshot();
                foreach (var entry in plan.Entries)
                {
                    if (entry.Kind == InventoryRewardGrantKind.MainVirtualCount)
                    {
                        snapshot._virtualCounts[entry.SlotIndex] =
                            inventory.GetMainVirtualCount(entry.SlotIndex)?.Count
                            ?? 0;
                        continue;
                    }

                    if (entry.Kind != InventoryRewardGrantKind.InventoryItem)
                        continue;
                    var key = (entry.ListType, entry.SlotIndex);
                    snapshot._items[key] = inventory.TryGetItem(
                        entry.ListType,
                        entry.SlotIndex,
                        out var item)
                        ? item.Copy()
                        : null;
                }
                return snapshot;
            }

            internal void Restore(
                InventoryService inventory,
                InventoryRewardGrantBatchPlan plan)
            {
                foreach (var entry in plan?.Entries
                    ?? Array.Empty<InventoryRewardGrantPlanEntry>())
                {
                    if (entry.Kind == InventoryRewardGrantKind.InventoryItem
                        && entry.CreateResult != null)
                    {
                        InventoryCreateService.DetachCreatedDetails(
                            inventory,
                            entry.CreateResult);
                    }
                }

                foreach (var pair in _items)
                    inventory.SetItem(
                        pair.Key.Item1,
                        pair.Key.Item2,
                        pair.Value?.Copy());
                foreach (var pair in _virtualCounts)
                    inventory.SetMainVirtualCount(pair.Key, pair.Value);
            }
        }
    }
}
