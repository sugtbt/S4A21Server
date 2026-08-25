using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

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
                    monster.NoChampion);
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
                    noChampionPromotion: false);

            internal MonsterDefinition(
                IReadOnlyList<MonsterCaptureItemDefinition> captureItems,
                bool noChampionPromotion)
            {
                CaptureItems = captureItems
                    ?? MonsterCaptureDefinitionCatalog.Empty;
                NoChampionPromotion = noChampionPromotion;
            }

            internal IReadOnlyList<MonsterCaptureItemDefinition> CaptureItems
            { get; }

            internal bool NoChampionPromotion { get; }
        }
    }
}
