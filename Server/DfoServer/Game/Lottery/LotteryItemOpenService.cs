using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Lottery
{
    public sealed class LotteryItemOpenService
    {
        private readonly string _connectionString;
        private readonly LotteryItemDefinitionProvider _definitions;
        private readonly LotteryDoubleRewardPolicy _doubleRewardPolicy;
        private readonly IncreaseChanceLotteryProgressRepository _progressRepository;

        public LotteryItemOpenService(
            string connectionString,
            LotteryItemDefinitionProvider definitions,
            LotteryDoubleRewardPolicy doubleRewardPolicy)
        {
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException("A database connection string is required.", nameof(connectionString));
            _definitions = definitions
                ?? throw new ArgumentNullException(nameof(definitions));
            _doubleRewardPolicy = doubleRewardPolicy
                ?? throw new ArgumentNullException(nameof(doubleRewardPolicy));
            _progressRepository = new IncreaseChanceLotteryProgressRepository(_connectionString);
        }

        internal bool CanOpen(
            InventoryService inventory,
            short slotIndex,
            out LotterySourceContext sourceContext)
        {
            sourceContext = null;
            if (!TryResolveSource(
                    inventory,
                    slotIndex,
                    out var source,
                    out var definition))
            {
                return false;
            }

            if (!UsableCountLimitService.CanUse(
                    _connectionString,
                    inventory.CharacterId,
                    source.Core.ItemId))
            {
                return false;
            }

            if (inventory.CountMainItem(0) < definition.GoldCost)
                return false;
            if (HasRequiredItemCost(definition)
                && inventory.CountMainItem(definition.RequiredItemTemplateId) < definition.RequiredItemCount)
                return false;

            sourceContext = new LotterySourceContext
            {
                SlotIndex = source.SlotIndex,
                ItemTemplateId = source.Core.ItemId,
                StackCount = source.Core.Count,
            };
            return true;
        }

        internal bool TryResetProgress(
            InventoryLease lease,
            int accountId,
            short slotIndex,
            int expectedItemTemplateId,
            out LotteryProgressSnapshot progress,
            out int updatedGold)
        {
            progress = null;
            updatedGold = lease?.Inventory?.CountMainItem(0) ?? 0;
            if (lease?.Inventory == null)
                return false;

            lock (lease.SyncRoot)
            {
                var inventory = lease.Inventory;
                if (!TryResolveSource(
                        inventory,
                        slotIndex,
                        out var source,
                        out var definition)
                    || source.Core.ItemId != expectedItemTemplateId
                    || !definition.UsesIncreaseChanceProgress)
                {
                    return false;
                }

                var resetCost = definition.ProgressResetGoldCost;
                var currentGold = inventory.CountMainItem(0);
                updatedGold = currentGold;
                if (currentGold < resetCost)
                    return false;

                var committedGold = currentGold;
                var committed = OnlineInventoryMutationCommitCoordinator
                    .TryCommit(
                        lease,
                        "increase-chance-lottery-reset",
                        (connection, transaction) =>
                        {
                            if (resetCost > 0
                                && (!inventory.TryConsumeMainItem(
                                        0,
                                        resetCost,
                                        out var consume)
                                    || !consume.Success))
                            {
                                return false;
                            }

                            committedGold = currentGold - resetCost;
                            _progressRepository.Reset(
                                connection,
                                transaction,
                                accountId,
                                definition.ItemTemplateId);
                            return true;
                        });
                if (!committed)
                    return false;

                updatedGold = committedGold;
                progress = new LotteryProgressSnapshot
                {
                    ItemTemplateId = definition.ItemTemplateId,
                    NewRewardIndex = -1,
                };
                return true;
            }
        }

        internal bool TryOpen(
            InventoryService inventory,
            short slotIndex,
            bool useDoubleReward,
            IInventoryOverflowRewardSink overflowSink,
            out LotteryOpenResult result)
        {
            return TryOpen(inventory, inventory?.AccountId ?? 0, slotIndex, useDoubleReward, overflowSink, out result);
        }

        internal bool TryCreateReservation(
            InventoryService inventory,
            int accountId,
            short slotIndex,
            LotteryOpenPlan openPlan,
            out LotteryOpenReservation reservation)
        {
            reservation = null;
            if (!TryResolveSource(
                    inventory,
                    slotIndex,
                    out var source,
                    out var definition))
            {
                return false;
            }

            if (inventory.CountMainItem(0) < definition.GoldCost
                || (HasRequiredItemCost(definition)
                    && inventory.CountMainItem(definition.RequiredItemTemplateId)
                        < definition.RequiredItemCount))
            {
                return false;
            }

            var claimed = definition.UsesIncreaseChanceProgress
                ? _progressRepository.Load(accountId, definition.ItemTemplateId)
                : new HashSet<int>();
            var selectedRewards = definition.UsesIncreaseChanceProgress
                ? RollProgressReward(definition.RewardPool, claimed)
                : RollRewards(definition.RewardPool);
            if (selectedRewards.Count == 0)
                return false;

            var progressRewardIndex = definition.UsesIncreaseChanceProgress
                ? FindRewardIndex(definition.RewardPool, selectedRewards[0])
                : -1;
            if (definition.UsesIncreaseChanceProgress
                && progressRewardIndex < 0)
            {
                return false;
            }

            reservation = new LotteryOpenReservation(
                source.SlotIndex,
                source.Core.ItemId,
                openPlan ?? LotteryOpenPlan.ConfirmedRegular())
            {
                SelectedRewards = selectedRewards.ToArray(),
                ProgressRewardIndex = progressRewardIndex,
            };
            return true;
        }

        internal bool TryOpen(
            InventoryLease lease,
            int accountId,
            LotteryOpenReservation reservation,
            IInventoryOverflowRewardSink overflowSink,
            out LotteryOpenResult result)
        {
            result = null;
            if (lease?.Inventory == null
                || reservation == null
                || reservation.SelectedRewards == null
                || reservation.SelectedRewards.Count == 0)
            {
                return false;
            }

            LotteryOpenResult committedResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "lottery-open",
                (connection, transaction) => TryApplyReservedOpen(
                    connection,
                    transaction,
                    lease.Inventory,
                    accountId,
                    reservation,
                    overflowSink,
                    out committedResult));
            if (!committed || committedResult == null)
                return false;

            result = committedResult;
            return !committedResult.SourceExpiredDeleted;
        }

        internal bool TryOpen(
            InventoryService inventory,
            int accountId,
            short slotIndex,
            bool useDoubleReward,
            IInventoryOverflowRewardSink overflowSink,
            out LotteryOpenResult result)
        {
            result = null;
            if (inventory == null)
                return false;

            if (!TryResolveSource(
                    inventory,
                    slotIndex,
                    out var source,
                    out var definition,
                    allowExpired: true))
            {
                return false;
            }

            var lifecyclePlan = InventoryItemLifecycleService.PrepareUse(
                inventory,
                InventoryListType.Main,
                source.SlotIndex,
                source.Core.ItemId,
                InventoryItemLifecycleService.UtcNowUnixSeconds());
            if (lifecyclePlan.SourceExpiredDeleted)
            {
                result = new LotteryOpenResult
                {
                    SourceExpiredDeleted = true,
                    SourceSlotIndex = source.SlotIndex,
                    SourceItemTemplateId = source.Core.ItemId,
                    SourceRemainingStackCount = 0,
                };
                return false;
            }

            if (!lifecyclePlan.Success)
                return false;

            var currentGold = inventory.CountMainItem(0);
            if (currentGold < definition.GoldCost)
                return false;

            var claimed = definition.UsesIncreaseChanceProgress
                ? _progressRepository.Load(accountId, definition.ItemTemplateId)
                : new HashSet<int>();
            var selectedRewards = definition.UsesIncreaseChanceProgress
                ? RollProgressReward(definition.RewardPool, claimed)
                : RollRewards(definition.RewardPool);
            if (selectedRewards.Count == 0)
                return false;

            var regularRewards = AggregateRewardEntries(selectedRewards, 1).ToList();
            var regularRequests = BuildLotteryRewardRequests(regularRewards);
            if (definition.UsesIncreaseChanceProgress)
                useDoubleReward = false;
            var effectiveOverflowSink = definition.UsesIncreaseChanceProgress
                ? overflowSink
                : RejectingInventoryOverflowRewardSink.Instance;
            if (!TryPlanOnlineOpen(
                    inventory,
                    source,
                    definition.GoldCost,
                    definition.RequiredItemTemplateId,
                    definition.RequiredItemCount,
                    regularRequests,
                    effectiveOverflowSink,
                    out var regularPlan,
                    out var regularDeliveredToMailbox))
                return false;

            var appliedDoubleReward = false;
            var appliedPlan = regularPlan;
            var deliveredToMailbox = regularDeliveredToMailbox;
            if (useDoubleReward && CanAttemptDoubleReward(inventory.CharacterId, inventory.AccountId))
            {
                var doubleRewards = AggregateRewardEntries(selectedRewards, 2).ToList();
                var doubleRequests = BuildLotteryRewardRequests(doubleRewards);
                if (!TryPlanOnlineOpen(
                        inventory,
                        source,
                        definition.GoldCost,
                        definition.RequiredItemTemplateId,
                        definition.RequiredItemCount,
                        doubleRequests,
                        effectiveOverflowSink,
                        out var doublePlan,
                        out var doubleDeliveredToMailbox))
                    return false;

                if (TryConsumeDoubleReward(inventory.CharacterId, inventory.AccountId))
                {
                    appliedDoubleReward = true;
                    appliedPlan = doublePlan;
                    deliveredToMailbox = doubleDeliveredToMailbox;
                }
            }

            var sourceItemTemplateId = source.Core.ItemId;
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    source.SlotIndex,
                    sourceItemTemplateId,
                    1,
                    out var sourceDelete))
                return false;

            var updatedGold = currentGold;
            if (definition.GoldCost > 0)
            {
                if (!inventory.TryConsumeMainItem(0, definition.GoldCost, out var goldConsume)
                    || !goldConsume.Success)
                    return false;

                updatedGold = goldConsume.RemainingCount;
            }

            InventoryMainItemConsumeResult requiredItemConsume = null;
            if (HasRequiredItemCost(definition))
            {
                if (!inventory.TryConsumeMainItem(
                        definition.RequiredItemTemplateId,
                        definition.RequiredItemCount,
                        out requiredItemConsume)
                    || !requiredItemConsume.Success)
                    return false;
            }

            InventoryRewardGrantBatchResult grantBatch = null;
            if (!deliveredToMailbox
                && (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, appliedPlan, out grantBatch)
                    || !grantBatch.Success))
            {
                return false;
            }

            var openResult = new LotteryOpenResult
            {
                SourceSlotIndex = source.SlotIndex,
                SourceItemTemplateId = sourceItemTemplateId,
                SourceRemainingStackCount = sourceDelete.RemainingCount,
                ConsumedGold = definition.GoldCost,
                UpdatedGold = updatedGold,
                ConsumedRequiredItemTemplateId = definition.RequiredItemTemplateId,
                ConsumedRequiredItemCount = HasRequiredItemCost(definition)
                    ? definition.RequiredItemCount
                    : 0,
                UsedDoubleReward = appliedDoubleReward,
                DeliveredToMailbox = deliveredToMailbox,
            };
            if (requiredItemConsume != null)
            {
                foreach (var change in requiredItemConsume.Changes.Slots)
                {
                    if (change.ListType == InventoryListType.Main
                        && !openResult.RequiredItemChangedSlots.Contains(change.SlotIndex))
                        openResult.RequiredItemChangedSlots.Add(change.SlotIndex);
                }
            }
            if (deliveredToMailbox)
                AddMailboxGrantResults(regularRequests, openResult.Rewards);
            else
                AddOnlineGrantResults(inventory, grantBatch, openResult.Rewards);
            InventoryItemLifecycleService.ApplyUseSuccess(inventory, lifecyclePlan);
            ApplyGoldRewardSummary(inventory, openResult);
            if (definition.UsesIncreaseChanceProgress)
            {
                var rewardIndex = FindRewardIndex(definition.RewardPool, selectedRewards[0]);
                if (rewardIndex < 0)
                    return false;

                claimed.Add(rewardIndex);
                var resetCount = Math.Min(definition.RewardPool.Count, Math.Max(1, definition.ProgressResetCount));
                var autoReset = claimed.Count >= resetCount;
                _progressRepository.SaveClaim(accountId, definition.ItemTemplateId, rewardIndex, autoReset);
                openResult.Progress = new LotteryProgressSnapshot
                {
                    ItemTemplateId = definition.ItemTemplateId,
                    NewRewardIndex = rewardIndex,
                    AutoReset = autoReset,
                };
                foreach (var index in claimed)
                    openResult.Progress.ClaimedRewardIndexes.Add(index);
            }
            result = openResult;
            return true;
        }

        private bool TryResolveSource(
            InventoryService inventory,
            short slotIndex,
            out OnlineLotterySource source,
            out LotteryItemDefinition definition,
            bool allowExpired = false)
        {
            source = null;
            definition = null;
            if (inventory == null || slotIndex < 0)
                return false;

            var core = inventory.GetItem(InventoryListType.Main, slotIndex);
            if (core == null || core.ItemId <= 0 || core.Count <= 0)
                return false;

            if (!allowExpired
                && core.ExpireTime > 0
                && core.ExpireTime <= DateTimeOffset.Now.ToUnixTimeSeconds())
                return false;

            if (!_definitions.TryGet(core.ItemId, out definition))
                return false;

            source = new OnlineLotterySource
            {
                SlotIndex = slotIndex,
                Core = core,
            };
            return true;
        }

        private bool TryApplyReservedOpen(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            int accountId,
            LotteryOpenReservation reservation,
            IInventoryOverflowRewardSink overflowSink,
            out LotteryOpenResult result)
        {
            result = null;
            if (!TryResolveSource(
                    inventory,
                    reservation.SlotIndex,
                    out var source,
                    out var definition,
                    allowExpired: true)
                || source.Core.ItemId != reservation.SourceItemTemplateId)
            {
                return false;
            }

            var lifecyclePlan = InventoryItemLifecycleService.PrepareUse(
                inventory,
                InventoryListType.Main,
                source.SlotIndex,
                source.Core.ItemId,
                InventoryItemLifecycleService.UtcNowUnixSeconds());
            if (lifecyclePlan.SourceExpiredDeleted)
            {
                result = new LotteryOpenResult
                {
                    SourceExpiredDeleted = true,
                    SourceSlotIndex = source.SlotIndex,
                    SourceItemTemplateId = source.Core.ItemId,
                    SourceRemainingStackCount = 0,
                };
                return true;
            }

            if (!lifecyclePlan.Success)
                return false;

            var currentGold = inventory.CountMainItem(0);
            if (currentGold < definition.GoldCost
                || (HasRequiredItemCost(definition)
                    && inventory.CountMainItem(definition.RequiredItemTemplateId)
                        < definition.RequiredItemCount))
            {
                return false;
            }

            var appliedDoubleReward = false;
            if (!definition.UsesIncreaseChanceProgress
                && reservation.OpenPlan.UseDoubleReward)
            {
                if (reservation.AppliedDoubleReward == true)
                {
                    if (!_doubleRewardPolicy.TryConsume(
                            connection,
                            transaction,
                            inventory.CharacterId,
                            accountId))
                    {
                        return false;
                    }

                    appliedDoubleReward = true;
                }
                else if (!reservation.AppliedDoubleReward.HasValue)
                {
                    appliedDoubleReward = _doubleRewardPolicy.TryConsume(
                        connection,
                        transaction,
                        inventory.CharacterId,
                        accountId);
                    reservation.AppliedDoubleReward = appliedDoubleReward;
                }
            }
            else
            {
                reservation.AppliedDoubleReward = false;
            }

            var rewardEntries = AggregateRewardEntries(
                    reservation.SelectedRewards,
                    appliedDoubleReward ? 2 : 1)
                .ToList();
            var rewardRequests = BuildLotteryRewardRequests(rewardEntries);
            var effectiveOverflowSink = overflowSink
                ?? RejectingInventoryOverflowRewardSink.Instance;
            if (!TryPrepareOnlineOpen(
                    inventory,
                    source,
                    definition.GoldCost,
                    definition.RequiredItemTemplateId,
                    definition.RequiredItemCount,
                    rewardRequests,
                    effectiveOverflowSink,
                    out var rewardPlan,
                    out var deliverToMailbox))
            {
                return false;
            }

            var sourceItemTemplateId = source.Core.ItemId;
            if (!UsableCountLimitService.TryRecordUseIfLimited(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    sourceItemTemplateId,
                    1,
                    out var usableCountState))
            {
                return false;
            }

            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    InventoryListType.Main,
                    source.SlotIndex,
                    sourceItemTemplateId,
                    1,
                    out var sourceDelete))
            {
                return false;
            }

            var updatedGold = currentGold;
            if (definition.GoldCost > 0)
            {
                if (!inventory.TryConsumeMainItem(
                        0,
                        definition.GoldCost,
                        out var goldConsume)
                    || !goldConsume.Success)
                {
                    return false;
                }

                updatedGold = goldConsume.RemainingCount;
            }

            InventoryMainItemConsumeResult requiredItemConsume = null;
            if (HasRequiredItemCost(definition)
                && (!inventory.TryConsumeMainItem(
                        definition.RequiredItemTemplateId,
                        definition.RequiredItemCount,
                        out requiredItemConsume)
                    || !requiredItemConsume.Success))
            {
                return false;
            }

            InventoryRewardGrantBatchResult grantBatch = null;
            if (deliverToMailbox)
            {
                if (!(effectiveOverflowSink
                        is MailboxInventoryOverflowRewardSink mailboxSink)
                    || !mailboxSink.TryDeliver(
                        connection,
                        transaction,
                        inventory,
                        rewardRequests,
                        string.Empty,
                        "物品栏没有剩余空间，礼物已邮件发送",
                        out _))
                {
                    return false;
                }
            }
            else if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                    inventory,
                    rewardPlan,
                    out grantBatch)
                || !grantBatch.Success)
            {
                return false;
            }

            var openResult = new LotteryOpenResult
            {
                SourceSlotIndex = source.SlotIndex,
                SourceItemTemplateId = sourceItemTemplateId,
                SourceRemainingStackCount = sourceDelete.RemainingCount,
                ConsumedGold = definition.GoldCost,
                UpdatedGold = updatedGold,
                ConsumedRequiredItemTemplateId = definition.RequiredItemTemplateId,
                ConsumedRequiredItemCount = HasRequiredItemCost(definition)
                    ? definition.RequiredItemCount
                    : 0,
                UsedDoubleReward = appliedDoubleReward,
                DeliveredToMailbox = deliverToMailbox,
                UsableCountState = usableCountState,
            };
            if (requiredItemConsume != null)
            {
                foreach (var change in requiredItemConsume.Changes.Slots)
                {
                    if (change.ListType == InventoryListType.Main
                        && !openResult.RequiredItemChangedSlots.Contains(
                            change.SlotIndex))
                    {
                        openResult.RequiredItemChangedSlots.Add(change.SlotIndex);
                    }
                }
            }

            if (deliverToMailbox)
                AddMailboxGrantResults(rewardRequests, openResult.Rewards);
            else
                AddOnlineGrantResults(inventory, grantBatch, openResult.Rewards);
            InventoryItemLifecycleService.ApplyUseSuccess(inventory, lifecyclePlan);
            ApplyGoldRewardSummary(inventory, openResult);

            if (definition.UsesIncreaseChanceProgress)
            {
                var claimed = _progressRepository.Load(
                    connection,
                    transaction,
                    accountId,
                    definition.ItemTemplateId);
                if (reservation.ProgressRewardIndex < 0
                    || reservation.ProgressRewardIndex >= definition.RewardPool.Count
                    || claimed.Contains(reservation.ProgressRewardIndex))
                {
                    return false;
                }

                claimed.Add(reservation.ProgressRewardIndex);
                var resetCount = Math.Min(
                    definition.RewardPool.Count,
                    Math.Max(1, definition.ProgressResetCount));
                var autoReset = claimed.Count >= resetCount;
                _progressRepository.SaveClaim(
                    connection,
                    transaction,
                    accountId,
                    definition.ItemTemplateId,
                    reservation.ProgressRewardIndex,
                    autoReset);
                openResult.Progress = new LotteryProgressSnapshot
                {
                    ItemTemplateId = definition.ItemTemplateId,
                    NewRewardIndex = reservation.ProgressRewardIndex,
                    AutoReset = autoReset,
                };
                foreach (var index in claimed)
                    openResult.Progress.ClaimedRewardIndexes.Add(index);
            }

            result = openResult;
            return true;
        }

        private bool CanAttemptDoubleReward(int characterId, int accountId)
        {
            return characterId > 0
                && accountId > 0
                && _doubleRewardPolicy.GetUsedCount(characterId) < LotteryDoubleRewardPolicy.DailyLimit
                && _doubleRewardPolicy.HasActiveBenefit(accountId);
        }

        private bool TryConsumeDoubleReward(int characterId, int accountId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    if (!_doubleRewardPolicy.TryConsume(
                            connection,
                            transaction,
                            characterId,
                            accountId))
                        return false;

                    transaction.Commit();
                }
                return true;
            }
        }

        private static bool TryPlanOnlineOpen(
            InventoryService inventory,
            OnlineLotterySource source,
            int goldCost,
            int requiredItemTemplateId,
            int requiredItemCount,
            IReadOnlyList<InventoryRewardGrantRequest> requests,
            IInventoryOverflowRewardSink overflowSink,
            out InventoryRewardGrantBatchPlan plan,
            out bool deliveredToMailbox)
        {
            plan = null;
            deliveredToMailbox = false;
            if (inventory == null || source == null || source.Core == null)
                return false;

            var planningInventory = InventorySpecialConsumableService.CreatePlanningInventory(inventory);
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    planningInventory,
                    InventoryListType.Main,
                    source.SlotIndex,
                    source.Core.ItemId,
                    1,
                    out _))
                return false;

            if (goldCost > 0
                && (!planningInventory.TryConsumeMainItem(0, goldCost, out var goldConsume)
                    || !goldConsume.Success))
                return false;

            if (requiredItemTemplateId > 0
                && requiredItemCount > 0
                && (!planningInventory.TryConsumeMainItem(
                        requiredItemTemplateId,
                        requiredItemCount,
                        out var requiredItemConsume)
                    || !requiredItemConsume.Success))
                return false;

            if (InventoryRewardGrantService.TryPlanBatch(planningInventory, requests, out plan))
                return true;

            overflowSink = overflowSink ?? RejectingInventoryOverflowRewardSink.Instance;
            if (overflowSink is MailboxInventoryOverflowRewardSink mailboxSink)
            {
                deliveredToMailbox = mailboxSink.TryDeliver(
                    inventory,
                    requests ?? Array.Empty<InventoryRewardGrantRequest>(),
                    string.Empty,
                    "物品栏没有剩余空间，礼物已邮件发送",
                    out _);
            }
            else
            {
                deliveredToMailbox = overflowSink.TryDeliver(
                    inventory,
                    requests ?? Array.Empty<InventoryRewardGrantRequest>(),
                    out _);
            }
            return deliveredToMailbox;
        }

        private static bool TryPrepareOnlineOpen(
            InventoryService inventory,
            OnlineLotterySource source,
            int goldCost,
            int requiredItemTemplateId,
            int requiredItemCount,
            IReadOnlyList<InventoryRewardGrantRequest> requests,
            IInventoryOverflowRewardSink overflowSink,
            out InventoryRewardGrantBatchPlan plan,
            out bool deliverToMailbox)
        {
            plan = null;
            deliverToMailbox = false;
            if (inventory == null || source?.Core == null)
                return false;

            var planningInventory = InventorySpecialConsumableService
                .CreatePlanningInventory(inventory);
            if (!InventoryDeleteService.TryConsumeFromSlot(
                    planningInventory,
                    InventoryListType.Main,
                    source.SlotIndex,
                    source.Core.ItemId,
                    1,
                    out _))
            {
                return false;
            }

            if (goldCost > 0
                && (!planningInventory.TryConsumeMainItem(
                        0,
                        goldCost,
                        out var goldConsume)
                    || !goldConsume.Success))
            {
                return false;
            }

            if (requiredItemTemplateId > 0
                && requiredItemCount > 0
                && (!planningInventory.TryConsumeMainItem(
                        requiredItemTemplateId,
                        requiredItemCount,
                        out var requiredItemConsume)
                    || !requiredItemConsume.Success))
            {
                return false;
            }

            if (InventoryRewardGrantService.TryPlanBatch(
                    planningInventory,
                    requests,
                    out plan))
            {
                return true;
            }

            // 只有容量溢出（InsertPlanFailed）才允许转邮件；无效物品、创建
            // 失败等其它规划错误必须整笔失败，不得伪装成溢出（背包规范
            // 「新物品创建/发放」节的溢出约束）。
            if (plan == null
                || plan.Error != InventoryRewardGrantError.InsertPlanFailed)
            {
                return false;
            }

            deliverToMailbox = overflowSink
                is MailboxInventoryOverflowRewardSink;
            return deliverToMailbox;
        }

        private static void AddMailboxGrantResults(
            IReadOnlyList<InventoryRewardGrantRequest> requests,
            List<LotteryRewardGrant> rewards)
        {
            if (requests == null || rewards == null)
                return;

            foreach (var request in requests)
            {
                if (request == null
                    || !InventoryRewardGrantService.TryCreateOnly(
                        request.ItemTemplateId,
                        request.Reason,
                        request.Count,
                        request.CreateOptions,
                        out var created)
                    || !created.Success)
                {
                    continue;
                }

                rewards.Add(new LotteryRewardGrant
                {
                    ListType = InventoryListType.Main,
                    SlotIndex = -1,
                    ItemTemplateId = request.ItemTemplateId,
                    StackCount = request.Count,
                    GrantedCount = request.Count,
                    DisplayCore = created.Core,
                });
            }
        }

        private static bool HasRequiredItemCost(LotteryItemDefinition definition)
        {
            return definition != null
                && definition.RequiredItemTemplateId > 0
                && definition.RequiredItemCount > 0;
        }

        internal static List<PvfLib.BoosterRewardEntry> RollRewards(
            IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            var selected = new List<PvfLib.BoosterRewardEntry>();
            if (rewards == null)
                return selected;

            foreach (var group in rewards.GroupBy(reward => reward.Group))
            {
                var totalWeight = group.Sum(reward => Math.Max(0, reward.Weight));
                if (totalWeight <= 0)
                    continue;

                var drawCount = Math.Max(1, group.Max(reward => reward.DrawCount));
                for (var drawIndex = 0; drawIndex < drawCount; drawIndex++)
                {
                    var roll = ServerRandom.Next(totalWeight);
                    var cumulative = 0;
                    foreach (var reward in group)
                    {
                        cumulative += Math.Max(0, reward.Weight);
                        if (roll >= cumulative)
                            continue;

                        selected.Add(reward);
                        break;
                    }
                }
            }

            return selected;
        }

        internal static List<PvfLib.BoosterRewardEntry> RollProgressReward(
            IReadOnlyList<PvfLib.BoosterRewardEntry> rewards,
            ISet<int> claimedIndexes)
        {
            if (rewards == null)
                return new List<PvfLib.BoosterRewardEntry>();

            var candidates = rewards
                .Select((reward, index) => new { reward, index })
                .Where(entry => entry.reward != null
                    && entry.reward.Weight > 0
                    && (claimedIndexes == null || !claimedIndexes.Contains(entry.index)))
                .ToList();
            var totalWeight = candidates.Sum(entry => entry.reward.Weight);
            if (totalWeight <= 0)
                return new List<PvfLib.BoosterRewardEntry>();

            var roll = ServerRandom.Next(totalWeight);
            var cumulative = 0;
            foreach (var candidate in candidates)
            {
                cumulative += candidate.reward.Weight;
                if (roll < cumulative)
                    return new List<PvfLib.BoosterRewardEntry> { candidate.reward };
            }
            return new List<PvfLib.BoosterRewardEntry>();
        }

        private static int FindRewardIndex(
            IReadOnlyList<PvfLib.BoosterRewardEntry> rewards,
            PvfLib.BoosterRewardEntry selected)
        {
            if (rewards == null || selected == null)
                return -1;
            for (var index = 0; index < rewards.Count; index++)
            {
                if (ReferenceEquals(rewards[index], selected))
                    return index;
            }
            return -1;
        }

        private static IEnumerable<PvfLib.BoosterRewardEntry> AggregateRewardEntries(
            IReadOnlyList<PvfLib.BoosterRewardEntry> rewards,
            int multiplier)
        {
            return rewards
                .Where(reward => reward != null && reward.ItemId >= 0 && reward.Count > 0)
                .GroupBy(reward => new { reward.ItemId, reward.UsablePeriodDays })
                .Select(group => new PvfLib.BoosterRewardEntry
                {
                    ItemId = group.Key.ItemId,
                    Count = (int)Math.Min(
                        int.MaxValue,
                        group.Sum(reward => (long)Math.Max(1, reward.Count))
                            * Math.Max(1L, multiplier)),
                    UsablePeriodDays = group.Key.UsablePeriodDays,
                });
        }

        private static List<InventoryRewardGrantRequest> BuildLotteryRewardRequests(
            IReadOnlyList<PvfLib.BoosterRewardEntry> rewards)
        {
            var requests = InventorySpecialConsumableService.BuildRewardRequests(
                (rewards ?? Array.Empty<PvfLib.BoosterRewardEntry>())
                    .Where(reward => reward != null && reward.ItemId > 0));
            var goldReward = SumGoldRewards(rewards);
            if (goldReward > 0)
            {
                requests.Insert(
                    0,
                    InventoryRewardGrantRequest.Create(
                        0,
                        goldReward,
                        ItemCreateReason.PackageOpen));
            }

            return requests;
        }

        private static int SumGoldRewards(
            IEnumerable<PvfLib.BoosterRewardEntry> rewards)
        {
            if (rewards == null)
                return 0;

            var total = rewards
                .Where(reward => reward != null && reward.ItemId == 0)
                .Sum(reward => (long)Math.Max(0, reward.Count));
            return (int)Math.Min(int.MaxValue, total);
        }

        private static void ApplyGoldRewardSummary(
            InventoryService inventory,
            LotteryOpenResult result)
        {
            if (result == null)
                return;

            result.GrantedGold = result.Rewards
                .Where(reward => reward != null && reward.ItemTemplateId == 0)
                .Sum(reward => Math.Max(0, reward.GrantedCount));
            if (inventory != null)
                result.UpdatedGold = inventory.CountMainItem(0);
        }

        private static void AddOnlineGrantResults(
            InventoryService inventory,
            InventoryRewardGrantBatchResult batch,
            List<LotteryRewardGrant> rewards)
        {
            if (batch == null || rewards == null)
                return;

            foreach (var grant in batch.Results)
            {
                var reward = ToLotteryRewardGrant(inventory, grant);
                if (reward != null)
                    rewards.Add(reward);
            }
        }

        private static LotteryRewardGrant ToLotteryRewardGrant(
            InventoryService inventory,
            InventoryRewardGrantResult grant)
        {
            if (grant == null || !grant.Success)
                return null;
            if (grant.Kind == InventoryRewardGrantKind.Premium)
                return null;

            var core = grant.Kind == InventoryRewardGrantKind.InventoryItem
                ? inventory?.GetItem(grant.ListType, grant.SlotIndex)
                : null;

            return new LotteryRewardGrant
            {
                ListType = grant.ListType,
                SlotIndex = grant.SlotIndex,
                ItemTemplateId = grant.ItemTemplateId,
                StackCount = ResolveStackCount(core, grant),
                GrantedCount = grant.GrantedCount,
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

            return InventoryStackRuleService.IsStackable(core)
                ? core.Count
                : core.InstanceValue;
        }

        private sealed class OnlineLotterySource
        {
            public short SlotIndex { get; set; }

            public ItemCore Core { get; set; }
        }
    }
}
