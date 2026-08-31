using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryTitleChangeResolution
    {
        public int SourceItemId { get; set; }

        public int TargetItemId { get; set; }

        public int ResultItemId { get; set; }

        public short ResultValue { get; set; }

        public bool IsSuccessBranch { get; set; }

        public bool IsLimitedCube { get; set; }

        public IReadOnlyList<InventoryMaterialRequirement> AdditionalMaterials { get; set; } =
            Array.Empty<InventoryMaterialRequirement>();
    }

    internal sealed class InventoryTitleChangeResultOption
    {
        public InventoryTitleChangeResultOption(int itemId, int weight)
            : this(itemId, 1, weight)
        {
        }

        public InventoryTitleChangeResultOption(int itemId, short resultValue, int weight)
        {
            ItemId = itemId;
            ResultValue = resultValue;
            Weight = weight;
        }

        public int ItemId { get; }

        public short ResultValue { get; }

        public int Weight { get; }
    }

    internal sealed class InventoryTitleChangeRule
    {
        private const int ProbabilityScale = 10000;
        private readonly Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>> _successOptions;
        private readonly Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>> _failureOptions;
        private readonly Dictionary<int, int> _successRates;
        private readonly bool _usesSuccessBranch;

        private InventoryTitleChangeRule(
            Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>> successOptions,
            Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>> failureOptions,
            Dictionary<int, int> successRates,
            bool usesSuccessBranch,
            IReadOnlyList<InventoryMaterialRequirement> additionalMaterials)
        {
            _successOptions = successOptions;
            _failureOptions = failureOptions;
            _successRates = successRates;
            _usesSuccessBranch = usesSuccessBranch;
            AdditionalMaterials = additionalMaterials
                ?? Array.Empty<InventoryMaterialRequirement>();
        }

        public IReadOnlyList<InventoryMaterialRequirement> AdditionalMaterials { get; }

        public static InventoryTitleChangeRule CreateLimitedCube(
            IEnumerable<int> targetItemIds,
            IEnumerable<InventoryTitleChangeResultOption> resultOptions,
            IReadOnlyList<InventoryMaterialRequirement> additionalMaterials)
        {
            var options = NormalizeOptions(resultOptions);
            var successOptions = new Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>>();
            foreach (var targetItemId in (targetItemIds ?? Array.Empty<int>())
                         .Where(itemId => itemId > 0)
                         .Distinct())
            {
                var targetOptions = options
                    .Where(option => option.ItemId != targetItemId)
                    .ToList();
                if (targetOptions.Count > 0)
                    successOptions[targetItemId] = targetOptions;
            }

            return new InventoryTitleChangeRule(
                successOptions,
                new Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>>(),
                new Dictionary<int, int>(),
                usesSuccessBranch: false,
                additionalMaterials);
        }

        public static InventoryTitleChangeRule CreateTitleChange(
            IReadOnlyDictionary<int, int> successRates,
            IReadOnlyDictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>> successOptions,
            IReadOnlyDictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>> failureOptions)
        {
            var normalizedRates = new Dictionary<int, int>();
            if (successRates != null)
            {
                foreach (var pair in successRates)
                {
                    if (pair.Key > 0)
                    {
                        normalizedRates[pair.Key] = Math.Max(
                            0,
                            Math.Min(ProbabilityScale, pair.Value));
                    }
                }
            }

            return new InventoryTitleChangeRule(
                NormalizeOptionsByTarget(successOptions),
                NormalizeOptionsByTarget(failureOptions),
                normalizedRates,
                usesSuccessBranch: true,
                Array.Empty<InventoryMaterialRequirement>());
        }

        public bool TrySelectResult(
            int targetItemId,
            Func<int, int> next,
            out InventoryTitleChangeResultOption resultOption,
            out bool isSuccessBranch)
        {
            resultOption = null;
            isSuccessBranch = true;
            if (!_usesSuccessBranch)
                return TrySelectOption(_successOptions, targetItemId, next, out resultOption);

            if (!_successRates.TryGetValue(targetItemId, out var successRate))
                return false;

            isSuccessBranch = successRate >= ProbabilityScale
                || successRate > 0 && Next(next, ProbabilityScale) < successRate;
            return TrySelectOption(
                isSuccessBranch ? _successOptions : _failureOptions,
                targetItemId,
                next,
                out resultOption);
        }

        private static Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>>
            NormalizeOptionsByTarget(
                IReadOnlyDictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>> optionsByTarget)
        {
            var result = new Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>>();
            if (optionsByTarget == null)
                return result;

            foreach (var pair in optionsByTarget)
            {
                var options = NormalizeOptions(pair.Value);
                if (pair.Key > 0 && options.Count > 0)
                    result[pair.Key] = options;
            }

            return result;
        }

        private static List<InventoryTitleChangeResultOption> NormalizeOptions(
            IEnumerable<InventoryTitleChangeResultOption> options)
        {
            return (options ?? Array.Empty<InventoryTitleChangeResultOption>())
                .Where(option => option != null
                    && option.ItemId > 0
                    && option.ResultValue > 0
                    && option.Weight > 0)
                .GroupBy(option => new { option.ItemId, option.ResultValue })
                .Select(group => new InventoryTitleChangeResultOption(
                    group.Key.ItemId,
                    group.Key.ResultValue,
                    (int)Math.Min(
                        int.MaxValue,
                        group.Sum(option => (long)option.Weight))))
                .ToList();
        }

        private static bool TrySelectOption(
            IReadOnlyDictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>> optionsByTarget,
            int targetItemId,
            Func<int, int> next,
            out InventoryTitleChangeResultOption resultOption)
        {
            resultOption = null;
            if (optionsByTarget == null
                || !optionsByTarget.TryGetValue(targetItemId, out var options)
                || options == null
                || options.Count == 0)
            {
                return false;
            }

            var totalWeight = options.Sum(option => (long)option.Weight);
            if (totalWeight <= 0 || totalWeight > int.MaxValue)
                return false;

            var roll = Next(next, (int)totalWeight);
            var cumulative = 0L;
            foreach (var option in options)
            {
                cumulative += option.Weight;
                if (roll < cumulative)
                {
                    resultOption = option;
                    return true;
                }
            }

            return false;
        }

        private static int Next(Func<int, int> next, int range)
        {
            var value = next != null ? next(range) : ServerRandom.Next(range);
            if (value < 0)
                return 0;
            return value < range ? value : value % range;
        }
    }

    internal static class InventoryTitleChangeRuleResolver
    {
        internal static bool TryResolveTitleChange(
            int sourceItemId,
            int targetItemId,
            out InventoryTitleChangeResolution resolution)
        {
            return TryResolveTitleChange(sourceItemId, targetItemId, null, out resolution);
        }

        internal static bool TryResolveTitleChange(
            int sourceItemId,
            int targetItemId,
            Func<int, int> next,
            out InventoryTitleChangeResolution resolution)
        {
            resolution = null;
            return PvfTitleChangeTableRuleProvider.TryGetRule(sourceItemId, out var rule)
                && TryResolveRule(sourceItemId, targetItemId, rule, next, false, out resolution);
        }

        internal static bool TryResolveLimitedCube(
            int sourceItemId,
            int targetItemId,
            out InventoryTitleChangeResolution resolution)
        {
            resolution = null;
            return PvfLimitedCubeTitleChangeRuleProvider.TryGetRule(sourceItemId, out var rule)
                && TryResolveRule(sourceItemId, targetItemId, rule, null, true, out resolution);
        }

        private static bool TryResolveRule(
            int sourceItemId,
            int targetItemId,
            InventoryTitleChangeRule rule,
            Func<int, int> next,
            bool isLimitedCube,
            out InventoryTitleChangeResolution resolution)
        {
            resolution = null;
            if (sourceItemId <= 0
                || targetItemId <= 0
                || rule == null
                || (!isLimitedCube && !ItemMetadataResolver.IsTitleEquipment(targetItemId))
                || !rule.TrySelectResult(
                    targetItemId,
                    next,
                    out var resultOption,
                    out var isSuccessBranch)
                || resultOption == null
                || (!isLimitedCube && !ItemMetadataResolver.IsTitleEquipment(resultOption.ItemId)))
            {
                return false;
            }

            resolution = new InventoryTitleChangeResolution
            {
                SourceItemId = sourceItemId,
                TargetItemId = targetItemId,
                ResultItemId = resultOption.ItemId,
                ResultValue = resultOption.ResultValue,
                IsSuccessBranch = isSuccessBranch,
                IsLimitedCube = isLimitedCube,
                AdditionalMaterials = rule.AdditionalMaterials,
            };
            return true;
        }
    }

    internal static class PvfTitleChangeTableRuleProvider
    {
        private const string MainTablePath = "etc/aradtitlechange_main.etc";
        private const string SubTablePath = "etc/aradtitlechange_sub.etc";
        private static readonly Lazy<IReadOnlyDictionary<int, InventoryTitleChangeRule>> Rules =
            new Lazy<IReadOnlyDictionary<int, InventoryTitleChangeRule>>(LoadRules);

        internal static bool TryGetRule(int sourceItemId, out InventoryTitleChangeRule rule)
        {
            return Rules.Value.TryGetValue(sourceItemId, out rule);
        }

        private static IReadOnlyDictionary<int, InventoryTitleChangeRule> LoadRules()
        {
            try
            {
                var main = TitleChangeMainFile.Parse(PvfArchiveAccessor.ReadText(MainTablePath));
                var sub = TitleChangeSubFile.Parse(PvfArchiveAccessor.ReadText(SubTablePath));
                var outcomes = sub.Entries
                    .GroupBy(entry => entry.TargetItemId)
                    .ToDictionary(group => group.Key, group => group.Last());
                var result = new Dictionary<int, InventoryTitleChangeRule>();

                foreach (var entry in main.Entries)
                {
                    if (entry == null
                        || entry.SourceItemId <= 0
                        || !ItemMetadataResolver.TryLoadStackableFile(entry.SourceItemId, out _))
                    {
                        continue;
                    }

                    var rates = new Dictionary<int, int>();
                    var successOptions = new Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>>();
                    var failureOptions = new Dictionary<int, IReadOnlyList<InventoryTitleChangeResultOption>>();
                    foreach (var target in entry.Targets
                                 .Where(target => target != null && target.ItemId > 0)
                                 .GroupBy(target => target.ItemId)
                                 .Select(group => group.Last()))
                    {
                        if (!outcomes.TryGetValue(target.ItemId, out var outcome))
                            continue;

                        rates[target.ItemId] = GetEffectiveSuccessRate(entry, target);
                        successOptions[target.ItemId] = ToOptions(outcome.SuccessItems);
                        failureOptions[target.ItemId] = ToOptions(outcome.FailureItems);
                    }

                    if (rates.Count > 0)
                    {
                        result[entry.SourceItemId] = InventoryTitleChangeRule.CreateTitleChange(
                            rates,
                            successOptions,
                            failureOptions);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[TitleChangeRule] load table failed: {ex.Message}");
                return new Dictionary<int, InventoryTitleChangeRule>();
            }
        }

        private static IReadOnlyList<InventoryTitleChangeResultOption> ToOptions(
            IEnumerable<TitleChangeWeightedItem> items)
        {
            return (items ?? Array.Empty<TitleChangeWeightedItem>())
                .Where(item => item != null)
                .Select(item => new InventoryTitleChangeResultOption(item.ItemId, item.Weight))
                .ToList();
        }

        internal static int GetEffectiveSuccessRate(
            TitleChangeMainEntry entry,
            TitleChangeTargetEntry target)
        {
            if (entry == null || target == null)
                return 0;

            return target.SuccessRate > 0 ? target.SuccessRate : entry.SuccessRate;
        }
    }

    internal static class PvfLimitedCubeTitleChangeRuleProvider
    {
        private static readonly ConcurrentDictionary<int, Lazy<InventoryTitleChangeRule>> Rules =
            new ConcurrentDictionary<int, Lazy<InventoryTitleChangeRule>>();

        internal static bool TryGetRule(int sourceItemId, out InventoryTitleChangeRule rule)
        {
            rule = Rules.GetOrAdd(
                sourceItemId,
                itemId => new Lazy<InventoryTitleChangeRule>(() => LoadRule(itemId))).Value;
            return rule != null;
        }

        private static InventoryTitleChangeRule LoadRule(int sourceItemId)
        {
            try
            {
                if (!ItemMetadataResolver.TryLoadStackableFile(sourceItemId, out var stackable)
                    || stackable?.UpgradeLimitCube == null
                    || !string.Equals(
                        NormalizeTag(stackable.ActionTypeName),
                        "[limited cube]",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var info = stackable.UpgradeLimitCube;
                if (info.ConditionItems.Any(item => item.Count != 1))
                    return null;

                var materials = NormalizeMaterials(info.AdditionalMaterials);
                if (materials == null)
                    return null;

                return InventoryTitleChangeRule.CreateLimitedCube(
                    info.ConditionItems.Select(item => item.ItemId),
                    info.Results.Select(result => new InventoryTitleChangeResultOption(
                        result.ItemId,
                        result.ResultValue,
                        result.Weight)),
                    materials);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[TitleChangeRule] load limited cube failed item=0x{sourceItemId:X8}: {ex.Message}");
                return null;
            }
        }

        private static IReadOnlyList<InventoryMaterialRequirement> NormalizeMaterials(
            IEnumerable<LimitedCubeItemRequirement> materials)
        {
            var totals = new Dictionary<int, long>();
            foreach (var material in materials ?? Array.Empty<LimitedCubeItemRequirement>())
            {
                if (material == null || material.ItemId <= 0 || material.Count <= 0)
                    return null;

                var total = (totals.TryGetValue(material.ItemId, out var current)
                    ? current
                    : 0L) + material.Count;
                if (total > int.MaxValue)
                    return null;
                totals[material.ItemId] = total;
            }

            return totals
                .OrderBy(pair => pair.Key)
                .Select(pair => new InventoryMaterialRequirement(pair.Key, (int)pair.Value))
                .ToList();
        }

        private static string NormalizeTag(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("`", string.Empty);
        }
    }
}
