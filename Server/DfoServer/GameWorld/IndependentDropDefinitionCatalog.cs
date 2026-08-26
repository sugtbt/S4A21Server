using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DfoServer.GameWorld
{
    internal enum IndependentDropPoolKind
    {
        None = 0,
        Inline = 1,
        External = 2,
    }

    internal readonly struct IndependentDropWeightedItemDefinition
    {
        internal IndependentDropWeightedItemDefinition(
            int itemId,
            int weight,
            int cumulativeWeight,
            int poolIndex)
        {
            ItemId = itemId;
            Weight = weight;
            CumulativeWeight = cumulativeWeight;
            PoolIndex = poolIndex;
        }

        internal int ItemId { get; }
        internal int Weight { get; }
        internal int CumulativeWeight { get; }
        internal int PoolIndex { get; }
    }

    internal sealed class IndependentDropWeightedPoolDefinition
    {
        private readonly IndependentDropWeightedItemDefinition[] _items;

        internal IndependentDropWeightedPoolDefinition(
            IEnumerable<RawIndependentDropPoolItem> items)
        {
            var definitions = new List<IndependentDropWeightedItemDefinition>();
            var cumulativeWeight = 0;
            foreach (var item in items ?? Array.Empty<RawIndependentDropPoolItem>())
            {
                if (item.ItemId <= 0 || item.Weight <= 0)
                    continue;

                cumulativeWeight = checked(cumulativeWeight + item.Weight);
                definitions.Add(new IndependentDropWeightedItemDefinition(
                    item.ItemId,
                    item.Weight,
                    cumulativeWeight,
                    item.PoolIndex));
            }

            _items = definitions.ToArray();
            TotalWeight = cumulativeWeight;
        }

        internal IReadOnlyList<IndependentDropWeightedItemDefinition> Items =>
            _items;

        internal int TotalWeight { get; }

        internal bool TrySelect(
            int roll,
            out IndependentDropWeightedItemDefinition selected)
        {
            selected = default;
            if (_items.Length == 0 || TotalWeight <= 0)
                return false;

            for (var index = 0; index < _items.Length; index++)
            {
                if (roll < _items[index].CumulativeWeight)
                {
                    selected = _items[index];
                    return true;
                }
            }

            selected = _items[_items.Length - 1];
            return true;
        }
    }

    internal sealed class IndependentDropEntryDefinition
    {
        private const int AnyJobGroup = -1;
        private readonly int[] _probabilities;
        private readonly int[] _counts;
        private readonly int[] _poolIndexes;
        private readonly Dictionary<int, IndependentDropWeightedPoolDefinition>
            _poolsByJobGroup;

        internal IndependentDropEntryDefinition(
            int monsterCode,
            int itemId,
            int[] probabilities,
            int[] counts,
            int levelMin,
            int levelMax,
            int difficulty,
            IndependentDropPoolKind poolKind,
            IEnumerable<int> poolIndexes,
            Dictionary<int, IndependentDropWeightedPoolDefinition> poolsByJobGroup)
        {
            MonsterCode = monsterCode;
            ItemId = itemId;
            _probabilities = probabilities?.ToArray() ?? Array.Empty<int>();
            _counts = counts?.ToArray() ?? Array.Empty<int>();
            LevelMin = levelMin;
            LevelMax = levelMax;
            Difficulty = difficulty;
            PoolKind = poolKind;
            _poolIndexes = poolIndexes?.Distinct().ToArray() ?? Array.Empty<int>();
            _poolsByJobGroup = poolsByJobGroup
                ?? new Dictionary<int, IndependentDropWeightedPoolDefinition>();
        }

        internal int MonsterCode { get; }
        internal int ItemId { get; }
        internal int LevelMin { get; }
        internal int LevelMax { get; }
        internal int Difficulty { get; }
        internal IndependentDropPoolKind PoolKind { get; }
        internal IReadOnlyList<int> PoolIndexes => _poolIndexes;
        internal bool HasItemPool => PoolKind != IndependentDropPoolKind.None;

        internal int GetProbability(int difficultyIndex)
            => difficultyIndex >= 0 && difficultyIndex < _probabilities.Length
                ? _probabilities[difficultyIndex]
                : 0;

        internal int GetCount(int index)
            => index >= 0 && index < _counts.Length ? _counts[index] : 0;

        internal bool TryResolvePool(
            int chronicleDropJobGroup,
            out IndependentDropWeightedPoolDefinition pool)
        {
            if (_poolsByJobGroup.TryGetValue(
                    chronicleDropJobGroup,
                    out pool))
            {
                return pool != null && pool.TotalWeight > 0;
            }

            return _poolsByJobGroup.TryGetValue(AnyJobGroup, out pool)
                && pool != null
                && pool.TotalWeight > 0;
        }

        internal static int UniversalJobGroup => AnyJobGroup;
    }

    internal readonly struct RawIndependentDropPoolItem
    {
        internal RawIndependentDropPoolItem(
            int itemId,
            int weight,
            int chronicleDropJobGroup,
            int poolIndex)
        {
            ItemId = itemId;
            Weight = weight;
            ChronicleDropJobGroup = chronicleDropJobGroup;
            PoolIndex = poolIndex;
        }

        internal int ItemId { get; }
        internal int Weight { get; }
        internal int ChronicleDropJobGroup { get; }
        internal int PoolIndex { get; }
    }

    internal static class IndependentDropDefinitionCatalog
    {
        private const int DifficultyTierCount = 5;
        private const int CountColumnCount = 5;
        private const string MainDefinitionPath = "Etc/Independent_Drop.etc";
        private const string ExternalPoolListPath = "Etc/IndependentDrop.lst";
        private const string JobMappingPath =
            "Etc/IndependentDrop/0_job_mapping_table.etc";

        private static readonly Lazy<CatalogSnapshot> Snapshot =
            new Lazy<CatalogSnapshot>(
                LoadSafely,
                LazyThreadSafetyMode.ExecutionAndPublication);
        private static readonly Lazy<HashSet<int>> MonsterCodeIndex =
            new Lazy<HashSet<int>>(
                LoadMonsterCodeIndex,
                LazyThreadSafetyMode.ExecutionAndPublication);

        internal static void WarmUp()
        {
            _ = Snapshot.Value;
        }

        internal static int ExternalPoolCount =>
            Snapshot.Value.ExternalPools.Count;

        internal static int ResolveChronicleDropJobGroup(
            int characterJob,
            int growType)
        {
            var firstGrowType = growType & 0x0F;
            return Snapshot.Value.JobMappings.TryGetValue(
                    (characterJob, firstGrowType),
                    out var group)
                ? group
                : -1;
        }

        internal static bool TryGetMonsterEntries(
            int monsterCode,
            out IReadOnlyList<IndependentDropEntryDefinition> entries)
        {
            if (Snapshot.Value.MonsterEntries.TryGetValue(
                    monsterCode,
                    out var found))
            {
                entries = found;
                return true;
            }

            entries = Array.Empty<IndependentDropEntryDefinition>();
            return false;
        }

        internal static bool HasMonsterDefinition(int monsterCode)
        {
            return monsterCode > 0
                && MonsterCodeIndex.Value.Contains(monsterCode);
        }

        internal static bool TryResolveExternalPool(
            int poolIndex,
            int chronicleDropJobGroup,
            out IndependentDropWeightedPoolDefinition pool)
        {
            pool = null;
            return Snapshot.Value.ExternalPools.TryGetValue(
                    poolIndex,
                    out var definition)
                && definition.TryResolve(chronicleDropJobGroup, out pool);
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
                    $"[IndependentDropDefinition] INIT FAILED: {ex}");
                return CatalogSnapshot.Empty;
            }
        }

        private static CatalogSnapshot Load()
        {
            var jobMappings = LoadJobMappings();
            var externalPools = LoadExternalPools();
            var monsterEntries = LoadMonsterEntries(externalPools);
            var entryCount = monsterEntries.Values.Sum(entries => entries.Length);

            FileLogger.Log(
                $"[IndependentDropDefinition] loaded monsters={monsterEntries.Count} " +
                $"entries={entryCount} externalPools={externalPools.Count} " +
                $"jobMappings={jobMappings.Count}");

            return new CatalogSnapshot(
                monsterEntries,
                externalPools,
                jobMappings);
        }

        private static HashSet<int> LoadMonsterCodeIndex()
        {
            try
            {
                var entries = LoadMonsterEntries(
                    new Dictionary<int, ExternalPoolDefinition>(),
                    reportMissingExternalPools: false);
                FileLogger.Log(
                    $"[IndependentDropDefinition] monster index loaded " +
                    $"count={entries.Count}");
                return new HashSet<int>(entries.Keys);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[IndependentDropDefinition] monster index failed: {ex.Message}");
                return new HashSet<int>();
            }
        }

        private static Dictionary<(int Job, int FirstGrowType), int>
            LoadJobMappings()
        {
            var result = new Dictionary<(int, int), int>();
            string text;
            try
            {
                text = PvfArchiveAccessor.ReadText(JobMappingPath);
            }
            catch (FileNotFoundException ex)
            {
                FileLogger.Log(
                    $"[IndependentDropDefinition] optional job mapping skipped " +
                    $"path={JobMappingPath}: {ex.Message}");
                return result;
            }

            var tokens = ReadSectionTokens(
                text,
                "[chronicle drop job mapping table]",
                "[/chronicle drop job mapping table]");

            if (tokens.Length % 3 != 0)
            {
                FileLogger.Log(
                    $"[IndependentDropDefinition] malformed job mapping " +
                    $"path={JobMappingPath} tokens={tokens.Length}");
                return result;
            }

            for (var index = 0; index < tokens.Length; index += 3)
            {
                if (!TryParseInt(tokens[index], out var job)
                    || !TryParseInt(tokens[index + 1], out var firstGrowType)
                    || !TryParseInt(tokens[index + 2], out var jobGroup)
                    || job < 0
                    || firstGrowType < 0
                    || jobGroup <= 0)
                {
                    FileLogger.Log(
                        $"[IndependentDropDefinition] invalid job mapping " +
                        $"path={JobMappingPath} offset={index}");
                    continue;
                }

                result[(job, firstGrowType)] = jobGroup;
            }

            return result;
        }

        private static Dictionary<int, ExternalPoolDefinition>
            LoadExternalPools()
        {
            var result = new Dictionary<int, ExternalPoolDefinition>();
            LstFile lst;
            try
            {
                lst = LstFile.Parse(
                    PvfArchiveAccessor.ReadText(ExternalPoolListPath));
            }
            catch (FileNotFoundException ex)
            {
                FileLogger.Log(
                    $"[IndependentDropDefinition] optional external pool list skipped " +
                    $"path={ExternalPoolListPath}: {ex.Message}");
                return result;
            }

            foreach (var entry in lst.Entries)
            {
                var sourcePath = "Etc/" + entry.FilePath;
                try
                {
                    var text = PvfArchiveAccessor.ReadText(sourcePath);
                    var tokens = ReadSectionTokens(
                        text,
                        "[list]",
                        "[/list]");
                    if (tokens.Length == 0)
                        continue;

                    if (tokens.Length % 3 != 0)
                    {
                        FileLogger.Log(
                            $"[IndependentDropDefinition] malformed external pool " +
                            $"pool={entry.Id} path={sourcePath} " +
                            $"tokens={tokens.Length}");
                        continue;
                    }

                    var items = new List<RawIndependentDropPoolItem>();
                    var valid = true;
                    for (var index = 0; index < tokens.Length; index += 3)
                    {
                        if (!TryParseInt(tokens[index], out var itemId)
                            || !TryParseInt(tokens[index + 1], out var weight)
                            || !TryParseInt(
                                tokens[index + 2],
                                out var jobGroup)
                            || itemId <= 0
                            || weight <= 0
                            || jobGroup <= 0)
                        {
                            valid = false;
                            FileLogger.Log(
                                $"[IndependentDropDefinition] invalid external pool row " +
                                $"pool={entry.Id} path={sourcePath} offset={index}");
                            break;
                        }

                        items.Add(new RawIndependentDropPoolItem(
                            itemId,
                            weight,
                            jobGroup,
                            entry.Id));
                    }

                    if (valid && items.Count > 0)
                    {
                        result[entry.Id] = new ExternalPoolDefinition(
                            entry.Id,
                            sourcePath,
                            items);
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[IndependentDropDefinition] external pool skipped " +
                        $"pool={entry.Id} path={sourcePath}: {ex.Message}");
                }
            }

            return result;
        }

        private static Dictionary<int, IndependentDropEntryDefinition[]>
            LoadMonsterEntries(
                IReadOnlyDictionary<int, ExternalPoolDefinition> externalPools,
                bool reportMissingExternalPools = true)
        {
            var text = PvfArchiveAccessor.ReadText(MainDefinitionPath);
            var sectionStart = text.IndexOf(
                "[independent drop]",
                StringComparison.OrdinalIgnoreCase);
            if (sectionStart < 0)
                return new Dictionary<int, IndependentDropEntryDefinition[]>();

            sectionStart += "[independent drop]".Length;
            var queue = new Queue<string>(Tokenize(text.Substring(sectionStart)));
            var builders = new Dictionary<int, List<IndependentDropEntryDefinition>>();

            while (queue.Count >= 17)
            {
                if (queue.Peek().StartsWith("[", StringComparison.Ordinal))
                    break;

                var type = ReadInt(queue, "type");
                var monsterCode = ReadInt(queue, "monsterCode");
                var itemId = ReadInt(queue, "itemId");

                var probabilities = new int[DifficultyTierCount];
                for (var index = 0; index < probabilities.Length; index++)
                    probabilities[index] = ReadInt(queue, "probability");

                var counts = new int[CountColumnCount];
                for (var index = 0; index < counts.Length; index++)
                    counts[index] = ReadInt(queue, "count");

                var levelMin = ReadInt(queue, "levelMin");
                var levelMax = ReadInt(queue, "levelMax");
                var difficulty = ReadInt(queue, "difficulty");
                var listFlag = ReadInt(queue, "listFlag");

                var poolKind = IndependentDropPoolKind.None;
                var poolIndexes = Array.Empty<int>();
                var poolsByJobGroup =
                    new Dictionary<int, IndependentDropWeightedPoolDefinition>();

                if (listFlag == 1)
                {
                    poolKind = IndependentDropPoolKind.Inline;
                    var inlineItems = ReadInlinePool(queue);
                    var pool = new IndependentDropWeightedPoolDefinition(
                        inlineItems);
                    if (pool.TotalWeight > 0)
                    {
                        poolsByJobGroup[
                            IndependentDropEntryDefinition.UniversalJobGroup] = pool;
                    }
                }
                else if (listFlag == 2)
                {
                    poolKind = IndependentDropPoolKind.External;
                    poolIndexes = ReadExternalPoolIndexes(queue).ToArray();
                    poolsByJobGroup = BuildMergedExternalPools(
                        poolIndexes,
                        externalPools,
                        reportMissingExternalPools);
                }

                if (type != 0)
                    continue;

                var definition = new IndependentDropEntryDefinition(
                    monsterCode,
                    itemId,
                    probabilities,
                    counts,
                    levelMin,
                    levelMax,
                    difficulty,
                    poolKind,
                    poolIndexes,
                    poolsByJobGroup);

                if (!builders.TryGetValue(monsterCode, out var entries))
                {
                    entries = new List<IndependentDropEntryDefinition>();
                    builders[monsterCode] = entries;
                }
                entries.Add(definition);
            }

            return builders.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray());
        }

        private static Dictionary<int, IndependentDropWeightedPoolDefinition>
            BuildMergedExternalPools(
                IEnumerable<int> poolIndexes,
                IReadOnlyDictionary<int, ExternalPoolDefinition> externalPools,
                bool reportMissingPools = true)
        {
            var grouped = new Dictionary<int, List<RawIndependentDropPoolItem>>();
            foreach (var poolIndex in poolIndexes ?? Array.Empty<int>())
            {
                if (!externalPools.TryGetValue(poolIndex, out var externalPool))
                {
                    if (reportMissingPools)
                    {
                        FileLogger.Log(
                            $"[IndependentDropDefinition] missing external pool " +
                            $"index={poolIndex}");
                    }
                    continue;
                }

                foreach (var jobGroup in externalPool.JobGroups)
                {
                    if (!externalPool.TryGetRawItems(jobGroup, out var items))
                        continue;
                    if (!grouped.TryGetValue(jobGroup, out var merged))
                    {
                        merged = new List<RawIndependentDropPoolItem>();
                        grouped[jobGroup] = merged;
                    }
                    merged.AddRange(items);
                }
            }

            return grouped.ToDictionary(
                pair => pair.Key,
                pair => new IndependentDropWeightedPoolDefinition(pair.Value));
        }

        private static List<RawIndependentDropPoolItem> ReadInlinePool(
            Queue<string> queue)
        {
            ExpectListStart(queue);
            var result = new List<RawIndependentDropPoolItem>();
            while (queue.Count > 0 && queue.Peek() != "[/list]")
            {
                var itemId = ReadInt(queue, "inlineItemId");
                if (queue.Count == 0 || queue.Peek() == "[/list]")
                    throw new FormatException("Inline independent-drop pool has an odd token count.");
                var weight = ReadInt(queue, "inlineWeight");
                result.Add(new RawIndependentDropPoolItem(
                    itemId,
                    weight,
                    IndependentDropEntryDefinition.UniversalJobGroup,
                    poolIndex: 0));
            }
            ExpectListEnd(queue);
            return result;
        }

        private static List<int> ReadExternalPoolIndexes(Queue<string> queue)
        {
            ExpectListStart(queue);
            var result = new List<int>();
            while (queue.Count > 0 && queue.Peek() != "[/list]")
                result.Add(ReadInt(queue, "externalPoolIndex"));
            ExpectListEnd(queue);
            return result;
        }

        private static void ExpectListStart(Queue<string> queue)
        {
            if (queue.Count == 0 || queue.Dequeue() != "[list]")
                throw new FormatException("Independent-drop list start tag is missing.");
        }

        private static void ExpectListEnd(Queue<string> queue)
        {
            if (queue.Count == 0 || queue.Dequeue() != "[/list]")
                throw new FormatException("Independent-drop list end tag is missing.");
        }

        private static int ReadInt(Queue<string> queue, string fieldName)
        {
            if (queue.Count == 0
                || !TryParseInt(queue.Dequeue(), out var value))
            {
                throw new FormatException(
                    $"Independent-drop field '{fieldName}' is not an integer.");
            }
            return value;
        }

        private static string[] ReadSectionTokens(
            string text,
            string startTag,
            string endTag)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<string>();

            var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return Array.Empty<string>();
            start += startTag.Length;

            var end = text.IndexOf(
                endTag,
                start,
                StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = text.Length;

            return Tokenize(text.Substring(start, end - start));
        }

        private static string[] Tokenize(string text)
            => (text ?? string.Empty).Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

        private static bool TryParseInt(string token, out int value)
            => int.TryParse(token, out value);

        private sealed class ExternalPoolDefinition
        {
            private readonly Dictionary<int, RawIndependentDropPoolItem[]>
                _rawItemsByJobGroup;
            private readonly Dictionary<int, IndependentDropWeightedPoolDefinition>
                _poolsByJobGroup;

            internal ExternalPoolDefinition(
                int poolIndex,
                string sourcePath,
                IEnumerable<RawIndependentDropPoolItem> items)
            {
                PoolIndex = poolIndex;
                SourcePath = sourcePath ?? string.Empty;
                _rawItemsByJobGroup = (items
                        ?? Array.Empty<RawIndependentDropPoolItem>())
                    .GroupBy(item => item.ChronicleDropJobGroup)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToArray());
                _poolsByJobGroup = _rawItemsByJobGroup.ToDictionary(
                    pair => pair.Key,
                    pair => new IndependentDropWeightedPoolDefinition(pair.Value));
            }

            internal int PoolIndex { get; }
            internal string SourcePath { get; }
            internal IEnumerable<int> JobGroups => _rawItemsByJobGroup.Keys;

            internal bool TryGetRawItems(
                int jobGroup,
                out RawIndependentDropPoolItem[] items)
                => _rawItemsByJobGroup.TryGetValue(jobGroup, out items);

            internal bool TryResolve(
                int jobGroup,
                out IndependentDropWeightedPoolDefinition pool)
                => _poolsByJobGroup.TryGetValue(jobGroup, out pool)
                    && pool != null
                    && pool.TotalWeight > 0;
        }

        private sealed class CatalogSnapshot
        {
            internal static CatalogSnapshot Empty { get; } =
                new CatalogSnapshot(
                    new Dictionary<int, IndependentDropEntryDefinition[]>(),
                    new Dictionary<int, ExternalPoolDefinition>(),
                    new Dictionary<(int, int), int>());

            internal CatalogSnapshot(
                Dictionary<int, IndependentDropEntryDefinition[]> monsterEntries,
                Dictionary<int, ExternalPoolDefinition> externalPools,
                Dictionary<(int Job, int FirstGrowType), int> jobMappings)
            {
                MonsterEntries = monsterEntries;
                ExternalPools = externalPools;
                JobMappings = jobMappings;
            }

            internal IReadOnlyDictionary<int, IndependentDropEntryDefinition[]>
                MonsterEntries { get; }
            internal IReadOnlyDictionary<int, ExternalPoolDefinition>
                ExternalPools { get; }
            internal IReadOnlyDictionary<(int Job, int FirstGrowType), int>
                JobMappings { get; }
        }
    }
}
