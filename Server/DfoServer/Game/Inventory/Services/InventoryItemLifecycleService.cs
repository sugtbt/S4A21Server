using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal enum InventoryItemLifecycleStatus
    {
        Success,
        SourceMissing,
        SourceChanged,
        SourceEmpty,
        SourceExpired,
        EffectActive,
        CooltimeActive,
        InvalidDefinition,
    }

    internal sealed class InventoryItemLifecycleUsePlan
    {
        public InventoryItemLifecycleStatus Status { get; set; }

        public string Detail { get; set; }

        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int EffectExpireTime { get; set; }

        public bool HadPreviousEffectState { get; set; }

        public int PreviousEffectExpireTime { get; set; }

        public int CooltimeExpireTime { get; set; }

        public bool HadPreviousCooltimeState { get; set; }

        public int PreviousCooltimeExpireTime { get; set; }

        public InventoryMutationResult SourceMutation { get; set; }

        public bool Success => Status == InventoryItemLifecycleStatus.Success;

        public bool SourceExpiredDeleted =>
            Status == InventoryItemLifecycleStatus.SourceExpired
            && SourceMutation != null;
    }

    internal static class InventoryItemLifecycleService
    {
        private static readonly InventoryListType[] ExpirableListTypes =
        {
            InventoryListType.Main,
            InventoryListType.Equipment,
            InventoryListType.Avatar,
            InventoryListType.Pet,
            InventoryListType.PersonalCargo,
            InventoryListType.AccountCargo,
            InventoryListType.GuildMedal,
        };

        internal static long UtcNowUnixSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        internal static InventoryItemLifecycleUsePlan PrepareUse(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            long nowUnixSeconds)
        {
            return PrepareUse(
                inventory,
                listType,
                slotIndex,
                expectedItemTemplateId,
                nowUnixSeconds,
                1);
        }

        internal static InventoryItemLifecycleUsePlan PrepareUse(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            long nowUnixSeconds,
            int requiredCount)
        {
            return PrepareUseCore(
                inventory,
                listType,
                slotIndex,
                expectedItemTemplateId,
                nowUnixSeconds,
                requiredCount,
                null,
                false);
        }

        internal static InventoryItemLifecycleUsePlan PrepareUseWithDefinition(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            long nowUnixSeconds,
            int requiredCount,
            PvfLib.StackableItemFile stackable)
        {
            return PrepareUseCore(
                inventory,
                listType,
                slotIndex,
                expectedItemTemplateId,
                nowUnixSeconds,
                requiredCount,
                stackable,
                true,
                true,
                true);
        }

        internal static InventoryItemLifecycleUsePlan PrepareUseWithDefinition(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            long nowUnixSeconds,
            int requiredCount,
            PvfLib.StackableItemFile stackable,
            bool checkEffectMaintenance,
            bool checkCooltimeMaintenance)
        {
            return PrepareUseCore(
                inventory,
                listType,
                slotIndex,
                expectedItemTemplateId,
                nowUnixSeconds,
                requiredCount,
                stackable,
                true,
                checkEffectMaintenance,
                checkCooltimeMaintenance);
        }

        private static InventoryItemLifecycleUsePlan PrepareUseCore(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            long nowUnixSeconds,
            int requiredCount,
            PvfLib.StackableItemFile stackableOverride,
            bool hasStackableOverride,
            bool checkEffectMaintenance = true,
            bool checkCooltimeMaintenance = true)
        {
            var result = new InventoryItemLifecycleUsePlan
            {
                Status = InventoryItemLifecycleStatus.SourceMissing,
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = Math.Max(0, expectedItemTemplateId),
            };

            if (inventory == null || slotIndex < 0)
            {
                result.Detail = "source slot is unavailable";
                return result;
            }

            var source = inventory.GetItem(listType, slotIndex);
            if (source == null || source.ItemId <= 0)
            {
                result.Detail = "source slot is empty";
                return result;
            }

            result.ItemTemplateId = source.ItemId;
            if (expectedItemTemplateId > 0 && source.ItemId != expectedItemTemplateId)
            {
                result.Status = InventoryItemLifecycleStatus.SourceChanged;
                result.Detail = "source slot item changed";
                return result;
            }

            if (source.Count < Math.Max(1, requiredCount))
            {
                result.Status = InventoryItemLifecycleStatus.SourceEmpty;
                result.Detail = "source stack is not enough";
                return result;
            }

            if (TryRemoveExpiredSource(
                    inventory,
                    listType,
                    slotIndex,
                    source.ItemId,
                    nowUnixSeconds,
                    out var expiredMutation))
            {
                result.Status = InventoryItemLifecycleStatus.SourceExpired;
                result.Detail = "source item has expired";
                result.SourceMutation = expiredMutation;
                return result;
            }

            var stackable = hasStackableOverride
                ? stackableOverride
                : StackableItemProvider.Load(source.ItemId);
            if (stackable == null)
            {
                result.Status = InventoryItemLifecycleStatus.Success;
                return result;
            }

            if (checkEffectMaintenance && stackable.HasEffectMaintenance)
            {
                if (stackable.StatChangeDurationMilliseconds <= 0)
                    return Reject(result, InventoryItemLifecycleStatus.InvalidDefinition, "missing [stat change duration]");

                if (inventory.ItemStates.TryGetExpireTime(
                        ItemStateKinds.Effect,
                        source.ItemId,
                        out var effectExpireTime))
                {
                    result.HadPreviousEffectState = true;
                    result.PreviousEffectExpireTime = effectExpireTime;
                    if (effectExpireTime > nowUnixSeconds)
                        return Reject(result, InventoryItemLifecycleStatus.EffectActive, "effect is still active");
                }

                result.EffectExpireTime = ToUnixDeadline(
                    nowUnixSeconds,
                    stackable.StatChangeDurationMilliseconds);
            }

            if (checkCooltimeMaintenance && stackable.HasCooltimeMaintenance)
            {
                if (stackable.CoolTime <= 0)
                    return Reject(result, InventoryItemLifecycleStatus.InvalidDefinition, "missing [cool time]");

                if (inventory.ItemStates.TryGetExpireTime(
                        ItemStateKinds.Cooltime,
                        source.ItemId,
                        out var cooltimeExpireTime))
                {
                    result.HadPreviousCooltimeState = true;
                    result.PreviousCooltimeExpireTime = cooltimeExpireTime;
                    if (cooltimeExpireTime > nowUnixSeconds)
                        return Reject(result, InventoryItemLifecycleStatus.CooltimeActive, "cooltime is still active");
                }

                result.CooltimeExpireTime = ToUnixDeadline(
                    nowUnixSeconds,
                    stackable.CoolTime);
            }

            result.Status = InventoryItemLifecycleStatus.Success;
            result.Detail = null;
            return result;
        }

        internal static void ApplyUseSuccess(
            InventoryService inventory,
            InventoryItemLifecycleUsePlan plan)
        {
            if (inventory == null || plan == null || !plan.Success || plan.ItemTemplateId <= 0)
                return;

            if (plan.EffectExpireTime > 0)
                inventory.ItemStates.Upsert(
                    ItemStateKinds.Effect,
                    plan.ItemTemplateId,
                    plan.EffectExpireTime);

            if (plan.CooltimeExpireTime > 0)
                inventory.ItemStates.Upsert(
                    ItemStateKinds.Cooltime,
                    plan.ItemTemplateId,
                    plan.CooltimeExpireTime);
        }

        internal static void RollbackUseSuccess(
            InventoryService inventory,
            InventoryItemLifecycleUsePlan plan)
        {
            if (inventory == null || plan == null || plan.ItemTemplateId <= 0)
                return;

            if (plan.EffectExpireTime > 0)
                RestoreState(
                    inventory,
                    ItemStateKinds.Effect,
                    plan.ItemTemplateId,
                    plan.HadPreviousEffectState,
                    plan.PreviousEffectExpireTime);

            if (plan.CooltimeExpireTime > 0)
                RestoreState(
                    inventory,
                    ItemStateKinds.Cooltime,
                    plan.ItemTemplateId,
                    plan.HadPreviousCooltimeState,
                    plan.PreviousCooltimeExpireTime);
        }

        internal static bool TryRemoveExpiredSource(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int expectedItemTemplateId,
            long nowUnixSeconds,
            out InventoryMutationResult mutation)
        {
            mutation = null;
            if (inventory == null || slotIndex < 0)
                return false;

            var source = inventory.GetItem(listType, slotIndex);
            if (source == null
                || source.ItemId <= 0
                || (expectedItemTemplateId > 0 && source.ItemId != expectedItemTemplateId)
                || !IsExpired(inventory, listType, source, nowUnixSeconds))
            {
                return false;
            }

            var snapshot = source.Copy();
            if (!RemoveItemWithDetails(inventory, listType, slotIndex, source))
                return false;

            mutation = BuildExpiredRemovalMutation(listType, slotIndex, snapshot);
            return true;
        }

        internal static int RemoveExpiredItems(
            InventoryService inventory,
            long nowUnixSeconds,
            InventoryMutationSet changes)
        {
            if (inventory == null)
                return 0;

            var removed = 0;
            foreach (var listType in ExpirableListTypes)
            {
                var items = new List<KeyValuePair<short, ItemCore>>(
                    inventory.GetItems(listType));
                foreach (var pair in items)
                {
                    var item = pair.Value;
                    if (!IsExpired(inventory, listType, item, nowUnixSeconds))
                        continue;

                    if (!RemoveItemWithDetails(inventory, listType, pair.Key, item))
                        continue;

                    removed++;
                    changes?.AddSlot(listType, pair.Key);
                }
            }

            return removed;
        }

        internal static int RemoveExpiredItemsInRange(
            InventoryService inventory,
            InventoryListType listType,
            ItemSlotRange range,
            long nowUnixSeconds,
            InventoryMutationSet changes)
        {
            if (inventory == null || range.Count <= 0)
                return 0;

            var removed = 0;
            for (var slot = range.Start; slot <= range.End; slot++)
            {
                var slotIndex = (short)slot;
                var item = inventory.GetItem(listType, slotIndex);
                if (!IsExpired(inventory, listType, item, nowUnixSeconds))
                    continue;

                if (!RemoveItemWithDetails(inventory, listType, slotIndex, item))
                    continue;

                removed++;
                changes?.AddSlot(listType, slotIndex);
            }

            return removed;
        }

        internal static bool IsExpired(ItemCore item, long nowUnixSeconds)
        {
            return item != null
                && item.ItemId > 0
                && item.ExpireTime > 0
                && item.ExpireTime <= nowUnixSeconds;
        }

        internal static bool IsExpired(
            InventoryService inventory,
            InventoryListType listType,
            ItemCore item,
            long nowUnixSeconds)
        {
            if (item == null || item.ItemId <= 0)
                return false;

            if (item.ItemKind == ItemCore.KindAvatar)
            {
                var detail = inventory?.AvatarDetails.GetDetail(item.Value);
                return detail != null
                    && detail.ExpireDate > 0
                    && detail.ExpireDate <= nowUnixSeconds;
            }

            if (item.ItemKind == ItemCore.KindCreature)
            {
                var detail = inventory?.CreatureDetails.GetDetail(item.Value);
                return detail != null
                    && detail.ExpireDate > 0
                    && detail.ExpireDate <= nowUnixSeconds;
            }

            return IsExpired(item, nowUnixSeconds);
        }

        private static bool RemoveItemWithDetails(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            ItemCore item)
        {
            if (inventory == null || item == null)
                return false;

            var itemKind = item.ItemKind;
            var detailKey = item.Value;
            if (!inventory.RemoveItem(listType, slotIndex))
                return false;

            if (itemKind == ItemCore.KindAvatar && detailKey > 0)
                inventory.AvatarDetails.RemoveDirty(detailKey);
            else if (itemKind == ItemCore.KindCreature && detailKey > 0)
                inventory.CreatureDetails.RemoveDirty(detailKey);

            return true;
        }

        private static InventoryItemLifecycleUsePlan Reject(
            InventoryItemLifecycleUsePlan result,
            InventoryItemLifecycleStatus status,
            string detail)
        {
            result.Status = status;
            result.Detail = detail;
            return result;
        }

        private static void RestoreState(
            InventoryService inventory,
            string stateKind,
            int itemTemplateId,
            bool hadPreviousState,
            int previousExpireTime)
        {
            if (hadPreviousState && previousExpireTime > 0)
            {
                inventory.ItemStates.Upsert(
                    stateKind,
                    itemTemplateId,
                    previousExpireTime);
                return;
            }

            inventory.ItemStates.Remove(stateKind, itemTemplateId);
        }

        private static int ToUnixDeadline(long nowUnixSeconds, int durationMilliseconds)
        {
            var seconds = ((long)durationMilliseconds + 999L) / 1000L;
            if (seconds <= 0)
                seconds = 1;

            var deadline = nowUnixSeconds + seconds;
            if (deadline > int.MaxValue)
                return int.MaxValue;

            return (int)Math.Max(1L, deadline);
        }

        private static InventoryMutationResult BuildExpiredRemovalMutation(
            InventoryListType listType,
            short slotIndex,
            ItemCore snapshot)
        {
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = snapshot?.ItemId ?? 0,
                RemainingStackCount = 0,
                InstanceValue = 0,
                Durability = snapshot?.Durability ?? (ushort)0,
                ExpireTime = snapshot?.ExpireTime ?? 0,
                RequestedCount = ClampCount(snapshot != null ? Math.Max(1, snapshot.Count) : 1),
                AppliedCount = ClampCount(snapshot != null ? Math.Max(1, snapshot.Count) : 1),
                CoreSnapshot = snapshot,
            };
        }

        private static short ClampCount(int count)
        {
            return checked((short)Math.Min(short.MaxValue, Math.Max(0, count)));
        }
    }
}
