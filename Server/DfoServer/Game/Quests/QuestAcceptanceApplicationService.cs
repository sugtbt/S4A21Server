using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestAcceptanceApplicationService
    {
        private readonly string _connectionString;
        private readonly QuestPrerequisiteEvaluator _prerequisites;

        internal QuestAcceptanceApplicationService(string connectionString)
        {
            _connectionString = connectionString;
            _prerequisites = new QuestPrerequisiteEvaluator(connectionString);
        }

        internal QuestAcceptResult Apply(
            QuestCommandOwnerContext owner,
            QuestAcceptCommand command)
        {
            var characterId = owner.CharacterId;
            var questId = command.QuestId;
            var lease = owner.InventoryLease;
            if (!owner.IsCurrentInventoryOwner()
                || lease.AccountId != owner.AccountId)
            {
                return QuestAcceptResult.Fail(0x17);
            }

            var repeatable = GameWorld.QuestData.IsImmediatelyRepeatableQuest(questId);
            var isDaily = GameWorld.QuestData.IsDailyQuest(questId);
            var eventItems = GameWorld.QuestData.GetEventItems(questId);
            var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(questId);
            var eventSlots = new List<ushort>(eventItems.Count);
            for (var index = 0; index < eventItems.Count; index++)
                eventSlots.Add(0);

            var clientInitialTrigger = GameWorld.QuestData.GetInitTrigger(questId);
            var committedTrigger = clientInitialTrigger;
            var slot = -1;
            var activationId = default(QuestActivationId);
            lock (lease.SyncRoot)
            {
                if (!owner.IsCurrentInventoryOwner())
                    return QuestAcceptResult.Fail(0x17);

                var inventory = lease.Inventory;
                var pendingEventItems = BuildMissingEventItemGrants(
                    inventory,
                    eventItems,
                    eventSlots,
                    out var grantRequests,
                    out var grantRequestIndexes);
                InventoryRewardGrantBatchPlan grantPlan = null;
                if (grantRequests.Count > 0
                    && (!InventoryRewardGrantService.TryPlanBatch(
                            inventory,
                            grantRequests,
                            out grantPlan)
                        || grantPlan == null
                        || !grantPlan.Success))
                {
                    LogEventItemInventoryDiagnostic(
                        characterId,
                        questId,
                        inventory,
                        eventItems,
                        pendingEventItems);
                    return QuestAcceptResult.Fail(0x11);
                }

                QuestAcceptanceInventoryRollback rollback = null;
                var inventoryMutated = false;
                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            var utcNow = DateTime.UtcNow;
                            if (!owner.IsCurrentInventoryOwner())
                                return QuestAcceptResult.Fail(0x17);

                            if (isDaily
                                && DailyQuestCompletionCycle.GetCurrentCounter(
                                    connection, transaction, characterId, questId, utcNow) > 0)
                            {
                                return QuestAcceptResult.Fail(18);
                            }

                            var active = QuestRepository.LoadActiveQuests(
                                connection,
                                transaction,
                                characterId);
                            if (QuestActiveListRules.FindByQuestId(active, questId) != null)
                                return QuestAcceptResult.Fail(18);
                            if (QuestRepository.IsQuestCleared(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId)
                                && !repeatable)
                            {
                                return QuestAcceptResult.Fail(18);
                            }
                            if (!TryLoadCharacterRestrictions(
                                    connection,
                                    transaction,
                                    characterId,
                                    out var characterLevel,
                                    out var characterJob,
                                    out var growType)
                                || !GameWorld.QuestRelationIndex
                                    .MeetsCharacterRestrictions(
                                        questId,
                                        characterLevel,
                                        characterJob,
                                        growType))
                            {
                                FileLogger.Log(
                                    $"[QuestAcceptanceApplicationService] ACCEPT " +
                                    $"blocked by character restrictions: " +
                                    $"quest={questId} cid={characterId}");
                                return QuestAcceptResult.Fail(21);
                            }
                            if (!_prerequisites.IsSatisfied(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId,
                                    active))
                            {
                                return QuestAcceptResult.Fail(21);
                            }
                            if (!QuestDungeonActivationPolicy
                                    .IsAcceptanceAllowed(questId, active))
                            {
                                FileLogger.Log(
                                    $"[QuestAcceptanceApplicationService] ACCEPT " +
                                    $"blocked by task-dungeon presentation priority: " +
                                    $"quest={questId} cid={characterId}");
                                return QuestAcceptResult.Fail(21);
                            }

                            slot = QuestActiveListRules.FindFreeSlot(active);
                            if (slot < 0)
                            {
                                return QuestAcceptResult.Fail(
                                    QuestSlotLayout.ActiveListFullFallbackError);
                            }

                            if (GameWorld.QuestData.IsQuestClearQuest(questId))
                            {
                                committedTrigger = QuestClearProgressRules.Compute(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId);
                            }
                            if (seekItems.Count > 0)
                            {
                                committedTrigger = QuestProgressReducer.ApplySeekingItems(
                                    new QuestTrigger(committedTrigger),
                                    seekItems,
                                    itemId => CountMainItemWithPendingRewards(
                                        inventory,
                                        itemId,
                                        pendingEventItems)).PackedValue;
                            }

                            if (grantPlan != null && grantPlan.Entries.Count > 0)
                            {
                                rollback = QuestAcceptanceInventoryRollback.Capture(
                                    inventory,
                                    grantPlan);
                                if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                                        inventory,
                                        grantPlan,
                                        out var grantResult)
                                    || grantResult == null
                                    || !grantResult.Success)
                                {
                                    throw new InvalidOperationException(
                                        $"quest event item apply failed: {grantResult?.Error}");
                                }
                                inventoryMutated = true;

                                for (var index = 0;
                                     index < grantResult.Results.Count
                                     && index < grantRequestIndexes.Count;
                                     index++)
                                {
                                    var slotIndex = grantResult.Results[index].SlotIndex;
                                    if (slotIndex >= 0)
                                    {
                                        eventSlots[grantRequestIndexes[index]] =
                                            (ushort)slotIndex;
                                    }
                                }
                            }

                            activationId = QuestRepository.InsertActiveQuest(
                                connection,
                                transaction,
                                characterId,
                                slot,
                                questId,
                                committedTrigger);
                            if (repeatable || isDaily)
                            {
                                QuestRepository.DeleteClearedFlag(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId);
                            }

                            if (inventoryMutated
                                && !InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                throw new InvalidOperationException(
                                    "quest event item persistence returned false");
                            }
                            if (!owner.IsCurrentInventoryOwner())
                            {
                                throw new InvalidOperationException(
                                    "quest accept inventory lease was replaced");
                            }

                            transaction.Commit();
                        }
                    }

                    if (inventoryMutated)
                        inventory.ClearDirtyState();
                }
                catch (Exception ex)
                {
                    if (inventoryMutated && rollback != null)
                        rollback.Restore(inventory, grantPlan);

                    FileLogger.Log(
                        $"[QuestAcceptanceApplicationService] ACCEPT failed before " +
                        $"atomic commit: quest={questId} cid={characterId} " +
                        $"error={ex.Message}");
                    return QuestAcceptResult.Fail(0x17);
                }
            }

            var result = new QuestAcceptResult
            {
                QuestId = questId,
                InitTrigger = clientInitialTrigger,
                CommittedTrigger = committedTrigger,
                ActivationId = activationId,
            };
            if (clientInitialTrigger != committedTrigger)
            {
                result.PostAcceptTriggerProjection = new QuestSetTriggerResult
                {
                    QuestId = questId,
                    PreviousTriggerValue = clientInitialTrigger,
                    TriggerValue = committedTrigger,
                };
            }
            for (var index = 0; index < eventItems.Count; index++)
            {
                result.EventItems.Add(new QuestEventItemGrant
                {
                    SlotIndex = eventSlots[index],
                    ItemId = eventItems[index].ItemId,
                    Count = eventItems[index].Count,
                });
            }
            FileLogger.Log(
                $"[QuestAcceptanceApplicationService] ACCEPT quest={questId} " +
                $"slot={slot} clientInitTrigger={clientInitialTrigger} " +
                $"committedTrigger={committedTrigger} " +
                $"eventItems={eventItems.Count}");
            return result;
        }

        private static bool TryLoadCharacterRestrictions(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            out int level,
            out int job,
            out int growType)
        {
            level = 0;
            job = -1;
            growType = -1;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT level, job, grow_type
FROM characters
WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    level = reader.GetInt32(0);
                    job = reader.GetInt32(1);
                    growType = reader.GetInt32(2);
                    return true;
                }
            }
        }

        private static int CountMainItemWithPendingRewards(
            InventoryService inventory,
            int itemId,
            IReadOnlyCollection<GameWorld.QuestRewardItem> pendingRewards)
        {
            var count = inventory != null ? inventory.CountMainItem(itemId) : 0;
            if (pendingRewards == null)
                return count;

            foreach (var reward in pendingRewards)
            {
                if (reward.ItemId <= 0 || reward.Count <= 0)
                    continue;
                if (GetMainItemIdentityKey(itemId)
                    != GetMainItemIdentityKey(reward.ItemId))
                {
                    continue;
                }

                var value = (long)Math.Max(0, count) + reward.Count;
                count = value > int.MaxValue ? int.MaxValue : (int)value;
            }
            return count;
        }

        internal static List<GameWorld.QuestRewardItem>
            BuildMissingEventItemGrants(
                InventoryService inventory,
                IReadOnlyCollection<GameWorld.QuestRewardItem> eventItems,
                IList<ushort> eventSlots,
                out List<InventoryRewardGrantRequest> grantRequests,
                out List<int> grantRequestIndexes)
        {
            grantRequests = new List<InventoryRewardGrantRequest>();
            grantRequestIndexes = new List<int>();
            var pending = new List<GameWorld.QuestRewardItem>();
            if (inventory == null || eventItems == null)
                return pending;

            var index = 0;
            foreach (var item in eventItems)
            {
                if (item.ItemId > 0 && item.Count > 0)
                {
                    var held = Math.Max(0, inventory.CountMainItem(item.ItemId));
                    var existingSlot = FindMainItemSlot(inventory, item.ItemId);
                    if (existingSlot >= 0 && eventSlots != null
                        && index < eventSlots.Count)
                    {
                        eventSlots[index] = (ushort)existingSlot;
                    }

                    var missing = Math.Max(0, item.Count - held);
                    if (missing > 0)
                    {
                        grantRequests.Add(InventoryRewardGrantRequest.CreateQuestEventItem(
                            item.ItemId,
                            missing,
                            ItemCreateReason.QuestReward));
                        grantRequestIndexes.Add(index);
                        pending.Add(new GameWorld.QuestRewardItem
                        {
                            ItemId = item.ItemId,
                            Count = missing,
                        });
                    }
                }
                index++;
            }
            return pending;
        }

        private static void LogEventItemInventoryDiagnostic(
            int characterId,
            ushort questId,
            InventoryService inventory,
            IReadOnlyCollection<GameWorld.QuestRewardItem> eventItems,
            IReadOnlyCollection<GameWorld.QuestRewardItem> pendingItems)
        {
            try
            {
                foreach (var item in eventItems ?? Array.Empty<GameWorld.QuestRewardItem>())
                {
                    var metadata = ItemMetadataResolver.Resolve(item.ItemId);
                    var held = inventory != null
                        ? inventory.CountMainItem(item.ItemId)
                        : 0;
                    var existingSlot = FindMainItemSlot(inventory, item.ItemId);
                    var range = "n/a";
                    var metadataRange = "n/a";
                    if (metadata != null)
                    {
                        metadata.GetSlotRange(out var metadataStart, out var metadataEnd);
                        metadataRange = $"{metadataStart}-{metadataEnd}";
                    }
                    if (ItemSlotBoundService.TryGetSlotRange(
                            ItemCore.KindQuest,
                            inventory != null
                                ? inventory.GetListParam16(InventoryListType.Main)
                                : 0,
                            out var listType,
                            out var slotRange))
                    {
                        range = $"{listType}:{slotRange.Start}-{slotRange.End}";
                    }

                    FileLogger.Log(
                        $"[QuestAcceptanceApplicationService] event-item diagnostic " +
                        $"quest={questId} cid={characterId} item={item.ItemId} " +
                        $"required={item.Count} held={held} existingSlot={existingSlot} " +
                        $"pvfKind={metadata?.ItemKind} stackType={metadata?.StackableType} " +
                        $"resolvedRange={metadataRange} " +
                        $"eventRange={range}");
                }

                FileLogger.Log(
                    $"[QuestAcceptanceApplicationService] event-item pending " +
                    $"quest={questId} cid={characterId} " +
                    $"items={pendingItems?.Count ?? 0}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestAcceptanceApplicationService] event-item diagnostic failed: {ex.Message}");
            }
        }

        private static int FindMainItemSlot(
            InventoryService inventory,
            int itemId)
        {
            if (inventory == null || itemId <= 0)
                return -1;

            if (InventoryService.TryResolveMainVirtualSlotByItemId(
                    itemId,
                    out var virtualSlot,
                    out _))
            {
                return inventory.GetMainVirtualCount(virtualSlot)?.Count > 0
                    ? virtualSlot
                    : -1;
            }

            foreach (var pair in inventory.GetItems(InventoryListType.Main))
            {
                if (pair.Value != null && pair.Value.ItemId == itemId)
                    return pair.Key;
            }
            return -1;
        }

        private static int GetMainItemIdentityKey(int itemId)
        {
            return InventoryService.TryResolveMainVirtualSlotByItemId(
                itemId,
                out var slotIndex,
                out _)
                ? -100000 - slotIndex
                : itemId;
        }

        private sealed class QuestAcceptanceInventoryRollback
        {
            private readonly Dictionary<(InventoryListType, short), ItemCore> _items =
                new Dictionary<(InventoryListType, short), ItemCore>();
            private readonly Dictionary<short, int> _virtualCounts =
                new Dictionary<short, int>();

            internal static QuestAcceptanceInventoryRollback Capture(
                InventoryService inventory,
                InventoryRewardGrantBatchPlan plan)
            {
                var snapshot = new QuestAcceptanceInventoryRollback();
                foreach (var entry in plan.Entries)
                {
                    if (entry.Kind == InventoryRewardGrantKind.MainVirtualCount)
                    {
                        if (!snapshot._virtualCounts.ContainsKey(entry.SlotIndex))
                        {
                            snapshot._virtualCounts[entry.SlotIndex] =
                                inventory.GetMainVirtualCount(entry.SlotIndex)?.Count ?? 0;
                        }
                        continue;
                    }
                    if (entry.Kind != InventoryRewardGrantKind.InventoryItem)
                        continue;

                    var key = (entry.ListType, entry.SlotIndex);
                    if (!snapshot._items.ContainsKey(key))
                    {
                        snapshot._items[key] = inventory.TryGetItem(
                            entry.ListType,
                            entry.SlotIndex,
                            out var item)
                            ? item.Copy()
                            : null;
                    }
                }
                return snapshot;
            }

            internal void Restore(
                InventoryService inventory,
                InventoryRewardGrantBatchPlan plan)
            {
                foreach (var entry in plan.Entries)
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
                {
                    if (pair.Value == null)
                        inventory.RemoveItem(pair.Key.Item1, pair.Key.Item2);
                    else
                    {
                        inventory.SetItem(
                            pair.Key.Item1,
                            pair.Key.Item2,
                            pair.Value.Copy());
                    }
                }
                foreach (var pair in _virtualCounts)
                    inventory.SetMainVirtualCount(pair.Key, pair.Value);
            }
        }
    }
}
