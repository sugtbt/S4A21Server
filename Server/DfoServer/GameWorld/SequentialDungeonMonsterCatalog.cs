using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal static class SequentialDungeonMonsterCatalog
    {
        private const string DefinitionPath =
            "Etc/sequential_dungeon_info.etc";

        private static readonly Lazy<Dictionary<int, HashSet<int>>>
            MonstersByDungeon =
                new Lazy<Dictionary<int, HashSet<int>>>(Load);

        internal static bool Contains(int dungeonId, int monsterCode)
        {
            return dungeonId > 0
                && monsterCode > 0
                && MonstersByDungeon.Value.TryGetValue(
                    dungeonId,
                    out var monsters)
                && monsters.Contains(monsterCode);
        }

        private static Dictionary<int, HashSet<int>> Load()
        {
            var result = new Dictionary<int, HashSet<int>>();
            try
            {
                var text = PvfArchiveAccessor.ReadText(DefinitionPath);
                var root = new ScriptParser().Parse(text);
                foreach (var section in root.GetChildren(
                    "sequential dungeon"))
                {
                    var dungeonIds = ReadPositiveIntegers(
                        section.GetChild("dungeon index check"),
                        text);
                    var monsterCodes = ReadPositiveIntegers(
                        section.GetChild("monster index check"),
                        text);
                    if (dungeonIds.Count == 0 || monsterCodes.Count == 0)
                        continue;

                    foreach (var dungeonId in dungeonIds)
                    {
                        if (!result.TryGetValue(
                                dungeonId,
                                out var monsters))
                        {
                            monsters = new HashSet<int>();
                            result[dungeonId] = monsters;
                        }

                        monsters.UnionWith(monsterCodes);
                    }
                }

                FileLogger.Log(
                    $"[SequentialDungeonMonsterCatalog] loaded " +
                    $"dungeons={result.Count}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[SequentialDungeonMonsterCatalog] load failed: " +
                    $"{ex.Message}");
            }

            return result;
        }

        private static List<int> ReadPositiveIntegers(
            ScriptNode node,
            string text)
        {
            var result = new List<int>();
            if (node == null)
                return result;

            foreach (var token in ScriptValueTokenizer.Tokenize(
                node.GetFirstDataContent(text)))
            {
                if (int.TryParse(token, out var value) && value > 0)
                    result.Add(value);
            }

            return result;
        }
    }
}
