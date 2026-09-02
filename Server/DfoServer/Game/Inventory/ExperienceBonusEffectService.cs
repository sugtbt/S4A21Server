using PvfLib;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    // 经验加成效果的轻量解析器：只负责从在线 item state 与 stk 里
    // 读取活跃倍率，不维护独立持久化表。
    internal static class ExperienceBonusEffectService
    {
        internal const int RateScale = 1000;

        internal static int GetActiveRate(InventoryService inventory, long nowUnixSeconds)
        {
            return GetActiveRate(inventory, nowUnixSeconds, StackableItemProvider.Load);
        }

        internal static int GetActiveRate(
            InventoryService inventory,
            long nowUnixSeconds,
            Func<int, StackableItemFile> stackableLoader)
        {
            if (inventory?.ItemStates == null)
                return 0;

            var loader = stackableLoader ?? StackableItemProvider.Load;
            var bestRate = 0;
            foreach (var entry in inventory.ItemStates.GetEntries())
            {
                if (entry == null
                    || entry.ItemId <= 0
                    || !string.Equals(entry.StateKind, ItemStateKinds.Effect, StringComparison.Ordinal)
                    || entry.ExpireTime <= nowUnixSeconds)
                {
                    continue;
                }

                if (!TryResolveRate(entry.ItemId, loader, out var rate))
                    continue;

                if (rate > bestRate)
                    bestRate = rate;
            }

            return bestRate;
        }

        internal static bool TryResolveRate(int itemTemplateId, out int rate)
            => TryResolveRate(itemTemplateId, StackableItemProvider.Load, out rate);

        internal static bool TryResolveRate(
            int itemTemplateId,
            Func<int, StackableItemFile> stackableLoader,
            out int rate)
        {
            rate = 0;
            if (itemTemplateId <= 0)
                return false;

            var item = stackableLoader?.Invoke(itemTemplateId);
            if (item?.Root == null)
                return false;

            var rateNodes = item.Root.GetChildren("exp bonus rate");
            if (rateNodes.Count == 0)
                return false;

            if (!TryReadScaledRate(item, rateNodes, out rate))
            {
                FileLogger.Log(
                    $"[ExperienceBonusEffect] invalid [exp bonus rate]: "
                    + BuildDefinitionDetail(itemTemplateId, item));
                return false;
            }

            return rate > 0;
        }

        internal static uint CalculateBonus(uint experience, int rate)
        {
            if (experience == 0 || rate <= 0)
                return 0;

            return (uint)Math.Min(
                uint.MaxValue,
                (ulong)experience * (ulong)rate / RateScale);
        }

        private static bool TryReadScaledRate(
            StackableItemFile item,
            System.Collections.Generic.IReadOnlyList<ScriptNode> nodes,
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
    }
}
