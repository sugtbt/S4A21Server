using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld
{
    public readonly struct MonsterCaptureItemDefinition
    {
        internal MonsterCaptureItemDefinition(
            int itemId,
            int count,
            int dropRate)
        {
            ItemId = itemId;
            Count = count;
            DropRate = dropRate;
        }

        public int ItemId { get; }
        public int Count { get; }
        public int DropRate { get; }
    }

    internal static class MonsterCaptureDefinitionCatalog
    {
        private static readonly IReadOnlyList<MonsterCaptureItemDefinition> Empty =
            Array.Empty<MonsterCaptureItemDefinition>();
        private static readonly Lazy<LstFile> MonsterList =
            new Lazy<LstFile>(() => Dungeon.LoadLstFile(
                Path.Combine("monster", "monster.lst")));
        private static readonly ConcurrentDictionary<int, MonsterDefinition>
            Definitions = new ConcurrentDictionary<int, MonsterDefinition>();

        internal static IReadOnlyList<MonsterCaptureItemDefinition> GetItems(
            int monsterCode)
        {
            if (monsterCode <= 0)
                return Empty;

            return GetDefinition(monsterCode).CaptureItems;
        }

        internal static bool IsChampionPromotionDisabled(int monsterCode)
        {
            return monsterCode > 0
                && GetDefinition(monsterCode).NoChampionPromotion;
        }

        // A MOB [item] section is an actor-owned PVF drop pool. These actors
        // are named/special encounters in normal MAPs and must not be reused
        // by the dungeon-wide random champion promotion pass.
        internal static bool HasExclusiveItemDrop(int monsterCode)
        {
            return monsterCode > 0
                && GetDefinition(monsterCode).HasExclusiveItemDrop;
        }

        private static MonsterDefinition GetDefinition(int monsterCode)
        {
            if (monsterCode <= 0)
                return MonsterDefinition.Empty;

            return Definitions.GetOrAdd(monsterCode, LoadDefinition);
        }

        private static MonsterDefinition LoadDefinition(
            int monsterCode)
        {
            try
            {
                var entry = MonsterList.Value.GetById(monsterCode);
                if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                    return MonsterDefinition.Empty;

                var monster = MonsterFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("monster", entry.FilePath)));
                var items = new List<MonsterCaptureItemDefinition>();
                foreach (var item in monster.CatchItems
                    ?? new List<MonsterCatchItemInfo>())
                {
                    if (item == null
                        || item.ItemId <= 0
                        || item.Count <= 0
                        || item.DropRate < 0
                        || item.DropRate > 100)
                    {
                        FileLogger.Log(
                            $"[MonsterCaptureDefinitionCatalog] invalid entry: " +
                            $"monster={monsterCode} item={item?.ItemId ?? 0} " +
                            $"count={item?.Count ?? 0} rate={item?.DropRate ?? 0}");
                        continue;
                    }

                    items.Add(new MonsterCaptureItemDefinition(
                        item.ItemId,
                        item.Count,
                        item.DropRate));
                }

                var captureItems = items.Count == 0
                    ? Empty
                    : (IReadOnlyList<MonsterCaptureItemDefinition>)
                        new ReadOnlyCollection<MonsterCaptureItemDefinition>(
                            items.ToArray());
                return new MonsterDefinition(
                    captureItems,
                    monster.NoChampion,
                    HasEffectiveItemDrop(monster.Item));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[MonsterCaptureDefinitionCatalog] load failed: " +
                    $"monster={monsterCode} error={ex.Message}");
                return MonsterDefinition.Empty;
            }
        }

        private sealed class MonsterDefinition
        {
            internal static MonsterDefinition Empty { get; } =
                new MonsterDefinition(
                    MonsterCaptureDefinitionCatalog.Empty,
                    noChampionPromotion: false,
                    hasExclusiveItemDrop: false);

            internal MonsterDefinition(
                IReadOnlyList<MonsterCaptureItemDefinition> captureItems,
                bool noChampionPromotion,
                bool hasExclusiveItemDrop)
            {
                CaptureItems = captureItems
                    ?? MonsterCaptureDefinitionCatalog.Empty;
                NoChampionPromotion = noChampionPromotion;
                HasExclusiveItemDrop = hasExclusiveItemDrop;
            }

            internal IReadOnlyList<MonsterCaptureItemDefinition> CaptureItems
            { get; }

            internal bool NoChampionPromotion { get; }

            internal bool HasExclusiveItemDrop { get; }
        }

        private static bool HasEffectiveItemDrop(string itemData)
        {
            if (string.IsNullOrWhiteSpace(itemData))
                return false;

            var values = new List<int>();
            foreach (Match match in Regex.Matches(itemData, @"-?\d+"))
            {
                if (int.TryParse(match.Value, out var value))
                    values.Add(value);
            }

            for (var index = 0; index + 1 < values.Count; index += 2)
            {
                if (values[index] > 0 && values[index + 1] > 0)
                    return true;
            }

            return false;
        }
    }
}
