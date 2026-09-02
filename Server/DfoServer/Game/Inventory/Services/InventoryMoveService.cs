using System;
using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryMoveServiceError
    {
        None = 0,
        InvalidInventory = 1,
        InvalidRequest = 2,
        UnsupportedListType = 3,
        SourceNotFound = 4,
        InvalidMoveCount = 5,
        InvalidDestinationSlot = 6,
        DestinationRejected = 7,
        CannotStack = 8,
        CannotSplitSameSpaceStack = 9,
        CannotSwap = 10,
        UpdateFailed = 11,
        ClientEcho = 12,
    }

    internal sealed class InventoryMoveServiceResult
    {
        public bool Success { get; set; }

        public bool Mutated { get; set; }

        public InventoryMoveServiceError Error { get; set; }

        public InventoryInsertError InsertError { get; set; }

        public InventoryListType SourceListType { get; set; }

        public short SourceSlotIndex { get; set; }

        public InventoryListType DestinationListType { get; set; }

        public short DestinationSlotIndex { get; set; }

        public InventoryListType ActualDestinationListType { get; set; }

        public short ActualDestinationSlotIndex { get; set; }

        public int MoveCount { get; set; }

        public int SourceRemainingCount { get; set; }

        public int DestinationCount { get; set; }

        public InventoryMutationSet Changes { get; } = new InventoryMutationSet();
    }

    internal static class InventoryMoveService
    {
        private const byte FemaleSlayerJob = 11;
        private const int VagabondGrowType = 4;

        private readonly struct ActorContext
        {
            internal ActorContext(byte job, int growType)
            {
                Job = job;
                GrowType = growType;
            }

            internal byte Job { get; }

            internal int GrowType { get; }
        }

        private static readonly ItemSlotRange MainQuickSlotRange = new ItemSlotRange(3, 8);

        internal static bool TryMove(
            InventoryService inventory,
            InventoryMoveRequest request,
            byte characterJob,
            int characterGrowType,
            out InventoryMoveServiceResult result)
        {
            result = CreateResult(request);
            var actor = new ActorContext(characterJob, characterGrowType);
            if (inventory == null)
                return Fail(result, InventoryMoveServiceError.InvalidInventory);

            if (request == null)
                return Fail(result, InventoryMoveServiceError.InvalidRequest);

            if (!IsSupportedListType(request.SourceListType)
                || !IsSupportedListType(request.DestinationListType))
                return Fail(result, InventoryMoveServiceError.UnsupportedListType);

            if (request.SourceListType == request.DestinationListType
                && request.SourceSlotIndex == request.DestinationSlotIndex)
                return SucceedNoOp(result);

            if (!inventory.TryGetItem(request.SourceListType, request.SourceSlotIndex, out var source)
                || source == null)
            {
                var reverseSource = inventory.GetItem(request.DestinationListType, request.DestinationSlotIndex);
                if (reverseSource == null)
                {
                    if (IsEmptyTitleNameClientEcho(request))
                        return SucceedNoOp(result);

                    return Fail(result, InventoryMoveServiceError.SourceNotFound);
                }

                if (IsEquipmentClientEchoWhenSourceMissing(request, reverseSource))
                    return Fail(result, InventoryMoveServiceError.ClientEcho);

                var reverseMoveCount = NormalizeMoveCount(reverseSource, request.MoveCount);
                if (reverseMoveCount <= 0)
                    return Fail(result, InventoryMoveServiceError.InvalidMoveCount);

                if (!TryValidateDestinationSlot(
                        inventory,
                        reverseSource,
                        request.DestinationListType,
                        request.DestinationSlotIndex,
                        request.SourceListType,
                        request.SourceSlotIndex,
                        actor,
                        result))
                    return false;

                if (!ApplyMoveToEmptySlot(
                        inventory,
                        reverseSource,
                        request.DestinationListType,
                        request.DestinationSlotIndex,
                        request.SourceListType,
                        request.SourceSlotIndex,
                        reverseMoveCount,
                        result,
                        request.MoveCount))
                    return false;

                result.DestinationListType = request.DestinationListType;
                result.DestinationSlotIndex = request.DestinationSlotIndex;
                return true;
            }

            var moveCount = NormalizeMoveCount(source, request.MoveCount);
            if (moveCount <= 0)
                return Fail(result, InventoryMoveServiceError.InvalidMoveCount);

            if (!TryValidateDestinationSlot(
                    inventory,
                    source,
                    request.SourceListType,
                    request.SourceSlotIndex,
                    request.DestinationListType,
                    request.DestinationSlotIndex,
                    actor,
                    result))
                return false;

            var destination = inventory.GetItem(request.DestinationListType, request.DestinationSlotIndex);
            if (IsTitleNameClientEcho(request, source, destination, actor))
                return Fail(result, InventoryMoveServiceError.ClientEcho);

            if (destination != null)
            {
                if (request.DestinationListType == InventoryListType.Equipment)
                    return ApplyEquipmentSlotReplace(
                        inventory,
                        source,
                        request.SourceListType,
                        request.SourceSlotIndex,
                        destination,
                        request.DestinationListType,
                        request.DestinationSlotIndex,
                        actor,
                        result);

                if (request.SourceListType != request.DestinationListType)
                    return ApplyCrossSpaceInsertToOccupiedDestination(
                        inventory,
                        source,
                        request.SourceListType,
                        request.SourceSlotIndex,
                        request.DestinationListType,
                        moveCount,
                        actor,
                        result);

                if (CanStackFull(source, destination, moveCount))
                    return ApplyStackMerge(
                        inventory,
                        source,
                        request.SourceListType,
                        request.SourceSlotIndex,
                        destination,
                        request.DestinationListType,
                        request.DestinationSlotIndex,
                        moveCount,
                        result);

                if (InventoryStackRuleService.IsStackable(source)
                    && InventoryStackRuleService.IsStackable(destination)
                    && source.ItemId == destination.ItemId)
                    return Fail(result, InventoryMoveServiceError.CannotStack);

                if (InventoryStackRuleService.IsStackable(source) && moveCount < source.Count)
                    return Fail(result, InventoryMoveServiceError.CannotSwap);

                if (WouldCreateDuplicateStack(
                        inventory,
                        source,
                        request.SourceListType,
                        request.SourceSlotIndex,
                        request.DestinationListType)
                    || WouldCreateDuplicateStack(
                        inventory,
                        destination,
                        request.DestinationListType,
                        request.DestinationSlotIndex,
                        request.SourceListType))
                    return Fail(result, InventoryMoveServiceError.CannotStack);

                return ApplySwap(
                    inventory,
                    source,
                    request.SourceListType,
                    request.SourceSlotIndex,
                    destination,
                    request.DestinationListType,
                    request.DestinationSlotIndex,
                    actor,
                    result);
            }

            if (InventoryStackRuleService.IsStackable(source)
                && request.SourceListType != request.DestinationListType
                && TryFindStackMergeSlot(
                    inventory,
                    source,
                    request.SourceListType,
                    request.SourceSlotIndex,
                    request.DestinationListType,
                    moveCount,
                    out var sameStackSlot,
                    out var stackTarget))
            {
                return ApplyStackMerge(
                    inventory,
                    source,
                    request.SourceListType,
                    request.SourceSlotIndex,
                    stackTarget,
                    request.DestinationListType,
                    sameStackSlot,
                    moveCount,
                    result);
            }

            return ApplyMoveToEmptySlot(
                inventory,
                source,
                request.SourceListType,
                request.SourceSlotIndex,
                request.DestinationListType,
                request.DestinationSlotIndex,
                moveCount,
                result);
        }

        private static bool ApplyCrossSpaceInsertToOccupiedDestination(
            InventoryService inventory,
            ItemCore source,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType destinationListType,
            int moveCount,
            ActorContext actor,
            InventoryMoveServiceResult result)
        {
            if (InventoryStackRuleService.IsStackable(source)
                && TryFindStackMergeSlot(
                    inventory,
                    source,
                    sourceListType,
                    sourceSlotIndex,
                    destinationListType,
                    moveCount,
                    out var stackSlot,
                    out var stackTarget))
            {
                return ApplyStackMerge(
                    inventory,
                    source,
                    sourceListType,
                    sourceSlotIndex,
                    stackTarget,
                    destinationListType,
                    stackSlot,
                    moveCount,
                    result);
            }

            if (TryFindEmptyDestinationSlot(
                    inventory,
                    source,
                    sourceListType,
                    sourceSlotIndex,
                    destinationListType,
                    actor,
                    out var emptySlot))
            {
                return ApplyMoveToEmptySlot(
                    inventory,
                    source,
                    sourceListType,
                    sourceSlotIndex,
                    destinationListType,
                    emptySlot,
                    moveCount,
                    result);
            }

            return Fail(result, InventoryMoveServiceError.CannotSwap);
        }

        private static bool ApplyEquipmentSlotReplace(
            InventoryService inventory,
            ItemCore source,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            ItemCore destination,
            InventoryListType destinationListType,
            short destinationSlotIndex,
            ActorContext actor,
            InventoryMoveServiceResult result)
        {
            if (sourceListType == destinationListType)
                return ApplySwap(
                    inventory,
                    source,
                    sourceListType,
                    sourceSlotIndex,
                    destination,
                    destinationListType,
                    destinationSlotIndex,
                    actor,
                    result);

            if (!TryValidateDestinationSlot(
                    inventory,
                    destination,
                    destinationListType,
                    destinationSlotIndex,
                    sourceListType,
                    sourceSlotIndex,
                    actor,
                    result))
                return false;

            return ApplySwap(
                inventory,
                source,
                sourceListType,
                sourceSlotIndex,
                destination,
                destinationListType,
                destinationSlotIndex,
                actor,
                result);
        }

        private static bool ApplyStackMerge(
            InventoryService inventory,
            ItemCore source,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            ItemCore destination,
            InventoryListType destinationListType,
            short destinationSlotIndex,
            int moveCount,
            InventoryMoveServiceResult result,
            int? ackMoveValue = null)
        {
            var oldDestination = destination.Copy();
            var updatedDestination = destination.Copy();
            updatedDestination.Count += moveCount;
            if (!inventory.SetItem(destinationListType, destinationSlotIndex, updatedDestination))
                return Fail(result, InventoryMoveServiceError.UpdateFailed);

            if (moveCount >= source.Count)
            {
                if (!inventory.RemoveItem(sourceListType, sourceSlotIndex))
                {
                    inventory.SetItem(destinationListType, destinationSlotIndex, oldDestination);
                    return Fail(result, InventoryMoveServiceError.UpdateFailed);
                }

                CompleteMutation(result, sourceListType, sourceSlotIndex, destinationListType, destinationSlotIndex, moveCount, 0, updatedDestination.Count);
                return true;
            }

            var updatedSource = source.Copy();
            updatedSource.Count -= moveCount;
            if (!inventory.SetItem(sourceListType, sourceSlotIndex, updatedSource))
            {
                inventory.SetItem(destinationListType, destinationSlotIndex, oldDestination);
                return Fail(result, InventoryMoveServiceError.UpdateFailed);
            }

            CompleteMutation(result, sourceListType, sourceSlotIndex, destinationListType, destinationSlotIndex, moveCount, updatedSource.Count, updatedDestination.Count);
            return true;
        }

        private static bool ApplyMoveToEmptySlot(
            InventoryService inventory,
            ItemCore source,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType destinationListType,
            short destinationSlotIndex,
            int moveCount,
            InventoryMoveServiceResult result,
            int? ackMoveValue = null)
        {
            var moving = PrepareForDestination(
                inventory,
                source,
                sourceListType,
                sourceSlotIndex,
                destinationListType,
                destinationSlotIndex);
            if (InventoryStackRuleService.IsStackable(moving))
                moving.Count = moveCount;

            if (!inventory.SetItem(destinationListType, destinationSlotIndex, moving))
                return Fail(result, InventoryMoveServiceError.UpdateFailed);

            if (InventoryStackRuleService.IsStackable(source) && moveCount < source.Count)
            {
                var updatedSource = source.Copy();
                updatedSource.Count -= moveCount;
                if (!inventory.SetItem(sourceListType, sourceSlotIndex, updatedSource))
                {
                    inventory.RemoveItem(destinationListType, destinationSlotIndex);
                    return Fail(result, InventoryMoveServiceError.UpdateFailed);
                }

                CompleteMutation(result, sourceListType, sourceSlotIndex, destinationListType, destinationSlotIndex, ackMoveValue ?? moveCount, updatedSource.Count, moving.Count);
                SyncAvatarClearAvatarId(inventory, moving, destinationListType, destinationSlotIndex, null);
                return true;
            }

            if (!inventory.RemoveItem(sourceListType, sourceSlotIndex))
            {
                inventory.RemoveItem(destinationListType, destinationSlotIndex);
                return Fail(result, InventoryMoveServiceError.UpdateFailed);
            }

            CompleteMutation(result, sourceListType, sourceSlotIndex, destinationListType, destinationSlotIndex, ackMoveValue ?? moveCount, 0, moving.Count);
            SyncAvatarClearAvatarId(inventory, moving, destinationListType, destinationSlotIndex, null);
            return true;
        }

        private static bool ApplySwap(
            InventoryService inventory,
            ItemCore source,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            ItemCore destination,
            InventoryListType destinationListType,
            short destinationSlotIndex,
            ActorContext actor,
            InventoryMoveServiceResult result)
        {
            if (!TryValidateDestinationSlot(
                    inventory,
                    destination,
                    destinationListType,
                    destinationSlotIndex,
                    sourceListType,
                    sourceSlotIndex,
                    actor,
                    result))
                return false;

            var sourceToDestination = PrepareForDestination(
                inventory,
                source,
                sourceListType,
                sourceSlotIndex,
                destinationListType,
                destinationSlotIndex);
            var destinationToSource = PrepareForDestination(
                inventory,
                destination,
                destinationListType,
                destinationSlotIndex,
                sourceListType,
                sourceSlotIndex);

            if (!inventory.SetItem(sourceListType, sourceSlotIndex, destinationToSource))
                return Fail(result, InventoryMoveServiceError.UpdateFailed);

            if (!inventory.SetItem(destinationListType, destinationSlotIndex, sourceToDestination))
            {
                inventory.SetItem(sourceListType, sourceSlotIndex, source);
                return Fail(result, InventoryMoveServiceError.UpdateFailed);
            }

            CompleteMutation(
                result,
                sourceListType,
                sourceSlotIndex,
                destinationListType,
                destinationSlotIndex,
                1,
                InventoryStackRuleService.IsStackable(destinationToSource) ? destinationToSource.Count : 0,
                InventoryStackRuleService.IsStackable(sourceToDestination) ? sourceToDestination.Count : 0);
            SyncAvatarClearAvatarId(inventory, destinationToSource, sourceListType, sourceSlotIndex, source);
            SyncAvatarClearAvatarId(inventory, sourceToDestination, destinationListType, destinationSlotIndex, destination);
            return true;
        }

        private static bool TryValidateDestinationSlot(
            InventoryService inventory,
            ItemCore item,
            InventoryListType currentListType,
            short currentSlotIndex,
            InventoryListType targetListType,
            short targetSlotIndex,
            ActorContext actor,
            InventoryMoveServiceResult result)
        {
            if (item == null || item.IsEmpty)
                return Fail(result, InventoryMoveServiceError.InvalidRequest);

            if (!ItemSlotBoundService.IsInItemSpacePhysicalRange(targetListType, targetSlotIndex))
                return Fail(result, InventoryMoveServiceError.InvalidDestinationSlot);

            if (targetListType == InventoryListType.Main)
            {
                if (InventoryService.IsVirtualMainSlot(targetSlotIndex)
                    || InventoryService.IsReservedMainSlot(targetSlotIndex))
                    return Fail(result, InventoryMoveServiceError.InvalidDestinationSlot);

                if (IsMainQuickSlot(targetSlotIndex))
                    return CanPlaceInMainQuickSlot(
                            inventory,
                            item,
                            currentListType,
                            currentSlotIndex,
                            targetSlotIndex)
                        ? true
                        : Fail(result, InventoryMoveServiceError.InvalidDestinationSlot);
            }

            if (targetListType == InventoryListType.PersonalCargo)
            {
                if (!inventory.Cargo.IsOpenSlot(targetSlotIndex))
                    return Fail(result, InventoryMoveServiceError.InvalidDestinationSlot);

                if (currentListType != InventoryListType.PersonalCargo
                    && !InventoryInsertService.ValidatePersonalCargoInsert(item, out var insertError))
                    return FailInsert(result, insertError);

                return true;
            }

            if (targetListType == InventoryListType.AccountCargo)
            {
                if (!inventory.AccountCargo.IsOpenSlot(targetSlotIndex))
                    return Fail(result, InventoryMoveServiceError.InvalidDestinationSlot);

                if (currentListType != InventoryListType.AccountCargo
                    && !InventoryInsertService.ValidateAccountCargoInsert(item, out var insertError))
                    return FailInsert(result, insertError);

                return true;
            }

            if (targetListType == InventoryListType.Equipment
                && targetSlotIndex == (short)EquipmentType.AuraSkinAvatar
                && !inventory.IsAuraSkinSlotOpened)
                return Fail(result, InventoryMoveServiceError.InvalidDestinationSlot);

            if (targetListType == InventoryListType.Equipment
                && !IsValidEquipmentSlotForItem(
                    item,
                    targetSlotIndex,
                    actor.Job,
                    actor.GrowType))
                return Fail(result, InventoryMoveServiceError.InvalidDestinationSlot);

            if (!ItemSlotBoundService.IsValidSlotForKind(
                    item.ItemKind,
                    targetListType,
                    targetSlotIndex,
                    inventory.GetListParam16(InventoryListType.Main)))
                return Fail(result, InventoryMoveServiceError.InvalidDestinationSlot);

            return true;
        }

        private static bool TryFindSameStackSlot(
            InventoryService inventory,
            ItemCore source,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType targetListType,
            out short slotIndex)
        {
            slotIndex = -1;
            if (!TryGetStackScanRange(inventory, targetListType, out var range))
                return false;

            for (var slot = range.Start; slot <= range.End; slot++)
            {
                if (sourceListType == targetListType && sourceSlotIndex == slot)
                    continue;

                var existing = inventory.GetItem(targetListType, slot);
                if (existing == null
                    || existing.ItemId != source.ItemId
                    || !InventoryStackRuleService.IsStackable(existing))
                    continue;

                slotIndex = slot;
                return true;
            }

            return false;
        }

        private static bool TryFindStackMergeSlot(
            InventoryService inventory,
            ItemCore source,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType targetListType,
            int moveCount,
            out short slotIndex,
            out ItemCore target)
        {
            slotIndex = -1;
            target = null;
            if (!TryGetStackScanRange(inventory, targetListType, out var range))
                return false;

            for (var slot = range.Start; slot <= range.End; slot++)
            {
                if (sourceListType == targetListType && sourceSlotIndex == slot)
                    continue;

                var existing = inventory.GetItem(targetListType, slot);
                if (existing == null
                    || existing.ItemId != source.ItemId
                    || !InventoryStackRuleService.IsStackable(existing)
                    || !CanStackFull(source, existing, moveCount))
                    continue;

                slotIndex = slot;
                target = existing;
                return true;
            }

            return false;
        }

        private static bool TryFindEmptyDestinationSlot(
            InventoryService inventory,
            ItemCore item,
            InventoryListType currentListType,
            short currentSlotIndex,
            InventoryListType targetListType,
            ActorContext actor,
            out short slotIndex)
        {
            slotIndex = -1;
            if (!TryGetStackScanRange(inventory, targetListType, out var range))
                return false;

            for (var slot = range.Start; slot <= range.End; slot++)
            {
                if (inventory.GetItem(targetListType, slot) != null)
                    continue;

                var probe = CreateResult(null);
                if (!TryValidateDestinationSlot(
                        inventory,
                        item,
                        currentListType,
                        currentSlotIndex,
                        targetListType,
                        slot,
                        actor,
                        probe))
                    continue;

                slotIndex = slot;
                return true;
            }

            return false;
        }

        private static bool WouldCreateDuplicateStack(
            InventoryService inventory,
            ItemCore source,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType targetListType)
        {
            return InventoryStackRuleService.IsStackable(source)
                && TryFindSameStackSlot(
                    inventory,
                    source,
                    sourceListType,
                    sourceSlotIndex,
                    targetListType,
                    out _);
        }

        private static bool TryGetStackScanRange(
            InventoryService inventory,
            InventoryListType listType,
            out ItemSlotRange range)
        {
            switch (listType)
            {
                case InventoryListType.PersonalCargo:
                    range = ItemSlotBoundService.GetPersonalCargoOpenRange(inventory.GetListParam16(listType));
                    return range.Count > 0;
                case InventoryListType.AccountCargo:
                    range = ItemSlotBoundService.GetAccountCargoOpenRange(inventory.GetListParam16(listType));
                    return range.Count > 0;
                default:
                    return ItemSlotBoundService.TryGetItemSpacePhysicalRange(listType, out range);
            }
        }

        private static bool CanStackFull(ItemCore source, ItemCore destination, int moveCount)
        {
            if (!InventoryStackRuleService.TryPlanMerge(
                    source,
                    destination,
                    moveCount,
                    out var plan,
                    out _))
                return false;

            return plan.AcceptedCount == moveCount && plan.RemainingCount == 0;
        }

        private static int NormalizeMoveCount(ItemCore source, int requestedMoveCount)
        {
            if (source == null)
                return 0;

            if (!InventoryStackRuleService.IsStackable(source))
                return 1;

            if (requestedMoveCount <= 0 || requestedMoveCount > source.Count)
                return source.Count;

            return requestedMoveCount;
        }

        private static ItemCore PrepareForDestination(
            InventoryService inventory,
            ItemCore item,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType destinationListType,
            short destinationSlotIndex)
        {
            var copy = item.Copy();
            if (!ShouldPreserveSortLock(
                    inventory,
                    item,
                    sourceListType,
                    sourceSlotIndex,
                    destinationListType,
                    destinationSlotIndex))
                copy.SortLockFlag = 0;

            if (destinationListType == InventoryListType.Equipment
                && copy.SealFlag == 1
                && (copy.ItemKind == ItemCore.KindEquipment
                    || copy.ItemKind == ItemCore.KindCreatureEquipment))
                copy.SealFlag = 0;

            return copy;
        }

        private static bool ShouldPreserveSortLock(
            InventoryService inventory,
            ItemCore item,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType destinationListType,
            short destinationSlotIndex)
        {
            if (item == null || item.SortLockFlag == 0)
                return true;

            if (sourceListType == InventoryListType.Equipment
                || destinationListType == InventoryListType.Equipment)
                return true;

            if (sourceListType != destinationListType)
                return false;

            if (sourceListType == InventoryListType.PersonalCargo
                || sourceListType == InventoryListType.AccountCargo)
                return true;

            if (sourceListType == InventoryListType.Main
                && (IsMainQuickSlot(sourceSlotIndex) || IsMainQuickSlot(destinationSlotIndex)))
                return false;

            return ItemSlotBoundService.TryGetItemKindBySlot(
                    sourceListType,
                    sourceSlotIndex,
                    inventory.GetListParam16(InventoryListType.Main),
                    out var sourceKind)
                && ItemSlotBoundService.TryGetItemKindBySlot(
                    destinationListType,
                    destinationSlotIndex,
                    inventory.GetListParam16(InventoryListType.Main),
                    out var destinationKind)
                && sourceKind == item.ItemKind
                && destinationKind == item.ItemKind;
        }

        private static bool CanUseMainQuickSlot(ItemCore item)
        {
            if (item == null)
                return false;

            return item.ItemKind != ItemCore.KindCreature
                && item.ItemKind != ItemCore.KindCreatureEquipment
                && item.ItemKind != ItemCore.KindCreatureConsumable
                && item.ItemKind != ItemCore.KindAvatar
                && item.ItemKind != ItemCore.KindAvatarEmblem;
        }

        private static bool CanPlaceInMainQuickSlot(
            InventoryService inventory,
            ItemCore item,
            InventoryListType currentListType,
            short currentSlotIndex,
            short targetSlotIndex)
        {
            if (!CanUseMainQuickSlot(item))
                return false;

            if (!IsCharmEquipment(item))
                return true;

            return !HasOtherCharmInMainQuickSlot(inventory, currentListType, currentSlotIndex, targetSlotIndex);
        }

        private static bool HasOtherCharmInMainQuickSlot(
            InventoryService inventory,
            InventoryListType currentListType,
            short currentSlotIndex,
            short targetSlotIndex)
        {
            for (var slot = MainQuickSlotRange.Start; slot <= MainQuickSlotRange.End; slot++)
            {
                if (slot == targetSlotIndex)
                    continue;

                if (currentListType == InventoryListType.Main && slot == currentSlotIndex)
                    continue;

                var existing = inventory.GetItem(InventoryListType.Main, slot);
                if (IsCharmEquipment(existing))
                    return true;
            }

            return false;
        }

        private static bool IsCharmEquipment(ItemCore item)
        {
            if (item == null || !item.IsEquipmentItem())
                return false;

            try
            {
                return EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(item.ItemId)) == EquipmentType.Charm;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsEquipmentClientEchoWhenSourceMissing(InventoryMoveRequest request, ItemCore equipmentItem)
        {
            return request != null
                && equipmentItem != null
                && request.SourceInstanceValue > 0
                && request.DestinationListType == InventoryListType.Equipment
                && equipmentItem.ItemId == request.SourceInstanceValue;
        }

        private static bool IsEmptyTitleNameClientEcho(InventoryMoveRequest request)
        {
            return request != null
                && request.SourceListType == InventoryListType.Main
                && request.DestinationListType == InventoryListType.Equipment
                && request.DestinationSlotIndex == (short)EquipmentType.TitleName
                && request.SourceInstanceValue == 0
                && request.DestinationInstanceValue == 0
                && request.MoveCount > 0;
        }

        private static bool IsTitleNameClientEcho(
            InventoryMoveRequest request,
            ItemCore source,
            ItemCore destination,
            ActorContext actor)
        {
            if (request == null
                || source == null
                || request.DestinationListType != InventoryListType.Equipment
                || request.DestinationSlotIndex != (short)EquipmentType.TitleName)
                return false;

            if (request.SourceInstanceValue <= 0)
            {
                return request.DestinationInstanceValue == 0
                    && destination == null
                    && IsValidEquipmentSlotForItem(
                        source,
                        (short)EquipmentType.TitleName,
                        actor.Job,
                        actor.GrowType);
            }

            if (source.ItemId != request.SourceInstanceValue)
                return true;

            return destination != null
                && destination.ItemId == request.SourceInstanceValue
                && request.DestinationInstanceValue == 0;
        }

        internal static void SyncAvatarClearAvatarId(
            InventoryService inventory,
            ItemCore item,
            InventoryListType listType,
            short slotIndex,
            ItemCore previousItemAtSlot,
            Func<int, bool> cloneAvatarResolver = null)
        {
            if (inventory == null || item == null || item.ItemKind != ItemCore.KindAvatar || item.AvatarUid <= 0)
                return;

            var detail = inventory.AvatarDetails.GetDetail(item.AvatarUid);
            if (detail == null)
                return;

            cloneAvatarResolver = cloneAvatarResolver ?? ItemMetadataResolver.IsCloneAvatarItem;
            var isCloneAvatar = cloneAvatarResolver(item.ItemId);
            var borrowCloneAppearance = isCloneAvatar
                && !ItemMetadataResolver.IsAuroraLookReplaceAvatar(item.ItemId);
            var clearAvatarId = 0;
            var color1 = detail.Color1;
            var color2 = detail.Color2;

            if (isCloneAvatar)
            {
                color1 = 0;
                color2 = 0;
            }

            if (IsEquippedAvatarSlot(listType, slotIndex) && borrowCloneAppearance)
            {
                if (previousItemAtSlot != null
                    && previousItemAtSlot.ItemKind == ItemCore.KindAvatar
                    && previousItemAtSlot.ItemId > 0
                    && !cloneAvatarResolver(previousItemAtSlot.ItemId))
                {
                    clearAvatarId = previousItemAtSlot.ItemId;

                    var previousDetail = previousItemAtSlot.AvatarUid > 0
                        ? inventory.AvatarDetails.GetDetail(previousItemAtSlot.AvatarUid)
                        : null;
                    color1 = previousDetail?.Color1 ?? 0;
                    color2 = previousDetail?.Color2 ?? 0;
                }
            }

            if (detail.ClearAvatarId == clearAvatarId
                && (!isCloneAvatar || (detail.Color1 == color1 && detail.Color2 == color2)))
                return;

            detail.ClearAvatarId = clearAvatarId;
            if (isCloneAvatar)
            {
                detail.Color1 = color1;
                detail.Color2 = color2;
            }
            inventory.AvatarDetails.MarkDirty(detail.AvatarUid);
        }

        private static bool IsEquippedAvatarSlot(InventoryListType listType, short slotIndex)
        {
            return listType == InventoryListType.Equipment
                && slotIndex >= (short)EquipmentType.HatAvatar
                && slotIndex <= (short)EquipmentType.AuroraIllusionAvatar;
        }

        private static bool IsValidEquipmentSlotForItem(
            ItemCore item,
            short slotIndex,
            byte characterJob,
            int characterGrowType)
        {
            if (item == null || item.IsEmpty)
                return false;

            EquipmentType type;
            try
            {
                type = EquipmentTypeInfo.ParseOrUnknown(ItemMetadataResolver.ResolveEquipmentType(item.ItemId));
            }
            catch
            {
                return false;
            }

            if (type == EquipmentType.Unknown)
                return false;

            if (slotIndex == (short)type)
                return true;

            if (type == EquipmentType.AuroraAvatar
                && slotIndex == (short)EquipmentType.AuroraIllusionAvatar)
                return true;

            return type == EquipmentType.Weapon
                && slotIndex == (short)EquipmentType.SupportWeapon
                && characterJob == FemaleSlayerJob
                && (characterGrowType & 0x0F) == VagabondGrowType;
        }

        private static bool IsMainQuickSlot(short slotIndex)
        {
            return slotIndex >= MainQuickSlotRange.Start && slotIndex <= MainQuickSlotRange.End;
        }

        private static bool IsSupportedListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet
                || listType == InventoryListType.AccountCargo
                || listType == InventoryListType.GuildMedal;
        }

        private static InventoryMoveServiceResult CreateResult(InventoryMoveRequest request)
        {
            return new InventoryMoveServiceResult
            {
                Success = false,
                Mutated = false,
                Error = InventoryMoveServiceError.None,
                InsertError = InventoryInsertError.None,
                SourceListType = request != null ? request.SourceListType : InventoryListType.Main,
                SourceSlotIndex = request != null ? request.SourceSlotIndex : (short)-1,
                DestinationListType = request != null ? request.DestinationListType : InventoryListType.Main,
                DestinationSlotIndex = request != null ? request.DestinationSlotIndex : (short)-1,
                ActualDestinationListType = request != null ? request.DestinationListType : InventoryListType.Main,
                ActualDestinationSlotIndex = request != null ? request.DestinationSlotIndex : (short)-1,
            };
        }

        private static bool SucceedNoOp(InventoryMoveServiceResult result)
        {
            result.Success = true;
            result.Mutated = false;
            result.Error = InventoryMoveServiceError.None;
            return true;
        }

        private static void CompleteMutation(
            InventoryMoveServiceResult result,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType destinationListType,
            short destinationSlotIndex,
            int moveCount,
            int sourceRemainingCount,
            int destinationCount)
        {
            result.Success = true;
            result.Mutated = true;
            result.Error = InventoryMoveServiceError.None;
            result.DestinationListType = destinationListType;
            result.DestinationSlotIndex = destinationSlotIndex;
            result.ActualDestinationListType = destinationListType;
            result.ActualDestinationSlotIndex = destinationSlotIndex;
            result.MoveCount = moveCount;
            result.SourceRemainingCount = sourceRemainingCount;
            result.DestinationCount = destinationCount;
            result.Changes.AddSlot(sourceListType, sourceSlotIndex);
            result.Changes.AddSlot(destinationListType, destinationSlotIndex);
        }

        private static bool FailInsert(InventoryMoveServiceResult result, InventoryInsertError insertError)
        {
            result.InsertError = insertError;
            return Fail(result, InventoryMoveServiceError.DestinationRejected);
        }

        private static bool Fail(InventoryMoveServiceResult result, InventoryMoveServiceError error)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            result.Success = false;
            result.Mutated = false;
            result.Error = error;
            return false;
        }
    }
}
