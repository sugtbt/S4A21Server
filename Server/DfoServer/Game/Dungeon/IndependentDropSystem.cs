using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    public static class IndependentDropSystem
    {
        private const int DifficultyTierCount = 5;
        private const int SoloDropCountIndex = 0;
        private const int StandardProbabilityDenominator = 1_000_000;
        private const int ExternalPoolProbabilityDenominator = 100_000_000;
        private const int MaxTraceItems = 24;

        public static List<DropInfo> GenerateDrops(
            int monsterCode,
            int difficulty,
            int dungeonLevel,
            int partyMemberCount,
            int chronicleDropJobGroup,
            DnfLcg lcg,
            ref ushort slotCounter)
        {
            var result = new List<DropInfo>();
            if (!IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    monsterCode,
                    out var entries))
            {
                return result;
            }

            var difficultyIndex = Math.Max(
                0,
                Math.Min(difficulty, DifficultyTierCount - 1));
            var partyCount = Math.Max(1, partyMemberCount);
            var matchedEntries = 0;
            var unresolvedPoolEntries = 0;
            var totalRolls = 0;
            var successfulRolls = 0;
            var emittedItemCount = 0;
            var emittedTrace = new List<string>();
            var unresolvedPoolTrace = new List<string>();

            for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                var entry = entries[entryIndex];

                if (entry.LevelMin > 0
                    && entry.LevelMax > 0
                    && (dungeonLevel < entry.LevelMin
                        || dungeonLevel > entry.LevelMax))
                {
                    continue;
                }

                if (entry.Difficulty >= 0
                    && entry.Difficulty != difficulty)
                {
                    continue;
                }

                IndependentDropWeightedPoolDefinition itemPool = null;
                if (entry.HasItemPool
                    && !entry.TryResolvePool(
                        chronicleDropJobGroup,
                        out itemPool))
                {
                    unresolvedPoolEntries++;
                    if (unresolvedPoolTrace.Count < MaxTraceItems)
                    {
                        unresolvedPoolTrace.Add(
                            string.Join(",", entry.PoolIndexes));
                    }
                    continue;
                }

                var probability = entry.GetProbability(difficultyIndex);
                var itemCount = entry.GetCount(SoloDropCountIndex);
                if (probability <= 0 || itemCount <= 0)
                    continue;

                matchedEntries++;
                totalRolls++;

                var hasDropTemplate = entry.ItemId > 0
                    || (itemPool != null && itemPool.TotalWeight > 0);
                if (!hasDropTemplate
                    || !IsProbabilityHit(
                        entry.PoolKind,
                        probability,
                        lcg.Next(GetProbabilityDenominator(entry.PoolKind))))
                {
                    continue;
                }

                successfulRolls++;
                emittedItemCount += itemCount;

                if (itemPool != null && itemPool.TotalWeight > 0)
                {
                    var roll = lcg.Next(itemPool.TotalWeight);
                    if (!itemPool.TrySelect(roll, out var selected))
                        continue;

                    AddDrop(result, selected.ItemId, itemCount, ref slotCounter);
                    if (emittedTrace.Count < MaxTraceItems)
                    {
                        emittedTrace.Add(
                            $"{selected.PoolIndex}:{selected.ItemId}x{itemCount}");
                    }
                }
                else if (entry.ItemId > 0)
                {
                    AddDrop(result, entry.ItemId, itemCount, ref slotCounter);
                    if (emittedTrace.Count < MaxTraceItems)
                        emittedTrace.Add($"direct:{entry.ItemId}x{itemCount}");
                }
            }

            if (matchedEntries > 0 || unresolvedPoolEntries > 0)
            {
                FileLogger.Log(
                    $"[IndependentDrop] monster={monsterCode} " +
                    $"difficulty={difficulty} party={partyCount} " +
                    $"jobGroup={chronicleDropJobGroup} " +
                    $"entries={matchedEntries} " +
                    $"unresolvedPools={unresolvedPoolEntries} " +
                    $"rolls={totalRolls} successes={successfulRolls} " +
                    $"itemCount={emittedItemCount} emitted={result.Count} " +
                    $"poolItems={FormatTrace(emittedTrace)} " +
                    $"missingPoolIndexes={FormatTrace(unresolvedPoolTrace)}");
            }

            return result;
        }

        internal static bool TryGetDirectItemProbability(
            int monsterCode,
            int difficulty,
            int itemId,
            out int probability)
        {
            probability = 0;
            if (itemId <= 0
                || !IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    monsterCode,
                    out var entries))
            {
                return false;
            }

            var difficultyIndex = Math.Max(
                0,
                Math.Min(difficulty, DifficultyTierCount - 1));
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry.ItemId != itemId
                    || entry.HasItemPool
                    || (entry.Difficulty >= 0
                        && entry.Difficulty != difficulty))
                {
                    continue;
                }

                probability = Math.Max(
                    probability,
                    entry.GetProbability(difficultyIndex));
            }

            return probability > 0;
        }

        internal static int GetProbabilityDenominator(
            IndependentDropPoolKind poolKind)
            => poolKind == IndependentDropPoolKind.External
                ? ExternalPoolProbabilityDenominator
                : StandardProbabilityDenominator;

        internal static bool IsProbabilityHit(
            IndependentDropPoolKind poolKind,
            int probability,
            int roll)
        {
            var denominator = GetProbabilityDenominator(poolKind);
            return probability > 0
                && roll >= 0
                && roll < denominator
                && probability > roll;
        }

        // Some dungeon mechanisms scale a configured item template instead of
        // rolling it at monster-death time. Resolve one active direct template;
        // list pools or multiple different candidates fail closed.
        internal static bool TryResolveSingleFixedDropTemplate(
            int monsterCode,
            int difficulty,
            int dungeonLevel,
            int partyMemberCount,
            out int itemId,
            out int count)
        {
            itemId = 0;
            count = 0;
            if (!IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    monsterCode,
                    out var entries))
            {
                return false;
            }

            var difficultyIndex = Math.Max(
                0,
                Math.Min(difficulty, DifficultyTierCount - 1));
            foreach (var entry in entries)
            {
                var itemCount = entry.GetCount(SoloDropCountIndex);
                if (entry.ItemId <= 0
                    || entry.HasItemPool
                    || entry.GetProbability(difficultyIndex) <= 0
                    || itemCount <= 0
                    || (entry.LevelMin > 0
                        && entry.LevelMax > 0
                        && (dungeonLevel < entry.LevelMin
                            || dungeonLevel > entry.LevelMax))
                    || (entry.Difficulty >= 0
                        && entry.Difficulty != difficulty))
                {
                    continue;
                }

                var candidateItemId = entry.ItemId;
                var candidateCount = itemCount;
                if (itemId != 0
                    && (itemId != candidateItemId
                        || count != candidateCount))
                {
                    itemId = 0;
                    count = 0;
                    return false;
                }

                itemId = candidateItemId;
                count = candidateCount;
            }

            return itemId > 0 && count > 0;
        }

        private static void AddDrop(
            List<DropInfo> drops,
            int itemId,
            int count,
            ref ushort slotCounter)
        {
            slotCounter++;
            drops.Add(DropInfo.CreateItem(slotCounter, itemId, count));
        }

        private static string FormatTrace(IReadOnlyList<string> values)
            => values == null || values.Count == 0
                ? "none"
                : string.Join(",", values);
    }
}
