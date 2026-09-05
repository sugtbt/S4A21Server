using DfoServer.Game.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal enum ExperienceItemGrantKind
    {
        None,
        Fixed,
        Percent,
        CrackOfDimension,
    }

    internal sealed class ExperienceItemDefinition
    {
        private static readonly string[] CharacterJobLabels =
        {
            "swordman", "fighter", "gunner", "mage", "priest",
            "at gunner", "thief", "at fighter", "at mage",
            "demonic swordman", "creator mage", "at swordman", "knight",
        };

        internal ExperienceItemDefinition(int itemTemplateId)
        {
            ItemTemplateId = itemTemplateId;
        }

        internal int ItemTemplateId { get; }
        internal ExperienceItemGrantKind GrantKind { get; set; }
        internal uint Value { get; set; }
        internal int MinimumLevel { get; set; } = -1;
        internal int MaximumLevel { get; set; } = -1;
        internal bool IsExperienceLike { get; set; }
        internal bool IsSupported { get; set; }
        internal bool BlockedInHardcore { get; set; }
        internal bool TownOnly { get; set; }
        internal int AbsoluteExpirationUnixTime { get; set; }
        internal int UsablePeriodDays { get; set; }
        internal int CooldownMilliseconds { get; set; }
        internal string CooldownGroup { get; set; } = string.Empty;
        internal string UnsupportedReason { get; set; }
        internal HashSet<string> AllowedJobLabels { get; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> ExcludedJobLabels { get; }
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal bool IsTemplateAvailableAt(uint unixTime)
            => IsSupported
                && (AbsoluteExpirationUnixTime <= 0
                    || (uint)AbsoluteExpirationUnixTime > unixTime);

        internal uint CalculateGain(byte level)
        {
            if (!IsSupported || level == 0)
                return 0;
            if (GrantKind == ExperienceItemGrantKind.Fixed)
                return Value;
            if (GrantKind != ExperienceItemGrantKind.Percent)
                return 0;

            var currentThreshold = (long)Math.Max(0, ExpTableProvider.GetLevelThreshold(level));
            var previousThreshold = level <= 1
                ? 0L
                : Math.Max(0, ExpTableProvider.GetLevelThreshold(level - 1));
            var levelSegment = Math.Max(0L, currentThreshold - previousThreshold);
            return (uint)Math.Min(uint.MaxValue, levelSegment * Value / 100L);
        }

        internal bool IsUsableByJob(byte job)
        {
            var label = job < CharacterJobLabels.Length
                ? CharacterJobLabels[job]
                : string.Empty;
            if (label.Length == 0)
                return false;
            if (AllowedJobLabels.Count > 0 && !AllowedJobLabels.Contains(label))
                return false;
            return !ExcludedJobLabels.Contains(label);
        }

        internal static bool IsKnownJobLabel(string label)
            => CharacterJobLabels.Contains(label ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    internal static class ExperienceItemDataProvider
    {
        private static readonly HashSet<int> UnmodeledServerRestrictionItemIds
            = new HashSet<int>
            {
                2683665,
                2749682,
                2749909,
            };

        private static readonly HashSet<string> AllowedRootTags
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "name", "name2", "explain", "flavor text", "grade", "rarity",
                "minimum level", "maximum level", "usable job", "suitable job",
                "attach type", "icon", "field image", "icon mark", "move wav",
                "stackable type", "sub type", "item group name", "item category", "stack limit",
                "total usable count",
                "price", "value", "weight", "cool time", "cooltime group",
                "cooltime maintenance", "effect maintenance", "stat change duration",
                "impossible contents", "expiration date", "usable period", "trade limit max",
                "daily delete item", "daily purchase limit", "use wav", "impossible jobs",
                "action usable place", "increase status type", "npc gift disallowance",
                // 本路径中 [need material] 是获得/兑换元数据，
                // 不是 [increase status type] 的使用消耗条件。
                "need material",
            };

        internal static ExperienceItemDefinition Resolve(int itemTemplateId)
            => Resolve(itemTemplateId, StackableItemProvider.Load(itemTemplateId));

        internal static ExperienceItemDefinition Resolve(
            int itemTemplateId,
            StackableItemFile stackable)
        {
            var result = new ExperienceItemDefinition(itemTemplateId);
            if (stackable?.Root == null)
                return result;

            var effects = stackable.StatusIncreases
                ?? new List<StackableStatusIncreaseEntry>();
            result.IsExperienceLike = effects.Any(effect =>
                IsCharacterExperienceEffect(effect?.EffectType));
            if (!result.IsExperienceLike)
                return result;

            if (!IsEtcVariantZeroStackable(stackable.StackableType))
            {
                return Reject(result, "experience effect is not a variant-0 [etc] stackable");
            }

            var statusNodes = stackable.Root.GetChildren("increase status type");
            if (effects.Count != 1
                || statusNodes.Count != 1
                || statusNodes[0].Children.Count != 0
                || statusNodes[0].DataItems.Count != 1)
            {
                return Reject(result, "experience effect is mixed with another stackable behavior");
            }

            var unknownTag = stackable.Root.Children.FirstOrDefault(node =>
                !AllowedRootTags.Contains(node.Tag));
            if (unknownTag != null)
                return Reject(result, $"unreviewed PVF behavior tag [{unknownTag.Tag}]");

            if (!TryApplyLevelRestrictions(stackable, result, out var restrictionError)
                || !TryApplyJobRestrictions(stackable, result, out restrictionError)
                || !TryApplyCooldown(stackable, result, out restrictionError)
                || !TryApplyUsablePlaces(stackable, result, out restrictionError)
                || !TryApplyImpossibleContents(stackable, result, out restrictionError))
            {
                return Reject(result, restrictionError);
            }

            if (!StackableExpirationPolicyResolver.TryResolve(stackable, out var expirationPolicy))
                return Reject(result, "invalid expiration definition");
            result.UsablePeriodDays = expirationPolicy.UsablePeriodDays;
            result.AbsoluteExpirationUnixTime = expirationPolicy.AbsoluteExpirationUnixTime;

            if (UnmodeledServerRestrictionItemIds.Contains(itemTemplateId))
            {
                return Reject(
                    result,
                    "experience-gauge restriction is not represented structurally in PVF");
            }

            return ApplyGrant(effects[0], result);
        }

        private static ExperienceItemDefinition ApplyGrant(
            StackableStatusIncreaseEntry effect,
            ExperienceItemDefinition result)
        {
            var normalizedEffect = NormalizeEffect(effect?.EffectType);
            // 异次元裂缝经验与普通固定经验共用同一套角色经验数学核:
            // 道具文案“经验值增加 %s”即固定经验, 数值来自 stk 文件该标签的值。
            if (normalizedEffect == "expup"
                || normalizedEffect == "expupbycrackofdimension")
            {
                if (effect?.Values == null
                    || effect.Values.Count != 1
                    || effect.Values[0] <= 0)
                {
                    return Reject(result, "invalid fixed experience value");
                }
                result.GrantKind = ExperienceItemGrantKind.Fixed;
                result.Value = (uint)effect.Values[0];
                result.IsSupported = true;
                return result;
            }

            if (normalizedEffect == "exppercentup" || normalizedEffect == "expupbypercent")
            {
                if (effect?.Values == null
                    || effect.Values.Count != 1
                    || effect.Values[0] <= 0
                    || effect.Values[0] > 100)
                {
                    return Reject(result, "invalid percentage experience value");
                }

                result.GrantKind = ExperienceItemGrantKind.Percent;
                result.Value = (uint)effect.Values[0];
                result.IsSupported = true;
                return result;
            }

            result.GrantKind = ExperienceItemGrantKind.CrackOfDimension;
            return Reject(result, "crack-of-dimension experience has no value in the stk file");
        }

        private static bool TryApplyLevelRestrictions(
            StackableItemFile stackable,
            ExperienceItemDefinition result,
            out string error)
        {
            error = null;
            if (!StackablePvfValueReader.TryReadOptionalNonNegativeInt(
                    stackable,
                    "minimum level",
                    out var hasMinimumLevel,
                    out var minimumLevel))
            {
                error = "invalid [minimum level] definition";
                return false;
            }
            if (!StackablePvfValueReader.TryReadOptionalNonNegativeInt(
                    stackable,
                    "maximum level",
                    out var hasMaximumLevel,
                    out var maximumLevel))
            {
                error = "invalid [maximum level] definition";
                return false;
            }

            result.MinimumLevel = hasMinimumLevel ? minimumLevel : -1;
            result.MaximumLevel = hasMaximumLevel ? maximumLevel : -1;
            if (!hasMinimumLevel || !hasMaximumLevel || minimumLevel <= maximumLevel)
                return true;

            error = "minimum level exceeds maximum level";
            return false;
        }

        private static bool TryApplyCooldown(
            StackableItemFile stackable,
            ExperienceItemDefinition result,
            out string error)
        {
            error = null;
            if (!stackable.HasCooltimeMaintenance)
            {
                result.CooldownMilliseconds = 0;
                result.CooldownGroup = string.Empty;
                return true;
            }

            if (!StackablePvfValueReader.TryReadOptionalNonNegativeInt(
                    stackable,
                    "cool time",
                    out var hasCooldown,
                    out var cooldownMilliseconds))
            {
                error = "invalid [cool time] definition";
                return false;
            }
            if (!StackablePvfValueReader.TryReadOptionalSingleValue(
                    stackable,
                    "cooltime group",
                    out var hasCooldownGroup,
                    out var cooldownGroup))
            {
                error = "invalid [cooltime group] definition";
                return false;
            }

            if (!hasCooldown || cooldownMilliseconds <= 0)
            {
                error = "missing [cool time] definition";
                return false;
            }

            result.CooldownMilliseconds = cooldownMilliseconds;
            result.CooldownGroup = hasCooldownGroup ? cooldownGroup : string.Empty;
            return true;
        }

        private static bool TryApplyJobRestrictions(
            StackableItemFile stackable,
            ExperienceItemDefinition result,
            out string error)
        {
            error = null;
            var usableJobNodes = stackable.Root.GetChildren("usable job");
            var usableJobs = new List<string>();
            foreach (var node in usableJobNodes)
            {
                if (node.Children.Count != 0 || node.DataItems.Count == 0)
                {
                    error = "invalid [usable job] definition";
                    return false;
                }

                foreach (var item in node.DataItems)
                {
                    var values = ExtractLabels(item.GetContent(stackable.Content));
                    if (values.Count == 0)
                    {
                        error = "invalid [usable job] definition";
                        return false;
                    }
                    usableJobs.AddRange(values);
                }
            }

            var unknownUsableJob = usableJobs.FirstOrDefault(label =>
                !string.Equals(label, "all", StringComparison.OrdinalIgnoreCase)
                && !ExperienceItemDefinition.IsKnownJobLabel(label));
            if (unknownUsableJob != null)
            {
                error = $"unknown [usable job] value [{unknownUsableJob}]";
                return false;
            }
            if (usableJobs.Count > 0
                && !usableJobs.Contains("all", StringComparer.OrdinalIgnoreCase))
            {
                result.AllowedJobLabels.UnionWith(usableJobs);
            }

            foreach (var node in stackable.Root.GetChildren("impossible jobs"))
            {
                if (node.Children.Count != 0)
                {
                    error = "invalid [impossible jobs] definition";
                    return false;
                }

                foreach (var item in node.DataItems)
                {
                    var labels = ExtractLabels(item.GetContent(stackable.Content));
                    if (labels.Count == 0)
                    {
                        error = "invalid [impossible jobs] definition";
                        return false;
                    }
                    foreach (var label in labels)
                    {
                        if (!ExperienceItemDefinition.IsKnownJobLabel(label))
                        {
                            error = $"unknown [impossible jobs] value [{label}]";
                            return false;
                        }
                        result.ExcludedJobLabels.Add(label);
                    }
                }
            }

            return true;
        }

        private static bool TryApplyUsablePlaces(
            StackableItemFile stackable,
            ExperienceItemDefinition result,
            out string error)
        {
            error = null;
            var nodes = stackable.Root.GetChildren("action usable place");
            if (nodes.Count == 0)
                return true;

            var places = new List<string>();
            foreach (var node in nodes)
            {
                if (node.Children.Count != 0 || node.DataItems.Count == 0)
                {
                    error = "invalid [action usable place] definition";
                    return false;
                }
                foreach (var item in node.DataItems)
                {
                    var values = ExtractLabels(item.GetContent(stackable.Content));
                    if (values.Count == 0)
                    {
                        error = "invalid [action usable place] definition";
                        return false;
                    }
                    places.AddRange(values);
                }
            }

            var unsupportedPlace = places.FirstOrDefault(place =>
                !string.Equals(place, "village", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(place, "seria room", StringComparison.OrdinalIgnoreCase));
            if (unsupportedPlace != null)
            {
                error = $"unsupported [action usable place] value [{unsupportedPlace}]";
                return false;
            }

            result.TownOnly = true;
            return true;
        }

        private static bool TryApplyImpossibleContents(
            StackableItemFile stackable,
            ExperienceItemDefinition result,
            out string error)
        {
            error = null;
            foreach (var node in stackable.Root.GetChildren("impossible contents"))
            {
                if (node.Children.Count != 0 || node.DataItems.Count == 0)
                {
                    error = "invalid [impossible contents] definition";
                    return false;
                }

                foreach (var item in node.DataItems)
                {
                    var contents = ExtractLabels(item.GetContent(stackable.Content));
                    if (contents.Count == 0)
                    {
                        error = "invalid [impossible contents] definition";
                        return false;
                    }

                    foreach (var content in contents)
                    {
                        if (string.Equals(content, "hardcore mode", StringComparison.OrdinalIgnoreCase))
                        {
                            result.BlockedInHardcore = true;
                            continue;
                        }
                        if (string.Equals(content, "ban redeemitem", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(content, "gift", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(content, "charac cargo", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        error = $"impossible content [{content}] is not implemented";
                        return false;
                    }
                }
            }

            return true;
        }

        private static List<string> ExtractLabels(string raw)
        {
            var values = new List<string>();
            foreach (Match match in Regex.Matches(raw ?? string.Empty, @"\[(?<value>[^\]]+)\]"))
                AddNormalizedLabel(values, match.Groups["value"].Value);
            if (values.Count > 0)
                return values;

            foreach (Match match in Regex.Matches(raw ?? string.Empty, @"`(?<value>[^`]+)`"))
                AddNormalizedLabel(values, match.Groups["value"].Value);
            return values;
        }

        private static void AddNormalizedLabel(List<string> values, string raw)
        {
            var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length > 0)
                values.Add(value);
        }

        private static bool IsEtcVariantZeroStackable(string raw)
        {
            var match = Regex.Match(
                raw ?? string.Empty,
                @"^\s*`?\[(?<type>[^\]]+)\]`?\s+(?<variant>-?\d+)\s*$");
            return match.Success
                && string.Equals(
                    match.Groups["type"].Value.Trim(),
                    "etc",
                    StringComparison.OrdinalIgnoreCase)
                && int.TryParse(match.Groups["variant"].Value, out var variant)
                && variant == 0;
        }

        private static bool IsCharacterExperienceEffect(string effectType)
        {
            var normalized = NormalizeEffect(effectType);
            return normalized == "expup"
                || normalized == "exppercentup"
                || normalized == "expupbypercent"
                || normalized == "expupbycrackofdimension";
        }

        private static string NormalizeEffect(string effectType)
            => (effectType ?? string.Empty)
                .Replace("[", string.Empty)
                .Replace("]", string.Empty)
                .Trim()
                .ToLowerInvariant();

        private static ExperienceItemDefinition Reject(
            ExperienceItemDefinition result,
            string reason)
        {
            result.IsSupported = false;
            result.UnsupportedReason = reason;
            return result;
        }
    }
}
