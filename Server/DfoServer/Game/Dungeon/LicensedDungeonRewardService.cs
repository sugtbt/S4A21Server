using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    internal sealed class LicensedDungeonRewardRuntime
    {
        internal LicensedDungeonRewardRuntime(
            int dungeonId,
            int licenseLevel,
            bool groupBossPresent,
            LicensedDungeonRewardDisplayItem dungeonClearReward,
            LicensedDungeonRewardDisplayItem dailyClearReward,
            LicensedDungeonRewardDisplayItem groupBossReward,
            IReadOnlyList<LicensedDungeonRewardEffectItem> rewards)
        {
            DungeonId = dungeonId;
            LicenseLevel = licenseLevel;
            GroupBossPresent = groupBossPresent;
            DungeonClearReward = dungeonClearReward;
            DailyClearReward = dailyClearReward;
            GroupBossReward = groupBossReward;
            Rewards = rewards ?? Array.Empty<LicensedDungeonRewardEffectItem>();
        }

        internal int DungeonId { get; }
        internal int LicenseLevel { get; }
        internal bool GroupBossPresent { get; }
        internal LicensedDungeonRewardDisplayItem DungeonClearReward { get; }
        internal LicensedDungeonRewardDisplayItem DailyClearReward { get; }
        internal LicensedDungeonRewardDisplayItem GroupBossReward { get; }
        internal IReadOnlyList<LicensedDungeonRewardEffectItem> Rewards { get; }
    }

    internal sealed class LicensedDungeonRewardDisplayItem
    {
        internal LicensedDungeonRewardDisplayItem(int itemId, int count)
        {
            if (itemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(itemId));
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            ItemId = itemId;
            Count = count;
        }

        internal int ItemId { get; }
        internal int Count { get; }
    }

    internal static class LicensedDungeonRewardService
    {
        internal static LicensedDungeonRewardRuntime Prepare(DungeonRun run)
        {
            if (run == null
                || !LicensedDungeonCatalog.TryGetDefinition(
                    run.DungeonId,
                    out var definition))
            {
                return null;
            }

            var rewards = new List<LicensedDungeonRewardEffectItem>();
            LicensedDungeonRewardDisplayItem dungeonClearReward = null;
            foreach (var reward in LicensedDungeonCatalog
                         .GetDungeonClearRewards(definition.LicenseLevel))
            {
                AddReward(rewards, reward.ItemId, reward.Count);
                dungeonClearReward ??= CreateDisplayItem(
                    reward.ItemId,
                    reward.Count);
            }

            LicensedDungeonRewardDisplayItem dailyClearReward = null;
            if (LicensedDungeonCatalog.TryGetDailyClearReward(
                    definition.DungeonId,
                    out var dailyReward))
            {
                AddReward(rewards, dailyReward.ItemId, dailyReward.Count);
                dailyClearReward = CreateDisplayItem(
                    dailyReward.ItemId,
                    dailyReward.Count);
            }

            var groupBossPresent = definition.BossRule != null
                && definition.BossRule.BossMazeIndices.Contains(run.MazeIndex);
            LicensedDungeonRewardDisplayItem groupBossReward = null;
            if (groupBossPresent)
            {
                var groupDrop = SelectWeightedGroupDrop(
                    LicensedDungeonCatalog.GetGroupDropItems(
                        definition.LicenseLevel));
                if (groupDrop != null)
                {
                    AddReward(rewards, groupDrop.ItemId, 1);
                    groupBossReward = CreateDisplayItem(groupDrop.ItemId, 1);
                }
            }

            if (rewards.Count == 0)
            {
                throw new InvalidOperationException(
                    $"licensed dungeon {definition.DungeonId} has no rewards");
            }

            return new LicensedDungeonRewardRuntime(
                definition.DungeonId,
                definition.LicenseLevel,
                groupBossPresent,
                dungeonClearReward,
                dailyClearReward,
                groupBossReward,
                rewards.AsReadOnly());
        }

        private static LicensedDungeonRewardDisplayItem CreateDisplayItem(
            int itemId,
            int count)
            => itemId > 0 && count > 0
                ? new LicensedDungeonRewardDisplayItem(itemId, count)
                : null;

        private static LicenseDungeonWeightedDropItem SelectWeightedGroupDrop(
            IReadOnlyList<LicenseDungeonWeightedDropItem> drops)
        {
            if (drops == null || drops.Count == 0)
                return null;

            long totalWeight = 0;
            foreach (var drop in drops)
            {
                if (drop == null || drop.ItemId <= 0 || drop.Weight <= 0)
                    continue;
                totalWeight += drop.Weight;
            }
            if (totalWeight <= 0)
                return null;

            var roll = ServerRandom.Next((int)Math.Min(
                int.MaxValue,
                totalWeight));
            long cursor = 0;
            foreach (var drop in drops)
            {
                if (drop == null || drop.ItemId <= 0 || drop.Weight <= 0)
                    continue;
                cursor += drop.Weight;
                if (roll < cursor)
                    return drop;
            }
            return drops.LastOrDefault(drop =>
                drop != null && drop.ItemId > 0 && drop.Weight > 0);
        }

        private static void AddReward(
            ICollection<LicensedDungeonRewardEffectItem> rewards,
            int itemId,
            int count)
        {
            if (itemId <= 0 || count <= 0)
                return;

            var existing = rewards.FirstOrDefault(item => item.ItemId == itemId);
            if (existing == null)
            {
                rewards.Add(new LicensedDungeonRewardEffectItem
                {
                    ItemId = itemId,
                    StackCount = count,
                });
                return;
            }

            var merged = Math.Min(
                int.MaxValue,
                (long)existing.StackCount + count);
            existing.StackCount = (int)merged;
        }
    }
}
