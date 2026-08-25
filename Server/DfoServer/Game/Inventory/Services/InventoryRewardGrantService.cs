using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;
using PremiumService = DfoServer.Game.Premium.PremiumService;
using ReviveCoinService = DfoServer.Game.ReviveCoin.ReviveCoinService;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryRewardGrantError
    {
        None = 0,
        InvalidInventory = 1,
        InvalidRequest = 2,
        InvalidItem = 3,
        InvalidCount = 4,
        CreateFailed = 5,
        InsertPlanFailed = 6,
        DetailCreateFailed = 7,
        InsertApplyFailed = 8,
        VirtualApplyFailed = 9,
    }

    internal enum InventoryRewardGrantKind
    {
        None = 0,
        InventoryItem = 1,
        MainVirtualCount = 2,
        Premium = 3,
        AccountCurrency = 4,
        EpicPiece = 5,
    }

    internal sealed class InventoryRewardGrantRequest
    {
        public int ItemTemplateId { get; set; }

        public int Count { get; set; } = 1;

        public ItemCreateReason Reason { get; set; } = ItemCreateReason.Unknown;

        public InventoryCreateOptions CreateOptions { get; set; }

        public ItemCore Core { get; set; }

        public bool UseExistingCore { get; set; }

        public static InventoryRewardGrantRequest Create(
            int itemTemplateId,
            int count,
            ItemCreateReason reason,
            InventoryCreateOptions options = null)
        {
            return new InventoryRewardGrantRequest
            {
                ItemTemplateId = itemTemplateId,
                Count = count,
                Reason = reason,
                CreateOptions = options,
                UseExistingCore = false,
            };
        }

        public static InventoryRewardGrantRequest CreateQuestEventItem(
            int itemTemplateId,
            int count,
            ItemCreateReason reason)
        {
            return Create(
                itemTemplateId,
                count,
                reason,
                null);
        }

        public static InventoryRewardGrantRequest Existing(
            ItemCore core,
            int count,
            ItemCreateReason reason = ItemCreateReason.Unknown,
            InventoryCreateOptions options = null)
        {
            return new InventoryRewardGrantRequest
            {
                ItemTemplateId = core != null ? core.ItemId : 0,
                Count = count,
                Reason = reason,
                CreateOptions = options,
                Core = core,
                UseExistingCore = true,
            };
        }
    }

    internal sealed class InventoryRewardGrantResult
    {
        public bool Success { get; set; }

        public InventoryRewardGrantError Error { get; set; }

        public InventoryRewardGrantKind Kind { get; set; }

        public int ItemTemplateId { get; set; }

        public int RequestedCount { get; set; }

        public int GrantedCount { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; } = -1;

        public int FinalCount { get; set; }

        public ItemCore Core { get; set; }

        public InventoryCreateResult CreateResult { get; set; }

        public InventoryInsertPlan InsertPlan { get; set; }

        public InventoryInsertResult InsertResult { get; set; }

        public SpecialRewardOutcome SpecialOutcome { get; set; }

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal sealed class InventoryRewardGrantBatchResult
    {
        private readonly List<InventoryRewardGrantResult> _results = new List<InventoryRewardGrantResult>();
        private readonly List<(int itemTemplateId, int count)> _activatedPremiums =
            new List<(int itemTemplateId, int count)>();

        public bool Success { get; set; }

        public InventoryRewardGrantError Error { get; set; }

        public IReadOnlyList<InventoryRewardGrantResult> Results => _results;

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();

        public IReadOnlyList<(int itemTemplateId, int count)> ActivatedPremiums => _activatedPremiums;

        internal void AddResult(InventoryRewardGrantResult result)
        {
            if (result == null)
                return;

            _results.Add(result);
            Changes.AddRange(result.Changes);
            if (result.SpecialOutcome != null && result.SpecialOutcome.Kind == SpecialRewardKind.Premium)
                _activatedPremiums.Add((result.SpecialOutcome.ItemTemplateId, result.SpecialOutcome.Count));
        }
    }

    internal sealed class InventoryRewardGrantBatchPlan
    {
        private readonly List<InventoryRewardGrantPlanEntry> _entries =
            new List<InventoryRewardGrantPlanEntry>();

        public bool Success { get; set; }

        public InventoryRewardGrantError Error { get; set; }

        public IReadOnlyList<InventoryRewardGrantPlanEntry> Entries => _entries;

        internal void AddEntry(InventoryRewardGrantPlanEntry entry)
        {
            if (entry != null)
                _entries.Add(entry);
        }
    }

    internal sealed class InventoryRewardGrantPlanEntry
    {
        public InventoryRewardGrantRequest Request { get; set; }

        public InventoryRewardGrantKind Kind { get; set; }

        public int ItemTemplateId { get; set; }

        public int RequestedCount { get; set; }

        public int GrantedCount { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; } = -1;

        public int SlotItemId { get; set; }

        public int FinalCount { get; set; }

        public ItemCore Core { get; set; }

        public InventoryInsertPlan InsertPlan { get; set; }

        public InventoryCreateResult CreateResult { get; set; }

        public SpecialRewardOutcome SpecialOutcome { get; set; }
    }

    internal static class InventoryRewardGrantService
    {
        internal static bool TryCreateOnly(
            int itemTemplateId,
            ItemCreateReason reason,
            int count,
            out InventoryRewardGrantResult result)
        {
            return TryCreateOnly(itemTemplateId, reason, count, null, out result);
        }

        internal static bool TryCreateOnly(
            int itemTemplateId,
            ItemCreateReason reason,
            int count,
            InventoryCreateOptions options,
            out InventoryRewardGrantResult result)
        {
            var request = InventoryRewardGrantRequest.Create(itemTemplateId, count, reason, options);
            result = CreateResult(request);
            if (!TryNormalizeRequest(request, out var normalizedItemId, out var normalizedCount, out var error))
                return Fail(result, error);

            if (TryCreateSpecialOnlyResult(normalizedItemId, normalizedCount, result))
                return true;

            if (!ItemMetadataResolver.TryResolveItemKind(normalizedItemId, out var itemKind))
                return Fail(result, InventoryRewardGrantError.CreateFailed);

            var core = InventoryCreateService.CreateCore(itemKind, normalizedItemId, reason, normalizedCount, options);
            result.Success = true;
            result.Error = InventoryRewardGrantError.None;
            result.Kind = InventoryRewardGrantKind.InventoryItem;
            result.Core = core;
            result.GrantedCount = InventoryStackRuleService.NormalizeInsertCount(core, normalizedCount);
            return true;
        }

        internal static bool TryCreateAndInsert(
            InventoryLease lease,
            int itemTemplateId,
            ItemCreateReason reason,
            int count,
            out InventoryRewardGrantResult result)
        {
            return TryCreateAndInsert(lease, itemTemplateId, reason, count, null, out result);
        }

        internal static bool TryCreateAndInsert(
            InventoryLease lease,
            int itemTemplateId,
            ItemCreateReason reason,
            int count,
            InventoryCreateOptions options,
            out InventoryRewardGrantResult result)
        {
            return TryGrant(lease, InventoryRewardGrantRequest.Create(itemTemplateId, count, reason, options), out result);
        }

        internal static bool TryCreateAndInsert(
            InventoryService inventory,
            int itemTemplateId,
            ItemCreateReason reason,
            int count,
            out InventoryRewardGrantResult result)
        {
            return TryCreateAndInsert(inventory, itemTemplateId, reason, count, null, out result);
        }

        internal static bool TryCreateAndInsert(
            InventoryService inventory,
            int itemTemplateId,
            ItemCreateReason reason,
            int count,
            InventoryCreateOptions options,
            out InventoryRewardGrantResult result)
        {
            return TryGrant(inventory, InventoryRewardGrantRequest.Create(itemTemplateId, count, reason, options), out result);
        }

        internal static bool TryInsertExisting(
            InventoryLease lease,
            ItemCore core,
            int count,
            out InventoryRewardGrantResult result)
        {
            return TryInsertExisting(lease, core, count, ItemCreateReason.Unknown, null, out result);
        }

        internal static bool TryInsertExisting(
            InventoryLease lease,
            ItemCore core,
            int count,
            ItemCreateReason reason,
            InventoryCreateOptions options,
            out InventoryRewardGrantResult result)
        {
            return TryGrant(lease, InventoryRewardGrantRequest.Existing(core, count, reason, options), out result);
        }

        internal static bool TryInsertExisting(
            InventoryService inventory,
            ItemCore core,
            int count,
            out InventoryRewardGrantResult result)
        {
            return TryInsertExisting(inventory, core, count, ItemCreateReason.Unknown, null, out result);
        }

        internal static bool TryInsertExisting(
            InventoryService inventory,
            ItemCore core,
            int count,
            ItemCreateReason reason,
            InventoryCreateOptions options,
            out InventoryRewardGrantResult result)
        {
            return TryGrant(inventory, InventoryRewardGrantRequest.Existing(core, count, reason, options), out result);
        }

        internal static bool TryGrant(
            InventoryLease lease,
            InventoryRewardGrantRequest request,
            out InventoryRewardGrantResult result)
        {
            result = null;
            if (lease == null)
            {
                result = CreateResult(request);
                return Fail(result, InventoryRewardGrantError.InvalidInventory);
            }

            lock (lease.SyncRoot)
                return TryGrant(lease.Inventory, request, out result);
        }

        internal static bool TryGrant(
            InventoryService inventory,
            InventoryRewardGrantRequest request,
            out InventoryRewardGrantResult result)
        {
            var requests = new List<InventoryRewardGrantRequest> { request };
            if (!TryGrantBatch(inventory, requests, out var batchResult))
            {
                result = batchResult.Results.Count > 0
                    ? batchResult.Results[0]
                    : CreateResult(request);
                result.Error = batchResult.Error;
                return false;
            }

            result = batchResult.Results.Count > 0 ? batchResult.Results[0] : CreateResult(request);
            return result.Success;
        }

        internal static bool TryGrantBatch(
            InventoryLease lease,
            IReadOnlyList<InventoryRewardGrantRequest> requests,
            out InventoryRewardGrantBatchResult result)
        {
            if (lease == null)
            {
                result = CreateBatchResult(InventoryRewardGrantError.InvalidInventory);
                return false;
            }

            lock (lease.SyncRoot)
                return TryGrantBatch(lease.Inventory, requests, out result);
        }

        internal static bool TryGrantBatch(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> requests,
            out InventoryRewardGrantBatchResult result)
        {
            result = CreateBatchResult(InventoryRewardGrantError.None);
            if (!TryPlanBatch(inventory, requests, out var plan))
            {
                result.Error = plan != null ? plan.Error : InventoryRewardGrantError.InvalidRequest;
                return false;
            }

            if (!TryPrepareDetails(inventory, plan, out var detailError))
            {
                result.Error = detailError;
                return false;
            }

            foreach (var entry in plan.Entries)
            {
                if (!TryApplyPlanEntry(inventory, entry, out var entryResult))
                {
                    result.AddResult(entryResult);
                    result.Error = entryResult.Error;
                    return false;
                }

                result.AddResult(entryResult);
            }

            result.Success = true;
            result.Error = InventoryRewardGrantError.None;
            return true;
        }

        internal static bool TryApplyPreparedBatch(
            InventoryService inventory,
            InventoryRewardGrantBatchPlan plan,
            out InventoryRewardGrantBatchResult result)
        {
            result = CreateBatchResult(InventoryRewardGrantError.None);
            if (inventory == null)
            {
                result.Error = InventoryRewardGrantError.InvalidInventory;
                return false;
            }

            if (plan == null || !plan.Success)
            {
                result.Error = plan != null ? plan.Error : InventoryRewardGrantError.InvalidRequest;
                return false;
            }

            if (!TryPrepareDetails(inventory, plan, out var detailError))
            {
                result.Error = detailError;
                return false;
            }

            foreach (var entry in plan.Entries)
            {
                if (!TryApplyPlanEntry(inventory, entry, out var entryResult))
                {
                    result.AddResult(entryResult);
                    result.Error = entryResult.Error;
                    return false;
                }

                result.AddResult(entryResult);
            }

            result.Success = true;
            result.Error = InventoryRewardGrantError.None;
            return true;
        }

        internal static bool TryPlanBatch(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> requests,
            out InventoryRewardGrantBatchPlan plan)
        {
            plan = CreatePlan(InventoryRewardGrantError.None);
            if (inventory == null)
                return Fail(plan, InventoryRewardGrantError.InvalidInventory);
            if (requests == null)
                return Fail(plan, InventoryRewardGrantError.InvalidRequest);
            if (requests.Count == 0)
            {
                plan.Success = true;
                return true;
            }

            var planningInventory = CreatePlanningInventory(inventory);
            for (var index = 0; index < requests.Count; index++)
            {
                if (!TryPlanOne(planningInventory, requests[index], out var entry, out var error))
                    return Fail(plan, error);

                plan.AddEntry(entry);
            }

            plan.Success = true;
            return true;
        }

        private static bool TryPlanOne(
            InventoryService planningInventory,
            InventoryRewardGrantRequest request,
            out InventoryRewardGrantPlanEntry entry,
            out InventoryRewardGrantError error)
        {
            entry = null;
            if (!TryNormalizeRequest(request, out var itemTemplateId, out var count, out error))
                return false;

            if (TryPlanPremium(itemTemplateId, count, request, out entry))
                return true;

            if (SpecialRewardRouter.TryResolveAccountCurrencyReward(itemTemplateId, count, out var accountCurrencyOutcome))
                return TryPlanAccountCurrency(
                    planningInventory,
                    request,
                    accountCurrencyOutcome,
                    out entry,
                    out error);

            if (EpicPieceCatalogService.IsEpicPieceId(itemTemplateId))
                return TryPlanEpicPiece(
                    planningInventory,
                    request,
                    itemTemplateId,
                    count,
                    out entry,
                    out error);

            if (TryResolveMainVirtualReward(itemTemplateId, out var slotIndex, out var slotItemId))
                return TryPlanMainVirtualCount(planningInventory, request, itemTemplateId, count, slotIndex, slotItemId, out entry, out error);

            var core = request.UseExistingCore
                ? request.Core.Copy()
                : CreateCore(itemTemplateId, count, request);
            if (core == null)
            {
                error = InventoryRewardGrantError.CreateFailed;
                return false;
            }

            var insertCount = InventoryStackRuleService.NormalizeInsertCount(core, count);
            if (!InventoryInsertService.TryPlanInsertByDefaultRule(planningInventory, core, insertCount, out var insertPlan))
            {
                LogInventoryInsertFailure(
                    planningInventory,
                    itemTemplateId,
                    core,
                    count,
                    insertCount,
                    insertPlan != null ? insertPlan.Error : InventoryInsertError.InvalidTargetList,
                    "plan");
                error = InventoryRewardGrantError.InsertPlanFailed;
                return false;
            }

            if (!InventoryInsertService.TryApplyInsertPlan(planningInventory, core, insertPlan, out var reserveResult)
                || !reserveResult.Success)
            {
                LogInventoryInsertFailure(
                    planningInventory,
                    itemTemplateId,
                    core,
                    count,
                    insertCount,
                    reserveResult != null ? reserveResult.Error : InventoryInsertError.UpdateFailed,
                    "reserve");
                error = InventoryRewardGrantError.InsertPlanFailed;
                return false;
            }

            entry = new InventoryRewardGrantPlanEntry
            {
                Request = request,
                Kind = InventoryRewardGrantKind.InventoryItem,
                ItemTemplateId = itemTemplateId,
                RequestedCount = count,
                GrantedCount = insertPlan.InsertedCount,
                ListType = insertPlan.ListType,
                SlotIndex = insertPlan.SlotIndex,
                Core = core.Copy(),
                InsertPlan = insertPlan,
            };
            error = InventoryRewardGrantError.None;
            return true;
        }

        private static bool TryPrepareDetails(
            InventoryService inventory,
            InventoryRewardGrantBatchPlan plan,
            out InventoryRewardGrantError error)
        {
            error = InventoryRewardGrantError.None;
            if (inventory == null || plan == null)
            {
                error = InventoryRewardGrantError.InvalidInventory;
                return false;
            }

            var createdResults = new List<InventoryCreateResult>();
            foreach (var entry in plan.Entries)
            {
                if (entry.Kind != InventoryRewardGrantKind.InventoryItem)
                    continue;

                var core = entry.Core != null ? entry.Core.Copy() : null;
                if (!InventoryCreateService.TryCreateDetails(
                        inventory,
                        core,
                        entry.Request != null ? entry.Request.Reason : ItemCreateReason.Unknown,
                        entry.Request != null ? entry.Request.CreateOptions : null,
                        out var createResult))
                {
                    foreach (var created in createdResults)
                        InventoryCreateService.DetachCreatedDetails(inventory, created);

                    error = InventoryRewardGrantError.DetailCreateFailed;
                    return false;
                }

                entry.Core = core;
                entry.CreateResult = createResult;
                createdResults.Add(createResult);
            }

            return true;
        }

        private static bool TryApplyPlanEntry(
            InventoryService inventory,
            InventoryRewardGrantPlanEntry entry,
            out InventoryRewardGrantResult result)
        {
            result = CreateResult(entry);
            switch (entry.Kind)
            {
                case InventoryRewardGrantKind.Premium:
                    return Complete(result);
                case InventoryRewardGrantKind.AccountCurrency:
                    return TryApplyAccountCurrency(inventory, entry, result);
                case InventoryRewardGrantKind.EpicPiece:
                    return TryApplyEpicPiece(inventory, entry, result);
                case InventoryRewardGrantKind.MainVirtualCount:
                    return TryApplyMainVirtualCount(inventory, entry, result);
                case InventoryRewardGrantKind.InventoryItem:
                    return TryApplyInventoryItem(inventory, entry, result);
                default:
                    return Fail(result, InventoryRewardGrantError.InvalidRequest);
            }
        }

        private static bool TryApplyAccountCurrency(
            InventoryService inventory,
            InventoryRewardGrantPlanEntry entry,
            InventoryRewardGrantResult result)
        {
            if (inventory == null)
                return Fail(result, InventoryRewardGrantError.InvalidInventory);
            if (entry?.SpecialOutcome == null
                || entry.SpecialOutcome.Kind != SpecialRewardKind.HappyTokenCera
                || !inventory.TryQueueHappyTokenCeraGrant(entry.GrantedCount))
                return Fail(result, InventoryRewardGrantError.VirtualApplyFailed);

            return Complete(result);
        }

        private static bool TryApplyEpicPiece(
            InventoryService inventory,
            InventoryRewardGrantPlanEntry entry,
            InventoryRewardGrantResult result)
        {
            if (inventory == null)
                return Fail(result, InventoryRewardGrantError.InvalidInventory);

            if (entry == null
                || !inventory.EpicPieces.TryAddByPieceId(
                    entry.ItemTemplateId,
                    entry.GrantedCount,
                    out var finalCount))
            {
                return Fail(result, InventoryRewardGrantError.VirtualApplyFailed);
            }

            result.FinalCount = finalCount;
            if (result.SpecialOutcome != null
                && result.SpecialOutcome.Kind == SpecialRewardKind.EpicPiece)
            {
                result.SpecialOutcome.WalletNewTotal = finalCount;
            }

            return Complete(result);
        }

        private static bool TryApplyMainVirtualCount(
            InventoryService inventory,
            InventoryRewardGrantPlanEntry entry,
            InventoryRewardGrantResult result)
        {
            if (inventory == null)
                return Fail(result, InventoryRewardGrantError.InvalidInventory);

            var current = inventory.GetMainVirtualCount(entry.SlotIndex);
            var finalCount = AddCount(current != null ? current.Count : 0, entry.GrantedCount);
            if (!inventory.SetMainVirtualCount(entry.SlotIndex, entry.SlotItemId, finalCount))
                return Fail(result, InventoryRewardGrantError.VirtualApplyFailed);

            result.FinalCount = finalCount;
            result.Changes.AddSlot(InventoryListType.Main, entry.SlotIndex);
            if (result.SpecialOutcome != null && result.SpecialOutcome.Kind == SpecialRewardKind.ReviveCoin)
                result.SpecialOutcome.WalletNewTotal = finalCount;

            return Complete(result);
        }

        private static bool TryApplyInventoryItem(
            InventoryService inventory,
            InventoryRewardGrantPlanEntry entry,
            InventoryRewardGrantResult result)
        {
            if (inventory == null)
                return Fail(result, InventoryRewardGrantError.InvalidInventory);

            if (!InventoryInsertService.TryApplyInsertPlan(inventory, entry.Core, entry.InsertPlan, out var insertResult)
                || !insertResult.Success)
            {
                result.InsertResult = insertResult;
                InventoryCreateService.DetachCreatedDetails(inventory, entry.CreateResult);
                return Fail(result, InventoryRewardGrantError.InsertApplyFailed);
            }

            result.Core = entry.Core;
            result.CreateResult = entry.CreateResult;
            result.InsertResult = insertResult;
            result.InsertPlan = entry.InsertPlan;
            result.ListType = insertResult.ListType;
            result.SlotIndex = insertResult.SlotIndex;
            result.GrantedCount = insertResult.InsertedCount;
            result.Changes.AddRange(insertResult.Changes);
            return Complete(result);
        }

        private static bool TryPlanPremium(
            int itemTemplateId,
            int count,
            InventoryRewardGrantRequest request,
            out InventoryRewardGrantPlanEntry entry)
        {
            entry = null;
            if (!PremiumService.IsContractItem(itemTemplateId))
                return false;

            var outcome = new SpecialRewardOutcome
            {
                Kind = SpecialRewardKind.Premium,
                ItemTemplateId = itemTemplateId,
                Count = count,
            };

            entry = new InventoryRewardGrantPlanEntry
            {
                Request = request,
                Kind = InventoryRewardGrantKind.Premium,
                ItemTemplateId = itemTemplateId,
                RequestedCount = count,
                GrantedCount = count,
                SpecialOutcome = outcome,
            };
            return true;
        }

        private static bool TryPlanAccountCurrency(
            InventoryService planningInventory,
            InventoryRewardGrantRequest request,
            SpecialRewardOutcome outcome,
            out InventoryRewardGrantPlanEntry entry,
            out InventoryRewardGrantError error)
        {
            entry = null;
            error = InventoryRewardGrantError.None;
            if (planningInventory == null || outcome == null || outcome.Count <= 0)
            {
                error = InventoryRewardGrantError.InvalidRequest;
                return false;
            }

            if (!planningInventory.TryQueueHappyTokenCeraGrant(outcome.Count))
            {
                error = InventoryRewardGrantError.VirtualApplyFailed;
                return false;
            }

            entry = new InventoryRewardGrantPlanEntry
            {
                Request = request,
                Kind = InventoryRewardGrantKind.AccountCurrency,
                ItemTemplateId = outcome.ItemTemplateId,
                RequestedCount = outcome.Count,
                GrantedCount = outcome.Count,
                SpecialOutcome = outcome,
            };
            return true;
        }

        private static bool TryPlanEpicPiece(
            InventoryService planningInventory,
            InventoryRewardGrantRequest request,
            int itemTemplateId,
            int count,
            out InventoryRewardGrantPlanEntry entry,
            out InventoryRewardGrantError error)
        {
            entry = null;
            if (planningInventory == null)
            {
                error = InventoryRewardGrantError.InvalidInventory;
                return false;
            }

            if (!planningInventory.EpicPieces.TryAddByPieceId(itemTemplateId, count, out var finalCount))
            {
                error = InventoryRewardGrantError.VirtualApplyFailed;
                return false;
            }

            entry = new InventoryRewardGrantPlanEntry
            {
                Request = request,
                Kind = InventoryRewardGrantKind.EpicPiece,
                ItemTemplateId = itemTemplateId,
                RequestedCount = count,
                GrantedCount = count,
                FinalCount = finalCount,
                SpecialOutcome = new SpecialRewardOutcome
                {
                    Kind = SpecialRewardKind.EpicPiece,
                    ItemTemplateId = itemTemplateId,
                    Count = count,
                    WalletNewTotal = finalCount,
                },
            };
            error = InventoryRewardGrantError.None;
            return true;
        }

        private static bool TryPlanMainVirtualCount(
            InventoryService planningInventory,
            InventoryRewardGrantRequest request,
            int itemTemplateId,
            int count,
            short slotIndex,
            int slotItemId,
            out InventoryRewardGrantPlanEntry entry,
            out InventoryRewardGrantError error)
        {
            entry = null;
            if (planningInventory == null)
            {
                error = InventoryRewardGrantError.InvalidInventory;
                return false;
            }

            var current = planningInventory.GetMainVirtualCount(slotIndex);
            var finalCount = AddCount(current != null ? current.Count : 0, count);
            if (!planningInventory.SetMainVirtualCount(slotIndex, slotItemId, finalCount))
            {
                error = InventoryRewardGrantError.VirtualApplyFailed;
                return false;
            }

            entry = new InventoryRewardGrantPlanEntry
            {
                Request = request,
                Kind = InventoryRewardGrantKind.MainVirtualCount,
                ItemTemplateId = itemTemplateId,
                RequestedCount = count,
                GrantedCount = count,
                ListType = InventoryListType.Main,
                SlotIndex = slotIndex,
                SlotItemId = slotItemId,
                FinalCount = finalCount,
                SpecialOutcome = CreateVirtualSpecialOutcome(itemTemplateId, count, slotIndex, finalCount),
            };
            error = InventoryRewardGrantError.None;
            return true;
        }

        private static bool TryCreateSpecialOnlyResult(
            int itemTemplateId,
            int count,
            InventoryRewardGrantResult result)
        {
            if (PremiumService.IsContractItem(itemTemplateId))
            {
                result.Success = true;
                result.Error = InventoryRewardGrantError.None;
                result.Kind = InventoryRewardGrantKind.Premium;
                result.GrantedCount = count;
                result.SpecialOutcome = new SpecialRewardOutcome
                {
                    Kind = SpecialRewardKind.Premium,
                    ItemTemplateId = itemTemplateId,
                    Count = count,
                };
                return true;
            }

            if (SpecialRewardRouter.TryResolveAccountCurrencyReward(itemTemplateId, count, out var accountCurrencyOutcome))
            {
                result.Success = true;
                result.Error = InventoryRewardGrantError.None;
                result.Kind = InventoryRewardGrantKind.AccountCurrency;
                result.GrantedCount = count;
                result.SpecialOutcome = accountCurrencyOutcome;
                return true;
            }

            if (EpicPieceCatalogService.IsEpicPieceId(itemTemplateId))
            {
                result.Success = true;
                result.Error = InventoryRewardGrantError.None;
                result.Kind = InventoryRewardGrantKind.EpicPiece;
                result.ItemTemplateId = itemTemplateId;
                result.GrantedCount = count;
                result.SpecialOutcome = new SpecialRewardOutcome
                {
                    Kind = SpecialRewardKind.EpicPiece,
                    ItemTemplateId = itemTemplateId,
                    Count = count,
                };
                return true;
            }

            if (!TryResolveMainVirtualReward(itemTemplateId, out var slotIndex, out var slotItemId))
                return false;

            result.Success = true;
            result.Error = InventoryRewardGrantError.None;
            result.Kind = InventoryRewardGrantKind.MainVirtualCount;
            result.ListType = InventoryListType.Main;
            result.SlotIndex = slotIndex;
            result.ItemTemplateId = itemTemplateId;
            result.GrantedCount = count;
            result.SpecialOutcome = CreateVirtualSpecialOutcome(itemTemplateId, count, slotIndex, 0);
            return true;
        }

        private static ItemCore CreateCore(
            int itemTemplateId,
            int count,
            InventoryRewardGrantRequest request)
        {
            if (!ItemMetadataResolver.TryResolveItemKind(itemTemplateId, out var itemKind))
                return null;

            return InventoryCreateService.CreateCore(
                itemKind,
                itemTemplateId,
                request != null ? request.Reason : ItemCreateReason.Unknown,
                count,
                request != null ? request.CreateOptions : null);
        }

        private static InventoryService CreatePlanningInventory(InventoryService source)
        {
            var inventory = new InventoryService(
                source.CharacterId,
                source.AccountId,
                source.Database);
            CopyListParam(source, inventory, InventoryListType.Main);
            CopyListParam(source, inventory, InventoryListType.Equipment);
            CopyListParam(source, inventory, InventoryListType.Avatar);
            CopyListParam(source, inventory, InventoryListType.Pet);
            CopyListParam(source, inventory, InventoryListType.PersonalCargo);
            CopyListParam(source, inventory, InventoryListType.AccountCargo);
            CopyListParam(source, inventory, InventoryListType.GuildMedal);

            CopyItems(source, inventory, InventoryListType.Main);
            CopyItems(source, inventory, InventoryListType.Equipment);
            CopyItems(source, inventory, InventoryListType.Avatar);
            CopyItems(source, inventory, InventoryListType.Pet);
            CopyItems(source, inventory, InventoryListType.PersonalCargo);
            CopyItems(source, inventory, InventoryListType.AccountCargo);
            CopyItems(source, inventory, InventoryListType.GuildMedal);

            foreach (var item in source.GetMainVirtualCounts())
                inventory.AttachMainVirtualCount(item.SlotIndex, item.ItemId, item.Count);
            inventory.EpicPieces.CopyFrom(source.EpicPieces);

            inventory.ClearDirtyState();
            if (source.PendingHappyTokenCeraGrant > 0)
                inventory.TryQueueHappyTokenCeraGrant(source.PendingHappyTokenCeraGrant);
            return inventory;
        }

        private static void CopyListParam(
            InventoryService source,
            InventoryService target,
            InventoryListType listType)
        {
            target.SetListParam16(listType, source.GetListParam16(listType));
        }

        private static void CopyItems(
            InventoryService source,
            InventoryService target,
            InventoryListType listType)
        {
            foreach (var pair in source.GetItems(listType))
                target.AttachItem(listType, pair.Key, pair.Value.Copy());
        }

        private static bool TryNormalizeRequest(
            InventoryRewardGrantRequest request,
            out int itemTemplateId,
            out int count,
            out InventoryRewardGrantError error)
        {
            itemTemplateId = 0;
            count = 0;
            error = InventoryRewardGrantError.None;
            if (request == null)
            {
                error = InventoryRewardGrantError.InvalidRequest;
                return false;
            }

            if (request.UseExistingCore)
            {
                if (request.Core == null || request.Core.IsEmpty || request.Core.ItemId <= 0)
                {
                    error = InventoryRewardGrantError.InvalidItem;
                    return false;
                }

                itemTemplateId = request.Core.ItemId;
            }
            else
            {
                itemTemplateId = request.ItemTemplateId;
            }

            if (itemTemplateId < 0)
            {
                error = InventoryRewardGrantError.InvalidItem;
                return false;
            }

            count = request.Count;
            if (count <= 0 && request.UseExistingCore && request.Core != null)
                count = InventoryStackRuleService.NormalizeInsertCount(request.Core, count);
            if (count <= 0)
            {
                error = InventoryRewardGrantError.InvalidCount;
                return false;
            }

            return true;
        }

        private static void LogInventoryInsertFailure(
            InventoryService inventory,
            int itemTemplateId,
            ItemCore core,
            int requestedCount,
            int insertCount,
            InventoryInsertError insertError,
            string stage)
        {
            try
            {
                var itemKind = core != null ? core.ItemKind : ItemCore.KindUnknown;
                var listText = "n/a";
                var rangeText = "n/a";
                var freeText = "n/a";
                var listParamText = "n/a";

                if (inventory != null
                    && ItemSlotBoundService.TryGetSlotRange(
                        itemKind,
                        inventory.GetListParam16(InventoryListType.Main),
                        out var listType,
                        out var range))
                {
                    listText = $"{(byte)listType}(0x{(byte)listType:X2})";
                    rangeText = $"{range.Start}-{range.End}";
                    freeText = CountFreeSlots(inventory, listType, range).ToString();
                    listParamText = inventory.GetListParam16(listType).ToString();
                }

                FileLogger.Log(
                    $"[InventoryReward] insert failed stage={stage} " +
                    $"item=0x{itemTemplateId:X8} kind={itemKind} requested={requestedCount} " +
                    $"insertCount={insertCount} error={insertError} list={listText} " +
                    $"range={rangeText} free={freeText} listParam16={listParamText}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryReward] insert failure logging failed: {ex.Message}");
            }
        }

        private static int CountFreeSlots(
            InventoryService inventory,
            InventoryListType listType,
            ItemSlotRange range)
        {
            if (inventory == null || range.Count <= 0)
                return 0;

            var free = 0;
            for (var slot = range.Start; slot <= range.End; slot++)
            {
                if (inventory.GetItem(listType, (short)slot) == null)
                    free++;
            }

            return free;
        }

        private static bool TryResolveMainVirtualReward(int itemTemplateId, out short slotIndex, out int slotItemId)
        {
            slotIndex = -1;
            slotItemId = 0;

            if (itemTemplateId == 0 || itemTemplateId == 2)
            {
                slotIndex = (short)itemTemplateId;
                slotItemId = itemTemplateId;
                return true;
            }

            if (ReviveCoinService.IsReviveCoinReward(itemTemplateId))
            {
                slotIndex = ReviveCoinService.WalletSlot;
                slotItemId = ReviveCoinService.ItemId;
                return true;
            }

            if (InventoryService.TryResolveMainVirtualSlotByItemId(itemTemplateId, out slotIndex, out slotItemId))
                return true;

            return false;
        }

        private static SpecialRewardOutcome CreateVirtualSpecialOutcome(
            int itemTemplateId,
            int count,
            short slotIndex,
            int finalCount)
        {
            if (!ReviveCoinService.IsReviveCoinReward(itemTemplateId))
                return null;

            return new SpecialRewardOutcome
            {
                Kind = SpecialRewardKind.ReviveCoin,
                ItemTemplateId = itemTemplateId,
                Count = count,
                WalletSlot = slotIndex,
                WalletNewTotal = finalCount,
            };
        }

        private static int AddCount(int current, int count)
        {
            var value = (long)Math.Max(0, current) + Math.Max(0, count);
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static InventoryRewardGrantBatchPlan CreatePlan(InventoryRewardGrantError error)
        {
            return new InventoryRewardGrantBatchPlan
            {
                Success = false,
                Error = error,
            };
        }

        private static InventoryRewardGrantBatchResult CreateBatchResult(InventoryRewardGrantError error)
        {
            return new InventoryRewardGrantBatchResult
            {
                Success = false,
                Error = error,
            };
        }

        private static InventoryRewardGrantResult CreateResult(InventoryRewardGrantRequest request)
        {
            return new InventoryRewardGrantResult
            {
                Success = false,
                Error = InventoryRewardGrantError.None,
                ItemTemplateId = request != null && request.UseExistingCore && request.Core != null
                    ? request.Core.ItemId
                    : request != null
                        ? request.ItemTemplateId
                        : 0,
                RequestedCount = request != null ? request.Count : 0,
            };
        }

        private static InventoryRewardGrantResult CreateResult(InventoryRewardGrantPlanEntry entry)
        {
            var result = new InventoryRewardGrantResult
            {
                Success = false,
                Error = InventoryRewardGrantError.None,
                Kind = entry != null ? entry.Kind : InventoryRewardGrantKind.None,
                ItemTemplateId = entry != null ? entry.ItemTemplateId : 0,
                RequestedCount = entry != null ? entry.RequestedCount : 0,
                GrantedCount = entry != null ? entry.GrantedCount : 0,
                ListType = entry != null ? entry.ListType : default,
                SlotIndex = entry != null ? entry.SlotIndex : (short)-1,
                FinalCount = entry != null ? entry.FinalCount : 0,
                Core = entry != null ? entry.Core : null,
                InsertPlan = entry != null ? entry.InsertPlan : null,
                CreateResult = entry != null ? entry.CreateResult : null,
                SpecialOutcome = entry != null ? entry.SpecialOutcome : null,
            };
            return result;
        }

        private static bool Complete(InventoryRewardGrantResult result)
        {
            result.Success = true;
            result.Error = InventoryRewardGrantError.None;
            return true;
        }

        private static bool Fail(InventoryRewardGrantBatchPlan plan, InventoryRewardGrantError error)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            plan.Success = false;
            plan.Error = error;
            return false;
        }

        private static bool Fail(InventoryRewardGrantResult result, InventoryRewardGrantError error)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            result.Success = false;
            result.Error = error;
            return false;
        }
    }
}
