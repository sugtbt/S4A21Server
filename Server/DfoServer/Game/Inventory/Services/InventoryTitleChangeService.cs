using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryTitleChangeError
    {
        None = 0,
        InvalidInventory = 1,
        InvalidRequest = 2,
        SourceNotFound = 3,
        SourceItemMismatch = 4,
        TargetNotFound = 5,
        TargetItemMismatch = 6,
        SourceNotStackable = 7,
        TargetNotTitle = 8,
        ResultNotTitle = 9,
        RuleNotFound = 10,
        InsufficientMaterials = 11,
        ConsumeFailed = 12,
        UpdateFailed = 13,
        TargetLocked = 14,
    }

    internal sealed class InventoryTitleChangeRequest
    {
        public short SourceSlotIndex { get; set; }

        public short TargetSlotIndex { get; set; }

        public int SourceItemId { get; set; }

        public int TargetItemId { get; set; }
    }

    internal sealed class InventoryTitleChangeResult
    {
        public bool Success { get; set; }

        public InventoryTitleChangeError Error { get; set; }

        public int SourceItemId { get; set; }

        public int TargetItemId { get; set; }

        public int ResultItemId { get; set; }

        public short ResultValue { get; set; }

        public byte ResultItemKind { get; set; }

        public bool IsSuccessBranch { get; set; }

        public int SourceRemainingCount { get; set; }
    }

    internal static class InventoryTitleChangeService
    {
        internal static bool TryChange(
            InventoryService inventory,
            InventoryTitleChangeRequest request,
            InventoryTitleChangeResolution resolution,
            out InventoryTitleChangeResult result)
        {
            result = CreateResult(request);
            if (inventory == null)
                return Fail(result, InventoryTitleChangeError.InvalidInventory);
            if (request == null
                || request.SourceSlotIndex < 0
                || request.TargetSlotIndex < 0
                || request.SourceSlotIndex == request.TargetSlotIndex
                || request.SourceItemId <= 0
                || request.TargetItemId <= 0)
            {
                return Fail(result, InventoryTitleChangeError.InvalidRequest);
            }

            if (resolution == null
                || resolution.SourceItemId != request.SourceItemId
                || resolution.TargetItemId != request.TargetItemId
                || resolution.ResultItemId <= 0
                || resolution.ResultValue <= 0)
            {
                return Fail(result, InventoryTitleChangeError.RuleNotFound);
            }

            var source = inventory.GetItem(InventoryListType.Main, request.SourceSlotIndex);
            if (source == null || source.ItemId <= 0)
                return Fail(result, InventoryTitleChangeError.SourceNotFound);
            if (source.ItemId != request.SourceItemId)
                return Fail(result, InventoryTitleChangeError.SourceItemMismatch);
            if (!InventoryStackRuleService.IsStackable(source) || source.Count < 1)
                return Fail(result, InventoryTitleChangeError.SourceNotStackable);

            var target = inventory.GetItem(InventoryListType.Main, request.TargetSlotIndex);
            if (target == null || target.ItemId <= 0)
                return Fail(result, InventoryTitleChangeError.TargetNotFound);
            if (target.ItemId != request.TargetItemId)
                return Fail(result, InventoryTitleChangeError.TargetItemMismatch);
            if ((!resolution.IsLimitedCube
                    && target.ItemKind != ItemCore.KindEquipment)
                || (!resolution.IsLimitedCube
                    && !ItemMetadataResolver.IsTitleEquipment(target.ItemId)))
            {
                return Fail(result, InventoryTitleChangeError.TargetNotTitle);
            }
            if (InventoryLockService.IsEquipmentItemLocked(inventory, target))
                return Fail(result, InventoryTitleChangeError.TargetLocked);
            if (!resolution.IsLimitedCube
                && !ItemMetadataResolver.IsTitleEquipment(resolution.ResultItemId))
                return Fail(result, InventoryTitleChangeError.ResultNotTitle);

            var additionalMaterials = resolution.AdditionalMaterials
                ?? Array.Empty<InventoryMaterialRequirement>();
            var allConsumables = new List<InventoryMaterialRequirement>
            {
                new InventoryMaterialRequirement(request.SourceItemId, 1),
            };
            allConsumables.AddRange(additionalMaterials);
            if (!InventoryMaterialConsumptionService.HasEnough(inventory, allConsumables))
                return Fail(result, InventoryTitleChangeError.InsufficientMaterials);

            var rollback = InventoryTitleChangeRollback.Capture(
                inventory,
                request.SourceSlotIndex,
                request.TargetSlotIndex,
                additionalMaterials);
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    request.SourceSlotIndex,
                    request.SourceItemId,
                    1,
                    out var sourceConsume)
                || sourceConsume == null
                || !sourceConsume.Success)
            {
                rollback.Restore(inventory);
                return Fail(result, InventoryTitleChangeError.ConsumeFailed);
            }

            if (additionalMaterials.Count > 0
                && !InventoryMaterialConsumptionService.TryConsume(
                    inventory,
                    additionalMaterials,
                    consumed: null))
            {
                rollback.Restore(inventory);
                return Fail(result, InventoryTitleChangeError.ConsumeFailed);
            }

            var updatedTarget = target.Copy();
            updatedTarget.ItemId = resolution.ResultItemId;
            InventoryRewardGrantResult resultGrant = null;
            if (ShouldSplitStackedTarget(resolution, target))
            {
                // A stacked orb change consumes one orb, not the whole stack.
                var remainingTarget = target.Copy();
                remainingTarget.Count--;
                if (!inventory.SetItem(
                        InventoryListType.Main,
                        request.TargetSlotIndex,
                        remainingTarget)
                    || !InventoryRewardGrantService.TryInsertExisting(
                        inventory,
                        updatedTarget,
                        1,
                        ItemCreateReason.Unknown,
                        null,
                        out resultGrant)
                    || resultGrant == null
                    || !resultGrant.Success
                    || resultGrant.GrantedCount != 1)
                {
                    rollback.Restore(inventory);
                    return Fail(result, InventoryTitleChangeError.UpdateFailed);
                }
            }
            else if (updatedTarget.ItemId != target.ItemId
                && !inventory.SetItem(
                    InventoryListType.Main,
                    request.TargetSlotIndex,
                    updatedTarget))
            {
                rollback.Restore(inventory);
                return Fail(result, InventoryTitleChangeError.UpdateFailed);
            }

            result.Success = true;
            result.Error = InventoryTitleChangeError.None;
            result.SourceItemId = request.SourceItemId;
            result.TargetItemId = target.ItemId;
            result.ResultItemId = updatedTarget.ItemId;
            result.ResultValue = resolution.ResultValue;
            result.ResultItemKind = updatedTarget.ItemKind;
            result.IsSuccessBranch = resolution.IsSuccessBranch;
            result.SourceRemainingCount = inventory
                .GetItem(InventoryListType.Main, request.SourceSlotIndex)?.Count ?? 0;
            return true;
        }

        internal static bool ShouldSplitStackedTarget(
            InventoryTitleChangeResolution resolution,
            ItemCore target)
        {
            return resolution != null
                && resolution.IsLimitedCube
                && InventoryStackRuleService.IsStackable(target)
                && target.Count > 1;
        }

        private static InventoryTitleChangeResult CreateResult(
            InventoryTitleChangeRequest request)
        {
            return new InventoryTitleChangeResult
            {
                SourceItemId = request != null ? request.SourceItemId : 0,
                TargetItemId = request != null ? request.TargetItemId : 0,
            };
        }

        private static bool Fail(
            InventoryTitleChangeResult result,
            InventoryTitleChangeError error)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            result.Success = false;
            result.Error = error;
            return false;
        }

        private sealed class InventoryTitleChangeRollback
        {
            private readonly Dictionary<short, ItemCore> _mainItems =
                new Dictionary<short, ItemCore>();
            private readonly Dictionary<short, int> _virtualCounts =
                new Dictionary<short, int>();

            internal static InventoryTitleChangeRollback Capture(
                InventoryService inventory,
                short sourceSlotIndex,
                short targetSlotIndex,
                IReadOnlyList<InventoryMaterialRequirement> materials)
            {
                var rollback = new InventoryTitleChangeRollback();
                rollback.CaptureMainItem(inventory, sourceSlotIndex);
                rollback.CaptureMainItem(inventory, targetSlotIndex);

                foreach (var material in materials
                             ?? Array.Empty<InventoryMaterialRequirement>())
                {
                    if (InventoryService.TryResolveMainVirtualSlotByItemId(
                            material.ItemTemplateId,
                            out var virtualSlot,
                            out _))
                    {
                        rollback._virtualCounts[virtualSlot] =
                            inventory.GetMainVirtualCount(virtualSlot)?.Count ?? 0;
                        continue;
                    }

                    foreach (var pair in inventory.GetItems(InventoryListType.Main)
                                 .Where(pair => pair.Value.ItemId == material.ItemTemplateId))
                    {
                        rollback.CaptureMainItem(inventory, pair.Key);
                    }
                }

                return rollback;
            }

            internal void Restore(InventoryService inventory)
            {
                foreach (var pair in _mainItems)
                    inventory.SetItem(InventoryListType.Main, pair.Key, pair.Value.Copy());
                foreach (var pair in _virtualCounts)
                    inventory.SetMainVirtualCount(pair.Key, pair.Value);
            }

            private void CaptureMainItem(InventoryService inventory, short slotIndex)
            {
                if (_mainItems.ContainsKey(slotIndex))
                    return;

                var item = inventory.GetItem(InventoryListType.Main, slotIndex);
                if (item != null)
                    _mainItems[slotIndex] = item.Copy();
            }
        }
    }
}
