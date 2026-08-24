using System;
using System.Collections.Generic;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryEquipmentMutationService
    {
        private const byte UnidentifiedAmplifyFlag = 0x80;
        private const short GoldSlot = 0;

        internal static bool TryEnchantByBead(
            InventoryService inventory,
            EnchantByBeadCommand command,
            out EnchantByBeadResult result)
        {
            if (command == null)
            {
                result = EnchantByBeadResult.Error(null, EnchantByBeadResult.ErrorInvalidBead);
                return false;
            }

            result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);
            if (inventory == null
                || command.BeadListType != InventoryListType.Main
                || !IsEnchantTargetList(command.TargetListType))
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorUnsupported);
                return false;
            }

            var bead = inventory.GetItem(command.BeadListType, command.BeadSlotIndex);
            if (bead == null || bead.Count <= 0)
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);
                return false;
            }

            var target = inventory.GetItem(command.TargetListType, command.TargetSlotIndex);
            if (target == null || target.ItemId <= 0 || !target.IsEquipmentItem())
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                return false;
            }

            if (IsItemLocked(inventory, target))
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                return false;
            }

            var enchantUpgradeCount = bead.EnchantUpgradeCount;
            if (!ItemMetadataResolver.TryValidateEnchantByBeadTarget(
                    bead.ItemId,
                    target.ItemId,
                    enchantUpgradeCount,
                    out var enchantCardItemId,
                    out var rejectReason))
            {
                var errorCode = rejectReason != null && rejectReason.StartsWith("target", StringComparison.Ordinal)
                    ? EnchantByBeadResult.ErrorInvalidTarget
                    : EnchantByBeadResult.ErrorUnsupported;
                result = EnchantByBeadResult.Error(command, errorCode);
                return false;
            }

            var updatedTarget = target.Copy();
            updatedTarget.EnchantCardId = enchantCardItemId;
            updatedTarget.EnchantUpgradeCount = enchantUpgradeCount;
            if (!inventory.SetItem(command.TargetListType, command.TargetSlotIndex, updatedTarget))
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
                return false;
            }

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    command.BeadListType,
                    command.BeadSlotIndex,
                    1,
                    out var delete))
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidBead);
                return false;
            }

            result = EnchantByBeadResult.Ok(command, Math.Max(0, delete.RemainingCount), enchantCardItemId);
            return true;
        }

        internal static bool TryOpenEquipmentSocket(
            InventoryService inventory,
            short targetSlotIndex,
            int targetItemTemplateId,
            short materialSlotIndex,
            out EquipmentSocketMutationResult result)
        {
            result = null;
            if (inventory == null || targetItemTemplateId <= 0)
                return false;

            var target = inventory.GetItem(InventoryListType.Main, targetSlotIndex);
            if (target == null
                || target.ItemKind != ItemCore.KindEquipment
                || target.ItemId != targetItemTemplateId)
                return false;

            if (IsItemLocked(inventory, target))
                return false;

            var currentOpenCount = GetEquipmentOpenCount(target, targetItemTemplateId);
            if (currentOpenCount > 0)
            {
                var repaired = target.Copy();
                EnsureEquipmentSocketOpenFields(repaired, targetItemTemplateId, currentOpenCount);
                inventory.SetItem(InventoryListType.Main, targetSlotIndex, repaired);
                result = new EquipmentSocketMutationResult { MaterialConsumed = false };
                return true;
            }

            var material = inventory.GetItem(InventoryListType.Main, materialSlotIndex);
            if (material == null || material.Count <= 0)
                return false;

            var updatedTarget = target.Copy();
            EnsureEquipmentSocketOpenFields(updatedTarget, targetItemTemplateId, GetEquipmentSocketOpenCount(targetItemTemplateId));
            if (!inventory.SetItem(InventoryListType.Main, targetSlotIndex, updatedTarget))
                return false;

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    materialSlotIndex,
                    1,
                    out var delete))
                return false;

            result = new EquipmentSocketMutationResult
            {
                MaterialConsumed = true,
                MaterialItem = CreateMutation(InventoryListType.Main, materialSlotIndex, material, delete),
            };
            return true;
        }

        internal static bool TrySetEquipmentEmblems(
            InventoryService inventory,
            short targetSlotIndex,
            int targetItemTemplateId,
            IReadOnlyList<EquipmentEmblemApplyRequest> emblems,
            out EquipmentEmblemMutationResult result)
        {
            result = null;
            if (inventory == null || emblems == null || emblems.Count == 0)
                return false;

            if (!TryResolveEquipmentTarget(
                    inventory,
                    targetSlotIndex,
                    targetItemTemplateId,
                    out var targetListType,
                    out var target))
                return false;

            if (IsItemLocked(inventory, target))
                return false;

            var openCount = target.EmblemSocketCount;
            if (openCount <= 0)
                return false;

            var updatedTarget = target.Copy();
            EnsureEquipmentSocketPlaceholders(updatedTarget, openCount);

            var socketType = ResolveJewelSocketType(targetItemTemplateId);
            var requiredCounts = new Dictionary<short, int>();
            var requiredItems = new Dictionary<short, ItemCore>();
            var consumed = new List<InventoryMutationResult>();
            foreach (var request in emblems)
            {
                if (!TryResolveEquipmentSocketRequest(
                        targetItemTemplateId,
                        openCount,
                        request.SocketIndex,
                        out var logicalSocketIndex))
                    return false;

                var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                    return false;

                var emblem = inventory.GetItem(InventoryListType.Main, request.EmblemSlot);
                if (emblem == null
                    || emblem.ItemId != request.EmblemItemTemplateId
                    || emblem.Count <= 0)
                    return false;

                requiredCounts.TryGetValue(request.EmblemSlot, out var requiredCount);
                requiredCount++;
                if (emblem.Count < requiredCount)
                    return false;

                requiredCounts[request.EmblemSlot] = requiredCount;
                requiredItems[request.EmblemSlot] = emblem;
                WriteEquipmentEmblem(updatedTarget, logicalSocketIndex, request.EmblemItemTemplateId);
            }

            foreach (var pair in requiredCounts)
            {
                var before = requiredItems[pair.Key];
                if (!InventoryDeleteService.TryDecreaseStack(
                        inventory,
                        InventoryListType.Main,
                        pair.Key,
                        pair.Value,
                        out var delete))
                    return false;

                var mutation = CreateMutation(InventoryListType.Main, pair.Key, before, delete);
                mutation.RequestedCount = (short)Math.Min(short.MaxValue, pair.Value);
                mutation.AppliedCount = mutation.RequestedCount;
                consumed.Add(mutation);
            }

            if (!inventory.SetItem(targetListType, targetSlotIndex, updatedTarget))
                return false;

            result = new EquipmentEmblemMutationResult
            {
                TargetListType = targetListType,
                TargetSlotIndex = targetSlotIndex,
                TargetEquipped = targetListType == InventoryListType.Equipment,
            };
            result.ConsumedEmblems.AddRange(consumed);
            return true;
        }

        internal static bool TryOpenAvatarSocket(
            InventoryService inventory,
            short targetSlotIndex,
            int targetItemTemplateId,
            short materialSlotIndex,
            out AvatarSocketMutationResult result)
        {
            result = null;
            if (inventory == null || targetItemTemplateId <= 0)
                return false;

            var target = inventory.GetItem(InventoryListType.Avatar, targetSlotIndex);
            if (target == null
                || target.ItemKind != ItemCore.KindAvatar
                || target.ItemId != targetItemTemplateId)
                return false;

            if (IsItemLocked(inventory, target))
                return false;

            var detail = GetOrCreateAvatarDetail(inventory, target);
            if (detail == null)
                return false;

            var expectedSocketTypes = ItemMetadataResolver.ResolveAvatarOpenSocketTypes(targetItemTemplateId);
            if (expectedSocketTypes == null || expectedSocketTypes.Count == 0)
            {
                var defaultSocketTypes = ItemMetadataResolver.ResolveAvatarDefaultSocketTypes(targetItemTemplateId);
                if (defaultSocketTypes == null || defaultSocketTypes.Count == 0)
                    return false;

                if (AvatarSocketLayoutMatches(detail, defaultSocketTypes))
                    return false;

                SetAvatarSocketTypes(detail, defaultSocketTypes);
                SaveAvatarDetail(inventory, detail);
                result = new AvatarSocketMutationResult { MaterialConsumed = false };
                return true;
            }

            if (detail.JewelSocketView.OpenCount > 0)
            {
                if (!AvatarSocketLayoutMatches(detail, expectedSocketTypes))
                {
                    SetAvatarSocketTypes(detail, expectedSocketTypes);
                    SaveAvatarDetail(inventory, detail);
                }

                result = new AvatarSocketMutationResult { MaterialConsumed = false };
                return true;
            }

            var material = inventory.GetItem(InventoryListType.Main, materialSlotIndex);
            if (material == null || material.Count <= 0)
                return false;

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    materialSlotIndex,
                    1,
                    out var delete))
                return false;

            SetAvatarSocketTypes(detail, expectedSocketTypes);
            SaveAvatarDetail(inventory, detail);
            result = new AvatarSocketMutationResult
            {
                MaterialConsumed = true,
                MaterialItem = CreateMutation(InventoryListType.Main, materialSlotIndex, material, delete),
            };
            return true;
        }

        internal static bool TrySetAvatarEmblems(
            InventoryService inventory,
            short targetSlotIndex,
            int targetItemTemplateId,
            IReadOnlyList<EquipmentEmblemApplyRequest> emblems,
            out AvatarEmblemMutationResult result)
        {
            result = null;
            if (inventory == null || emblems == null || emblems.Count == 0)
                return false;

            if (!TryResolveAvatarTarget(
                    inventory,
                    targetSlotIndex,
                    targetItemTemplateId,
                    out var targetListType,
                    out var target))
                return false;

            if (IsItemLocked(inventory, target))
                return false;

            var detail = GetOrCreateAvatarDetail(inventory, target);
            if (detail == null)
                return false;

            var socket = detail.JewelSocketView;
            if (socket.OpenCount <= 0)
                return false;

            var requiredCounts = new Dictionary<short, int>();
            var requiredItems = new Dictionary<short, ItemCore>();
            var consumed = new List<InventoryMutationResult>();
            foreach (var request in emblems)
            {
                if (request.SocketIndex >= JewelSocket.SocketCount)
                    return false;

                var socketType = ToSocketTypeByte(socket.GetSocketType(request.SocketIndex));
                if (socketType == 0)
                    return false;

                var emblemType = ItemMetadataResolver.ResolveEmblemSocketType(request.EmblemItemTemplateId);
                if (!CanAttachEmblemToJewelSocket(socketType, emblemType))
                    return false;

                var emblem = inventory.GetItem(InventoryListType.Main, request.EmblemSlot);
                if (emblem == null
                    || emblem.ItemId != request.EmblemItemTemplateId
                    || emblem.Count <= 0)
                    return false;

                requiredCounts.TryGetValue(request.EmblemSlot, out var requiredCount);
                requiredCount++;
                if (emblem.Count < requiredCount)
                    return false;

                requiredCounts[request.EmblemSlot] = requiredCount;
                requiredItems[request.EmblemSlot] = emblem;
                socket.SetEmblemId(request.SocketIndex, request.EmblemItemTemplateId);
            }

            foreach (var pair in requiredCounts)
            {
                var before = requiredItems[pair.Key];
                if (!InventoryDeleteService.TryDecreaseStack(
                        inventory,
                        InventoryListType.Main,
                        pair.Key,
                        pair.Value,
                        out var delete))
                    return false;

                var mutation = CreateMutation(InventoryListType.Main, pair.Key, before, delete);
                mutation.RequestedCount = (short)Math.Min(short.MaxValue, pair.Value);
                mutation.AppliedCount = mutation.RequestedCount;
                consumed.Add(mutation);
            }

            detail.JewelSocketView = socket;
            SaveAvatarDetail(inventory, detail);
            result = new AvatarEmblemMutationResult
            {
                TargetListType = targetListType,
                TargetSlotIndex = targetSlotIndex,
                TargetEquipped = targetListType == InventoryListType.Equipment,
            };
            result.ConsumedEmblems.AddRange(consumed);
            return true;
        }

        internal static bool TryUseGuardianGem(
            InventoryService inventory,
            GuardianGemUseCommand command,
            out GuardianGemUseResult result)
        {
            result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorInvalidRequest);
            if (inventory == null || command == null)
                return false;

            if (command.SocketIndex >= ItemCore.GuardianGemSlotCount
                || !ItemSlotBoundService.IsValidSlotForKind(
                    ItemCore.KindGuardianGem,
                    InventoryListType.GuildMedal,
                    command.MaterialSlotIndex,
                    ItemSlotBoundService.MainExpandStageFull))
            {
                result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorInvalidRequest);
                return false;
            }

            var targetSlotIndex = (short)EquipmentType.GuildMedal;
            var target = inventory.GetItem(InventoryListType.Equipment, targetSlotIndex);
            if (target == null
                || target.ItemKind != ItemCore.KindGuildMedal
                || target.ItemId != command.EquippedMedalItemTemplateId)
            {
                result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorInvalidRequest);
                return false;
            }

            if (IsItemLocked(inventory, target))
            {
                result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorInvalidRequest);
                return false;
            }

            var material = inventory.GetItem(InventoryListType.GuildMedal, command.MaterialSlotIndex);
            if (material == null
                || material.ItemKind != ItemCore.KindGuardianGem
                || material.ItemId != command.GuardianGemItemTemplateId
                || material.Count <= 0)
            {
                result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorGuardianGemMissing);
                return false;
            }

            if (!TryGetGuardianGemEffectTypes(material.ItemId, out var newEffectTypes))
            {
                result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorGuardianGemMissing);
                return false;
            }

            var updatedTarget = target.Copy();
            var previousGuardianGemItemId = updatedTarget.GetGuardianGemItemId(command.SocketIndex);

            for (var index = 0; index < ItemCore.GuardianGemSlotCount; index++)
            {
                if (index == command.SocketIndex)
                    continue;

                var existingGuardianGemItemId = updatedTarget.GetGuardianGemItemId(index);
                if (existingGuardianGemItemId <= 0)
                    continue;

                if (!TryGetGuardianGemEffectTypes(existingGuardianGemItemId, out var existingEffectTypes))
                {
                    result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorInvalidRequest);
                    return false;
                }

                if (newEffectTypes.Overlaps(existingEffectTypes))
                {
                    result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorGuardianGemMissing);
                    return false;
                }
            }

            updatedTarget.SetGuardianGemItemId(command.SocketIndex, command.GuardianGemItemTemplateId);
            if (!inventory.SetItem(InventoryListType.Equipment, targetSlotIndex, updatedTarget))
            {
                result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorInvalidRequest);
                return false;
            }

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.GuildMedal,
                    command.MaterialSlotIndex,
                    1,
                    out var delete))
            {
                result = GuardianGemUseResult.Error(command, GuardianGemUseResult.ErrorGuardianGemMissing);
                return false;
            }

            result = GuardianGemUseResult.Ok(command, previousGuardianGemItemId, delete.RemainingCount);
            return true;
        }

        internal static bool TryUseEquipmentEffectRune(
            InventoryService inventory,
            EquipmentEffectRuneUseRequest request,
            out EquipmentEffectRuneUseResult result)
        {
            result = CreateEquipmentEffectRuneResult(request);
            if (inventory == null || request == null || !IsSupportedEquipmentEffectSourceList(request.SourceListType))
                return false;

            var source = inventory.GetItem(request.SourceListType, request.SourceSlotIndex);
            if (source == null)
            {
                if (!IsEquipmentEffectRuneItem(request.ExpectedSourceItemTemplateId, out _, out _))
                    return false;

                result.Status = EquipmentEffectRuneStatus.MissingSource;
                result.SourceItemTemplateId = request.ExpectedSourceItemTemplateId;
                return true;
            }

            result.SourceItemTemplateId = source.ItemId;
            result.SourceInstanceValue = source.Value != 0 ? source.Value : request.SourceInstanceValue;
            if (request.ExpectedSourceItemTemplateId > 0 && source.ItemId != request.ExpectedSourceItemTemplateId)
            {
                result.Status = EquipmentEffectRuneStatus.MissingSource;
                return true;
            }

            if (!IsEquipmentEffectRuneItem(source.ItemId, out _, out var effectId))
                return false;

            if (IsItemLocked(inventory, source))
            {
                result.Status = EquipmentEffectRuneStatus.Locked;
                return true;
            }

            if (source.Count <= 0)
            {
                result.Status = EquipmentEffectRuneStatus.MissingSource;
                return true;
            }

            if (!TryResolveTargetWeapon(inventory, request, out var target))
            {
                result.Status = EquipmentEffectRuneStatus.InvalidTarget;
                return true;
            }

            if (target.Core.SealFlag != 0 || IsItemLocked(inventory, target.Core))
            {
                result.Status = target.Core.SealFlag != 0
                    ? EquipmentEffectRuneStatus.InvalidTarget
                    : EquipmentEffectRuneStatus.Locked;
                result.TargetListType = target.ListType;
                result.TargetSlotIndex = target.SlotIndex;
                result.TargetItemTemplateId = target.Core.ItemId;
                return true;
            }

            var updatedTarget = target.Core.Copy();
            updatedTarget.Rune = effectId;
            if (!inventory.SetItem(target.ListType, target.SlotIndex, updatedTarget))
            {
                result.Status = EquipmentEffectRuneStatus.InvalidTarget;
                result.TargetListType = target.ListType;
                result.TargetSlotIndex = target.SlotIndex;
                result.TargetItemTemplateId = target.Core.ItemId;
                return true;
            }

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    request.SourceListType,
                    request.SourceSlotIndex,
                    1,
                    out var delete))
            {
                result.Status = EquipmentEffectRuneStatus.MissingSource;
                return true;
            }

            result.Status = EquipmentEffectRuneStatus.Applied;
            result.SourceItemTemplateId = source.ItemId;
            result.SourceRemainingStackCount = delete.RemainingCount;
            result.TargetListType = target.ListType;
            result.TargetSlotIndex = target.SlotIndex;
            result.TargetItemTemplateId = target.Core.ItemId;
            result.AppliedEffectId = effectId;
            return true;
        }

        internal static bool IsEquipmentEffectRuneItem(int itemTemplateId)
        {
            return IsEquipmentEffectRuneItem(itemTemplateId, out _, out _);
        }

        internal static bool TryPurifyItem(
            InventoryService inventory,
            PurifyItemRequest request,
            out PurifyItemResult result)
        {
            result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidRequest);
            if (inventory == null || request == null || request.TargetSlotIndex < 0 || request.MaterialSlotIndex < 0)
                return false;

            var target = inventory.GetItem(InventoryListType.Main, request.TargetSlotIndex);
            if (target == null
                || target.ItemId != request.TargetItemTemplateId
                || target.ItemKind != ItemCore.KindEquipment)
            {
                result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidTarget);
                return false;
            }

            if (IsItemLocked(inventory, target))
            {
                result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorLocked);
                return false;
            }

            var material = inventory.GetItem(InventoryListType.Main, request.MaterialSlotIndex);
            if (material == null
                || material.ItemId != request.MaterialItemTemplateId
                || material.Count <= 0)
            {
                result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidMaterial);
                return false;
            }

            if (!TryResolvePurifyAction(material.ItemId, out var action, out var materialCount)
                || material.Count < materialCount)
            {
                result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidMaterial);
                return false;
            }

            var metadata = ItemMetadataResolver.Resolve(target.ItemId);
            if (!CanUseOutworldVigorItem(target, metadata))
            {
                result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorUnsupported);
                return false;
            }

            var currentAmplifyType = target.AmplifyType;
            var isUnidentified = (currentAmplifyType & UnidentifiedAmplifyFlag) != 0;
            if (!isUnidentified)
            {
                result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidTarget);
                return false;
            }

            var updatedTarget = target.Copy();
            if (action == PurifyItemAction.Purify)
            {
                var attributeType = RollAmplifyAttributeType();
                updatedTarget.AmplifyType = (byte)attributeType;
                updatedTarget.AmplifyValue = ItemAmplifier.CalculateInitialAttributeValue(metadata.Rarity, attributeType);
            }
            else
            {
                updatedTarget.AmplifyType = 0;
                updatedTarget.AmplifyValue = 0;
            }

            if (!inventory.SetItem(InventoryListType.Main, request.TargetSlotIndex, updatedTarget))
                return false;

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    request.MaterialSlotIndex,
                    Math.Max(1, materialCount),
                    out var delete))
            {
                result = CreatePurifyErrorResult(request, PurifyItemResult.ErrorInvalidMaterial);
                return false;
            }

            result = new PurifyItemResult
            {
                Request = request,
                ErrorCode = 0,
                Action = action,
                TargetSlotIndex = request.TargetSlotIndex,
                MaterialSlotIndex = request.MaterialSlotIndex,
                MaterialRemainingCount = delete.RemainingCount,
                AmplifyType = updatedTarget.AmplifyType,
                AmplifyValue = updatedTarget.AmplifyValue,
            };
            return true;
        }

        internal static bool TryInvestItemAmplifyOption(
            InventoryService inventory,
            InvestItemAmplifyOptionRequest request,
            out InvestItemAmplifyOptionResult result)
        {
            result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidRequest);
            if (inventory == null || request == null || request.TargetSlotIndex < 0 || request.MaterialSlotIndex < 0)
                return false;

            var target = inventory.GetItem(InventoryListType.Main, request.TargetSlotIndex);
            if (target == null
                || target.ItemId != request.TargetItemTemplateId
                || target.ItemKind != ItemCore.KindEquipment)
            {
                result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidTarget);
                return false;
            }

            if (IsItemLocked(inventory, target))
            {
                result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorLocked);
                return false;
            }

            var material = inventory.GetItem(InventoryListType.Main, request.MaterialSlotIndex);
            if (material == null
                || material.ItemId != request.MaterialItemTemplateId
                || material.Count <= 0)
            {
                result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidMaterial);
                return false;
            }

            if (!TryResolveInvestMaterial(request, material.ItemId, out var configuredOptionType, out var materialCount)
                || material.Count < materialCount)
            {
                result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidMaterial);
                return false;
            }

            var metadata = ItemMetadataResolver.Resolve(target.ItemId);
            if (!CanUseOutworldVigorItem(target, metadata))
            {
                result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorUnsupported);
                return false;
            }

            var selectedType = ResolveInvestAmplifyAttributeType(request, configuredOptionType);
            if (selectedType == AmplifyAttributeType.None)
            {
                result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidRequest);
                return false;
            }

            var currentAmplifyType = target.AmplifyType;
            var isUnidentified = (currentAmplifyType & UnidentifiedAmplifyFlag) != 0;
            var currentIdentifiedType = (byte)(currentAmplifyType & 0x7F);
            if (!CanApplyInvestAction(request.Action, isUnidentified, currentIdentifiedType, target.Upgrade, out var actionErrorCode))
            {
                result = CreateInvestAmplifyErrorResult(request, actionErrorCode);
                return false;
            }

            if (currentIdentifiedType == (byte)selectedType)
            {
                result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorSameOption);
                return false;
            }

            var updatedTarget = target.Copy();
            updatedTarget.AmplifyType = (byte)selectedType;
            updatedTarget.AmplifyValue = ItemAmplifier.CalculateInitialAttributeValue(metadata.Rarity, selectedType);
            if (request.Action == InvestItemAmplifyOptionAction.PureGold)
                updatedTarget.Upgrade = RollPureGoldAmplifyLevel(material.ItemId);

            if (!inventory.SetItem(InventoryListType.Main, request.TargetSlotIndex, updatedTarget))
                return false;

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    request.MaterialSlotIndex,
                    Math.Max(1, materialCount),
                    out var delete))
            {
                result = CreateInvestAmplifyErrorResult(request, InvestItemAmplifyOptionResult.ErrorInvalidMaterial);
                return false;
            }

            result = new InvestItemAmplifyOptionResult
            {
                Request = request,
                ErrorCode = 0,
                TargetSlotIndex = request.TargetSlotIndex,
                MaterialSlotIndex = request.MaterialSlotIndex,
                MaterialRemainingCount = delete.RemainingCount,
                AmplifyType = updatedTarget.AmplifyType,
                AmplifyValue = updatedTarget.AmplifyValue,
                AmplifyLevel = updatedTarget.Upgrade,
            };
            return true;
        }

        internal static bool TryUnsealRandomOption(
            InventoryService inventory,
            short targetSlotIndex,
            int targetItemTemplateId,
            out RandomOptionUnsealResult result)
        {
            result = null;
            if (inventory == null)
                return false;

            var target = inventory.GetItem(InventoryListType.Main, targetSlotIndex);
            if (target == null
                || target.ItemKind != ItemCore.KindEquipment
                || (targetItemTemplateId > 0 && target.ItemId != targetItemTemplateId))
                return false;

            var metadata = ItemMetadataResolver.Resolve(target.ItemId);
            if (!RandomOptionResolver.TryRollOptions(metadata, out var entries))
                return false;

            var goldCost = RandomOptionResolver.ResolveBreakSealGoldCost(metadata);
            if (!TrySpendGold(inventory, goldCost, out var updatedGold))
                return false;

            var updatedTarget = target.Copy();
            ApplyRandomOptions(updatedTarget, entries);
            if (!inventory.SetItem(InventoryListType.Main, targetSlotIndex, updatedTarget))
                return false;

            result = new RandomOptionUnsealResult
            {
                TargetListType = InventoryListType.Main,
                TargetSlotIndex = targetSlotIndex,
                TargetItemTemplateId = target.ItemId,
                GoldCost = goldCost,
                UpdatedGold = updatedGold,
                RandomOptions = new List<RandomOptionEntry>(entries),
            };
            return true;
        }

        internal static bool TryChangeRandomOption(
            InventoryService inventory,
            short targetSlotIndex,
            int targetItemTemplateId,
            byte requestedOptionIndex,
            out RandomOptionUnsealResult result)
        {
            result = null;
            if (inventory == null)
                return false;

            var target = inventory.GetItem(InventoryListType.Main, targetSlotIndex);
            if (target == null
                || target.ItemKind != ItemCore.KindEquipment
                || (targetItemTemplateId > 0 && target.ItemId != targetItemTemplateId))
                return false;

            var metadata = ItemMetadataResolver.Resolve(target.ItemId);
            var entries = ToRandomOptionEntries(target.RandomOptions);
            if (!TryReplaceSingleOption(metadata, requestedOptionIndex, entries, out var replacedIndex))
                return false;

            var goldCost = RandomOptionResolver.ResolveOptionModificationGoldCost(metadata);
            if (!TrySpendGold(inventory, goldCost, out var updatedGold))
                return false;

            var updatedTarget = target.Copy();
            ApplyRandomOptions(updatedTarget, entries);
            if (!inventory.SetItem(InventoryListType.Main, targetSlotIndex, updatedTarget))
                return false;

            result = new RandomOptionUnsealResult
            {
                TargetListType = InventoryListType.Main,
                TargetSlotIndex = targetSlotIndex,
                TargetItemTemplateId = target.ItemId,
                GoldCost = goldCost,
                UpdatedGold = updatedGold,
                RandomOptions = new List<RandomOptionEntry>(entries),
                ReplacedOptionIndex = replacedIndex,
                ChangeOptionCandidates = RandomOptionResolver.ResolveChangeOptionCandidates(metadata, replacedIndex),
            };
            return true;
        }

        private static bool IsEnchantTargetList(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.Equipment;
        }

        private static bool TryResolveEquipmentTarget(
            InventoryService inventory,
            short slotIndex,
            int itemTemplateId,
            out InventoryListType listType,
            out ItemCore core)
        {
            if (TryResolveTarget(inventory, InventoryListType.Main, slotIndex, itemTemplateId, out core)
                && core.ItemKind == ItemCore.KindEquipment)
            {
                listType = InventoryListType.Main;
                return true;
            }

            if (TryResolveTarget(inventory, InventoryListType.Equipment, slotIndex, itemTemplateId, out core)
                && core.ItemKind == ItemCore.KindEquipment)
            {
                listType = InventoryListType.Equipment;
                return true;
            }

            listType = InventoryListType.Main;
            core = null;
            return false;
        }

        private static bool TryResolveAvatarTarget(
            InventoryService inventory,
            short slotIndex,
            int itemTemplateId,
            out InventoryListType listType,
            out ItemCore core)
        {
            if (TryResolveTarget(inventory, InventoryListType.Avatar, slotIndex, itemTemplateId, out core)
                && core.ItemKind == ItemCore.KindAvatar)
            {
                listType = InventoryListType.Avatar;
                return true;
            }

            if (TryResolveTarget(inventory, InventoryListType.Equipment, slotIndex, itemTemplateId, out core)
                && core.ItemKind == ItemCore.KindAvatar)
            {
                listType = InventoryListType.Equipment;
                return true;
            }

            listType = InventoryListType.Avatar;
            core = null;
            return false;
        }

        private static bool TryResolveTarget(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int itemTemplateId,
            out ItemCore core)
        {
            core = inventory.GetItem(listType, slotIndex);
            return core != null && core.ItemId == itemTemplateId;
        }

        private static AvatarDetail GetOrCreateAvatarDetail(InventoryService inventory, ItemCore core)
        {
            if (inventory == null || core == null || core.Value <= 0)
                return null;

            if (inventory.AvatarDetails.TryGetDetail(core.Value, out var detail))
                return detail;

            detail = new AvatarDetail
            {
                AvatarUid = core.Value,
                OwnerId = inventory.AccountId,
                CharacterId = inventory.CharacterId,
                ItemId = core.ItemId,
                ExpireDate = 0,
                ClearAvatarId = 0,
                JewelSocket = new byte[JewelSocket.Size],
                Color1 = 0,
                Color2 = 0,
                DeleteDate = 0,
            };
            inventory.AvatarDetails.Attach(detail);
            return detail;
        }

        private static void SaveAvatarDetail(InventoryService inventory, AvatarDetail detail)
        {
            if (inventory == null || detail == null)
                return;

            inventory.AvatarDetails.Attach(detail);
            inventory.AvatarDetails.MarkDirty(detail.AvatarUid);
        }

        private static int GetEquipmentOpenCount(ItemCore core, int itemTemplateId)
        {
            if (core == null)
                return 0;

            return Math.Min(core.EmblemSocketCount, GetEquipmentSocketOpenCount(itemTemplateId));
        }

        private static void EnsureEquipmentSocketOpenFields(ItemCore core, int itemTemplateId, int openCount)
        {
            if (core == null)
                return;

            var visibleCount = Math.Min(Math.Max(openCount, 0), GetEquipmentSocketOpenCount(itemTemplateId));
            core.EmblemSocketCount = (byte)visibleCount;
            EnsureEquipmentSocketPlaceholders(core, visibleCount);
        }

        private static void EnsureEquipmentSocketPlaceholders(ItemCore core, int openCount)
        {
            if (core == null)
                return;

            var visibleCount = Math.Min(Math.Max(openCount, 0), 2);
            if (visibleCount > 0 && core.EmblemId1 == 0)
                core.EmblemId1 = -1;
            if (visibleCount > 1 && core.EmblemId2 == 0)
                core.EmblemId2 = -1;
        }

        private static void WriteEquipmentEmblem(ItemCore core, byte socketIndex, int emblemItemTemplateId)
        {
            if (socketIndex == 0)
                core.EmblemId1 = emblemItemTemplateId;
            else if (socketIndex == 1)
                core.EmblemId2 = emblemItemTemplateId;
        }

        private static int GetEquipmentSocketOpenCount(int itemTemplateId)
        {
            return IsSingleMiddleEquipmentSocket(itemTemplateId) ? 1 : 2;
        }

        private static bool IsSingleMiddleEquipmentSocket(int itemTemplateId)
        {
            var equipmentType = ItemMetadataResolver.ResolveEquipmentType(itemTemplateId);
            return string.Equals(equipmentType, "[support]", StringComparison.OrdinalIgnoreCase)
                || string.Equals(equipmentType, "[magic stone]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveEquipmentSocketRequest(
            int itemTemplateId,
            int openCount,
            byte requestSocketIndex,
            out byte logicalSocketIndex)
        {
            logicalSocketIndex = 0;
            var visibleOpenCount = Math.Min(openCount, 2);
            if (requestSocketIndex >= JewelSocket.SocketCount || visibleOpenCount <= 0)
                return false;

            if (IsSingleMiddleEquipmentSocket(itemTemplateId))
            {
                if (requestSocketIndex > 1)
                    return false;

                return true;
            }

            if (requestSocketIndex >= visibleOpenCount)
                return false;

            logicalSocketIndex = requestSocketIndex;
            return true;
        }

        private static byte ResolveJewelSocketType(int itemTemplateId)
        {
            var equipmentType = ItemMetadataResolver.ResolveEquipmentType(itemTemplateId);
            if (string.IsNullOrWhiteSpace(equipmentType))
                return 0x10;

            switch (equipmentType)
            {
                case "[coat]":
                case "[pants]":
                    return 0x04;
                case "[shoulder]":
                case "[amulet]":
                    return 0x02;
                case "[belt]":
                case "[waist]":
                case "[ring]":
                    return 0x01;
                case "[shoes]":
                case "[wrist]":
                    return 0x08;
                default:
                    return 0x10;
            }
        }

        private static bool CanAttachEmblemToJewelSocket(byte socketType, byte emblemType)
        {
            if (socketType == 0 || emblemType == 0)
                return true;

            return (socketType & emblemType) != 0;
        }

        private static byte ToSocketTypeByte(ushort socketType)
        {
            return (byte)(socketType & 0xFF);
        }

        private static bool AvatarSocketLayoutMatches(AvatarDetail detail, IReadOnlyList<byte> socketTypes)
        {
            if (detail == null || socketTypes == null || socketTypes.Count == 0)
                return false;

            var socket = detail.JewelSocketView;
            var expectedCount = Math.Min(JewelSocket.SocketCount, socketTypes.Count);
            for (var index = 0; index < expectedCount; index++)
            {
                if (ToSocketTypeByte(socket.GetSocketType(index)) != socketTypes[index])
                    return false;
            }

            for (var index = expectedCount; index < JewelSocket.SocketCount; index++)
            {
                if (socket.GetSocketType(index) != 0)
                    return false;
            }

            return true;
        }

        private static void SetAvatarSocketTypes(AvatarDetail detail, IReadOnlyList<byte> socketTypes)
        {
            var socket = detail.JewelSocketView;
            for (var index = 0; index < JewelSocket.SocketCount; index++)
            {
                var socketType = socketTypes != null && index < socketTypes.Count
                    ? socketTypes[index]
                    : (byte)0;
                socket.Set(index, socketType, socketType != 0 ? -1 : 0);
            }
            detail.JewelSocketView = socket;
        }

        private static EquipmentEffectRuneUseResult CreateEquipmentEffectRuneResult(EquipmentEffectRuneUseRequest request)
        {
            return new EquipmentEffectRuneUseResult
            {
                SourceListType = request != null ? request.SourceListType : InventoryListType.Main,
                SourceSlotIndex = request != null ? request.SourceSlotIndex : (short)0,
                SourceInstanceValue = request != null ? request.SourceInstanceValue : 0,
                SourceItemTemplateId = request != null ? request.ExpectedSourceItemTemplateId : 0,
            };
        }

        private static bool IsSupportedEquipmentEffectSourceList(InventoryListType listType)
        {
            return listType == InventoryListType.Main || listType == InventoryListType.PersonalCargo;
        }

        private static bool IsSupportedEquipmentEffectTargetList(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Equipment;
        }

        private static bool IsEquipmentEffectRuneItem(int itemTemplateId, out StackableItemFile stackable, out ushort effectId)
        {
            stackable = null;
            effectId = 0;
            if (itemTemplateId <= 0)
                return false;

            stackable = StackableItemProvider.Load(itemTemplateId);
            if (stackable == null || stackable.StackableType == null)
                return false;

            if (stackable.StackableType.IndexOf("[equipment effect]", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return EquipmentEffectRuneUseRequest.TryParseEffectId(stackable.IntData, out effectId);
        }

        private static bool TryResolveTargetWeapon(
            InventoryService inventory,
            EquipmentEffectRuneUseRequest request,
            out EquipmentEffectTarget target)
        {
            target = null;
            var explicitCandidate = request != null && request.HasExplicitTarget
                ? new EquipmentEffectTargetCandidate
                {
                    ListType = request.TargetListType,
                    SlotIndex = request.TargetSlotIndex,
                    ExpectedItemTemplateId = request.ExpectedTargetItemTemplateId,
                }
                : null;

            if (explicitCandidate != null && TryResolveTargetCandidate(inventory, explicitCandidate, out target))
                return true;

            foreach (var candidate in ParseTargetCandidates(request != null ? request.RawBody : null))
            {
                if (TryResolveTargetCandidate(inventory, candidate, out target))
                    return true;
            }

            return false;
        }

        private static bool TryResolveTargetCandidate(
            InventoryService inventory,
            EquipmentEffectTargetCandidate candidate,
            out EquipmentEffectTarget target)
        {
            target = null;
            if (inventory == null || candidate == null || !IsSupportedEquipmentEffectTargetList(candidate.ListType))
                return false;

            var core = inventory.GetItem(candidate.ListType, candidate.SlotIndex);
            if (core == null)
                return false;

            if (candidate.ExpectedItemTemplateId > 0 && core.ItemId != candidate.ExpectedItemTemplateId)
                return false;

            if (core.ItemKind != ItemCore.KindEquipment)
                return false;

            if (!ItemMetadataResolver.TryLoadEquipmentFile(core.ItemId, out var equipment))
                return false;

            if (!EquipmentTypeInfo.IsWeapon(EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType)))
                return false;

            if (equipment.Grade > 0 && equipment.Grade <= 2)
                return false;

            target = new EquipmentEffectTarget
            {
                ListType = candidate.ListType,
                SlotIndex = candidate.SlotIndex,
                Core = core,
            };
            return true;
        }

        private static IReadOnlyList<EquipmentEffectTargetCandidate> ParseTargetCandidates(byte[] body)
        {
            var candidates = new List<EquipmentEffectTargetCandidate>();
            if (body == null || body.Length < 13)
                return candidates;

            for (var offset = 11; offset < body.Length; offset++)
            {
                if (offset + 3 <= body.Length)
                {
                    var slot = BitConverter.ToInt16(body, offset);
                    var listType = (InventoryListType)body[offset + 2];
                    if (IsSupportedEquipmentEffectTargetList(listType) && IsPlausibleInventorySlot(slot))
                    {
                        AddTargetCandidate(candidates, listType, slot, ReadPositiveItemId(body, offset + 3));
                        AddTargetCandidate(candidates, listType, slot, ReadPositiveItemId(body, offset + 7));
                    }
                }

                if (offset + 6 <= body.Length)
                {
                    var slot = BitConverter.ToInt16(body, offset);
                    if (IsPlausibleInventorySlot(slot))
                        AddTargetCandidate(candidates, InventoryListType.Main, slot, ReadPositiveItemId(body, offset + 2));
                }

                if (offset + 6 <= body.Length)
                {
                    var itemId = ReadPositiveItemId(body, offset);
                    var slot = BitConverter.ToInt16(body, offset + 4);
                    if (itemId > 0 && IsPlausibleInventorySlot(slot))
                        AddTargetCandidate(candidates, InventoryListType.Main, slot, itemId);
                }

                if (offset + 7 <= body.Length)
                {
                    var listType = (InventoryListType)body[offset];
                    var slot = BitConverter.ToInt16(body, offset + 1);
                    if (IsSupportedEquipmentEffectTargetList(listType) && IsPlausibleInventorySlot(slot))
                        AddTargetCandidate(candidates, listType, slot, ReadPositiveItemId(body, offset + 3));
                }
            }

            return candidates;
        }

        private static int ReadPositiveItemId(byte[] body, int offset)
        {
            if (body == null || offset < 0 || offset + 4 > body.Length)
                return 0;

            var value = BitConverter.ToInt32(body, offset);
            return value >= 1000 ? value : 0;
        }

        private static bool IsPlausibleInventorySlot(short slotIndex)
        {
            return slotIndex >= 0 && slotIndex <= 500;
        }

        private static void AddTargetCandidate(
            List<EquipmentEffectTargetCandidate> candidates,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId)
        {
            if (!IsSupportedEquipmentEffectTargetList(listType) || !IsPlausibleInventorySlot(slotIndex))
                return;

            foreach (var existing in candidates)
            {
                if (existing.ListType == listType
                    && existing.SlotIndex == slotIndex
                    && existing.ExpectedItemTemplateId == expectedItemTemplateId)
                    return;
            }

            candidates.Add(new EquipmentEffectTargetCandidate
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ExpectedItemTemplateId = expectedItemTemplateId,
            });
        }

        private static bool TryResolvePurifyAction(int itemTemplateId, out PurifyItemAction action, out int materialCount)
        {
            action = PurifyItemAction.Unknown;
            materialCount = 0;
            if (ItemUpgradeTableProvider.TryGetPurifyMaterialCount(itemTemplateId, out materialCount))
            {
                action = PurifyItemAction.Purify;
                return true;
            }

            if (ItemUpgradeTableProvider.TryGetOutworldVigorClearMaterialCount(itemTemplateId, out materialCount))
            {
                action = PurifyItemAction.Clear;
                return true;
            }

            return false;
        }

        private static bool CanUseOutworldVigorItem(ItemCore target, ItemMetadata metadata)
        {
            return target != null
                && metadata != null
                && target.ItemKind == ItemCore.KindEquipment
                && string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                && metadata.MinimumLevel >= ItemUpgradeTableProvider.GetAmplifyEquipLevelConst()
                && metadata.Rarity >= 2;
        }

        private static AmplifyAttributeType RollAmplifyAttributeType()
        {
            var types = new[]
            {
                AmplifyAttributeType.Vitality,
                AmplifyAttributeType.Spirit,
                AmplifyAttributeType.Strength,
                AmplifyAttributeType.Intelligence,
            };
            return types[ServerRandom.Next(types.Length)];
        }

        private static bool TryResolveInvestMaterial(
            InvestItemAmplifyOptionRequest request,
            int materialItemTemplateId,
            out AmplifyOptionType optionType,
            out int materialCount)
        {
            optionType = AmplifyOptionType.None;
            materialCount = 0;
            if (request == null)
                return false;

            if (request.Action == InvestItemAmplifyOptionAction.Invest)
                return ItemUpgradeTableProvider.TryGetInvestAmplifyOption(materialItemTemplateId, out optionType, out materialCount);

            if (request.Action == InvestItemAmplifyOptionAction.Twist)
                return ItemUpgradeTableProvider.TryGetReinvestAmplifyOption(materialItemTemplateId, out optionType, out materialCount);

            if (request.Action == InvestItemAmplifyOptionAction.PureGold)
                return ItemUpgradeTableProvider.TryGetRandomInvestUpgradeOption(materialItemTemplateId, out optionType, out materialCount);

            return false;
        }

        private static bool CanApplyInvestAction(
            InvestItemAmplifyOptionAction action,
            bool isUnidentified,
            byte currentIdentifiedType,
            byte currentUpgradeLevel,
            out byte errorCode)
        {
            errorCode = InvestItemAmplifyOptionResult.ErrorInvalidTarget;
            if (action == InvestItemAmplifyOptionAction.Invest)
            {
                if (isUnidentified || currentIdentifiedType != 0)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorAlreadyHasAmplifyOption;
                    return false;
                }

                if (currentUpgradeLevel != 0)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorAlreadyUpgraded;
                    return false;
                }

                return true;
            }

            if (action == InvestItemAmplifyOptionAction.Twist)
            {
                if (isUnidentified || currentIdentifiedType == 0)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorNoAmplifyOption;
                    return false;
                }

                if (currentUpgradeLevel != 0)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorAlreadyUpgraded;
                    return false;
                }

                return true;
            }

            if (action == InvestItemAmplifyOptionAction.PureGold)
            {
                if (isUnidentified)
                {
                    errorCode = InvestItemAmplifyOptionResult.ErrorNoAmplifyOption;
                    return false;
                }

                return true;
            }

            return false;
        }

        private static AmplifyAttributeType ResolveInvestAmplifyAttributeType(
            InvestItemAmplifyOptionRequest request,
            AmplifyOptionType optionType)
        {
            if (optionType == AmplifyOptionType.All)
                return MapInvestOptionToAmplifyType(request.SelectedOption);

            return MapConfiguredOptionToAmplifyType(optionType);
        }

        private static AmplifyAttributeType MapConfiguredOptionToAmplifyType(AmplifyOptionType optionType)
        {
            switch (optionType)
            {
                case AmplifyOptionType.PhysicalAttack:
                    return AmplifyAttributeType.Strength;
                case AmplifyOptionType.MagicalAttack:
                    return AmplifyAttributeType.Intelligence;
                case AmplifyOptionType.PhysicalDefense:
                    return AmplifyAttributeType.Vitality;
                case AmplifyOptionType.MagicalDefense:
                    return AmplifyAttributeType.Spirit;
                default:
                    return AmplifyAttributeType.None;
            }
        }

        private static AmplifyAttributeType MapInvestOptionToAmplifyType(byte selectedOption)
        {
            switch (selectedOption)
            {
                case 1:
                    return AmplifyAttributeType.Vitality;
                case 2:
                    return AmplifyAttributeType.Spirit;
                case 3:
                    return AmplifyAttributeType.Strength;
                case 4:
                    return AmplifyAttributeType.Intelligence;
                default:
                    return AmplifyAttributeType.None;
            }
        }

        private static byte RollPureGoldAmplifyLevel(int materialItemTemplateId)
        {
            if (ItemMetadataResolver.TryLoadStackableFile(materialItemTemplateId, out var stackable)
                && stackable.AmplificationRandomValues != null
                && stackable.AmplificationRandomValues.Count > 0)
            {
                var totalWeight = 0;
                foreach (var entry in stackable.AmplificationRandomValues)
                {
                    if (entry != null && entry.Weight > 0)
                        totalWeight += entry.Weight;
                }

                if (totalWeight > 0)
                {
                    var roll = ServerRandom.Next(totalWeight);
                    foreach (var entry in stackable.AmplificationRandomValues)
                    {
                        if (entry == null || entry.Weight <= 0)
                            continue;

                        roll -= entry.Weight;
                        if (roll < 0)
                            return (byte)Math.Max(0, Math.Min(byte.MaxValue, entry.UpgradeLevel));
                    }
                }
            }

            return RollDefaultPureGoldAmplifyLevel();
        }

        private static byte RollDefaultPureGoldAmplifyLevel()
        {
            var roll = ServerRandom.Next(100);
            if (roll < 50)
                return 3;
            if (roll < 80)
                return 4;
            if (roll < 95)
                return 5;
            return 6;
        }

        private static bool TryReplaceSingleOption(
            ItemMetadata metadata,
            byte requestedOptionIndex,
            List<RandomOptionEntry> entries,
            out int replacedIndex)
        {
            replacedIndex = requestedOptionIndex;
            if (entries == null || entries.Count == 0 || replacedIndex >= entries.Count)
                return false;
            if (!RandomOptionResolver.TryRollReplacementOption(metadata, replacedIndex, entries, out var replacement)
                || replacement == null)
                return false;

            entries[replacedIndex] = replacement;
            return true;
        }

        private static void ApplyRandomOptions(ItemCore core, IReadOnlyList<RandomOptionEntry> entries)
        {
            core.SetRandomOptions(ToRandomOptions(entries));
            core.RandomOptionState = 0;
            core.RandomOptionChangedIndex = ItemCore.RandomOptionChangedIndexDefault;
            core.RandomOptionChangeState = 0;
            core.RandomOptionChange.Clear();
        }

        private static List<RandomOptionEntry> ToRandomOptionEntries(IReadOnlyList<RandomOption> options)
        {
            var entries = new List<RandomOptionEntry>();
            if (options == null)
                return entries;

            for (var index = 0; index < options.Count && index < 3; index++)
            {
                var option = options[index];
                if (option == null || option.IsEmpty)
                    continue;

                entries.Add(new RandomOptionEntry
                {
                    Type = option.Type,
                    Value1 = option.Value1,
                    Value2 = option.Value2,
                });
            }

            return entries;
        }

        private static List<RandomOption> ToRandomOptions(IReadOnlyList<RandomOptionEntry> entries)
        {
            var options = new List<RandomOption>();
            if (entries == null)
                return options;

            for (var index = 0; index < entries.Count && index < 3; index++)
            {
                var entry = entries[index];
                if (entry == null)
                    continue;

                options.Add(new RandomOption
                {
                    Type = entry.Type,
                    Value1 = entry.Value1,
                    Value2 = entry.Value2,
                });
            }

            return options;
        }

        private static bool TrySpendGold(InventoryService inventory, int goldCost, out int updatedGold)
        {
            updatedGold = inventory.GetMainVirtualCount(GoldSlot)?.Count ?? 0;
            if (goldCost <= 0)
                return true;

            if (updatedGold < goldCost)
                return false;

            updatedGold -= goldCost;
            return inventory.SetMainVirtualCount(GoldSlot, updatedGold);
        }

        private static bool TryGetGuardianGemEffectTypes(int itemTemplateId, out HashSet<string> effectTypes)
        {
            effectTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (itemTemplateId <= 0)
                return false;

            var stackable = StackableItemProvider.Load(itemTemplateId);
            if (stackable == null || stackable.GuardianGemEnchantEntries == null)
                return false;

            foreach (var entry in stackable.GuardianGemEnchantEntries)
            {
                if (entry == null)
                    continue;

                var effectType = NormalizeGuardianGemEffectType(entry.EffectType);
                if (!string.IsNullOrWhiteSpace(effectType))
                    effectTypes.Add(effectType);
            }

            return effectTypes.Count > 0;
        }

        private static string NormalizeGuardianGemEffectType(string effectType)
        {
            if (string.IsNullOrWhiteSpace(effectType))
                return string.Empty;

            var value = effectType.Trim();
            if (value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']')
                value = value.Substring(1, value.Length - 2).Trim();

            if (value.Length >= 2 && value[0] == '`' && value[value.Length - 1] == '`')
                value = value.Substring(1, value.Length - 2).Trim();

            return value;
        }

        private static bool IsItemLocked(InventoryService inventory, ItemCore core)
        {
            return inventory != null
                && core != null
                && core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static InventoryMutationResult CreateMutation(
            InventoryListType listType,
            short slotIndex,
            ItemCore before,
            InventoryDeleteResult delete)
        {
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = before != null ? before.ItemId : 0,
                RemainingStackCount = delete != null ? delete.RemainingCount : 0,
                InstanceValue = delete != null ? delete.RemainingCount : 0,
                Durability = before != null ? before.Durability : (ushort)0,
                RequestedCount = 1,
                AppliedCount = 1,
            };
        }

        private static PurifyItemResult CreatePurifyErrorResult(PurifyItemRequest request, byte errorCode)
        {
            return new PurifyItemResult
            {
                Request = request,
                ErrorCode = errorCode,
            };
        }

        private static InvestItemAmplifyOptionResult CreateInvestAmplifyErrorResult(
            InvestItemAmplifyOptionRequest request,
            byte errorCode)
        {
            return new InvestItemAmplifyOptionResult
            {
                Request = request,
                ErrorCode = errorCode,
            };
        }

        internal static bool TryResetItemQuality(
            InventoryService inventory,
            ResetItemQualityRequest request,
            out ResetItemQualityResult result)
        {
            result = CreateResetQualityErrorResult(request, ResetItemQualityResult.ErrorInvalidRequest);
            if (inventory == null
                || request == null
                || request.TargetSlotIndex < 0
                || request.MaterialSlotIndex < 0
                || request.TargetSlotIndex == request.MaterialSlotIndex
                || request.TargetItemTemplateId <= 0)
            {
                return false;
            }

            var target = inventory.GetItem(InventoryListType.Main, request.TargetSlotIndex);
            if (target == null
                || target.ItemId != request.TargetItemTemplateId
                || target.ItemKind != ItemCore.KindEquipment)
            {
                result = CreateResetQualityErrorResult(request, ResetItemQualityResult.ErrorInvalidTarget);
                return false;
            }

            if (IsItemLocked(inventory, target))
            {
                result = CreateResetQualityErrorResult(request, ResetItemQualityResult.ErrorLocked);
                return false;
            }

            // 材料必须是可堆叠道具(非装备/时装/宠物); 具体是否为品级调整箱交给 policy resolver 按 PVF 裁决。
            var material = inventory.GetItem(InventoryListType.Main, request.MaterialSlotIndex);
            if (material == null
                || material.Count <= 0
                || material.ItemKind == ItemCore.KindUnknown
                || material.ItemKind == ItemCore.KindEquipment
                || material.ItemKind == ItemCore.KindCreature
                || material.ItemKind == ItemCore.KindAvatar)
            {
                result = CreateResetQualityErrorResult(request, ResetItemQualityResult.ErrorInvalidMaterial);
                return false;
            }

            if (!ItemMetadataResolver.TryLoadStackableFile(material.ItemId, out var stackable)
                || !ResetItemQualityPolicyResolver.TryResolve(material.ItemId, stackable, out var policy))
            {
                result = CreateResetQualityErrorResult(request, ResetItemQualityResult.ErrorInvalidMaterial);
                return false;
            }

            var metadata = ItemMetadataResolver.Resolve(target.ItemId);
            var equipmentType = metadata != null
                ? EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType)
                : EquipmentType.Unknown;
            if (metadata == null || !policy.Allows(equipmentType))
            {
                result = CreateResetQualityErrorResult(request, ResetItemQualityResult.ErrorUnsupported);
                return false;
            }

            var oldQualitySeed = target.Value;
            var newQualitySeed = policy.Mode == ResetItemQualityMode.Highest
                ? unchecked((int)ItemQuality.TopQualitySeed)
                : RollRandomQualitySeed(oldQualitySeed);

            var updatedTarget = target.Copy();
            updatedTarget.Value = newQualitySeed;

            if (!inventory.SetItem(InventoryListType.Main, request.TargetSlotIndex, updatedTarget))
                return false;

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    request.MaterialSlotIndex,
                    1,
                    out var delete))
            {
                result = CreateResetQualityErrorResult(request, ResetItemQualityResult.ErrorInvalidMaterial);
                return false;
            }

            result = new ResetItemQualityResult
            {
                Request = request,
                ErrorCode = 0,
                Mode = policy.Mode,
                TargetSlotIndex = request.TargetSlotIndex,
                TargetItemTemplateId = request.TargetItemTemplateId,
                MaterialSlotIndex = request.MaterialSlotIndex,
                MaterialItemTemplateId = material.ItemId,
                MaterialRemainingCount = delete.RemainingCount,
                OldQualitySeed = oldQualitySeed,
                NewQualitySeed = newQualitySeed,
            };
            return true;
        }

        private static int RollRandomQualitySeed(int currentQualitySeed)
        {
            var topQualitySeed = unchecked((int)ItemQuality.TopQualitySeed);
            var qualitySeed = ServerRandom.Next(1, topQualitySeed);
            if (qualitySeed == currentQualitySeed)
                qualitySeed = qualitySeed + 1 < topQualitySeed ? qualitySeed + 1 : qualitySeed - 1;
            return qualitySeed;
        }

        private static ResetItemQualityResult CreateResetQualityErrorResult(ResetItemQualityRequest request, byte errorCode)
        {
            return new ResetItemQualityResult
            {
                Request = request,
                ErrorCode = errorCode,
            };
        }

        //  0x0051 RESET_ITEM_ATTR: 黄金蜜蜡重新封装装备。
        //  消耗 N 个蜜蜡 → 装备封印(seal_flag=1) + 封装次数+1(上限7)。
        internal static bool TryWaxReseal(
            InventoryService inventory,
            short targetSlot,
            int expectedTargetItemId,
            short waxSlot,
            out WaxResealResult result)
        {
            result = null;

            if (inventory == null || targetSlot < 0 || waxSlot < 0)
                return false;

            var target = inventory.GetItem(InventoryListType.Main, targetSlot);
            if (target == null || target.ItemId <= 0 || !target.IsEquipmentItem())
                return false;

            if (expectedTargetItemId != 0 && target.ItemId != expectedTargetItemId)
                return false;

            if (IsItemLocked(inventory, target))
                return false;

            var metadata = ItemMetadataResolver.Resolve(target.ItemId);
            var rarity = metadata?.Rarity ?? 0;
            var minimumLevel = metadata?.MinimumLevel ?? 0;

            var currentCount = target.ReSealCount;
            if (currentCount >= 7)
                return false;

            var newCount = (byte)(currentCount + 1);
            var waxCost = ComputeWaxResealCost(rarity, minimumLevel, newCount);

            var waxItem = inventory.GetItem(InventoryListType.Main, waxSlot);
            if (waxItem == null || waxItem.Count < waxCost)
                return false;

            var updatedTarget = target.Copy();
            updatedTarget.SealFlag = 1;
            updatedTarget.ReSealCount = newCount;

            if (!inventory.SetItem(InventoryListType.Main, targetSlot, updatedTarget))
                return false;

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    InventoryListType.Main,
                    waxSlot,
                    waxCost,
                    out var deleteResult)
                || !deleteResult.Success)
            {
                inventory.SetItem(InventoryListType.Main, targetSlot, target);
                return false;
            }

            result = new WaxResealResult
            {
                TargetSlotIndex = targetSlot,
                TargetItemTemplateId = target.ItemId,
                WaxSlotIndex = waxSlot,
                WaxCost = waxCost,
                NewSealFlag = 1,
                NewReSealCount = newCount,
            };
            return true;
        }

        internal static int ComputeWaxResealCost(int rarity, int minimumLevel, int newResealCount)
        {
            if (newResealCount < 1) newResealCount = 1;
            if (newResealCount > 7) newResealCount = 7;

            var baseCost = rarity switch
            {
                2 => minimumLevel switch // 稀有
                {
                    <= 30 => 3,
                    <= 50 => 6,
                    <= 70 => 9,
                    _ => 12,
                },
                3 => minimumLevel switch // 神器
                {
                    <= 30 => 4,
                    <= 50 => 8,
                    <= 70 => 12,
                    _ => 16,
                },
                6 => minimumLevel switch // 传说
                {
                    <= 30 => 6,
                    <= 50 => 12,
                    <= 70 => 18,
                    _ => 24,
                },
                _ => 1,
            };

            return baseCost + (newResealCount - 1);
        }

        private sealed class EquipmentEffectTargetCandidate
        {
            public InventoryListType ListType { get; set; }

            public short SlotIndex { get; set; }

            public int ExpectedItemTemplateId { get; set; }
        }

        private sealed class EquipmentEffectTarget
        {
            public InventoryListType ListType { get; set; }

            public short SlotIndex { get; set; }

            public ItemCore Core { get; set; }
        }
    }

    internal sealed class WaxResealResult
    {
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public short WaxSlotIndex { get; set; }
        public int WaxCost { get; set; }
        public byte NewSealFlag { get; set; }
        public byte NewReSealCount { get; set; }
    }
}
