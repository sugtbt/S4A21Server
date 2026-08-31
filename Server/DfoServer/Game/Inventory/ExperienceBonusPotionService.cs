using DfoServer.Game.CharacterData;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    // Server-authoritative handling for PVF [exp bonus rate] consumables.
    // PVF 倍率写法可能是小数（如 1.5 倍秘药为 0.5），库存字段 bonus_rate 统一存
    // 千分率（倍率 * RateScale），CalculateBonus 计算时再除回来。
    //
    // 与上游 MR 的适配差异：冷却真源是在线 ItemStates book（state_kind='cooltime'，
    // 存绝对到期 unix 秒），随统一背包事务持久化到 character_item_states；
    // 活跃效果（倍率）写 character_experience_bonus_effects，登录时由
    // SqliteSelectCharacterDataSource 换算剩余秒下发 0x00AE。
    internal static class ExperienceBonusPotionService
    {
        internal const int RateScale = 1000;

        // 仅在 lease.SyncRoot 下调用。返回 false = 不是秘药（走通用消耗品流程）；
        // 返回 true = 已拦截/处理，mutation != null 表示使用成功。
        internal static bool TryUse(
            InventoryLease lease,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId,
            bool isInDungeon,
            out InventoryMutationResult mutation,
            out string detail)
        {
            mutation = null;
            detail = null;
            if (lease?.Inventory == null || characterId <= 0
                || listType != InventoryListType.Main)
            {
                return false;
            }

            var inventory = lease.Inventory;
            var source = inventory.GetItem(listType, slotIndex);
            if (source == null || source.IsEmpty || source.Count <= 0
                || (expectedItemId > 0 && source.ItemId != expectedItemId))
            {
                return false;
            }

            var definitionStatus = TryResolveDefinition(
                source.ItemId,
                out var rate,
                out var durationMilliseconds,
                out var cooldownMilliseconds);
            if (definitionStatus == DefinitionStatus.NotApplicable)
            {
                return false;
            }
            if (definitionStatus == DefinitionStatus.Invalid)
            {
                detail = $"invalid [exp bonus rate] definition item={source.ItemId}";
                FileLogger.Log($"[ExperienceBonusPotion] {detail}");
                return true;
            }

            if (!isInDungeon)
            {
                detail = "experience bonus potion can only be used in a dungeon";
                return true;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // 共享冷却：同 [cooltime group] 任一道具冷却中即整组拦截。
            if (IsCooltimeBlocked(inventory, source.ItemId, now))
            {
                detail = "experience bonus potion is on cooldown";
                return true;
            }

            var hadPreviousCooltime = inventory.ItemStates.TryGetExpireTime(
                ItemStateKinds.Cooltime,
                source.ItemId,
                out var previousCooltimeExpireTime);

            if (!InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    listType,
                    slotIndex,
                    source.ItemId,
                    1,
                    out var deleted)
                || !deleted.Success)
            {
                detail = "inventory deduction failed";
                return true;
            }

            var durationSeconds = Math.Max(
                1L,
                ((long)durationMilliseconds + 999L) / 1000L);
            var cooldownExpireTime = 0;
            if (cooldownMilliseconds > 0)
            {
                var cooldownSeconds = Math.Max(
                    1L,
                    ((long)cooldownMilliseconds + 999L) / 1000L);
                var deadline = now + cooldownSeconds;
                cooldownExpireTime = (int)Math.Min(int.MaxValue, Math.Max(1L, deadline));
                inventory.ItemStates.Upsert(
                    ItemStateKinds.Cooltime,
                    source.ItemId,
                    cooldownExpireTime);
            }

            var database = inventory.Database ?? GameDatabase.CreateDefault();
            using (var connection = database.OpenConnection())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                try
                {
                    // 效果真源：倍率+到期时间入效果表，供结算与登录状态栏恢复。
                    CharacterExperienceBonusEffectRepository.UpsertEffect(
                        connection,
                        transaction,
                        characterId,
                        source.ItemId,
                        rate,
                        now + durationSeconds);

                    if (!InventoryPersistenceService.SaveDirtyInTransaction(
                            connection,
                            transaction,
                            lease))
                    {
                        RollbackUse(
                            inventory,
                            listType,
                            slotIndex,
                            deleted,
                            cooldownExpireTime > 0,
                            hadPreviousCooltime,
                            previousCooltimeExpireTime,
                            source.ItemId);
                        detail = "inventory persistence failed";
                        return true;
                    }

                    transaction.Commit();
                }
                catch (SqliteException ex)
                {
                    RollbackUse(
                        inventory,
                        listType,
                        slotIndex,
                        deleted,
                        cooldownExpireTime > 0,
                        hadPreviousCooltime,
                        previousCooltimeExpireTime,
                        source.ItemId);
                    FileLogger.Log(
                        $"[ExperienceBonusPotion] SQLite failure item={source.ItemId} "
                        + $"cid={characterId} slot={slotIndex}: {ex.Message}");
                    detail = "database transaction failed";
                    return true;
                }
            }

            inventory.ClearDirtyState();
            mutation = new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = source.ItemId,
                RemainingStackCount = deleted.RemainingCount,
                InstanceValue = deleted.RemainingCount,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            detail = $"ratePerMille={rate} durationMs={durationMilliseconds} cooldownMs={cooldownMilliseconds}";
            return true;
        }

        // 结算用：当前活跃秘药倍率（千分率），无活跃效果返回 0。
        internal static int GetActiveRate(string connectionString, int characterId)
        {
            return CharacterExperienceBonusEffectRepository.TryGetActiveEffect(
                connectionString,
                characterId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                out _,
                out var bonusRate,
                out _)
                ? bonusRate
                : 0;
        }

        internal static uint CalculateBonus(uint experience, int rate)
        {
            if (experience == 0 || rate <= 0)
                return 0;
            return (uint)Math.Min(
                uint.MaxValue,
                (ulong)experience * (ulong)rate / RateScale);
        }

        // 共享冷却判定：同道具或同 [cooltime group] 成员有未到期冷却即拦截。
        internal static bool IsCooltimeBlocked(
            InventoryService inventory,
            int itemId,
            long nowUnixSeconds)
            => IsCooltimeBlocked(
                inventory,
                StackableItemProvider.ResolveCooltimeGroupMembers(itemId),
                nowUnixSeconds);

        internal static bool IsCooltimeBlocked(
            InventoryService inventory,
            IReadOnlyList<int> memberItemIds,
            long nowUnixSeconds)
        {
            if (inventory?.ItemStates == null || memberItemIds == null)
                return false;

            foreach (var memberId in memberItemIds)
            {
                if (memberId <= 0)
                    continue;
                if (inventory.ItemStates.TryGetExpireTime(
                        ItemStateKinds.Cooltime,
                        memberId,
                        out var expireTime)
                    && expireTime > nowUnixSeconds)
                {
                    return true;
                }
            }

            return false;
        }

        // 登录初始化用：读取未到期的秘药效果，换算成剩余秒供 0x00AE 效果列表下发（客户端按秒解释、自行除以 60 显示分钟）。
        internal static bool TryGetActiveEffect(
            string connectionString,
            int characterId,
            long nowUnixSeconds,
            out int sourceItemId,
            out int remainingSeconds)
        {
            sourceItemId = 0;
            remainingSeconds = 0;
            if (!CharacterExperienceBonusEffectRepository.TryGetActiveEffect(
                    connectionString,
                    characterId,
                    nowUnixSeconds,
                    out sourceItemId,
                    out _,
                    out var expiresAtUnixSeconds))
            {
                return false;
            }

            var remainingSec = expiresAtUnixSeconds - nowUnixSeconds;
            if (remainingSec <= 0)
                return false;
            remainingSeconds = (int)Math.Min(int.MaxValue, remainingSec);
            return true;
        }

        private static void RollbackUse(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            InventoryDeleteResult deleted,
            bool cooltimeWritten,
            bool hadPreviousCooltime,
            int previousCooltimeExpireTime,
            int itemId)
        {
            if (cooltimeWritten)
            {
                if (hadPreviousCooltime && previousCooltimeExpireTime > 0)
                {
                    inventory.ItemStates.Upsert(
                        ItemStateKinds.Cooltime,
                        itemId,
                        previousCooltimeExpireTime);
                }
                else
                {
                    inventory.ItemStates.Remove(ItemStateKinds.Cooltime, itemId);
                }
            }

            if (deleted?.SourceSnapshot != null)
                inventory.SetItem(listType, slotIndex, deleted.SourceSnapshot.Copy());
        }

        private static DefinitionStatus TryResolveDefinition(
            int itemId,
            out int rate,
            out int durationMilliseconds,
            out int cooldownMilliseconds)
        {
            rate = 0;
            durationMilliseconds = 0;
            cooldownMilliseconds = 0;
            var item = StackableItemProvider.Load(itemId);
            if (item?.Root == null)
                return DefinitionStatus.NotApplicable;

            var rateNodes = item.Root.GetChildren("exp bonus rate");
            if (rateNodes.Count == 0)
                return DefinitionStatus.NotApplicable;
            if (!TryReadScaledRate(item, rateNodes, out rate))
            {
                FileLogger.Log(
                    $"[ExperienceBonusPotion] invalid [exp bonus rate]: "
                    + BuildDefinitionDetail(itemId, item));
                return DefinitionStatus.Invalid;
            }

            if (item.StatChangeDurationMilliseconds <= 0)
            {
                FileLogger.Log(
                    $"[ExperienceBonusPotion] invalid [stat change duration]: "
                    + BuildDefinitionDetail(itemId, item));
                return DefinitionStatus.Invalid;
            }
            durationMilliseconds = item.StatChangeDurationMilliseconds;

            // [cool time] 可选：缺失或异常按无冷却处理，不影响使用判定。
            if (item.CoolTime > 0)
                cooldownMilliseconds = item.CoolTime;

            return DefinitionStatus.Supported;
        }

        private static bool TryReadScaledRate(
            StackableItemFile item,
            IReadOnlyList<ScriptNode> nodes,
            out int scaledRate)
        {
            scaledRate = 0;
            foreach (var node in nodes)
            {
                if (TryReadScaledRate(item, node, out scaledRate))
                    return true;
            }

            return false;
        }

        private static bool TryReadScaledRate(
            StackableItemFile item,
            ScriptNode node,
            out int scaledRate)
        {
            foreach (var dataItem in node.DataItems)
            {
                if (TryParseScaledRate(dataItem.GetContent(item.Content), out scaledRate))
                    return true;
            }

            foreach (var child in node.Children)
            {
                if (TryReadScaledRate(item, child, out scaledRate))
                    return true;
            }

            scaledRate = 0;
            return false;
        }

        // PVF [exp bonus rate] 既可能是整数（1=2倍、2=3倍）也可能是小数（0.5=1.5倍），
        // 统一按不变区域性解析后乘以 RateScale 存为千分率。
        internal static bool TryParseScaledRate(string raw, out int scaledRate)
        {
            scaledRate = 0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var text = raw.Trim().Trim('`').Trim();
            var match = Regex.Match(text, @"(?<!\d)\d+(?:\.\d+)?");
            if (!match.Success
                || !double.TryParse(
                    match.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var rate)
                || rate <= 0)
            {
                return false;
            }

            scaledRate = (int)Math.Round(
                rate * RateScale,
                MidpointRounding.AwayFromZero);
            return scaledRate > 0;
        }

        private static string BuildDefinitionDetail(
            int itemId,
            StackableItemFile item)
        {
            var tags = string.Join(
                ",",
                item.Root.Children.Select(node => node.Tag));
            return $"item={itemId} tags=[{tags}]";
        }

        private enum DefinitionStatus
        {
            NotApplicable,
            Invalid,
            Supported,
        }
    }
}
