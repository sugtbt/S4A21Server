using System;
using System.Collections.Generic;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class QuestCatalog
    {
        private static readonly Lazy<CatalogIndex> Index =
            new Lazy<CatalogIndex>(BuildIndex);
        private static readonly Dictionary<int, QuestFile> Cache =
            new Dictionary<int, QuestFile>();
        private static readonly object CacheLock = new object();

        internal static IReadOnlyList<int> OrderedIds => Index.Value.OrderedIds;

        internal static QuestFile Get(int questId)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(questId, out var cached))
                    return cached;
            }

            if (!Index.Value.Paths.TryGetValue(questId, out var path))
                return null;

            try
            {
                var quest = QuestFile.Parse(PvfArchiveAccessor.ReadText(path));
                lock (CacheLock)
                    Cache[questId] = quest;
                return quest;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestCatalog] quest parse failed: " +
                    $"quest={questId} path={path}: {ex.Message}");
                return null;
            }
        }

        internal static bool TryGetPath(int questId, out string path)
        {
            path = string.Empty;
            if (questId <= 0)
                return false;

            return Index.Value.Paths.TryGetValue(questId, out path)
                && !string.IsNullOrWhiteSpace(path);
        }

        private static CatalogIndex BuildIndex()
        {
            var index = new CatalogIndex();
            try
            {
                var list = LstFile.Parse(
                    PvfArchiveAccessor.ReadText("n_quest/quest.lst"));
                foreach (var entry in list.Entries)
                {
                    if (index.Paths.ContainsKey(entry.Id))
                        continue;

                    index.Paths[entry.Id] = "n_quest/" + entry.FilePath;
                    index.OrderedIds.Add(entry.Id);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestCatalog] Failed to load quest.lst: {ex.Message}");
            }

            return index;
        }

        private sealed class CatalogIndex
        {
            internal Dictionary<int, string> Paths { get; } =
                new Dictionary<int, string>();
            internal List<int> OrderedIds { get; } = new List<int>();
        }
    }
}
