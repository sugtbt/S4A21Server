using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DfoServer.GameWorld
{
    internal sealed class DimensionGateChronicleDropDefinition
    {
        private readonly int[] _normalItems;
        private readonly int[] _setItems;
        private readonly int[] _combinedItems;

        internal DimensionGateChronicleDropDefinition(
            int job,
            int firstGrowType,
            IEnumerable<int> normalItems,
            IEnumerable<int> setItems)
        {
            Job = job;
            FirstGrowType = NormalizeGrowType(firstGrowType);
            _normalItems = NormalizeItems(normalItems);
            _setItems = NormalizeItems(setItems);
            _combinedItems = _normalItems
                .Concat(_setItems)
                .ToArray();
        }

        internal int Job { get; }
        internal int FirstGrowType { get; }
        internal IReadOnlyList<int> NormalItems => _normalItems;
        internal IReadOnlyList<int> SetItems => _setItems;
        internal IReadOnlyList<int> CombinedItems => _combinedItems;
        internal bool HasNormalItems => _normalItems.Length > 0;
        internal bool HasSetItems => _setItems.Length > 0;
        internal bool HasAnyItems => _combinedItems.Length > 0;

        internal static int NormalizeGrowType(int growType)
            => growType & 0x0F;

        private static int[] NormalizeItems(IEnumerable<int> items)
            => (items ?? Array.Empty<int>())
                .Where(itemId => itemId > 0)
                .ToArray();
    }

    internal static class DimensionGateDropDefinitionCatalog
    {
        private const string DefinitionPath = "etc/dimensiongatedroplist.etc";

        private static readonly Lazy<CatalogSnapshot> Snapshot =
            new Lazy<CatalogSnapshot>(
                LoadSafely,
                LazyThreadSafetyMode.ExecutionAndPublication);

        internal static void WarmUp()
        {
            _ = Snapshot.Value;
        }

        internal static int DefinitionCount => Snapshot.Value.Definitions.Count;

        internal static bool TryResolve(
            int characterJob,
            int growType,
            out DimensionGateChronicleDropDefinition definition)
        {
            var firstGrowType =
                DimensionGateChronicleDropDefinition.NormalizeGrowType(growType);
            return Snapshot.Value.Definitions.TryGetValue(
                (characterJob, firstGrowType),
                out definition);
        }

        internal static IReadOnlyDictionary<(int Job, int FirstGrowType),
            DimensionGateChronicleDropDefinition> ParseDefinitions(
                string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new Dictionary<(int, int),
                    DimensionGateChronicleDropDefinition>();
            }

            var root = new ScriptParser().Parse(text);
            var builders = new Dictionary<(int Job, int FirstGrowType),
                DefinitionBuilder>();
            foreach (var group in root.GetChildren("chronicle grow type"))
            {
                var header = ReadDataInts(text, group.DataItems);
                if (header.Count < 2)
                    continue;

                var job = header[0];
                var firstGrowType =
                    DimensionGateChronicleDropDefinition.NormalizeGrowType(
                        header[1]);
                if (job < 0)
                    continue;

                var key = (job, firstGrowType);
                if (!builders.TryGetValue(key, out var builder))
                {
                    builder = new DefinitionBuilder(job, firstGrowType);
                    builders[key] = builder;
                }

                builder.NormalItems.AddRange(ReadListItems(
                    text,
                    group.GetChild("normal chronicle list")));
                builder.SetItems.AddRange(ReadListItems(
                    text,
                    group.GetChild("set chronicle list")));
            }

            return builders.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Build());
        }

        private static CatalogSnapshot LoadSafely()
        {
            try
            {
                return Load();
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DimensionGateDropDefinition] INIT FAILED: {ex}");
                return CatalogSnapshot.Empty;
            }
        }

        private static CatalogSnapshot Load()
        {
            string text;
            try
            {
                text = PvfArchiveAccessor.ReadText(DefinitionPath);
            }
            catch (FileNotFoundException ex)
            {
                FileLogger.Log(
                    $"[DimensionGateDropDefinition] definition missing " +
                    $"path={DefinitionPath}: {ex.Message}");
                return CatalogSnapshot.Empty;
            }

            var definitions = ParseDefinitions(text);
            var normalItemCount = definitions.Values.Sum(
                definition => definition.NormalItems.Count);
            var setItemCount = definitions.Values.Sum(
                definition => definition.SetItems.Count);
            FileLogger.Log(
                $"[DimensionGateDropDefinition] loaded " +
                $"groups={definitions.Count} normalItems={normalItemCount} " +
                $"setItems={setItemCount}");
            return new CatalogSnapshot(definitions);
        }

        private static IReadOnlyList<int> ReadListItems(
            string fullText,
            ScriptNode node)
        {
            if (node == null || node.DataItems.Count == 0)
                return Array.Empty<int>();

            return ReadDataInts(fullText, node.DataItems);
        }

        private static List<int> ReadDataInts(
            string fullText,
            IReadOnlyList<ScriptDataItem> dataItems)
        {
            var result = new List<int>();
            if (dataItems == null || dataItems.Count == 0)
                return result;

            foreach (var item in dataItems)
            {
                var line = RemoveLineComment(item.GetContent(fullText));
                var tokens = line.Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in tokens)
                {
                    if (int.TryParse(token, out var value))
                        result.Add(value);
                }
            }

            return result;
        }

        private static string RemoveLineComment(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var hash = line.IndexOf('#');
            var slash = line.IndexOf("//", StringComparison.Ordinal);
            var cut = -1;
            if (hash >= 0)
                cut = hash;
            if (slash >= 0 && (cut < 0 || slash < cut))
                cut = slash;

            return cut >= 0 ? line.Substring(0, cut) : line;
        }

        private sealed class DefinitionBuilder
        {
            internal DefinitionBuilder(int job, int firstGrowType)
            {
                Job = job;
                FirstGrowType = firstGrowType;
            }

            internal int Job { get; }
            internal int FirstGrowType { get; }
            internal List<int> NormalItems { get; } = new List<int>();
            internal List<int> SetItems { get; } = new List<int>();

            internal DimensionGateChronicleDropDefinition Build()
                => new DimensionGateChronicleDropDefinition(
                    Job,
                    FirstGrowType,
                    NormalItems,
                    SetItems);
        }

        private sealed class CatalogSnapshot
        {
            internal static CatalogSnapshot Empty { get; } =
                new CatalogSnapshot(
                    new Dictionary<(int, int),
                        DimensionGateChronicleDropDefinition>());

            internal CatalogSnapshot(
                IReadOnlyDictionary<(int Job, int FirstGrowType),
                    DimensionGateChronicleDropDefinition> definitions)
            {
                Definitions = definitions
                    ?? new Dictionary<(int, int),
                        DimensionGateChronicleDropDefinition>();
            }

            internal IReadOnlyDictionary<(int Job, int FirstGrowType),
                DimensionGateChronicleDropDefinition> Definitions { get; }
        }
    }
}
