using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonEntryItemRequirement
    {
        internal DungeonEntryItemRequirement(
            int itemId,
            int count,
            bool consumeOnEntry)
        {
            ItemId = itemId;
            Count = count;
            ConsumeOnEntry = consumeOnEntry;
        }

        internal int ItemId { get; }
        internal int Count { get; }
        internal bool ConsumeOnEntry { get; }
    }

    internal sealed class DungeonEntryCostAlternative
    {
        internal DungeonEntryCostAlternative(
            IReadOnlyList<DungeonEntryItemRequirement> requirements,
            bool isFreePass = false)
        {
            Requirements = requirements;
            IsFreePass = isFreePass;
        }

        internal IReadOnlyList<DungeonEntryItemRequirement> Requirements { get; }
        internal bool IsFreePass { get; }
    }

    internal sealed class DungeonEntryCostAlternativeGroup
    {
        internal DungeonEntryCostAlternativeGroup(
            IReadOnlyList<DungeonEntryCostAlternative> alternatives,
            int missingAlternativeIndex)
        {
            Alternatives = alternatives;
            MissingAlternativeIndex = missingAlternativeIndex;
        }

        internal IReadOnlyList<DungeonEntryCostAlternative> Alternatives { get; }
        internal int MissingAlternativeIndex { get; }
    }

    internal sealed class DungeonEntryCostPlan
    {
        private readonly List<DungeonEntryItemRequirement> _requiredItems =
            new List<DungeonEntryItemRequirement>();
        private readonly List<DungeonEntryCostAlternativeGroup>
            _alternativeGroups =
                new List<DungeonEntryCostAlternativeGroup>();

        internal DungeonEntryCostPlan(string source)
        {
            Source = string.IsNullOrWhiteSpace(source)
                ? "dungeon-entry"
                : source;
        }

        internal string Source { get; }
        internal IReadOnlyList<DungeonEntryItemRequirement> RequiredItems =>
            _requiredItems;
        internal IReadOnlyList<DungeonEntryCostAlternativeGroup>
            AlternativeGroups => _alternativeGroups;
        internal int GoldCost { get; private set; }

        internal void AddRequiredItems(
            IEnumerable<DungeonEntryItemRequirement> requirements)
        {
            if (requirements == null)
                return;
            _requiredItems.AddRange(requirements);
        }

        internal void AddAlternativeGroup(
            IReadOnlyList<DungeonEntryCostAlternative> alternatives,
            int missingAlternativeIndex = 0)
        {
            _alternativeGroups.Add(new DungeonEntryCostAlternativeGroup(
                alternatives,
                missingAlternativeIndex));
        }

        internal bool TryAddGoldCost(int amount)
        {
            if (amount < 0)
                return false;
            var total = (long)GoldCost + amount;
            if (total > int.MaxValue)
                return false;
            GoldCost = (int)total;
            return true;
        }
    }

    internal readonly struct DungeonEntryCostCommitRequest
    {
        internal DungeonEntryCostCommitRequest(
            InventoryLease lease,
            DungeonEntryCostPlan plan)
        {
            Lease = lease;
            Plan = plan;
        }

        internal InventoryLease Lease { get; }
        internal DungeonEntryCostPlan Plan { get; }
    }

    internal sealed class DungeonEntryCostService
    {
        private readonly IGameDatabase _database;
        private readonly Func<InventoryLease, bool> _persistInventory;

        internal DungeonEntryCostService()
            : this(GameDatabase.CreateDefault(), null)
        {
        }

        internal DungeonEntryCostService(IGameDatabase database)
            : this(database, null)
        {
        }

        internal DungeonEntryCostService(
            Func<InventoryLease, bool> persistInventory)
            : this(GameDatabase.CreateDefault(), persistInventory)
        {
        }

        internal DungeonEntryCostService(
            IGameDatabase database,
            Func<InventoryLease, bool> persistInventory)
        {
            _database = database ?? GameDatabase.CreateDefault();
            _persistInventory = persistInventory
                ?? InventoryPersistenceService.SaveDirty;
        }

        internal static IReadOnlyList<DungeonEntryItemRequirement>
            ProjectPvfRequiredItems(
                IReadOnlyList<PvfLib.DungeonRequiredItem> source)
        {
            if (source == null)
                return null;
            if (source.Count == 0)
                return Array.Empty<DungeonEntryItemRequirement>();

            var result = new List<DungeonEntryItemRequirement>(source.Count);
            foreach (var item in source)
            {
                result.Add(item == null
                    ? default
                    : new DungeonEntryItemRequirement(
                        item.ItemId,
                        item.Count,
                        item.ConsumeOnEntry));
            }
            return result;
        }

        internal EntryCostResult TryConsumeRequiredItems(
            InventoryLease lease,
            IReadOnlyList<DungeonEntryItemRequirement> requiredItems)
        {
            if (requiredItems == null)
            {
                return new EntryCostResult().Fail(
                    "required item definition is missing",
                    EntryCostFailureKind.Unavailable);
            }

            var plan = new DungeonEntryCostPlan("required-item");
            plan.AddRequiredItems(requiredItems);
            return TryCommitPlan(lease, plan);
        }

        internal EntryCostResult TryConsumePreferredAlternative(
            InventoryLease lease,
            IReadOnlyList<IReadOnlyList<DungeonEntryItemRequirement>>
                alternatives)
        {
            if (alternatives == null || alternatives.Count == 0)
                return new EntryCostResult().Fail(
                    "entry item alternatives are missing",
                    EntryCostFailureKind.Unavailable);

            var projected = new List<DungeonEntryCostAlternative>(
                alternatives.Count);
            foreach (var alternative in alternatives)
            {
                projected.Add(new DungeonEntryCostAlternative(alternative));
            }

            var plan = new DungeonEntryCostPlan("preferred-alternative");
            plan.AddAlternativeGroup(projected);
            return TryCommitPlan(lease, plan);
        }

        internal EntryCostResult TryValidatePlan(
            InventoryLease lease,
            DungeonEntryCostPlan plan)
            => EvaluatePlan(lease, plan, commit: false);

        internal EntryCostResult TryCommitPlan(
            InventoryLease lease,
            DungeonEntryCostPlan plan)
            => EvaluatePlan(lease, plan, commit: true);

        internal bool TryCommitPlans(
            IReadOnlyList<DungeonEntryCostCommitRequest> source,
            out IReadOnlyList<EntryCostResult> results)
        {
            results = Array.Empty<EntryCostResult>();
            if (source == null || source.Count == 0)
                return false;

            var states = source
                .Select(request => new GroupCommitState(request))
                .OrderBy(state => state.Request.Lease?.CharacterId ?? 0)
                .ToArray();
            if (states.Any(state => state.Request.Lease?.Inventory == null
                                    || state.Request.Plan == null)
                || states.Select(state => state.Request.Lease.CharacterId)
                    .Distinct().Count() != states.Length)
            {
                return false;
            }

            var entered = new List<InventoryLease>(states.Length);
            var committed = false;
            string connectionString = null;
            try
            {
                foreach (var state in states)
                {
                    var lease = state.Request.Lease;
                    Monitor.Enter(lease.SyncRoot);
                    entered.Add(lease);
                    if (!InventoryContext.IsCurrentLease(
                            lease,
                            lease.SessionId,
                            lease.CharacterId))
                    {
                        state.Result.Fail(
                            "inventory lease is stale",
                            EntryCostFailureKind.InvalidState);
                        return false;
                    }
                    if (!TryResolvePlan(
                            lease.Inventory,
                            state.Request.Plan,
                            state.Result,
                            out _,
                            out var consumedCounts,
                            out var failureReason))
                    {
                        state.Result.Fail(
                            failureReason,
                            state.Result.FailureKind
                                == EntryCostFailureKind.None
                                ? EntryCostFailureKind.Unavailable
                                : state.Result.FailureKind);
                        return false;
                    }
                    state.ConsumedCounts = consumedCounts;
                    state.Snapshot = EntryItemSnapshot.Capture(
                        lease.Inventory,
                        consumedCounts.Keys);
                }

                if (states.All(state => state.ConsumedCounts.Count == 0))
                {
                    foreach (var state in states)
                        state.Result.Success = true;
                    committed = true;
                    results = ProjectGroupResults(source, states);
                    return true;
                }

                connectionString = states[0].Request.Lease.Inventory.Database
                    ?.ConnectionString;
                if (string.IsNullOrWhiteSpace(connectionString)
                    || states.Any(state => !string.Equals(
                        state.Request.Lease.Inventory.Database?.ConnectionString,
                        connectionString,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                using (var connection = states[0].Request.Lease.Inventory
                           .Database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var state in states)
                    {
                        ApplyGroupCostLocked(state);
                        FinalizeGroupResult(state);
                    }
                    var committedResults = ProjectGroupResults(source, states);
                    foreach (var state in states)
                    {
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                state.Request.Lease))
                        {
                            throw new InvalidOperationException(
                                $"entry cost persistence failed " +
                                $"cid={state.Request.Lease.CharacterId}");
                        }
                    }
                    transaction.Commit();
                    committed = true;
                    foreach (var state in states)
                    {
                        try
                        {
                            state.Request.Lease.Inventory.ClearDirtyState();
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log(
                                $"[DungeonEntryCost] post-commit dirty-state " +
                                $"cleanup failed cid=" +
                                $"{state.Request.Lease.CharacterId}: " +
                                ex.Message);
                        }
                    }
                    results = committedResults;
                }

                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonEntryCost] group transaction failed: " +
                    ex.Message);
                return false;
            }
            finally
            {
                if (!committed)
                {
                    foreach (var state in states)
                        state.Snapshot?.Restore(state.Request.Lease.Inventory);
                }
                for (var index = entered.Count - 1; index >= 0; index--)
                    Monitor.Exit(entered[index].SyncRoot);

                if (!committed && !string.IsNullOrWhiteSpace(connectionString))
                {
                    foreach (var state in states)
                    {
                        try
                        {
                            InventoryRollbackRecoveryService
                                .ReloadOnlineInventory(
                                    connectionString,
                                    state.Request.Lease);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log(
                                $"[DungeonEntryCost] group rollback reload " +
                                $"failed cid={state.Request.Lease.CharacterId}: " +
                                ex.Message);
                        }
                    }
                }
            }
        }

        private sealed class GroupCommitState
        {
            internal GroupCommitState(DungeonEntryCostCommitRequest request)
            {
                Request = request;
            }

            internal DungeonEntryCostCommitRequest Request { get; }
            internal EntryCostResult Result { get; } = new EntryCostResult();
            internal Dictionary<int, int> ConsumedCounts { get; set; } =
                new Dictionary<int, int>();
            internal EntryItemSnapshot Snapshot { get; set; }
            internal List<InventoryMaterialConsumptionEntry> Consumed { get; } =
                new List<InventoryMaterialConsumptionEntry>();
        }

        private static void ApplyGroupCostLocked(GroupCommitState state)
        {
            var inventory = state.Request.Lease.Inventory;
            var requirements = state.ConsumedCounts
                .Where(pair => pair.Key != 0)
                .OrderBy(pair => pair.Key)
                .Select(pair => new InventoryMaterialRequirement(
                    pair.Key,
                    pair.Value))
                .ToList();
            state.Result.GoldBefore = inventory.CountMainItem(0);
            if (!InventoryMaterialConsumptionService.TryConsume(
                    inventory,
                    requirements,
                    state.Consumed))
            {
                throw new InvalidOperationException(
                    $"entry cost consumption failed " +
                    $"cid={state.Request.Lease.CharacterId}");
            }
            if (state.ConsumedCounts.TryGetValue(0, out var goldCost)
                && goldCost > 0)
            {
                if (!inventory.TryConsumeMainItem(
                        0,
                        goldCost,
                        out var goldConsume)
                    || !goldConsume.Success)
                {
                    throw new InvalidOperationException(
                        $"entry gold consumption failed " +
                        $"cid={state.Request.Lease.CharacterId}");
                }
                state.Consumed.Add(new InventoryMaterialConsumptionEntry
                {
                    SlotIndex = goldConsume.SlotIndex,
                    ItemTemplateId = 0,
                    Count = goldConsume.ConsumedCount,
                });
            }
        }

        private static void FinalizeGroupResult(GroupCommitState state)
        {
            var inventory = state.Request.Lease.Inventory;
            foreach (var entry in state.Consumed)
            {
                if (entry.ItemTemplateId == 0)
                {
                    state.Result.GoldCost += entry.Count;
                    continue;
                }
                state.Result.ConsumedItems.Add(new ItemConsumeUpdate
                {
                    ItemId = entry.ItemTemplateId,
                    Count = entry.Count,
                    SlotIndex = entry.SlotIndex,
                    RemainingCount = ResolveRemainingCount(
                        inventory,
                        entry.SlotIndex),
                });
            }
            state.Result.GoldAfter = inventory.CountMainItem(0);
            state.Result.Success = true;
        }

        private static IReadOnlyList<EntryCostResult> ProjectGroupResults(
            IReadOnlyList<DungeonEntryCostCommitRequest> source,
            IReadOnlyList<GroupCommitState> states)
        {
            var byCharacterId = states.ToDictionary(
                state => state.Request.Lease.CharacterId,
                state => state.Result);
            var projected = new EntryCostResult[source.Count];
            for (var index = 0; index < source.Count; index++)
            {
                projected[index] = byCharacterId[
                    source[index].Lease.CharacterId];
            }
            return projected;
        }

        internal bool CheckHellQuestRequirement(
            int characterId,
            WorldMapArea area,
            out int missingQuestId)
        {
            var repository = new QuestRepository(_database.ConnectionString);
            return EvaluateHellQuestRequirement(
                area,
                questId => questId <= ushort.MaxValue
                    && repository.IsQuestCleared(characterId, (ushort)questId),
                out missingQuestId);
        }

        internal static bool EvaluateHellQuestRequirement(
            WorldMapArea area,
            Func<int, bool> isQuestCleared,
            out int missingQuestId)
        {
            missingQuestId = 0;
            if (area == null)
                return false;
            if (area.HellQuestIds.Count == 0)
                return true;
            if (isQuestCleared == null)
                throw new ArgumentNullException(nameof(isQuestCleared));

            foreach (var questId in area.HellQuestIds)
            {
                if (questId <= 0)
                    continue;

                if (!isQuestCleared(questId))
                {
                    missingQuestId = questId;
                    return false;
                }
            }

            return true;
        }

        private EntryCostResult EvaluatePlan(
            InventoryLease lease,
            DungeonEntryCostPlan plan,
            bool commit)
        {
            var result = new EntryCostResult();
            if (lease?.Inventory == null)
            {
                return result.Fail(
                    "inventory lease is missing",
                    EntryCostFailureKind.InvalidState);
            }
            if (plan == null)
            {
                return result.Fail(
                    "entry cost plan is missing",
                    EntryCostFailureKind.Unavailable);
            }

            EntryItemSnapshot snapshot = null;
            lock (lease.SyncRoot)
            {
                try
                {
                    if (!TryResolvePlan(
                            lease.Inventory,
                            plan,
                            result,
                            out var requiredCounts,
                            out var consumedCounts,
                            out var failureReason))
                    {
                        return result.Fail(
                            failureReason,
                            result.FailureKind == EntryCostFailureKind.None
                                ? EntryCostFailureKind.Unavailable
                                : result.FailureKind);
                    }

                    if (!commit || consumedCounts.Count == 0)
                    {
                        result.Success = true;
                        return result;
                    }

                    snapshot = EntryItemSnapshot.Capture(
                        lease.Inventory,
                        consumedCounts.Keys);
                    var requirements = consumedCounts
                        .Where(pair => pair.Key != 0)
                        .OrderBy(pair => pair.Key)
                        .Select(pair => new InventoryMaterialRequirement(
                            pair.Key,
                            pair.Value))
                        .ToList();
                    var consumed =
                        new List<InventoryMaterialConsumptionEntry>();
                    result.GoldBefore = lease.Inventory.CountMainItem(0);
                    if (!InventoryMaterialConsumptionService.TryConsume(
                            lease.Inventory,
                            requirements,
                            consumed))
                    {
                        snapshot.Restore(lease.Inventory);
                        return result.Fail(
                            "entry cost consumption failed",
                            EntryCostFailureKind.InvalidState);
                    }
                    if (consumedCounts.TryGetValue(0, out var goldCost)
                        && goldCost > 0)
                    {
                        if (!lease.Inventory.TryConsumeMainItem(
                                0,
                                goldCost,
                                out var goldConsume)
                            || !goldConsume.Success)
                        {
                            snapshot.Restore(lease.Inventory);
                            return result.Fail(
                                "entry gold consumption failed",
                                EntryCostFailureKind.InvalidState);
                        }
                        consumed.Add(new InventoryMaterialConsumptionEntry
                        {
                            SlotIndex = goldConsume.SlotIndex,
                            ItemTemplateId = 0,
                            Count = goldConsume.ConsumedCount,
                        });
                    }

                    if (!_persistInventory(lease))
                    {
                        snapshot.Restore(lease.Inventory);
                        return result.Fail(
                            "entry cost persistence failed",
                            EntryCostFailureKind.InvalidState);
                    }

                    foreach (var entry in consumed)
                    {
                        if (entry.ItemTemplateId == 0)
                        {
                            result.GoldCost += entry.Count;
                            continue;
                        }

                        result.ConsumedItems.Add(new ItemConsumeUpdate
                        {
                            ItemId = entry.ItemTemplateId,
                            Count = entry.Count,
                            SlotIndex = entry.SlotIndex,
                            RemainingCount = ResolveRemainingCount(
                                lease.Inventory,
                                entry.SlotIndex),
                        });
                    }
                    result.GoldAfter = lease.Inventory.CountMainItem(0);
                    result.Success = true;
                    return result;
                }
                catch (Exception ex)
                {
                    snapshot?.Restore(lease.Inventory);
                    FileLogger.Log(
                        $"[DungeonEntryCost] {plan.Source} ERROR: " +
                        ex.Message);
                    return result.Fail(
                        ex.Message,
                        EntryCostFailureKind.InvalidState);
                }
            }
        }

        private static bool TryResolvePlan(
            InventoryService inventory,
            DungeonEntryCostPlan plan,
            EntryCostResult result,
            out Dictionary<int, int> requiredCounts,
            out Dictionary<int, int> consumedCounts,
            out string failureReason)
        {
            if (!TryNormalizeRequiredItems(
                    plan.RequiredItems,
                    out requiredCounts,
                    out consumedCounts,
                    out failureReason))
            {
                return false;
            }

            if (plan.GoldCost < 0
                || (plan.GoldCost > 0
                    && (!TryAddCount(requiredCounts, 0, plan.GoldCost)
                        || !TryAddCount(consumedCounts, 0, plan.GoldCost))))
            {
                failureReason = "entry gold cost is invalid";
                return false;
            }

            if (!TryFindFirstMissing(
                    inventory,
                    requiredCounts,
                    result))
            {
                failureReason = result.FailReason;
                return false;
            }

            for (var groupIndex = 0;
                 groupIndex < plan.AlternativeGroups.Count;
                 groupIndex++)
            {
                var group = plan.AlternativeGroups[groupIndex];
                var alternatives = group?.Alternatives;
                if (alternatives == null || alternatives.Count == 0)
                {
                    failureReason =
                        $"entry item alternative group {groupIndex} is empty";
                    return false;
                }

                Dictionary<int, int> selectedRequired = null;
                Dictionary<int, int> selectedConsumed = null;
                var selectedIndex = -1;
                var selectedIsFreePass = false;
                for (var optionIndex = 0;
                     optionIndex < alternatives.Count;
                     optionIndex++)
                {
                    var option = alternatives[optionIndex];
                    if (option?.Requirements == null)
                    {
                        failureReason =
                            $"entry item alternative {groupIndex}:" +
                            $"{optionIndex} is missing";
                        return false;
                    }
                    if (!TryNormalizeRequiredItems(
                            option.Requirements,
                            out var optionRequired,
                            out var optionConsumed,
                            out var optionFailure))
                    {
                        failureReason =
                            $"entry item alternative {groupIndex}:" +
                            $"{optionIndex} is invalid: {optionFailure}";
                        return false;
                    }

                    var candidateRequired =
                        new Dictionary<int, int>(requiredCounts);
                    var candidateConsumed =
                        new Dictionary<int, int>(consumedCounts);
                    if (!TryMergeCounts(
                            candidateRequired,
                            optionRequired)
                        || !TryMergeCounts(
                            candidateConsumed,
                            optionConsumed))
                    {
                        failureReason =
                            $"entry item alternative {groupIndex}:" +
                            $"{optionIndex} count overflow";
                        return false;
                    }

                    if (!HasRequiredCounts(inventory, candidateRequired))
                        continue;

                    selectedRequired = candidateRequired;
                    selectedConsumed = candidateConsumed;
                    selectedIndex = optionIndex;
                    selectedIsFreePass = option.IsFreePass;
                    break;
                }

                if (selectedIndex < 0)
                {
                    var missingIndex = group.MissingAlternativeIndex;
                    if (missingIndex < 0 || missingIndex >= alternatives.Count)
                    {
                        failureReason =
                            $"entry item alternative group {groupIndex} " +
                            $"has invalid missing index {missingIndex}";
                        return false;
                    }
                    var primary = alternatives[missingIndex];
                    if (primary?.Requirements == null
                        || !TryNormalizeRequiredItems(
                            primary.Requirements,
                            out var primaryRequired,
                            out _,
                            out failureReason)
                        || !TryMergeCounts(requiredCounts, primaryRequired))
                    {
                        return false;
                    }

                    TryFindFirstMissing(inventory, requiredCounts, result);
                    result.AlternativeIndex = -1;
                    failureReason = result.FailReason;
                    return false;
                }

                requiredCounts = selectedRequired;
                consumedCounts = selectedConsumed;
                result.SelectedAlternativeIndexes.Add(selectedIndex);
                if (groupIndex == 0)
                    result.AlternativeIndex = selectedIndex;
                if (selectedIsFreePass)
                    result.IsFreePass = true;
            }

            failureReason = string.Empty;
            return true;
        }

        private static bool TryFindFirstMissing(
            InventoryService inventory,
            IReadOnlyDictionary<int, int> requiredCounts,
            EntryCostResult result)
        {
            foreach (var requirement in requiredCounts
                         .OrderBy(pair => pair.Key))
            {
                var current = inventory.CountMainItem(requirement.Key);
                if (current >= requirement.Value)
                    continue;

                result.MissingItemId = requirement.Key;
                result.RequiredCount = requirement.Value;
                result.AvailableCount = current;
                result.FailureKind = EntryCostFailureKind.MissingRequiredItem;
                result.FailReason =
                    $"entry item missing item={requirement.Key} " +
                    $"need={requirement.Value} have={current}";
                return false;
            }

            return true;
        }

        private static bool HasRequiredCounts(
            InventoryService inventory,
            IReadOnlyDictionary<int, int> requiredCounts)
        {
            foreach (var requirement in requiredCounts)
            {
                if (inventory.CountMainItem(requirement.Key)
                    < requirement.Value)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryMergeCounts(
            IDictionary<int, int> target,
            IReadOnlyDictionary<int, int> source)
        {
            foreach (var pair in source)
            {
                if (!TryAddCount(target, pair.Key, pair.Value))
                    return false;
            }
            return true;
        }

        private static bool TryNormalizeRequiredItems(
            IReadOnlyList<DungeonEntryItemRequirement> requiredItems,
            out Dictionary<int, int> requiredCounts,
            out Dictionary<int, int> consumedCounts,
            out string failureReason)
        {
            requiredCounts = new Dictionary<int, int>();
            consumedCounts = new Dictionary<int, int>();
            failureReason = string.Empty;
            if (requiredItems == null)
            {
                failureReason = "entry item requirement list is missing";
                return false;
            }
            foreach (var item in requiredItems)
            {
                if (item.ItemId <= 0 || item.Count <= 0)
                {
                    failureReason =
                        $"entry item definition is invalid item={item.ItemId} " +
                        $"count={item.Count}";
                    return false;
                }

                if (!TryAddCount(
                        requiredCounts,
                        item.ItemId,
                        item.Count))
                {
                    failureReason =
                        $"entry item count overflow item={item.ItemId}";
                    return false;
                }

                if (item.ConsumeOnEntry
                    && !TryAddCount(
                        consumedCounts,
                        item.ItemId,
                        item.Count))
                {
                    failureReason =
                        $"entry item consume count overflow item={item.ItemId}";
                    return false;
                }
            }

            return true;
        }

        private static bool TryAddCount(
            IDictionary<int, int> counts,
            int itemId,
            int count)
        {
            var total = (long)(counts.TryGetValue(itemId, out var current)
                ? current
                : 0) + count;
            if (total > int.MaxValue)
                return false;
            counts[itemId] = (int)total;
            return true;
        }

        private static int ResolveRemainingCount(
            InventoryService inventory,
            short slotIndex)
        {
            if (inventory == null)
                return 0;
            if (InventoryService.IsVirtualMainSlot(slotIndex))
                return inventory.GetMainVirtualCount(slotIndex)?.Count ?? 0;
            var item = inventory.GetItem(InventoryListType.Main, slotIndex);
            if (item == null)
                return 0;
            return InventoryStackRuleService.IsStackable(item)
                ? Math.Max(0, item.Count)
                : 1;
        }

        private sealed class EntryItemSnapshot
        {
            private readonly Dictionary<short, ItemCore> _items =
                new Dictionary<short, ItemCore>();
            private readonly Dictionary<short, VirtualCountItem> _virtualItems =
                new Dictionary<short, VirtualCountItem>();

            internal static EntryItemSnapshot Capture(
                InventoryService inventory,
                IEnumerable<int> itemIds)
            {
                var snapshot = new EntryItemSnapshot();
                foreach (var itemId in itemIds.Distinct())
                {
                    if (InventoryService.TryResolveMainVirtualSlotByItemId(
                            itemId,
                            out var virtualSlot,
                            out _))
                    {
                        var virtualItem = inventory.GetMainVirtualCount(
                            virtualSlot);
                        if (virtualItem != null)
                            snapshot._virtualItems[virtualSlot] =
                                virtualItem.Copy();
                        continue;
                    }

                    foreach (var pair in inventory.GetItems(
                                 InventoryListType.Main))
                    {
                        if (pair.Value != null
                            && pair.Value.ItemId == itemId
                            && !snapshot._items.ContainsKey(pair.Key))
                        {
                            snapshot._items[pair.Key] = pair.Value.Copy();
                        }
                    }
                }

                return snapshot;
            }

            internal void Restore(InventoryService inventory)
            {
                if (inventory == null)
                    return;
                foreach (var pair in _items)
                {
                    inventory.SetItem(
                        InventoryListType.Main,
                        pair.Key,
                        pair.Value.Copy());
                }
                foreach (var pair in _virtualItems)
                {
                    inventory.SetMainVirtualCount(
                        pair.Key,
                        pair.Value.ItemId,
                        pair.Value.Count);
                }
            }
        }
    }

    internal sealed class EntryCostResult
    {
        public bool Success;
        public bool IsFreePass;
        public string FailReason;
        public EntryCostFailureKind FailureKind;
        public int MissingItemId;
        public int RequiredCount;
        public int AvailableCount;
        public int AlternativeIndex = -1;
        public int GoldCost;
        public int GoldBefore;
        public int GoldAfter;
        public List<int> SelectedAlternativeIndexes { get; } =
            new List<int>();
        public List<ItemConsumeUpdate> ConsumedItems { get; } = new List<ItemConsumeUpdate>();

        internal EntryCostResult Fail(
            string reason,
            EntryCostFailureKind failureKind)
        {
            FailReason = reason;
            FailureKind = failureKind;
            return this;
        }
    }

    internal enum EntryCostFailureKind : byte
    {
        None = 0,
        InvalidState = 1,
        Unavailable = 2,
        MissingPermission = 3,
        MissingRequiredItem = 4,
    }

    internal sealed class ItemConsumeUpdate
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
        public short SlotIndex { get; set; }
        public int RemainingCount { get; set; }
    }
}
