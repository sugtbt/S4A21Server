using System;
using System.Collections.Concurrent;
using System.IO;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class DungeonCatalog
    {
        private static readonly object DungeonListLock = new object();
        private static LstFile _dungeonList;

        private static readonly ConcurrentDictionary<int, (DungeonFile File, string FilePath)>
            DungeonFiles = new ConcurrentDictionary<int, (DungeonFile, string)>();

        internal static LstFile LoadListFile(string relativePath)
        {
            var content = PvfArchiveAccessor.ReadText(relativePath);
            return LstFile.Parse(content);
        }

        internal static LstFile LoadDungeonList()
        {
            var cached = _dungeonList;
            if (cached != null)
                return cached;

            lock (DungeonListLock)
            {
                if (_dungeonList == null)
                {
                    _dungeonList = LoadListFile(
                        Path.Combine("dungeon", "dungeon.lst"));
                }

                return _dungeonList;
            }
        }

        internal static DungeonFile GetDungeonFile(int dungeonId)
            => GetDungeonFileWithPath(dungeonId).File;

        internal static bool IsDimensionDungeon(int dungeonId)
        {
            if (dungeonId <= 0)
                return false;

            try
            {
                return GetDungeonFile(dungeonId)?.DimensionDungeon == true;
            }
            catch
            {
                return false;
            }
        }

        internal static (DungeonFile File, string FilePath)
            GetDungeonFileWithPath(int dungeonId)
        {
            return DungeonFiles.GetOrAdd(dungeonId, id =>
            {
                var path = ResolveFilePath(
                    LoadDungeonList(),
                    id,
                    "dungeon");
                var file = DungeonFile.Parse(
                    PvfArchiveAccessor.ReadText(
                        Path.Combine("dungeon", path)));
                return (file, path);
            });
        }

        internal static string ResolveFilePath(
            LstFile listFile,
            int id,
            string description)
        {
            var entry = listFile.GetById(id);
            if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                throw new Exception($"未找到{description}编号{id}");

            return entry.FilePath.Replace('/', Path.DirectorySeparatorChar);
        }

        internal static byte GetBasicLevel(int dungeonId)
        {
            var file = GetDungeonFile(dungeonId);
            if (file.Mazes == null || file.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            return (byte)file.BasisLevel;
        }

        internal static int GetMinimumRequiredLevel(int dungeonId)
        {
            try
            {
                var file = GetDungeonFile(dungeonId);
                return file.MinimumRequiredLevel > 0
                    ? file.MinimumRequiredLevel
                    : file.BasisLevel;
            }
            catch
            {
                return 0;
            }
        }

        internal static bool TryGetSuitableLevelRange(
            int dungeonId,
            out int minLevel,
            out int maxLevel)
        {
            minLevel = 0;
            maxLevel = 0;

            try
            {
                var file = GetDungeonFile(dungeonId);
                minLevel = file.MinimumRequiredLevel;
                maxLevel = file.BasisLevel;

                if (minLevel <= 0 && maxLevel <= 0)
                    return false;
                if (minLevel <= 0)
                    minLevel = maxLevel;
                if (maxLevel <= 0)
                    maxLevel = minLevel;
                if (minLevel > maxLevel)
                {
                    var value = minLevel;
                    minLevel = maxLevel;
                    maxLevel = value;
                }

                return minLevel > 0 && maxLevel > 0;
            }
            catch
            {
                minLevel = 0;
                maxLevel = 0;
                return false;
            }
        }

        internal static int GetMaximumDifficultyCount(int dungeonId)
        {
            try
            {
                var file = GetDungeonFile(dungeonId);
                if (file.DifficultyLevel != null
                    && file.DifficultyLevel.Length > 0)
                {
                    var count = 0;
                    foreach (var value in file.DifficultyLevel)
                    {
                        if (value != 0)
                            count++;
                    }

                    return count;
                }

                if (file.DesignateDungeonDifficulty != null
                    && file.DesignateDungeonDifficulty.Length > 0)
                {
                    return 5;
                }

                return file.Difficulty >= 0 ? 5 : 0;
            }
            catch
            {
                return 0;
            }
        }

        internal static MazeInfo GetDefaultMaze(int dungeonId)
        {
            var list = LoadDungeonList();
            if (list == null)
            {
                throw new Exception(
                    "未能成功解析地下城LST文件 dungeon/dungeon.lst");
            }

            // Keep the legacy public API's diagnostic wording for an unknown
            // dungeon while still loading the definition through the cache.
            _ = ResolveFilePath(list, dungeonId, "地下城");
            var file = GetDungeonFile(dungeonId);
            if (file.Mazes == null || file.Mazes.Count == 0)
                throw new Exception("未解析到迷宫信息");

            foreach (var maze in file.Mazes)
            {
                if (maze.QuestConnection == null)
                    return maze;
            }

            return file.Mazes[0];
        }

        internal static MazeInfo GetMaze(int dungeonId, int mazeIndex)
        {
            try
            {
                var file = GetDungeonFile(dungeonId);
                if (file.Mazes == null || file.Mazes.Count == 0)
                    return null;

                return mazeIndex >= 0 && mazeIndex < file.Mazes.Count
                    ? file.Mazes[mazeIndex]
                    : file.Mazes[0];
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Dungeon] GetDungeonMaze ERROR: " +
                    $"dungeon={dungeonId} maze={mazeIndex} {ex.Message}");
                return null;
            }
        }
    }
}
