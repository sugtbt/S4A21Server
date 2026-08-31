using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Accounts;
using DfoServer.Game.Premium;
using DfoServer.Game.ReviveCoin;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventorySpecialConsumableService
    {
        private sealed class SourceContext
        {
            public short SlotIndex { get; set; }
            public ItemCore Core { get; set; }
        }

        private sealed class MaterialContext
        {
            public short SlotIndex { get; set; }
            public ItemCore Core { get; set; }
            public int RequiredCount { get; set; }
        }

        private sealed class ConsumeAndGrantResult
        {
            public InventoryDeleteResult SourceDelete { get; set; }
            public InventoryDeleteResult MaterialDelete { get; set; }
            public InventoryRewardGrantBatchResult GrantBatch { get; set; }
        }

        internal static bool TryUseBoosterItem(
            InventoryService inventory,
            BoosterUseRequest request,
            string characterJobLabel,
            IInventoryOverflowRewardSink overflowSink,
            out BoosterUseResult result)
            => TryUseBoosterItem(
                null,
                null,
                inventory,
                request,
                characterJobLabel,
                overflowSink,
                out result,
                out _);

        internal static bool TryUseBoosterItem(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            BoosterUseRequest request,
            string characterJobLabel,
            IInventoryOverflowRewardSink overflowSink,
            out BoosterUseResult result,
            out bool databaseAccessFailed)
        {
            result = new BoosterUseResult();
            databaseAccessFailed = false;
            request = request ?? new BoosterUseRequest();
            var selectedItemTemplateIds = request.SelectedItemTemplateIds ?? Array.Empty<int>();

            if (!TryResolveBoosterSource(
                    inventory,
                    request.SlotIndex,
                    request.ExpectedItemTemplateId,
                    out var source))
                return false;

            if (request.ExpectedItemTemplateId > 0 && source.Core.ItemId != request.ExpectedItemTemplateId)
                return false;

            var sourceItemTemplateId = source.Core.ItemId;
            var sourceInstanceValue = source.Core.InstanceValue;
            var requestedCount = Math.Max(1, request.RequestedCount);
            if (!ValidateSourceCount(source.Core, requestedCount))
                return false;

            var lifecyclePlan = InventoryItemLifecycleService.PrepareUse(
                inventory,
                InventoryListType.Main,
                source.SlotIndex,
                sourceItemTemplateId,
                InventoryItemLifecycleService.UtcNowUnixSeconds(),
                requestedCount);
            if (lifecyclePlan.SourceExpiredDeleted)
            {
                result.ErrorCode = BoosterUseResult.ErrorInvalidRequest;
                result.SourceExpiredDeleted = true;
                result.SourceSlotIndex = source.SlotIndex;
                result.SourceItemTemplateId = sourceItemTemplateId;
                result.SourceRemainingStackCount = 0;
                result.SourceInstanceValue = sourceInstanceValue;
                return true;
            }

            if (!lifecyclePlan.Success)
                return false;

            var stackable = StackableItemProvider.Load(sourceItemTemplateId);
            if (stackable == null)
                return false;

            var stackableType = InventoryPackageRewardResolver.NormalizeStackableType(stackable.StackableType);
            InventoryPackageRewardResolver.ResolveNeedMaterial(
                sourceItemTemplateId,
                stackable,
                out var materialItemTemplateId,
                out var materialCountPerUse);
            if (request.ExpectedMaterialItemTemplateId > 0
                && materialItemTemplateId > 0
                && materialItemTemplateId != request.ExpectedMaterialItemTemplateId)
                return false;

            var totalMaterialCountLong = (long)materialCountPerUse * requestedCount;
            if (totalMaterialCountLong > int.MaxValue)
                return false;
            var totalMaterialCount = (int)totalMaterialCountLong;

            if (!UsableCountLimitService.TryRecordUseIfLimited(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    sourceItemTemplateId,
                    requestedCount,
                    out var usableCountState))
            {
                return false;
            }
            result.UsableCountState = usableCountState;

            if (!TryResolveMaterial(
                    inventory,
                    request.MaterialSlotIndex,
                    materialItemTemplateId,
                    totalMaterialCount,
                    result,
                    out var material))
                return false;

            var isSeriaLuckValueSource = sourceItemTemplateId == SeriaLuckItemConstants.ItemTemplateId;
            var seriaLuckValueBefore = 0;
            if (isSeriaLuckValueSource
                && !TryLoadSeriaLuckValue(
                    connection,
                    transaction,
                    inventory,
                    out seriaLuckValueBefore))
            {
                databaseAccessFailed = true;
                return false;
            }

            var seriaLuckValue = seriaLuckValueBefore;
            var displayRewardEntries = new List<PvfLib.BoosterRewardEntry>();
            var doubleRewardEntries = new List<PvfLib.BoosterRewardEntry>();

            for (var useIndex = 0; useIndex < requestedCount; useIndex++)
            {
                if (!InventoryPackageRewardResolver.TryResolvePackageRewards(
                        sourceItemTemplateId,
                        stackable,
                        stackableType,
                        selectedItemTemplateIds,
                        characterJobLabel,
                        out var rewards))
                {
                    var selectedText = selectedItemTemplateIds.Count == 0
                        ? "none"
                        : string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"));
                    FileLogger.Log($"  [BoosterOnline] unsupported/empty item=0x{sourceItemTemplateId:X8} type={stackableType} selected={selectedText} rewards(random={stackable.BoosterRewards.Count},select={stackable.BoosterSelectionRewards.Count},package={stackable.PackageRewards.Count},randombox={stackable.RandomBoxRewards.Count})");
                    return false;
                }

                var validRewards = InventoryPackageRewardResolver.NormalizeRewardEntries(rewards);
                if (validRewards.Count == 0)
                    return false;

                var triggeredDouble = isSeriaLuckValueSource
                    && seriaLuckValue >= SqliteAccountRepository.SeriaLuckValueMax;
                if (isSeriaLuckValueSource)
                {
                    FileLogger.Log(
                        $"[BoosterOnline] seria-roll cid={inventory.CharacterId} " +
                        $"item=0x{sourceItemTemplateId:X8} use={useIndex + 1}/{requestedCount} " +
                        $"luckBefore={seriaLuckValue}/{SqliteAccountRepository.SeriaLuckValueMax} " +
                        $"double={triggeredDouble} rewards={FormatRewardEntryList(validRewards)}");
                }

                AddRewardEntries(displayRewardEntries, validRewards);
                if (triggeredDouble)
                {
                    AddRewardEntries(doubleRewardEntries, validRewards);
                    seriaLuckValue = 0;
                }

                if (!isSeriaLuckValueSource)
                    continue;

                seriaLuckValue = Math.Min(SqliteAccountRepository.SeriaLuckValueMax, seriaLuckValue + 1);
            }

            var rewardsToGrant = new List<PvfLib.BoosterRewardEntry>();
            AddRewardEntries(rewardsToGrant, displayRewardEntries);
            AddRewardEntries(rewardsToGrant, doubleRewardEntries, countMultiplier: 2);
            var rewardRequests = BuildRewardRequests(rewardsToGrant);
            if (isSeriaLuckValueSource)
            {
                FileLogger.Log(
                    $"[BoosterOnline] seria-grant-plan cid={inventory.CharacterId} " +
                    $"item=0x{sourceItemTemplateId:X8} displayTotals={FormatRewardEntryTotals(displayRewardEntries)} " +
                    $"doubleTotals={FormatRewardEntryTotals(doubleRewardEntries)} " +
                    $"finalTotals={FormatRewardEntryTotals(rewardsToGrant)} " +
                    $"finalEntries={FormatRewardEntryList(rewardsToGrant)} " +
                    $"requests={FormatRewardRequestTotals(rewardRequests)}");
            }

            if (!TryConsumeAndGrantRewards(
                    inventory,
                    source,
                    requestedCount,
                    material,
                    rewardRequests,
                    overflowSink,
                    result,
                    out var applied))
                return false;

            InventoryItemLifecycleService.ApplyUseSuccess(
                inventory,
                lifecyclePlan);

            result.ErrorCode = 0;
            result.SourceSlotIndex = source.SlotIndex;
            result.SourceItemTemplateId = sourceItemTemplateId;
            result.SourceRemainingStackCount = applied.SourceDelete?.RemainingCount ?? 0;
            result.SourceInstanceValue = sourceInstanceValue;
            result.ConsumedSourceCount = requestedCount;
            result.ConsumedMaterialItemTemplateId = materialItemTemplateId;
            result.ConsumedMaterialCount = material?.RequiredCount ?? 0;
            result.ConsumedMaterialSlotIndex = material?.SlotIndex ?? 0;
            result.ConsumedMaterialRemainingStackCount = applied.MaterialDelete?.RemainingCount ?? 0;
            result.IsSeriaLuckValueSource = isSeriaLuckValueSource;
            result.SeriaLuckValueBefore = seriaLuckValueBefore;
            result.SeriaLuckValueAfter = isSeriaLuckValueSource ? seriaLuckValue : 0;
            result.SeriaLuckValueMax = SqliteAccountRepository.SeriaLuckValueMax;
            result.SeriaLuckDoubleTriggered = doubleRewardEntries.Count > 0;
            AddDisplayRewards(result.DisplayRewards, displayRewardEntries);
            AddDisplayRewards(result.DoubleRewards, doubleRewardEntries);
            AddGrantResults(inventory, applied.GrantBatch, result.Rewards, result.ActivatedPremiums);
            if (isSeriaLuckValueSource)
            {
                FileLogger.Log(
                    $"[BoosterOnline] seria-grant-applied cid={inventory.CharacterId} " +
                    $"item=0x{sourceItemTemplateId:X8} appliedTotals={FormatBoosterRewardTotals(result.Rewards)} " +
                    $"appliedEntries={FormatBoosterRewardList(result.Rewards)}");
            }

            if (isSeriaLuckValueSource
                && !TryUpdateSeriaLuckValue(
                    connection,
                    transaction,
                    inventory,
                    seriaLuckValue))
            {
                databaseAccessFailed = true;
                return false;
            }

            return true;
        }

        internal static bool TryOpenPackage0207(
            InventoryService inventory,
            short slotIndex,
            IReadOnlyList<int> selectedItemTemplateIds,
            IInventoryOverflowRewardSink overflowSink,
            out BoosterUseResult result)
        {
            result = new BoosterUseResult();
            if (!TryGetMainSource(inventory, slotIndex, out var source))
                return false;

            var sourceItemTemplateId = source.Core.ItemId;
            var sourceInstanceValue = source.Core.InstanceValue;
            var stackable = StackableItemProvider.Load(sourceItemTemplateId);
            if (stackable == null)
                return false;

            var stackableType = InventoryPackageRewardResolver.NormalizeStackableType(stackable.StackableType);
            if (!stackableType.Equals("[usable cera package]", StringComparison.OrdinalIgnoreCase)
                && !stackableType.Equals("[booster selection]", StringComparison.OrdinalIgnoreCase))
                return false;

            List<PvfLib.BoosterRewardEntry> rewards;
            if ((selectedItemTemplateIds == null || selectedItemTemplateIds.Count == 0)
                && stackableType.Equals("[usable cera package]", StringComparison.OrdinalIgnoreCase))
            {
                rewards = stackable.PackageRewards.ToList();
            }
            else if (!InventoryPackageRewardResolver.TryResolveClientSelectedRewards(stackable, selectedItemTemplateIds, out rewards))
            {
                var selectedText = selectedItemTemplateIds == null
                    ? "null"
                    : string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"));
                FileLogger.Log($"  [OpenPkg0207Online] PVF validation failed source=0x{sourceItemTemplateId:X8} selected={selectedText}");
                return false;
            }

            if (rewards.Count == 0)
                return false;

            var rewardRequests = BuildRewardRequests(InventoryPackageRewardResolver.NormalizeRewardEntries(rewards));
            if (!TryConsumeAndGrantRewards(
                    inventory,
                    source,
                    1,
                    null,
                    rewardRequests,
                    overflowSink,
                    result,
                    out var applied))
                return false;

            result.ErrorCode = 0;
            result.SourceSlotIndex = source.SlotIndex;
            result.SourceItemTemplateId = sourceItemTemplateId;
            result.SourceRemainingStackCount = applied.SourceDelete?.RemainingCount ?? 0;
            result.SourceInstanceValue = sourceInstanceValue;
            result.ConsumedSourceCount = 1;
            AddGrantResults(inventory, applied.GrantBatch, result.Rewards, result.ActivatedPremiums);
            return true;
        }

        internal static bool TryOpenAvatarPackage(
            InventoryService inventory,
            AvatarPackageOpenRequest request,
            IInventoryOverflowRewardSink overflowSink,
            out AvatarPackageOpenResult result)
        {
            result = null;
            if (request == null || request.Choices.Count == 0)
                return false;

            if (!TryGetMainSource(inventory, request.SlotIndex, out var source))
                return false;

            var packageItemTemplateId = source.Core.ItemId;
            if (!AvatarPackageDefinitionResolver.TryResolve(packageItemTemplateId, out var definition))
                return false;

            if (!ValidateAvatarPackageChoices(definition, request, out var optionByItemId))
                return false;

            var rewardRequests = new List<InventoryRewardGrantRequest>();
            foreach (var reward in definition.Rewards)
            {
                if (reward == null || reward.ItemTemplateId <= 0 || reward.Count <= 0)
                    continue;

                var option = reward.IsAvatar && optionByItemId.TryGetValue(reward.ItemTemplateId, out var abilityNo)
                    ? abilityNo
                    : (byte)0;
                AddRewardRequest(
                    rewardRequests,
                    reward.ItemTemplateId,
                    reward.Count,
                    reward.ExpireTime,
                    option);
            }

            if (!TryConsumeAndGrantRewards(
                    inventory,
                    source,
                    1,
                    null,
                    rewardRequests,
                    overflowSink,
                    null,
                    out var applied))
                return false;

            result = new AvatarPackageOpenResult
            {
                SlotIndex = request.SlotIndex,
                PackageItemTemplateId = packageItemTemplateId,
                SourceRemainingStackCount = applied.SourceDelete?.RemainingCount ?? 0,
            };
            AddPackageGrantResults(inventory, applied.GrantBatch, result.GrantedItems, result.ActivatedPremiums);
            CountPackageGrants(result.GrantedItems, out var mainCount, out var avatarCount, out var petCount);
            result.AddedMainItemCount = mainCount;
            result.AddedAvatarCount = avatarCount;
            result.AddedPetCount = petCount;
            return true;
        }

        internal static bool TryOpenSelectablePackage(
            InventoryService inventory,
            SelectablePackageOpenRequest request,
            IInventoryOverflowRewardSink overflowSink,
            out SelectablePackageOpenResult result)
        {
            result = null;
            if (request == null)
                return false;

            if (!TryGetMainSource(inventory, request.SlotIndex, out var source))
                return false;

            if (source.Core.ExpireTime > 0 && source.Core.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                return false;

            var packageItemTemplateId = source.Core.ItemId;
            if (!SelectablePackageDefinitionResolver.TryResolve(packageItemTemplateId, out var definition))
                return false;

            var rewardRequests = new List<InventoryRewardGrantRequest>();
            PackageRewardEntry rewardForResult = null;
            if (request.HasAvatarChoices)
            {
                if (!DefinitionHasAvatarReward(definition))
                    return false;

                var seenAvatarChoices = new HashSet<int>();
                foreach (var choice in request.AvatarChoices)
                {
                    if (!seenAvatarChoices.Add(choice.ItemTemplateId))
                        return false;
                    if (!SelectablePackageDefinitionResolver.IsAvatarEquipment(choice.ItemTemplateId))
                        return false;

                    if (!definition.TryGetReward(choice.ItemTemplateId, out var avatarReward))
                    {
                        avatarReward = new PackageRewardEntry
                        {
                            ItemTemplateId = choice.ItemTemplateId,
                            Count = 1,
                            ExpireTime = SelectablePackageDefinitionResolver.ResolveItemExpirationUnixTime(choice.ItemTemplateId),
                        };
                    }

                    if (avatarReward.ExpireTime > 0
                        && avatarReward.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                        return false;

                    rewardForResult = rewardForResult ?? avatarReward;
                    AddRewardRequest(
                        rewardRequests,
                        choice.ItemTemplateId,
                        Math.Max(1, avatarReward.Count),
                        avatarReward.ExpireTime,
                        choice.OptionValue);
                }
            }
            else
            {
                if (!definition.TryGetReward(request.SelectedItemTemplateId, out var reward))
                {
                    if (!DefinitionHasAvatarReward(definition)
                        || !SelectablePackageDefinitionResolver.IsAvatarEquipment(request.SelectedItemTemplateId))
                        return false;

                    reward = new PackageRewardEntry
                    {
                        ItemTemplateId = request.SelectedItemTemplateId,
                        Count = 1,
                        ExpireTime = SelectablePackageDefinitionResolver.ResolveItemExpirationUnixTime(request.SelectedItemTemplateId),
                    };
                }

                if (reward.ExpireTime > 0
                    && reward.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                    return false;

                var metadata = ItemMetadataResolver.Resolve(reward.ItemTemplateId);
                if (metadata.ItemKind == "special"
                    && !EpicPieceCatalogService.IsEpicPieceId(reward.ItemTemplateId))
                    return false;

                rewardForResult = reward;
                AddRewardRequest(
                    rewardRequests,
                    reward.ItemTemplateId,
                    reward.Count,
                    reward.ExpireTime,
                    request.SelectionFlag);
            }

            if (rewardForResult == null)
                rewardForResult = new PackageRewardEntry
                {
                    ItemTemplateId = request.SelectedItemTemplateId,
                    Count = Math.Max(1, request.AvatarChoices.Count),
                };

            if (!TryConsumeAndGrantRewards(
                    inventory,
                    source,
                    1,
                    null,
                    rewardRequests,
                    overflowSink,
                    null,
                    out var applied))
                return false;

            result = new SelectablePackageOpenResult
            {
                SlotIndex = request.SlotIndex,
                PackageItemTemplateId = packageItemTemplateId,
                SourceRemainingStackCount = applied.SourceDelete?.RemainingCount ?? 0,
                RewardItemTemplateId = rewardForResult.ItemTemplateId,
            };
            AddPackageGrantResults(inventory, applied.GrantBatch, result.GrantedItems, result.ActivatedPremiums);
            CountPackageGrants(result.GrantedItems, out var mainCount, out var avatarCount, out var petCount);
            result.AddedMainItemCount = mainCount;
            result.AddedAvatarCount = avatarCount;
            result.AddedPetCount = petCount;
            return true;
        }

        internal static string ResolveCharacterJobLabel(byte job)
        {
            string[] labels =
            {
                "swordman", "fighter", "gunner", "mage", "priest",
                "at gunner", "thief", "at fighter", "at mage",
                "demonic swordman", "creator mage", "at swordman", "knight",
            };
            var index = (int)job;
            return index < labels.Length ? labels[index] : null;
        }

        private static bool TryConsumeAndGrantRewards(
            InventoryService inventory,
            SourceContext source,
            int sourceCount,
            MaterialContext material,
            IReadOnlyList<InventoryRewardGrantRequest> rewardRequests,
            IInventoryOverflowRewardSink overflowSink,
            BoosterUseResult errorResult,
            out ConsumeAndGrantResult result)
        {
            result = null;
            if (inventory == null || source == null || source.Core == null || sourceCount <= 0)
                return false;

            rewardRequests = rewardRequests ?? Array.Empty<InventoryRewardGrantRequest>();
            var planningInventory = CreatePlanningInventory(inventory);
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    planningInventory,
                    InventoryListType.Main,
                    source.SlotIndex,
                    source.Core.ItemId,
                    sourceCount,
                    out _))
                return false;

            if (material != null
                && !InventoryDeleteService.TryConsumeFromSlot(
                    planningInventory,
                    InventoryListType.Main,
                    material.SlotIndex,
                    material.Core.ItemId,
                    material.RequiredCount,
                    out _))
                return false;

            if (!TryPlanDirectRewards(
                    planningInventory,
                    rewardRequests,
                    out var directPlan,
                    out var overflowRewards))
                return false;

            if (overflowRewards.Count > 0)
            {
                overflowSink = overflowSink ?? RejectingInventoryOverflowRewardSink.Instance;
                if (!overflowSink.TryDeliver(inventory, overflowRewards, out _))
                {
                    if (errorResult != null)
                        errorResult.ErrorCode = BoosterUseResult.ErrorInventoryFull;
                    return false;
                }
            }

            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    source.SlotIndex,
                    source.Core.ItemId,
                    sourceCount,
                    out var sourceDelete))
                return false;

            InventoryDeleteResult materialDelete = null;
            if (material != null
                && !InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    material.SlotIndex,
                    material.Core.ItemId,
                    material.RequiredCount,
                    out materialDelete))
                return false;

            if (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, directPlan, out var grantBatch)
                || !grantBatch.Success)
            {
                if (errorResult != null)
                    errorResult.ErrorCode = BoosterUseResult.ErrorInventoryFull;
                return false;
            }

            result = new ConsumeAndGrantResult
            {
                SourceDelete = sourceDelete,
                MaterialDelete = materialDelete,
                GrantBatch = grantBatch,
            };
            return true;
        }

        internal static bool TryPlanDirectRewards(
            InventoryService planningInventory,
            IReadOnlyList<InventoryRewardGrantRequest> requests,
            out InventoryRewardGrantBatchPlan directPlan,
            out List<InventoryRewardGrantRequest> overflowRewards)
        {
            directPlan = new InventoryRewardGrantBatchPlan
            {
                Success = true,
                Error = InventoryRewardGrantError.None,
            };
            overflowRewards = new List<InventoryRewardGrantRequest>();
            if (requests == null || requests.Count == 0)
                return true;

            foreach (var request in requests)
            {
                if (TryPlanAndReserveRewardRequest(
                        planningInventory,
                        request,
                        out var singlePlan,
                        out var error))
                {
                    AddPlanEntries(directPlan, singlePlan);
                    continue;
                }

                if (error != InventoryRewardGrantError.InsertPlanFailed)
                    return false;

                var requestedCount = ResolveRewardRequestCount(request);
                if (requestedCount <= 1)
                {
                    overflowRewards.Add(CloneRewardRequest(request, Math.Max(1, requestedCount)));
                    continue;
                }

                var directCount = FindLargestPlannableRewardCount(planningInventory, request, requestedCount);
                if (directCount > 0)
                {
                    var directRequest = CloneRewardRequest(request, directCount);
                    if (!TryPlanAndReserveRewardRequest(
                            planningInventory,
                            directRequest,
                            out var partialPlan,
                            out _))
                        return false;

                    AddPlanEntries(directPlan, partialPlan);
                }

                var overflowCount = requestedCount - directCount;
                if (overflowCount > 0)
                    overflowRewards.Add(CloneRewardRequest(request, overflowCount));
            }

            return true;
        }

        private static bool TryPlanAndReserveRewardRequest(
            InventoryService planningInventory,
            InventoryRewardGrantRequest request,
            out InventoryRewardGrantBatchPlan plan,
            out InventoryRewardGrantError error)
        {
            plan = null;
            error = InventoryRewardGrantError.None;
            if (!InventoryRewardGrantService.TryPlanBatch(
                    planningInventory,
                    new[] { request },
                    out plan)
                || plan == null
                || !plan.Success)
            {
                error = plan != null ? plan.Error : InventoryRewardGrantError.InvalidRequest;
                return false;
            }

            if (!ReservePlanOnPlanningInventory(planningInventory, plan))
            {
                error = InventoryRewardGrantError.InsertPlanFailed;
                return false;
            }

            return true;
        }

        private static int ResolveRewardRequestCount(InventoryRewardGrantRequest request)
        {
            var count = request != null ? request.Count : 0;
            if (count <= 0 && request != null && request.UseExistingCore && request.Core != null)
                count = InventoryStackRuleService.NormalizeInsertCount(request.Core, count);

            return count;
        }

        private static int FindLargestPlannableRewardCount(
            InventoryService planningInventory,
            InventoryRewardGrantRequest request,
            int requestedCount)
        {
            var low = 1;
            var high = Math.Max(0, requestedCount);
            var best = 0;
            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                if (CanPlanRewardRequestCount(planningInventory, request, mid))
                {
                    best = mid;
                    low = mid + 1;
                    continue;
                }

                high = mid - 1;
            }

            return best;
        }

        private static bool CanPlanRewardRequestCount(
            InventoryService planningInventory,
            InventoryRewardGrantRequest request,
            int count)
        {
            if (count <= 0)
                return false;

            var candidate = CloneRewardRequest(request, count);
            return InventoryRewardGrantService.TryPlanBatch(
                    planningInventory,
                    new[] { candidate },
                    out var plan)
                && plan != null
                && plan.Success;
        }

        private static InventoryRewardGrantRequest CloneRewardRequest(
            InventoryRewardGrantRequest request,
            int count)
        {
            if (request == null)
                return null;

            count = Math.Max(1, count);
            return request.UseExistingCore
                ? InventoryRewardGrantRequest.Existing(
                    request.Core?.Copy(),
                    count,
                    request.Reason,
                    request.CreateOptions)
                : InventoryRewardGrantRequest.Create(
                    request.ItemTemplateId,
                    count,
                    request.Reason,
                    request.CreateOptions);
        }

        private static void AddPlanEntries(
            InventoryRewardGrantBatchPlan target,
            InventoryRewardGrantBatchPlan source)
        {
            if (target == null || source == null)
                return;

            foreach (var entry in source.Entries)
                target.AddEntry(entry);
        }

        private static bool ReservePlanOnPlanningInventory(
            InventoryService planningInventory,
            InventoryRewardGrantBatchPlan plan)
        {
            if (planningInventory == null || plan == null || !plan.Success)
                return false;

            foreach (var entry in plan.Entries)
            {
                switch (entry.Kind)
                {
                    case InventoryRewardGrantKind.Premium:
                        break;
                    case InventoryRewardGrantKind.AccountCurrency:
                        if (entry.SpecialOutcome == null
                            || entry.SpecialOutcome.Kind != SpecialRewardKind.HappyTokenCera
                            || !planningInventory.TryQueueHappyTokenCeraGrant(entry.GrantedCount))
                            return false;
                        break;
                    case InventoryRewardGrantKind.MainVirtualCount:
                        if (!planningInventory.SetMainVirtualCount(
                                entry.SlotIndex,
                                entry.SlotItemId,
                                entry.FinalCount))
                            return false;
                        break;
                    case InventoryRewardGrantKind.EpicPiece:
                        if (!planningInventory.EpicPieces.TryAddByPieceId(
                                entry.ItemTemplateId,
                                entry.GrantedCount,
                                out _))
                            return false;
                        break;
                    case InventoryRewardGrantKind.InventoryItem:
                        if (!InventoryInsertService.TryApplyInsertPlan(
                                planningInventory,
                                entry.Core,
                                entry.InsertPlan,
                                out var insertResult)
                            || !insertResult.Success)
                            return false;
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        internal static List<InventoryRewardGrantRequest> BuildRewardRequests(
            IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            return BuildRewardRequests(rewards, skipExpiredStaticItems: false);
        }

        internal static List<InventoryRewardGrantRequest> BuildRewardRequests(
            IEnumerable<PvfLib.BoosterRewardEntry> rewards,
            bool skipExpiredStaticItems)
        {
            var requests = new List<InventoryRewardGrantRequest>();
            if (rewards == null)
                return requests;

            foreach (var reward in rewards)
            {
                if (reward == null || reward.ItemId <= 0 || reward.Count <= 0)
                    continue;

                AddRewardRequest(
                    requests,
                    reward.ItemId,
                    reward.Count,
                    ResolveUsablePeriodExpireTime(reward.UsablePeriodDays),
                    0,
                    skipExpiredStaticItems);
            }

            return requests;
        }

        internal static void AddRewardRequest(
            List<InventoryRewardGrantRequest> requests,
            int itemTemplateId,
            int count,
            int expireTime,
            byte avatarAbilityNo,
            bool skipExpiredStaticItems = false)
        {
            if (requests == null || itemTemplateId <= 0 || count <= 0)
                return;

            if (skipExpiredStaticItems && IsStaticExpirationExpired(itemTemplateId))
            {
                FileLogger.Log($"[InventoryReward] skip expired static reward item=0x{itemTemplateId:X8}");
                return;
            }

            var options = CreateOptions(expireTime, avatarAbilityNo);
            var requestCount = ShouldSplitNonStackableReward(itemTemplateId)
                ? 1
                : count;
            var repeat = requestCount == 1 && count > 1 && ShouldSplitNonStackableReward(itemTemplateId)
                ? count
                : 1;

            for (var index = 0; index < repeat; index++)
                requests.Add(InventoryRewardGrantRequest.Create(
                    itemTemplateId,
                    requestCount,
                    ItemCreateReason.PackageOpen,
                    options));
        }

        private static bool ShouldSplitNonStackableReward(int itemTemplateId)
        {
            if (itemTemplateId <= 0
                || itemTemplateId == 0
                || itemTemplateId == 2
                || ReviveCoinService.IsReviveCoinReward(itemTemplateId)
                || InventoryService.TryResolveMainVirtualSlotByItemId(itemTemplateId, out _, out _)
                || PremiumService.IsContractItem(itemTemplateId))
                return false;

            try
            {
                var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
                return metadata != null && !metadata.IsStackable;
            }
            catch
            {
                return false;
            }
        }

        private static InventoryCreateOptions CreateOptions(int expireTime, byte avatarAbilityNo)
        {
            if (expireTime <= 0 && avatarAbilityNo == 0)
                return null;

            return new InventoryCreateOptions
            {
                ExpireTime = expireTime,
                AvatarAbilityNo = avatarAbilityNo,
            };
        }

        private static int ResolveUsablePeriodExpireTime(int usablePeriodDays)
        {
            return usablePeriodDays > 0
                ? PvfExpirationMetadata.AddDaysFromNow(usablePeriodDays)
                : 0;
        }

        internal static bool IsStaticExpirationExpired(int itemTemplateId)
        {
            var expireTime = ResolveStaticExpirationTime(itemTemplateId);
            return expireTime > 0 && expireTime <= DateTimeOffset.Now.ToUnixTimeSeconds();
        }

        private static int ResolveStaticExpirationTime(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return 0;

            try
            {
                if (ItemMetadataResolver.TryLoadStackableFile(itemTemplateId, out var stackable)
                    && StackableExpirationPolicyResolver.TryResolve(stackable, out var stackablePolicy))
                    return stackablePolicy.AbsoluteExpirationUnixTime;

                if (ItemMetadataResolver.TryLoadEquipmentFile(itemTemplateId, out var equipment)
                    && EquipmentExpirationPolicyResolver.TryResolve(equipment, out var equipmentPolicy))
                    return equipmentPolicy.AbsoluteExpirationUnixTime;
            }
            catch
            {
                return 0;
            }

            return 0;
        }

        private static bool TryResolveBoosterSource(
            InventoryService inventory,
            short? slotIndex,
            int expectedItemTemplateId,
            out SourceContext source)
        {
            source = null;
            if (slotIndex.HasValue)
                TryGetMainSource(inventory, slotIndex.Value, out source, allowExpired: true);
            else
                source = FindFirstPackageItem(inventory, allowExpired: true);

            if (source != null && (expectedItemTemplateId <= 0 || source.Core.ItemId == expectedItemTemplateId))
                return true;

            if (expectedItemTemplateId <= 0)
                return source != null;

            if (!TryFindMainItemByTemplateIdInMetadataRange(
                    inventory,
                    expectedItemTemplateId,
                    out var fallback,
                    allowExpired: true))
                return false;

            if (slotIndex.HasValue && fallback.SlotIndex != slotIndex.Value)
            {
                FileLogger.Log($"  [BoosterOnline] WARN: source slot stale requested={slotIndex.Value}, actual={fallback.SlotIndex}, item=0x{expectedItemTemplateId:X8}");
            }

            source = fallback;
            return true;
        }

        private static bool TryGetMainSource(
            InventoryService inventory,
            short slotIndex,
            out SourceContext source,
            bool allowExpired = false)
        {
            source = null;
            if (inventory == null)
                return false;

            var core = inventory.GetItem(InventoryListType.Main, slotIndex);
            if (core == null || core.ItemId <= 0 || core.Count <= 0)
                return false;

            if (!allowExpired
                && core.ExpireTime > 0
                && core.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                return false;

            source = new SourceContext
            {
                SlotIndex = slotIndex,
                Core = core,
            };
            return true;
        }

        private static bool ValidateSourceCount(ItemCore source, int count)
        {
            return source != null
                && count > 0
                && InventoryStackRuleService.IsStackable(source)
                && source.Count >= count;
        }

        private static bool TryResolveMaterial(
            InventoryService inventory,
            short? requestedSlotIndex,
            int materialItemTemplateId,
            int requiredCount,
            BoosterUseResult result,
            out MaterialContext material)
        {
            material = null;
            if (materialItemTemplateId <= 0 || requiredCount <= 0)
                return true;

            SourceContext found = null;
            if (requestedSlotIndex.HasValue)
            {
                var requested = inventory?.GetItem(InventoryListType.Main, requestedSlotIndex.Value);
                if (requested != null && requested.ItemId == materialItemTemplateId)
                {
                    found = new SourceContext
                    {
                        SlotIndex = requestedSlotIndex.Value,
                        Core = requested,
                    };
                }
            }

            if (found == null)
            {
                if (!TryFindMainItemByTemplateIdInMetadataRange(inventory, materialItemTemplateId, out found))
                {
                    SetMaterialNotEnoughResult(result, materialItemTemplateId, requiredCount, 0);
                    return false;
                }

                if (requestedSlotIndex.HasValue && found.SlotIndex != requestedSlotIndex.Value)
                {
                    FileLogger.Log($"  [BoosterOnline] WARN: material slot stale requested={requestedSlotIndex.Value}, actual={found.SlotIndex}, item=0x{materialItemTemplateId:X8}");
                }
            }

            if (found.Core == null || found.Core.Count < requiredCount)
            {
                SetMaterialNotEnoughResult(
                    result,
                    materialItemTemplateId,
                    requiredCount,
                    Math.Max(0, found.Core?.Count ?? 0));
                return false;
            }

            material = new MaterialContext
            {
                SlotIndex = found.SlotIndex,
                Core = found.Core,
                RequiredCount = requiredCount,
            };
            return true;
        }

        private static bool TryFindMainItemByTemplateIdInMetadataRange(
            InventoryService inventory,
            int itemTemplateId,
            out SourceContext source,
            bool allowExpired = false)
        {
            source = null;
            if (inventory == null || itemTemplateId <= 0)
                return false;

            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            metadata.GetSlotRange(out var slotStart, out var slotEnd);
            for (var slot = slotStart; slot <= slotEnd; slot++)
            {
                var core = inventory.GetItem(InventoryListType.Main, (short)slot);
                if (core == null || core.ItemId != itemTemplateId)
                    continue;
                if (!allowExpired
                    && core.ExpireTime > 0
                    && core.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                {
                    continue;
                }

                source = new SourceContext
                {
                    SlotIndex = (short)slot,
                    Core = core,
                };
                return true;
            }

            return false;
        }

        private static SourceContext FindFirstPackageItem(
            InventoryService inventory,
            bool allowExpired = false)
        {
            if (inventory == null)
                return null;

            foreach (var pair in inventory.GetItems(InventoryListType.Main))
            {
                var core = pair.Value;
                if (core == null || core.ItemId <= 0 || core.Count <= 0)
                    continue;
                if (!allowExpired
                    && core.ExpireTime > 0
                    && core.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                {
                    continue;
                }

                var stackable = StackableItemProvider.Load(core.ItemId);
                if (stackable == null)
                    continue;

                var stackableType = InventoryPackageRewardResolver.NormalizeStackableType(stackable.StackableType);
                if (!InventoryPackageRewardResolver.IsSupportedPackageType(stackableType))
                    continue;

                return new SourceContext
                {
                    SlotIndex = pair.Key,
                    Core = core,
                };
            }

            return null;
        }

        internal static InventoryService CreatePlanningInventory(InventoryService source)
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

        private static void AddGrantResults(
            InventoryService inventory,
            InventoryRewardGrantBatchResult batch,
            List<BoosterRewardResult> rewards,
            List<(int itemTemplateId, int count)> activatedPremiums)
        {
            if (batch == null)
                return;

            foreach (var grant in batch.Results)
            {
                var reward = ToBoosterRewardResult(inventory, grant);
                if (reward != null)
                    rewards.Add(reward);
            }

            foreach (var premium in batch.ActivatedPremiums)
                activatedPremiums.Add(premium);
        }

        private static void AddPackageGrantResults(
            InventoryService inventory,
            InventoryRewardGrantBatchResult batch,
            List<PackageGrantedItem> grantedItems,
            List<(int itemTemplateId, int count)> activatedPremiums)
        {
            if (batch == null)
                return;

            foreach (var grant in batch.Results)
            {
                var reward = ToPackageGrantedItem(inventory, grant);
                if (reward != null)
                    grantedItems.Add(reward);
            }

            foreach (var premium in batch.ActivatedPremiums)
                activatedPremiums.Add(premium);
        }

        private static BoosterRewardResult ToBoosterRewardResult(
            InventoryService inventory,
            InventoryRewardGrantResult grant)
        {
            if (grant == null || !grant.Success)
                return null;

            if (grant.SpecialOutcome != null)
                return BoosterRewardResult.FromSpecialOutcome(grant.SpecialOutcome);

            var core = grant.Kind == InventoryRewardGrantKind.InventoryItem
                ? inventory?.GetItem(grant.ListType, grant.SlotIndex)
                : null;
            return new BoosterRewardResult
            {
                ListType = grant.ListType,
                SlotIndex = grant.SlotIndex,
                ItemTemplateId = grant.ItemTemplateId,
                StackCount = ResolveStackCount(core, grant),
                GrantedCount = grant.GrantedCount,
                Durability = core != null ? core.Durability : (ushort)0,
                Attr = core != null ? core.Attr : (byte)0,
                ExpireTime = core != null ? core.ExpireTime : 0,
                CoreSnapshot = core?.Copy(),
            };
        }

        private static PackageGrantedItem ToPackageGrantedItem(
            InventoryService inventory,
            InventoryRewardGrantResult grant)
        {
            var reward = ToBoosterRewardResult(inventory, grant);
            if (reward == null)
                return null;

            return new PackageGrantedItem
            {
                ListType = reward.ListType,
                SlotIndex = reward.SlotIndex,
                ItemTemplateId = reward.ItemTemplateId,
                DisplayCount = reward.GrantedCount <= 0 ? 1 : reward.GrantedCount,
                Durability = reward.Durability,
                Attr = reward.Attr,
                ExpireTime = reward.ExpireTime,
                SpecialOutcome = reward.SpecialOutcome,
            };
        }

        private static int ResolveStackCount(ItemCore core, InventoryRewardGrantResult grant)
        {
            if (grant == null)
                return 0;

            if (grant.Kind == InventoryRewardGrantKind.MainVirtualCount)
                return grant.FinalCount;

            if (core == null)
                return 0;

            if (InventoryStackRuleService.IsStackable(core))
                return core.Count;

            return grant.ListType == InventoryListType.Main ? core.InstanceValue : 0;
        }

        private static void CountPackageGrants(
            IReadOnlyList<PackageGrantedItem> grantedItems,
            out int mainCount,
            out int avatarCount,
            out int petCount)
        {
            mainCount = 0;
            avatarCount = 0;
            petCount = 0;
            foreach (var item in grantedItems ?? Array.Empty<PackageGrantedItem>())
            {
                var count = Math.Max(1, item.DisplayCount);
                if (item.ListType == InventoryListType.Avatar)
                    avatarCount += count;
                else if (item.ListType == InventoryListType.Pet)
                    petCount += count;
                else if (item.ListType == InventoryListType.Main)
                    mainCount += count;
            }
        }

        private static bool ValidateAvatarPackageChoices(
            AvatarPackageDefinition definition,
            AvatarPackageOpenRequest request,
            out Dictionary<int, byte> optionByItemId)
        {
            optionByItemId = new Dictionary<int, byte>();
            if (definition == null || request == null)
                return false;

            if (request.Choices.Count != definition.AvatarItemIds.Count)
                return false;

            foreach (var choice in request.Choices)
            {
                if (!definition.AvatarItemIds.Contains(choice.ItemTemplateId))
                    return false;
                if (optionByItemId.ContainsKey(choice.ItemTemplateId))
                    return false;

                optionByItemId[choice.ItemTemplateId] = choice.OptionValue;
            }

            return true;
        }

        private static bool DefinitionHasAvatarReward(SelectablePackageDefinition definition)
        {
            if (definition == null || definition.Rewards == null)
                return false;

            foreach (var reward in definition.Rewards)
                if (SelectablePackageDefinitionResolver.IsAvatarEquipment(reward.ItemTemplateId))
                    return true;

            return false;
        }

        private static void AddDisplayRewards(
            List<PackageGrantedItem> target,
            IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (target == null || rewards == null)
                return;

            foreach (var reward in rewards)
            {
                if (reward == null || reward.ItemId <= 0 || reward.Count <= 0)
                    continue;

                target.Add(new PackageGrantedItem
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = 0,
                    ItemTemplateId = reward.ItemId,
                    DisplayCount = Math.Max(1, reward.Count),
                    Durability = 0,
                    ExpireTime = ResolveUsablePeriodExpireTime(reward.UsablePeriodDays),
                });
            }
        }

        private static void AddRewardEntries(
            List<PvfLib.BoosterRewardEntry> target,
            IEnumerable<PvfLib.BoosterRewardEntry> rewards,
            int countMultiplier = 1)
        {
            if (target == null || rewards == null)
                return;

            countMultiplier = Math.Max(1, countMultiplier);
            foreach (var reward in rewards)
            {
                if (reward == null || reward.ItemId <= 0 || reward.Count <= 0)
                    continue;

                target.Add(new PvfLib.BoosterRewardEntry
                {
                    ItemId = reward.ItemId,
                    Count = Math.Max(1, reward.Count) * countMultiplier,
                    Weight = reward.Weight,
                    Group = reward.Group,
                    DrawCount = reward.DrawCount,
                    CharacterJobLabel = reward.CharacterJobLabel,
                    UsablePeriodDays = reward.UsablePeriodDays,
                });
            }
        }

        private static string FormatRewardEntryList(IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (rewards == null)
                return "none";

            var parts = rewards
                .Where(reward => reward != null && reward.ItemId > 0 && reward.Count > 0)
                .Select(reward => $"0x{reward.ItemId:X8}x{Math.Max(1, reward.Count)}")
                .ToList();
            return parts.Count > 0 ? string.Join(",", parts) : "none";
        }

        private static string FormatRewardEntryTotals(IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (rewards == null)
                return "none";

            var parts = rewards
                .Where(reward => reward != null && reward.ItemId > 0 && reward.Count > 0)
                .GroupBy(reward => reward.ItemId)
                .Select(group => $"0x{group.Key:X8}x{group.Sum(reward => Math.Max(1, reward.Count))}")
                .ToList();
            return parts.Count > 0 ? string.Join(",", parts) : "none";
        }

        private static string FormatRewardRequestTotals(IEnumerable<InventoryRewardGrantRequest> requests)
        {
            if (requests == null)
                return "none";

            var parts = requests
                .Where(request => request != null)
                .Select(request => new
                {
                    ItemId = request.UseExistingCore && request.Core != null
                        ? request.Core.ItemId
                        : request.ItemTemplateId,
                    Count = request.Count,
                })
                .Where(request => request.ItemId > 0 && request.Count > 0)
                .GroupBy(request => request.ItemId)
                .Select(group => $"0x{group.Key:X8}x{group.Sum(request => Math.Max(1, request.Count))}")
                .ToList();
            return parts.Count > 0 ? string.Join(",", parts) : "none";
        }

        private static string FormatBoosterRewardList(IEnumerable<BoosterRewardResult> rewards)
        {
            if (rewards == null)
                return "none";

            var parts = rewards
                .Where(reward => reward != null && reward.ItemTemplateId > 0 && reward.GrantedCount > 0)
                .Select(reward => $"{reward.ListType}:0x{reward.ItemTemplateId:X8}x{reward.GrantedCount}@{reward.SlotIndex}")
                .ToList();
            return parts.Count > 0 ? string.Join(",", parts) : "none";
        }

        private static string FormatBoosterRewardTotals(IEnumerable<BoosterRewardResult> rewards)
        {
            if (rewards == null)
                return "none";

            var parts = rewards
                .Where(reward => reward != null && reward.ItemTemplateId > 0 && reward.GrantedCount > 0)
                .GroupBy(reward => reward.ItemTemplateId)
                .Select(group => $"0x{group.Key:X8}x{group.Sum(reward => Math.Max(1, reward.GrantedCount))}")
                .ToList();
            return parts.Count > 0 ? string.Join(",", parts) : "none";
        }

        private static void SetMaterialNotEnoughResult(
            BoosterUseResult result,
            int materialItemTemplateId,
            int requiredCount,
            int availableCount)
        {
            if (result == null)
                return;

            result.ErrorCode = BoosterUseResult.ErrorMaterialNotEnough;
            result.RequiredMaterialItemTemplateId = materialItemTemplateId;
            result.RequiredMaterialName = InventoryPackageRewardResolver.ResolveBoosterItemName(materialItemTemplateId);
            result.RequiredMaterialCount = requiredCount;
            result.AvailableMaterialCount = Math.Max(0, availableCount);
        }

        private static bool TryLoadSeriaLuckValue(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            out int value)
        {
            value = 0;
            var accountId = inventory?.AccountId ?? 0;
            try
            {
                if (connection != null)
                {
                    value = SqliteAccountRepository.LoadSeriaLuckValue(
                        connection,
                        transaction,
                        accountId);
                    return true;
                }

                var database = inventory?.Database
                    ?? GameDatabase.CreateDefault();
                using (var standaloneConnection = database.OpenConnection())
                {
                    value = SqliteAccountRepository.LoadSeriaLuckValue(
                        standaloneConnection,
                        null,
                        accountId);
                    return true;
                }
            }
            catch (Exception exception)
            {
                FileLogger.Log($"  [BoosterOnline] load Seria luck failed account={accountId}: {exception.Message}");
                return false;
            }
        }

        private static bool TryUpdateSeriaLuckValue(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            int value)
        {
            var accountId = inventory?.AccountId ?? 0;
            try
            {
                if (connection != null)
                {
                    SqliteAccountRepository.UpdateSeriaLuckValue(
                        connection,
                        transaction,
                        accountId,
                        value);
                    return true;
                }

                var database = inventory?.Database
                    ?? GameDatabase.CreateDefault();
                using (var standaloneConnection = database.OpenConnection())
                {
                    SqliteAccountRepository.UpdateSeriaLuckValue(
                        standaloneConnection,
                        null,
                        accountId,
                        value);
                    return true;
                }
            }
            catch (Exception exception)
            {
                FileLogger.Log($"  [BoosterOnline] update Seria luck failed account={accountId}: {exception.Message}");
                return false;
            }
        }
    }
}
