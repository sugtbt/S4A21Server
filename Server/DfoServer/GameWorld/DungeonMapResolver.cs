using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal enum MapFileType : byte
    {
        Normal,
        Start,
        Boss,
        Named,
        End,
        Hidden,
        Quest,
        Default,
    }

    internal struct MapFileEntry
    {
        public int MapId;
        public MapFileType FileType;
        public bool HasCoordinate;
        public int CoordX;
        public int CoordY;
        public int DirectoryPriority;
    }

    internal sealed class DungeonMapDirectoryIndex
    {
        public Dictionary<long, List<MapFileEntry>> ByCoordinate { get; } = new Dictionary<long, List<MapFileEntry>>();
        public Dictionary<MapFileType, List<MapFileEntry>> ByType { get; } = new Dictionary<MapFileType, List<MapFileEntry>>();
        public List<MapFileEntry> Entries { get; } = new List<MapFileEntry>();

        public static long CoordKey(int x, int y) => ((long)x << 32) | (uint)y;

        public void Add(MapFileEntry entry)
        {
            Entries.Add(entry);
            if (entry.HasCoordinate)
            {
                var key = CoordKey(entry.CoordX, entry.CoordY);
                if (!ByCoordinate.TryGetValue(key, out var list))
                {
                    list = new List<MapFileEntry>();
                    ByCoordinate[key] = list;
                }
                list.Add(entry);
            }

            if (!ByType.TryGetValue(entry.FileType, out var typeList))
            {
                typeList = new List<MapFileEntry>();
                ByType[entry.FileType] = typeList;
            }
            typeList.Add(entry);
        }
    }

    internal static class DungeonMapResolver
    {
        private static readonly Regex MapCoordinateFileNameRegex =
            new Regex(@"\((?<x>-?\d+)[,.](?<y>-?\d+)\)", RegexOptions.Compiled);

        private static readonly ConcurrentDictionary<int, bool> BossActorMapCache =
            new ConcurrentDictionary<int, bool>();

        private static readonly ConcurrentDictionary<int, HashSet<int>>
            MapMonsterCodeCache =
                new ConcurrentDictionary<int, HashSet<int>>();

        private static readonly ConcurrentDictionary<int, int>
            MapDungeonOwnerCache =
                new ConcurrentDictionary<int, int>();

        private static readonly ConcurrentDictionary<int, bool>
            MapDungeonStartAreaCache =
                new ConcurrentDictionary<int, bool>();

        private static readonly ConcurrentDictionary<int, string>
            MapGreedSignatureCache =
                new ConcurrentDictionary<int, string>();

        private static readonly ConcurrentDictionary<int, int>
            MapEntranceMaskCache =
                new ConcurrentDictionary<int, int>();

        private static readonly ConcurrentDictionary<long, bool>
            MapAiCharacterCache =
                new ConcurrentDictionary<long, bool>();

        private static readonly ConcurrentDictionary<string, DungeonMapDirectoryIndex> DirIndexCache =
            new ConcurrentDictionary<string, DungeonMapDirectoryIndex>(StringComparer.OrdinalIgnoreCase);

        internal static int ResolveMapId(int dungeonId, int x, int y, MazeInfo maze, int mazeIndex, int[] bossPos)
        {
            var maplst = DungeonMapCatalog.LoadMapList();
            var loaded = Dungeon.LoadDungeonFileWithPath(dungeonId);

            var towerFloor = loaded.File.TowerOfDespair > 0
                             && Dungeon.TryGetTowerOfDespairFloor(dungeonId, out var floor)
                ? floor
                : 0;

            var mapDirCandidates = Dungeon.BuildMapDirCandidates(maplst, maze, loaded.FilePath);

            var effectiveBoss = bossPos ?? (maze.BossMap != null && maze.BossMap.Length >= 2
                ? new[] { maze.BossMap[0], maze.BossMap[1] } : null);

            bool isStartRoom = ContainsCoordinate(maze.StartMap, x, y);
            bool isBossRoom = effectiveBoss != null && effectiveBoss[0] == x && effectiveBoss[1] == y;
            bool isQuestConnected = maze.QuestConnection != null && maze.QuestConnection.Length >= 2;

            var index = GetOrBuildIndex(dungeonId, maplst, mapDirCandidates);

            // A boss specification belongs to the selected maze. Resolve the
            // maze-local declaration first so another directory entry at the same
            // coordinate cannot be selected from the fallback index.
            if (isBossRoom)
            {
                var explicitBossMapId = ResolveFromMapSpecification(
                    maplst,
                    maze,
                    x,
                    y,
                    isBossRoom: true,
                    allowMapTypeForBossRoom: false);
                if (explicitBossMapId > 0)
                    return explicitBossMapId;

                // An omitted Boss cell may share its physical coordinate with
                // assets owned by another quest maze. Anchor the implicit Boss
                // to this maze's declared MAP resource group before using the
                // directory-wide coordinate fallback.
                if (!HasMapSpecificationAt(maze, x, y))
                {
                    var expectedGreed = TryGetMazeCellGreed(
                        maze,
                        x,
                        y,
                        out var bossGreed)
                            ? bossGreed
                            : string.Empty;
                    var anchoredBoss = PickImplicitBossByExplicitMapAffinity(
                        index,
                        maze,
                        maplst,
                        dungeonId,
                        expectedGreed);
                    if (anchoredBoss > 0)
                    {
                        FileLogger.Log(
                            $"[DungeonMapResolver] BOSS_AFFINITY: " +
                            $"dungeon={dungeonId} maze={mazeIndex} " +
                            $"room=({x},{y}) greed={expectedGreed} " +
                            $"map={anchoredBoss}");
                        return anchoredBoss;
                    }
                }
            }

            // An explicit start MAP whose entrance mask matches the selected maze
            // is authoritative. Directory-wide "start" variants can belong to a
            // different quest route at the same physical coordinate.
            if (isStartRoom)
            {
                var explicitStartMapId = ResolveFromMapSpecification(
                    maplst,
                    maze,
                    x,
                    y,
                    isBossRoom: false);
                if (explicitStartMapId > 0)
                {
                    if (isQuestConnected)
                        return explicitStartMapId;

                    if (IsMapCompatibleWithMazeCellEntrance(
                            maze,
                            x,
                            y,
                            explicitStartMapId))
                    {
                        return explicitStartMapId;
                    }

                    var key = DungeonMapDirectoryIndex.CoordKey(x, y);
                    if (index.ByCoordinate.TryGetValue(
                            key,
                            out var startEntries))
                    {
                        var typedStartCandidates = GetOwnedByTypeCandidates(
                            startEntries,
                            MapFileType.Start,
                            maplst,
                            dungeonId);
                        if (typedStartCandidates.Contains(explicitStartMapId))
                            return explicitStartMapId;
                        if (typedStartCandidates.Count == 1)
                            return typedStartCandidates[0];
                    }

                    return explicitStartMapId;
                }
            }

            if (isQuestConnected
                && isStartRoom
                && !HasMapSpecificationAt(maze, x, y)
                && TryResolveQuestCompanionStartMap(
                    index,
                    maplst,
                    dungeonId,
                    maze,
                    out var companionStartMapId,
                    out var companionQuestId,
                    out var companionApcId))
            {
                if (companionStartMapId > 0)
                {
                    FileLogger.Log(
                        $"[DungeonMapResolver] QUEST_COMPANION_START: " +
                        $"dungeon={dungeonId} maze={mazeIndex} " +
                        $"quest={companionQuestId} apc={companionApcId} " +
                        $"room=({x},{y}) map={companionStartMapId}");
                    return companionStartMapId;
                }

                FileLogger.Log(
                    $"[DungeonMapResolver] QUEST_COMPANION_START_UNRESOLVED: " +
                    $"dungeon={dungeonId} maze={mazeIndex} " +
                    $"quest={companionQuestId} apc={companionApcId} " +
                    $"room=({x},{y}) fallback=maze_affinity");
            }

            // For non-quest start/boss rooms without an explicit maze-local match,
            // prefer a typed directory file at the exact coordinate. Quest-connected
            // mazes use their affinity-aware fallback below when the start is omitted.
            if (!isQuestConnected && (isStartRoom || isBossRoom))
            {
                var key = DungeonMapDirectoryIndex.CoordKey(x, y);
                if (index.ByCoordinate.TryGetValue(key, out var typed))
                {
                    var preferType = isBossRoom ? MapFileType.Boss : MapFileType.Start;
                    var hit = PickOwnedByType(
                        typed,
                        preferType,
                        maplst,
                        dungeonId);
                    if (hit > 0) return hit;
                }
            }

            // Step 1: MapSpecification
            int mapId = ResolveFromMapSpecification(maplst, maze, x, y, isBossRoom);
            if (mapId > 0)
                return mapId;

            // Multi-room Tower of Despair floors use the base map for room zero and
            // same-floor "_x" map variants for later rooms. Keep the base map as a
            // fallback only after explicit MapSpecification candidates were checked.
            if (towerFloor > 0)
            {
                mapId = ResolveTowerOfDespairMapId(maplst, towerFloor, x);
                if (mapId > 0)
                    return mapId;
            }

            // Tournament MAP resources share one directory. Their explicit
            // [dungeon] owner is stronger than directory order and must be
            // applied before the ordinary fallback can select a sibling MAP.
            if (loaded.File.TournamentDungeon)
            {
                mapId = ResolveFromDeclaredDungeonOwner(
                    index,
                    maplst,
                    dungeonId,
                    maze,
                    mazeIndex,
                    x,
                    y,
                    isStartRoom,
                    isBossRoom,
                    isQuestConnected);
                if (mapId > 0)
                    return mapId;
            }

            // Step 2+3: Directory index (coordinate + type pool)
            mapId = ResolveFromDirectoryIndex(
                index,
                maplst,
                dungeonId,
                maze,
                mazeIndex,
                x,
                y,
                isStartRoom,
                isBossRoom,
                isQuestConnected);
            if (mapId > 0)
                return mapId;

            FileLogger.Log($"[DungeonMapResolver] UNRESOLVED: dungeon={dungeonId} maze={mazeIndex} room=({x},{y}) start={isStartRoom} boss={isBossRoom} quest={isQuestConnected} dirEntries={CountIndexEntries(index)}");
            return -1;
        }

        private static bool IsMapCompatibleWithMazeCellEntrance(
            MazeInfo maze,
            int x,
            int y,
            int mapId)
        {
            return TryGetMazeCellGreed(maze, x, y, out var cellGreed)
                && TryDecodeGreedSymbol(cellGreed, out var expectedMask)
                && TryGetMapEntranceMask(mapId, out var entranceMask)
                && entranceMask == expectedMask;
        }

        private static int ResolveFromDeclaredDungeonOwner(
            DungeonMapDirectoryIndex index,
            LstFile mapList,
            int dungeonId,
            MazeInfo maze,
            int mazeIndex,
            int x,
            int y,
            bool isStartRoom,
            bool isBossRoom,
            bool isQuestConnected)
        {
            if (index == null || mapList == null || index.Entries.Count == 0)
                return -1;

            var owned = new DungeonMapDirectoryIndex();
            foreach (var entry in index.Entries)
            {
                if (entry.MapId > 0
                    && GetMapDungeonOwner(mapList, entry.MapId) == dungeonId)
                {
                    owned.Add(entry);
                }
            }
            if (owned.Entries.Count == 0)
                return -1;

            return ResolveFromDirectoryIndex(
                owned,
                mapList,
                dungeonId,
                maze,
                mazeIndex,
                x,
                y,
                isStartRoom,
                isBossRoom,
                isQuestConnected);
        }

        private static bool ContainsCoordinate(int[] positions, int x, int y)
        {
            if (positions == null)
                return false;

            for (var i = 0; i + 1 < positions.Length; i += 2)
            {
                if (positions[i] == x && positions[i + 1] == y)
                    return true;
            }

            return false;
        }

        private static bool HasMapSpecificationAt(
            MazeInfo maze,
            int x,
            int y)
        {
            if (maze?.MapSpecifications == null)
                return false;

            foreach (var specification in maze.MapSpecifications)
            {
                if (specification.X == x && specification.Y == y)
                    return true;
            }

            return false;
        }

        internal static bool HasExplicitBossCandidatePool(
            MazeInfo maze,
            int x,
            int y)
        {
            if (maze?.MapSpecifications == null)
                return false;

            foreach (var item in maze.MapSpecifications)
            {
                if (item.X != x
                    || item.Y != y
                    || (!string.Equals(
                            item.Type,
                            "boss",
                            StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(
                            item.Type,
                            "map",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (item.MapCandidates != null
                    && item.MapCandidates.Length > 1)
                {
                    return true;
                }
            }

            return false;
        }

        internal static List<int> GetExplicitBossCandidateMapIds(
            MazeInfo maze,
            int x,
            int y)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            if (maze?.MapSpecifications == null)
                return result;

            foreach (var item in maze.MapSpecifications)
            {
                if (item.X != x
                    || item.Y != y
                    || (!string.Equals(
                            item.Type,
                            "boss",
                            StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(
                            item.Type,
                            "map",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var candidates = item.MapCandidates != null
                    && item.MapCandidates.Length > 0
                        ? item.MapCandidates
                        : new[] { item.Index };
                foreach (var mapId in candidates)
                {
                    if (mapId > 0 && seen.Add(mapId))
                        result.Add(mapId);
                }
            }

            return result;
        }

        internal static int ResolveExplicitBossCandidateMapId(
            MazeInfo maze,
            int x,
            int y)
        {
            if (!HasExplicitBossCandidatePool(maze, x, y))
                return -1;

            var maplst = DungeonMapCatalog.LoadMapList();
            return ResolveFromMapSpecification(
                maplst,
                maze,
                x,
                y,
                isBossRoom: true);
        }

        internal static bool MapContainsMonsterCode(
            int mapId,
            int monsterCode)
        {
            if (mapId <= 0 || monsterCode <= 0)
                return false;

            try
            {
                return MapMonsterCodeCache
                    .GetOrAdd(mapId, LoadMapMonsterCodes)
                    .Contains(monsterCode);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonMapResolver] quest target inspection failed: " +
                    $"map={mapId} monster={monsterCode} error={ex.Message}");
                return false;
            }
        }

        private static int ResolveTowerOfDespairMapId(LstFile maplst, int floor, int x)
        {
            if (maplst == null || floor <= 0)
                return -1;

            var expectedRoomMapSuffix = $"despair{floor:000}_{x}.map";
            foreach (var entry in maplst.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                    continue;

                var normalizedPath = entry.FilePath.Replace('\\', '/');
                if ((normalizedPath.StartsWith("towerofdespair_down/", StringComparison.OrdinalIgnoreCase)
                     || normalizedPath.StartsWith("towerofdespair_up/", StringComparison.OrdinalIgnoreCase))
                    && normalizedPath.EndsWith(expectedRoomMapSuffix, StringComparison.OrdinalIgnoreCase))
                    return entry.Id;
            }

            var expectedMapSuffix = $"despair{floor:000}.map";
            foreach (var entry in maplst.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                    continue;

                var normalizedPath = entry.FilePath.Replace('\\', '/');
                if ((normalizedPath.StartsWith("towerofdespair_down/", StringComparison.OrdinalIgnoreCase)
                     || normalizedPath.StartsWith("towerofdespair_up/", StringComparison.OrdinalIgnoreCase))
                    && normalizedPath.EndsWith(expectedMapSuffix, StringComparison.OrdinalIgnoreCase))
                    return entry.Id;
            }

            return -1;
        }

        // --- Step 1: MapSpecification ---

        private static int ResolveFromMapSpecification(
            LstFile maplst,
            MazeInfo maze,
            int x,
            int y,
            bool isBossRoom,
            bool allowMapTypeForBossRoom = true)
        {
            if (maze.MapSpecifications == null || maze.MapSpecifications.Count == 0)
                return -1;

            if (isBossRoom)
            {
                var bossActorMapIds = new List<int>();
                var firstCandidates = new List<int>();
                var allowRandomFallback = false;

                foreach (var item in maze.MapSpecifications)
                {
                    if (item.X != x || item.Y != y) continue;
                    var specType = item.Type ?? string.Empty;
                    if (!string.Equals(specType, "boss", StringComparison.OrdinalIgnoreCase)
                        && !(allowMapTypeForBossRoom
                            && string.Equals(specType, "map", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var candidates = item.MapCandidates != null && item.MapCandidates.Length > 0
                        ? item.MapCandidates
                        : new[] { item.Index };
                    allowRandomFallback |= item.MapCandidates != null
                        && item.MapCandidates.Length > 1;
                    AddDistinctPositiveMapIds(firstCandidates, candidates);

                    foreach (var cid in candidates)
                    {
                        if (cid > 0 && HasBossActor(maplst, cid))
                            AddDistinctPositiveMapId(bossActorMapIds, cid);
                    }
                }

                if (bossActorMapIds.Count > 0)
                    return SelectMapSpecificationCandidate(
                        maze,
                        x,
                        y,
                        bossActorMapIds,
                        allowRandomFallback);
                if (firstCandidates.Count > 0)
                    return SelectMapSpecificationCandidate(
                        maze,
                        x,
                        y,
                        firstCandidates,
                        allowRandomFallback);

                // The boss-only lookup at the start of ResolveMapId must not fall
                // through to an ordinary "map" specification. Returning the normal
                // map here prevents the directory index from selecting a boss
                // variant at the same coordinate (for example Ice Crystal Forest).
                if (!allowMapTypeForBossRoom)
                    return -1;
            }

            var mapCandidates = new List<int>();
            var allowRandomFallbackForMaps = false;
            foreach (var item in maze.MapSpecifications)
            {
                if (item.X != x || item.Y != y) continue;
                if (string.Equals(item.Type, "boss", StringComparison.OrdinalIgnoreCase)) continue;
                allowRandomFallbackForMaps |= item.MapCandidates != null
                    && item.MapCandidates.Length > 1;
                AddDistinctPositiveMapIds(
                    mapCandidates,
                    item.MapCandidates != null && item.MapCandidates.Length > 0
                        ? item.MapCandidates
                        : new[] { item.Index });
            }

            return SelectMapSpecificationCandidate(
                maze,
                x,
                y,
                mapCandidates,
                allowRandomFallbackForMaps);
        }

        private static int SelectMapSpecificationCandidate(
            MazeInfo maze,
            int x,
            int y,
            IReadOnlyList<int> candidates,
            bool allowRandomFallback)
        {
            if (candidates == null || candidates.Count == 0)
                return -1;

            var expectedMask = TryGetMazeCellGreed(maze, x, y, out var cellGreed)
                && TryDecodeGreedSymbol(cellGreed, out var decodedMask)
                    ? (int?)decodedMask
                    : null;

            if (expectedMask.HasValue)
            {
                var compatible = new List<int>();
                foreach (var mapId in candidates)
                {
                    if (mapId <= 0
                        || !TryGetMapEntranceMask(mapId, out var entranceMask)
                        || entranceMask != expectedMask.Value)
                    {
                        continue;
                    }

                    AddDistinctPositiveMapId(compatible, mapId);
                }

                if (compatible.Count > 0)
                {
                    return compatible.Count > 1
                        ? compatible[Infrastructure.ServerRandom.Next(compatible.Count)]
                        : compatible[0];
                }

                FileLogger.Log(
                    $"[DungeonMapResolver] MAP_SPEC_GREED_MISMATCH: " +
                    $"room=({x},{y}) expected={cellGreed} " +
                    $"candidates={string.Join(",", candidates)}");
            }

            return allowRandomFallback && candidates.Count > 1
                ? candidates[Infrastructure.ServerRandom.Next(candidates.Count)]
                : candidates[0];
        }

        private static void AddDistinctPositiveMapIds(
            List<int> destination,
            IEnumerable<int> mapIds)
        {
            if (destination == null || mapIds == null)
                return;

            foreach (var mapId in mapIds)
                AddDistinctPositiveMapId(destination, mapId);
        }

        private static void AddDistinctPositiveMapId(
            List<int> destination,
            int mapId)
        {
            if (destination == null || mapId <= 0)
                return;

            if (!destination.Contains(mapId))
                destination.Add(mapId);
        }

        // --- Step 2+3: Directory Index ---

        private static int ResolveFromDirectoryIndex(
            DungeonMapDirectoryIndex index,
            LstFile maplst,
            int dungeonId,
            MazeInfo maze,
            int mazeIndex,
            int x,
            int y,
            bool isStartRoom, bool isBossRoom, bool isQuestConnected)
        {
            var expectedGreed = TryGetMazeCellGreed(
                maze,
                x,
                y,
                out var mazeCellGreed)
                    ? mazeCellGreed
                    : string.Empty;

            // A maze cell's two-character greed value describes the MAP gate
            // layout. Quest mazes frequently omit intermediate/start MAP ids and
            // expect the server to choose a resource with the same gate layout.
            if (!string.IsNullOrEmpty(expectedGreed))
            {
                var preferredType = isBossRoom
                    ? MapFileType.Boss
                    : isStartRoom
                        ? MapFileType.Start
                        : isQuestConnected
                            ? MapFileType.Quest
                            : MapFileType.Normal;
                var greedMatch = PickOwnedByGreed(
                    index,
                    maplst,
                    dungeonId,
                    preferredType,
                    expectedGreed,
                    x,
                    y,
                    out var exactGreedMatch);

                if (greedMatch <= 0
                    && !isStartRoom
                    && !isBossRoom
                    && preferredType != MapFileType.Normal)
                {
                    greedMatch = PickOwnedByGreed(
                        index,
                        maplst,
                        dungeonId,
                        MapFileType.Normal,
                        expectedGreed,
                        x,
                        y,
                        out exactGreedMatch);
                    preferredType = MapFileType.Normal;
                }

                if (greedMatch > 0)
                {
                    FileLogger.Log(
                        $"[DungeonMapResolver] GREED_MATCH: " +
                        $"dungeon={dungeonId} maze={mazeIndex} " +
                        $"room=({x},{y}) greed={expectedGreed} " +
                        $"type={preferredType} map={greedMatch} " +
                        $"exact={exactGreedMatch}");
                    return greedMatch;
                }
            }

            // Quest mazes often omit their start room from [map specification].
            // Their MAP filenames may retain physical coordinates that differ from
            // the logical maze grid, so pair the start with this maze's explicit
            // MAP resource group before considering dungeon-wide coordinate hits.
            if (isQuestConnected && isStartRoom)
            {
                var exactStart = -1;
                var exactKey = DungeonMapDirectoryIndex.CoordKey(x, y);
                if (index.ByCoordinate.TryGetValue(
                        exactKey,
                        out var exactEntries))
                {
                    exactStart = PickOwnedByType(
                        exactEntries,
                        MapFileType.Start,
                        maplst,
                        dungeonId);
                }

                var anchoredStart = PickQuestStartByExplicitMapAffinity(
                    index,
                    maze,
                    maplst,
                    dungeonId,
                    exactStart,
                    out var hasAnchoredStartCandidates);
                if (anchoredStart > 0)
                {
                    FileLogger.Log(
                        $"[DungeonMapResolver] " +
                        $"{(anchoredStart == exactStart
                            ? "QUEST_START_EXACT"
                            : "QUEST_START_ANCHORED")}: " +
                        $"dungeon={dungeonId} maze={mazeIndex} " +
                        $"room=({x},{y}) map={anchoredStart}");
                    return anchoredStart;
                }
                if (hasAnchoredStartCandidates)
                    return -1;
                if (exactStart > 0)
                    return exactStart;
            }

            // Step 2: Coordinate lookup
            var key = DungeonMapDirectoryIndex.CoordKey(x, y);
            if (index.ByCoordinate.TryGetValue(key, out var coordEntries) && coordEntries.Count > 0)
            {
                // Quest preference
                if (isQuestConnected)
                {
                    var questHit = PickByType(coordEntries, MapFileType.Quest);
                    if (questHit > 0) return questHit;
                }

                // Type preference
                if (isStartRoom)
                {
                    var startHit = PickByType(coordEntries, MapFileType.Start);
                    if (startHit > 0) return startHit;
                }
                if (isBossRoom)
                {
                    var bossHit = PickByType(coordEntries, MapFileType.Boss);
                    if (bossHit > 0) return bossHit;
                }

                // Normal or any
                var normalHit = PickByType(coordEntries, MapFileType.Normal);
                if (normalHit > 0) return normalHit;

                // Any coordinate match
                return coordEntries[coordEntries.Count > 1
                    ? Infrastructure.ServerRandom.Next(coordEntries.Count) : 0].MapId;
            }

            // Step 3: Type-pool lookup (files without coordinates)
            if (isQuestConnected)
            {
                var questPool = PickFromPool(index, MapFileType.Quest);
                if (questPool > 0) return questPool;
            }
            if (isStartRoom)
            {
                var startPool = PickFromPool(index, MapFileType.Start);
                if (startPool > 0) return startPool;

                // Some DGN mazes use logical grid coordinates while their MAP file
                // names retain the expanded physical grid coordinates. When the
                // start room is omitted from [map specification], prefer the nearest
                // typed start resource instead of falling through to an unrelated
                // uncoordinated normal/quest map.
                var nearestStart = PickNearestCoordinateByType(
                    index,
                    MapFileType.Start,
                    x,
                    y,
                    out var hasCoordinateStartCandidates);
                if (nearestStart > 0)
                    return nearestStart;
                if (hasCoordinateStartCandidates)
                    return -1;

                // Some numeric MAP resources are entrance templates even though
                // the maze omits its start coordinate from [map specification].
                // Use the MAP's own [dungeon start area] marker and its affinity
                // to this maze's explicit resources before the generic normal pool.
                var anchoredStart = PickDungeonStartAreaByExplicitMapAffinity(
                    index,
                    maze,
                    maplst,
                    dungeonId,
                    expectedGreed,
                    out var hasDungeonStartAreaCandidates);
                if (anchoredStart > 0)
                {
                    FileLogger.Log(
                        $"[DungeonMapResolver] DUNGEON_START_AREA_ANCHORED: " +
                        $"dungeon={dungeonId} maze={mazeIndex} " +
                        $"room=({x},{y}) map={anchoredStart}");
                    return anchoredStart;
                }
                if (hasDungeonStartAreaCandidates)
                {
                    FileLogger.Log(
                        $"[DungeonMapResolver] DUNGEON_START_AREA_AMBIGUOUS: " +
                        $"dungeon={dungeonId} maze={mazeIndex} " +
                        $"room=({x},{y})");
                    return -1;
                }
            }
            if (isBossRoom)
            {
                var bossPool = PickFromPool(index, MapFileType.Boss);
                if (bossPool > 0) return bossPool;
            }

            var normalPool = PickFromPool(index, MapFileType.Normal);
            if (normalPool > 0) return normalPool;

            return -1;
        }

        internal static bool TryGetMazeCellGreed(
            MazeInfo maze,
            int x,
            int y,
            out string greed)
        {
            greed = string.Empty;
            if (maze == null
                || maze.Width <= 0
                || maze.Height <= 0
                || x < 0
                || y < 0
                || x >= maze.Width
                || y >= maze.Height
                || string.IsNullOrWhiteSpace(maze.Greed))
            {
                return false;
            }

            var values = new List<char>();
            foreach (var ch in maze.Greed)
            {
                if (!char.IsWhiteSpace(ch) && ch != '`' && ch != ',')
                    values.Add(ch);
            }

            var cellCount = maze.Width * maze.Height;
            var charsPerCell = values.Count >= cellCount * 2 ? 2 : 1;
            var offset = ((y * maze.Width) + x) * charsPerCell;
            if (offset < 0 || offset + charsPerCell > values.Count)
                return false;

            greed = new string(values.GetRange(offset, charsPerCell).ToArray());
            return greed.Length > 0;
        }

        internal static bool TryDecodeGreedSymbol(
            string symbol,
            out int mask)
        {
            mask = 0;
            if (string.IsNullOrWhiteSpace(symbol))
                return false;

            char value = '\0';
            var count = 0;
            foreach (var raw in symbol)
            {
                if (char.IsWhiteSpace(raw) || raw == '`' || raw == ',')
                    continue;

                var current = char.ToUpperInvariant(raw);
                if (current < 'A' || current > 'P')
                    return false;
                if (count > 0 && current != value)
                    return false;

                value = current;
                count++;
            }

            if (count < 1 || count > 2)
                return false;

            mask = value - 'A';
            return true;
        }

        internal static bool TryGetMapEntranceMask(
            int mapId,
            out int entranceMask)
        {
            entranceMask = 0;
            if (mapId <= 0)
                return false;

            var cached = MapEntranceMaskCache.GetOrAdd(
                mapId,
                id => LoadMapEntranceMask(id));
            if (cached < 0)
                return false;

            entranceMask = cached;
            return true;
        }

        private static int LoadMapEntranceMask(int mapId)
        {
            try
            {
                var greed = DungeonMapCatalog.GetMapFile(mapId).Greed;
                if (string.IsNullOrWhiteSpace(greed))
                    return -1;

                var values = new List<char>();
                foreach (var raw in greed)
                {
                    if (char.IsWhiteSpace(raw) || raw == '`' || raw == ',')
                        continue;
                    values.Add(char.ToUpperInvariant(raw));
                }

                // ImportMapScript stores each MAP greed row as pairs of
                // two-character A..P symbols. CMap::CheckEntrance evaluates the
                // second symbol of every pair as a four-bit invasion mask.
                if (values.Count == 0 || values.Count % 4 != 0)
                    return -1;

                var result = 0;
                for (var offset = 0; offset < values.Count; offset += 4)
                {
                    var source = new string(new[]
                    {
                        values[offset],
                        values[offset + 1],
                    });
                    var entrance = new string(new[]
                    {
                        values[offset + 2],
                        values[offset + 3],
                    });
                    if (!TryDecodeGreedSymbol(source, out _)
                        || !TryDecodeGreedSymbol(entrance, out var mask))
                    {
                        return -1;
                    }

                    result |= mask;
                }

                return result;
            }
            catch
            {
                return -1;
            }
        }

        private static bool TryResolveQuestCompanionStartMap(
            DungeonMapDirectoryIndex index,
            LstFile maplst,
            int dungeonId,
            MazeInfo maze,
            out int mapId,
            out int questId,
            out int companionApcId)
        {
            mapId = -1;
            questId = 0;
            companionApcId = 0;
            var connection = maze?.QuestConnection;
            if (index == null
                || maplst == null
                || connection == null
                || connection.Length < 2
                || connection[0] != 0
                || connection[1] <= 0)
            {
                return false;
            }

            questId = connection[1];
            if (!QuestTargetIndex.TryGetClearMapDefinition(
                    questId,
                    out var definition)
                || !definition.HasCompanion)
            {
                return false;
            }

            companionApcId = definition.CompanionApcId;
            if (GetMapDungeonOwner(maplst, definition.TargetId) != dungeonId)
                return true;

            var candidates = new List<int>();
            var bestDirectoryPriority = int.MaxValue;
            foreach (var entry in index.Entries)
            {
                if (entry.MapId <= 0
                    || GetMapDungeonOwner(maplst, entry.MapId) != dungeonId
                    || !MapContainsAiCharacter(
                        entry.MapId,
                        companionApcId))
                {
                    continue;
                }

                if (entry.DirectoryPriority < bestDirectoryPriority)
                {
                    bestDirectoryPriority = entry.DirectoryPriority;
                    candidates.Clear();
                }
                if (entry.DirectoryPriority == bestDirectoryPriority
                    && !candidates.Contains(entry.MapId))
                {
                    candidates.Add(entry.MapId);
                }
            }

            if (candidates.Count == 1)
            {
                mapId = candidates[0];
                return true;
            }

            var startAreaCandidates = new List<int>();
            foreach (var candidate in candidates)
            {
                if (HasDungeonStartArea(maplst, candidate))
                    startAreaCandidates.Add(candidate);
            }
            if (startAreaCandidates.Count == 1)
                mapId = startAreaCandidates[0];
            return true;
        }

        private static bool MapContainsAiCharacter(int mapId, int apcCode)
        {
            if (mapId <= 0 || apcCode <= 0)
                return false;

            var key = ((long)mapId << 32) | (uint)apcCode;
            return MapAiCharacterCache.GetOrAdd(
                key,
                _ =>
                {
                    try
                    {
                        var map = DungeonMapCatalog.GetMapFile(mapId);
                        foreach (var apc in map.AICharacters)
                        {
                            if (apc.Code == apcCode)
                                return true;
                        }
                    }
                    catch
                    {
                    }

                    return false;
                });
        }

        private static int PickOwnedByGreed(
            DungeonMapDirectoryIndex index,
            LstFile maplst,
            int dungeonId,
            MapFileType type,
            string expectedGreed,
            int x,
            int y,
            out bool exactCoordinate)
        {
            exactCoordinate = false;
            if (index == null
                || maplst == null
                || string.IsNullOrEmpty(expectedGreed)
                || !index.ByType.TryGetValue(type, out var pool)
                || pool.Count == 0)
            {
                return -1;
            }

            var candidates = new List<MapFileEntry>();
            var bestDirectoryPriority = int.MaxValue;
            foreach (var entry in pool)
            {
                if (entry.MapId <= 0
                    || GetMapDungeonOwner(maplst, entry.MapId) != dungeonId
                    || !string.Equals(
                        GetMapGreedSignature(maplst, entry.MapId),
                        expectedGreed,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.DirectoryPriority < bestDirectoryPriority)
                {
                    bestDirectoryPriority = entry.DirectoryPriority;
                    candidates.Clear();
                }
                if (entry.DirectoryPriority == bestDirectoryPriority)
                    candidates.Add(entry);
            }

            if (candidates.Count == 0)
                return -1;

            var exact = new List<int>();
            var any = new List<int>();
            foreach (var entry in candidates)
            {
                if (!any.Contains(entry.MapId))
                    any.Add(entry.MapId);
                if (entry.HasCoordinate
                    && entry.CoordX == x
                    && entry.CoordY == y
                    && !exact.Contains(entry.MapId))
                {
                    exact.Add(entry.MapId);
                }
            }

            var selected = exact.Count > 0 ? exact : any;
            exactCoordinate = exact.Count > 0;
            return selected.Count > 1
                ? selected[Infrastructure.ServerRandom.Next(selected.Count)]
                : selected[0];
        }

        private static string GetMapGreedSignature(
            LstFile maplst,
            int mapId)
        {
            if (maplst == null || mapId <= 0)
                return string.Empty;

            return MapGreedSignatureCache.GetOrAdd(
                mapId,
                id =>
                {
                    try
                    {
                        var mapFile = DungeonMapCatalog.GetMapFile(id);
                        if (string.IsNullOrWhiteSpace(mapFile.Greed))
                            return string.Empty;

                        var values = new List<char>();
                        foreach (var ch in mapFile.Greed)
                        {
                            if (!char.IsWhiteSpace(ch)
                                && ch != '`'
                                && ch != ',')
                            {
                                values.Add(ch);
                            }
                        }

                        if (values.Count == 0)
                            return string.Empty;
                        return new string(values.GetRange(
                            0,
                            Math.Min(2, values.Count)).ToArray());
                    }
                    catch
                    {
                        return string.Empty;
                    }
                });
        }

        private static int PickQuestStartByExplicitMapAffinity(
            DungeonMapDirectoryIndex index,
            MazeInfo maze,
            LstFile maplst,
            int dungeonId,
            int preferredMapId,
            out bool hasCandidates)
        {
            hasCandidates = false;
            if (index == null
                || maze?.MapSpecifications == null
                || maze.MapSpecifications.Count == 0
                || !index.ByType.TryGetValue(
                    MapFileType.Start,
                    out var startPool)
                || startPool.Count == 0)
            {
                return -1;
            }

            var anchors = new HashSet<int>();
            foreach (var specification in maze.MapSpecifications)
            {
                AddPositiveMapId(anchors, specification.Index);
                if (specification.MapCandidates != null)
                {
                    foreach (var mapId in specification.MapCandidates)
                        AddPositiveMapId(anchors, mapId);
                }
                if (specification.LayeredMapIds != null)
                {
                    foreach (var mapId in specification.LayeredMapIds)
                        AddPositiveMapId(anchors, mapId);
                }
            }
            if (anchors.Count == 0)
                return -1;

            var candidateIds = new HashSet<int>();
            var bestDirectoryPriority = int.MaxValue;
            foreach (var entry in startPool)
            {
                if (entry.MapId <= 0
                    || GetMapDungeonOwner(maplst, entry.MapId) != dungeonId)
                {
                    continue;
                }

                if (entry.DirectoryPriority < bestDirectoryPriority)
                {
                    bestDirectoryPriority = entry.DirectoryPriority;
                    candidateIds.Clear();
                }
                if (entry.DirectoryPriority == bestDirectoryPriority)
                    candidateIds.Add(entry.MapId);
            }

            hasCandidates = candidateIds.Count > 0;
            if (!hasCandidates)
                return -1;

            var bestMapId = -1;
            var bestDistance = long.MaxValue;
            var preferredDistance = long.MaxValue;
            var tied = false;
            foreach (var candidateId in candidateIds)
            {
                var candidateDistance = long.MaxValue;
                foreach (var anchorId in anchors)
                {
                    var distance = Math.Abs((long)candidateId - anchorId);
                    if (distance < candidateDistance)
                        candidateDistance = distance;
                }
                if (candidateId == preferredMapId)
                    preferredDistance = candidateDistance;

                if (candidateDistance < bestDistance)
                {
                    bestDistance = candidateDistance;
                    bestMapId = candidateId;
                    tied = false;
                }
                else if (candidateDistance == bestDistance
                         && candidateId != bestMapId)
                {
                    tied = true;
                }
            }

            if (tied)
                return preferredMapId > 0
                    && preferredDistance == bestDistance
                        ? preferredMapId
                        : -1;

            return bestMapId;
        }

        private static int PickDungeonStartAreaByExplicitMapAffinity(
            DungeonMapDirectoryIndex index,
            MazeInfo maze,
            LstFile maplst,
            int dungeonId,
            string expectedGreed,
            out bool hasCandidates)
        {
            hasCandidates = false;
            if (index == null
                || maze?.MapSpecifications == null
                || maze.MapSpecifications.Count == 0
                || maplst == null)
            {
                return -1;
            }

            var anchors = new HashSet<int>();
            foreach (var specification in maze.MapSpecifications)
            {
                AddPositiveMapId(anchors, specification.Index);
                if (specification.MapCandidates != null)
                {
                    foreach (var mapId in specification.MapCandidates)
                        AddPositiveMapId(anchors, mapId);
                }
                if (specification.LayeredMapIds != null)
                {
                    foreach (var mapId in specification.LayeredMapIds)
                        AddPositiveMapId(anchors, mapId);
                }
            }
            if (anchors.Count == 0)
                return -1;

            var candidateIds = new HashSet<int>();
            var bestDirectoryPriority = int.MaxValue;
            foreach (var pool in index.ByType.Values)
            {
                foreach (var entry in pool)
                {
                    if (entry.MapId <= 0
                        || anchors.Contains(entry.MapId)
                        || GetMapDungeonOwner(maplst, entry.MapId) != dungeonId
                        || !HasDungeonStartArea(maplst, entry.MapId)
                        || (!string.IsNullOrEmpty(expectedGreed)
                            && !string.Equals(
                                GetMapGreedSignature(maplst, entry.MapId),
                                expectedGreed,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (entry.DirectoryPriority < bestDirectoryPriority)
                    {
                        bestDirectoryPriority = entry.DirectoryPriority;
                        candidateIds.Clear();
                    }
                    if (entry.DirectoryPriority == bestDirectoryPriority)
                        candidateIds.Add(entry.MapId);
                }
            }

            hasCandidates = candidateIds.Count > 0;
            if (!hasCandidates)
                return -1;

            var bestMapId = -1;
            var bestDistance = long.MaxValue;
            var tied = false;
            foreach (var candidateId in candidateIds)
            {
                var candidateDistance = long.MaxValue;
                foreach (var anchorId in anchors)
                {
                    var distance = Math.Abs((long)candidateId - anchorId);
                    if (distance < candidateDistance)
                        candidateDistance = distance;
                }

                if (candidateDistance < bestDistance)
                {
                    bestDistance = candidateDistance;
                    bestMapId = candidateId;
                    tied = false;
                }
                else if (candidateDistance == bestDistance
                         && candidateId != bestMapId)
                {
                    tied = true;
                }
            }

            return tied ? -1 : bestMapId;
        }

        private static int PickImplicitBossByExplicitMapAffinity(
            DungeonMapDirectoryIndex index,
            MazeInfo maze,
            LstFile maplst,
            int dungeonId,
            string expectedGreed)
        {
            if (index == null
                || maze?.MapSpecifications == null
                || maze.MapSpecifications.Count == 0
                || maplst == null
                || !index.ByType.TryGetValue(
                    MapFileType.Boss,
                    out var bossPool)
                || bossPool.Count == 0)
            {
                return -1;
            }

            var anchors = new HashSet<int>();
            foreach (var specification in maze.MapSpecifications)
            {
                AddPositiveMapId(anchors, specification.Index);
                if (specification.MapCandidates != null)
                {
                    foreach (var mapId in specification.MapCandidates)
                        AddPositiveMapId(anchors, mapId);
                }
                if (specification.LayeredMapIds != null)
                {
                    foreach (var mapId in specification.LayeredMapIds)
                        AddPositiveMapId(anchors, mapId);
                }
            }
            if (anchors.Count == 0)
                return -1;

            var candidateIds = new HashSet<int>();
            var bestDirectoryPriority = int.MaxValue;
            foreach (var entry in bossPool)
            {
                if (entry.MapId <= 0
                    || GetMapDungeonOwner(maplst, entry.MapId) != dungeonId
                    || (!string.IsNullOrEmpty(expectedGreed)
                        && !string.Equals(
                            GetMapGreedSignature(maplst, entry.MapId),
                            expectedGreed,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (entry.DirectoryPriority < bestDirectoryPriority)
                {
                    bestDirectoryPriority = entry.DirectoryPriority;
                    candidateIds.Clear();
                }
                if (entry.DirectoryPriority == bestDirectoryPriority)
                    candidateIds.Add(entry.MapId);
            }
            if (candidateIds.Count == 0)
                return -1;

            var bestMapId = -1;
            var bestDistance = long.MaxValue;
            var tied = false;
            foreach (var candidateId in candidateIds)
            {
                var candidateDistance = long.MaxValue;
                foreach (var anchorId in anchors)
                {
                    var distance = Math.Abs((long)candidateId - anchorId);
                    if (distance < candidateDistance)
                        candidateDistance = distance;
                }

                if (candidateDistance < bestDistance)
                {
                    bestDistance = candidateDistance;
                    bestMapId = candidateId;
                    tied = false;
                }
                else if (candidateDistance == bestDistance
                         && candidateId != bestMapId)
                {
                    tied = true;
                }
            }

            return tied ? -1 : bestMapId;
        }

        private static void AddPositiveMapId(HashSet<int> mapIds, int mapId)
        {
            if (mapId > 0)
                mapIds.Add(mapId);
        }

        private static int PickNearestCoordinateByType(
            DungeonMapDirectoryIndex index,
            MapFileType type,
            int x,
            int y,
            out bool hasCandidates)
        {
            hasCandidates = false;
            if (!index.ByType.TryGetValue(type, out var pool)
                || pool.Count == 0)
            {
                return -1;
            }

            var bestMapId = -1;
            var bestDirectoryPriority = int.MaxValue;
            var bestDistance = int.MaxValue;
            var tied = false;
            foreach (var entry in pool)
            {
                if (!entry.HasCoordinate || entry.MapId <= 0)
                    continue;

                hasCandidates = true;
                var distance = Math.Abs(entry.CoordX - x)
                    + Math.Abs(entry.CoordY - y);
                if (entry.DirectoryPriority < bestDirectoryPriority
                    || (entry.DirectoryPriority == bestDirectoryPriority
                        && distance < bestDistance))
                {
                    bestDirectoryPriority = entry.DirectoryPriority;
                    bestDistance = distance;
                    bestMapId = entry.MapId;
                    tied = false;
                }
                else if (entry.DirectoryPriority == bestDirectoryPriority
                         && distance == bestDistance
                         && entry.MapId != bestMapId)
                {
                    tied = true;
                }
            }

            // Ambiguous typed starts are safer left unresolved than randomly mapped
            // to another maze's entrance.
            return tied ? -1 : bestMapId;
        }

        private static int PickByType(List<MapFileEntry> entries, MapFileType type)
        {
            var candidates = new List<int>();
            var bestDirectoryPriority = int.MaxValue;
            foreach (var e in entries)
            {
                if (e.FileType != type)
                    continue;

                if (e.DirectoryPriority < bestDirectoryPriority)
                {
                    bestDirectoryPriority = e.DirectoryPriority;
                    candidates.Clear();
                }
                if (e.DirectoryPriority == bestDirectoryPriority)
                    candidates.Add(e.MapId);
            }
            if (candidates.Count == 0) return -1;
            return candidates.Count > 1
                ? candidates[Infrastructure.ServerRandom.Next(candidates.Count)]
                : candidates[0];
        }

        private static int PickOwnedByType(
            List<MapFileEntry> entries,
            MapFileType type,
            LstFile maplst,
            int dungeonId)
        {
            var candidates = GetOwnedByTypeCandidates(
                entries,
                type,
                maplst,
                dungeonId);
            if (candidates.Count == 0)
                return -1;
            return candidates.Count > 1
                ? candidates[Infrastructure.ServerRandom.Next(candidates.Count)]
                : candidates[0];
        }

        private static List<int> GetOwnedByTypeCandidates(
            List<MapFileEntry> entries,
            MapFileType type,
            LstFile maplst,
            int dungeonId)
        {
            var candidates = new List<int>();
            var bestDirectoryPriority = int.MaxValue;
            foreach (var entry in entries)
            {
                if (entry.FileType == type
                    && GetMapDungeonOwner(maplst, entry.MapId) == dungeonId)
                {
                    if (entry.DirectoryPriority < bestDirectoryPriority)
                    {
                        bestDirectoryPriority = entry.DirectoryPriority;
                        candidates.Clear();
                    }
                    if (entry.DirectoryPriority == bestDirectoryPriority)
                    {
                        if (!candidates.Contains(entry.MapId))
                            candidates.Add(entry.MapId);
                    }
                }
            }

            return candidates;
        }

        private static int PickFromPool(DungeonMapDirectoryIndex index, MapFileType type)
        {
            if (!index.ByType.TryGetValue(type, out var pool) || pool.Count == 0)
                return -1;
            // Only pick from entries WITHOUT coordinates (pure type-pool files)
            var noCoord = new List<int>();
            var bestDirectoryPriority = int.MaxValue;
            foreach (var e in pool)
            {
                if (e.HasCoordinate)
                    continue;

                if (e.DirectoryPriority < bestDirectoryPriority)
                {
                    bestDirectoryPriority = e.DirectoryPriority;
                    noCoord.Clear();
                }
                if (e.DirectoryPriority == bestDirectoryPriority)
                    noCoord.Add(e.MapId);
            }
            if (noCoord.Count == 0) return -1;
            return noCoord.Count > 1
                ? noCoord[Infrastructure.ServerRandom.Next(noCoord.Count)]
                : noCoord[0];
        }

        // --- Index building ---

        private static DungeonMapDirectoryIndex GetOrBuildIndex(int dungeonId, LstFile maplst, List<string> mapDirCandidates)
        {
            var normalizedDirectories = new List<string>();
            if (mapDirCandidates != null)
            {
                foreach (var directory in mapDirCandidates)
                {
                    normalizedDirectories.Add((directory ?? string.Empty)
                        .Replace('\\', '/')
                        .TrimEnd('/')
                        .ToLowerInvariant());
                }
            }
            var cacheKey = dungeonId + ":" + string.Join("|", normalizedDirectories);
            return DirIndexCache.GetOrAdd(
                cacheKey,
                _ => BuildIndex(maplst, mapDirCandidates));
        }

        internal static DungeonMapDirectoryIndex BuildIndex(LstFile maplst, IReadOnlyList<string> mapDirCandidates)
        {
            var index = new DungeonMapDirectoryIndex();
            if (maplst == null) return index;

            foreach (var entry in maplst.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.FilePath))
                    continue;
                var directoryPriority = GetMapDirectoryPriority(
                    entry.FilePath,
                    mapDirCandidates);
                if (directoryPriority < 0)
                    continue;

                var fileName = Path.GetFileName(entry.FilePath);
                var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;

                var fileType = ClassifyFileType(stem);
                TryParseMapFileCoordinate(fileName, out var hasCoord, out var cx, out var cy);

                index.Add(new MapFileEntry
                {
                    MapId = entry.Id,
                    FileType = fileType,
                    HasCoordinate = hasCoord,
                    CoordX = cx,
                    CoordY = cy,
                    DirectoryPriority = directoryPriority,
                });
            }

            return index;
        }

        internal static MapFileType ClassifyFileType(string stem)
        {
            if (string.IsNullOrEmpty(stem))
                return MapFileType.Normal;

            // Strip coordinate pattern like "(0,0)" to isolate prefix+suffix
            var typeStr = MapCoordinateFileNameRegex.Replace(stem, "").Trim();
            if (typeStr != stem)
            {
                // Had coordinate; check suffix after stripping digits
                var lower = typeStr.ToLowerInvariant();
                // Strip leading digits to get pure type suffix: "20start" → "start", "77001b" → "b"
                int suffixStart = 0;
                while (suffixStart < lower.Length && char.IsDigit(lower[suffixStart])) suffixStart++;
                var suffix = suffixStart < lower.Length ? lower.Substring(suffixStart) : "";
                var terminalToken = GetTerminalMapTypeToken(suffix);
                // Also check prefix (before coordinate): "s407(4,0)" → prefix "s407" → starts with 's'
                int prefixEnd = 0;
                while (prefixEnd < lower.Length && (char.IsDigit(lower[prefixEnd]) || char.IsLetter(lower[prefixEnd]))) prefixEnd++;
                var prefix = lower.Substring(0, Math.Min(prefixEnd, suffixStart));

                // Composite names such as "35104_quest_s" retain both their
                // affinity and their structural room role. The terminal role
                // token must win before the broader quest marker.
                if (suffix.Contains("start") || terminalToken == "s" || prefix.StartsWith("s")) return MapFileType.Start;
                if (suffix.Contains("boss") || terminalToken == "b" || prefix.StartsWith("b")) return MapFileType.Boss;
                if (suffix.Contains("normal") || terminalToken == "n" || prefix.StartsWith("n")) return MapFileType.Normal;
                if (suffix.Contains("quest") || suffix.StartsWith("q") || prefix.StartsWith("q")) return MapFileType.Quest;
                if (prefix.StartsWith("h")) return MapFileType.Hidden;
                if (prefix.StartsWith("e")) return MapFileType.End;
                if (prefix.StartsWith("d")) return MapFileType.Default;
                if (prefix.StartsWith("bn")) return MapFileType.Named;
                return MapFileType.Normal;
            }

            // No coordinate in filename — classify by prefix
            var stemLower = stem.ToLowerInvariant();

            if (stemLower.StartsWith("q_") || stemLower.StartsWith("quest"))
                return MapFileType.Quest;
            if (stemLower.Length > 1 && stemLower[0] == 'q' && char.IsDigit(stemLower[1]))
                return MapFileType.Quest;

            if (stemLower.StartsWith("bn") && stemLower.Length > 2 && char.IsDigit(stemLower[2]))
                return MapFileType.Named;

            if (stemLower.Contains("boss"))
                return MapFileType.Boss;
            if (stemLower.StartsWith("b") && stemLower.Length > 1 && char.IsDigit(stemLower[1]))
                return MapFileType.Boss;
            // Lowercase trailing 'b' after digit or ')' for coordinate-encoded boss files like "77001(2,4)b"
            if (stemLower.Length >= 2 && stemLower[stemLower.Length - 1] == 'b')
            {
                var prev = stemLower[stemLower.Length - 2];
                if (char.IsDigit(prev) || prev == ')') return MapFileType.Boss;
            }

            if (stemLower.Contains("start"))
                return MapFileType.Start;
            if (stemLower.StartsWith("s") && stemLower.Length > 1 && char.IsDigit(stemLower[1]))
                return MapFileType.Start;
            // Trailing 'S' after digit
            if (stemLower.Length >= 2 && stem[stem.Length - 1] == 'S')
            {
                var prev = stemLower[stemLower.Length - 2];
                if (char.IsDigit(prev) || prev == ')') return MapFileType.Start;
            }

            if (stemLower.StartsWith("e") && stemLower.Length > 1 && char.IsDigit(stemLower[1]))
                return MapFileType.End;

            if (stemLower.StartsWith("h") && stemLower.Length > 1 && char.IsDigit(stemLower[1]))
                return MapFileType.Hidden;

            if (stemLower.StartsWith("d") && stemLower.Length > 1 && char.IsDigit(stemLower[1]))
                return MapFileType.Default;

            if (stemLower.StartsWith("n") && stemLower.Length > 1 && char.IsDigit(stemLower[1]))
                return MapFileType.Normal;
            if (stemLower.Contains("normal"))
                return MapFileType.Normal;
            // Trailing 'N' after digit
            if (stemLower.Length >= 2 && stem[stem.Length - 1] == 'N')
            {
                var prev = stemLower[stemLower.Length - 2];
                if (char.IsDigit(prev) || prev == ')') return MapFileType.Normal;
            }

            // Pure numeric stem
            bool allDigit = true;
            for (int i = 0; i < stemLower.Length; i++)
                if (!char.IsDigit(stemLower[i])) { allDigit = false; break; }
            if (allDigit && stemLower.Length > 0) return MapFileType.Normal;

            // Unrecognized — treat as Normal
            return MapFileType.Normal;
        }

        private static string GetTerminalMapTypeToken(string suffix)
        {
            if (string.IsNullOrEmpty(suffix))
                return string.Empty;

            var end = suffix.Length - 1;
            while (end >= 0 && !char.IsLetterOrDigit(suffix[end]))
                end--;
            if (end < 0)
                return string.Empty;

            var start = end;
            while (start > 0 && char.IsLetterOrDigit(suffix[start - 1]))
                start--;
            return suffix.Substring(start, end - start + 1);
        }

        internal static void TryParseMapFileCoordinate(string fileName, out bool found, out int x, out int y)
        {
            found = false;
            x = 0;
            y = 0;
            if (string.IsNullOrEmpty(fileName)) return;
            var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            var match = MapCoordinateFileNameRegex.Match(stem);
            if (match.Success && int.TryParse(match.Groups["x"].Value, out x) && int.TryParse(match.Groups["y"].Value, out y))
                found = true;
        }

        // kept for old selftest compatibility
        internal static bool TryParseMapFileCoordinate(string fileName, out int x, out int y)
        {
            TryParseMapFileCoordinate(fileName, out var found, out x, out y);
            return found;
        }

        // --- Boss actor verification (for MapSpecification step) ---

        private static bool HasBossActor(LstFile maplst, int mapId)
        {
            if (maplst == null || mapId <= 0) return false;
            if (BossActorMapCache.TryGetValue(mapId, out var cached))
                return cached;

            var found = false;
            try
            {
                var mapFile = DungeonMapCatalog.GetMapFile(mapId);
                foreach (var monster in mapFile.Monsters)
                {
                    if (monster.MonsterId.GetValueOrDefault() > 0 && monster.Type == MonsterType.Boss)
                    { found = true; break; }
                }
                if (!found)
                {
                    foreach (var apc in mapFile.AICharacters)
                    {
                        if (apc.Code > 0 && apc.AIType == ApcAIType.Boss)
                        { found = true; break; }
                    }
                }
            }
            catch { }
            BossActorMapCache[mapId] = found;
            return found;
        }

        private static HashSet<int> LoadMapMonsterCodes(int mapId)
        {
            var result = new HashSet<int>();
            var mapFile = DungeonMapCatalog.GetMapFile(mapId);

            AddMonsterCodes(result, mapFile.Monsters);
            AddMonsterCodes(result, mapFile.MonsterConditionMonsters);
            AddMonsterCodes(result, mapFile.ConditionalSummonMonsters);

            foreach (var apc in mapFile.AICharacters)
            {
                if (apc.Code > 0)
                    result.Add(apc.Code);
            }

            foreach (var obj in mapFile.SpecialPassiveObjects)
            {
                if (obj?.Spawns == null)
                    continue;

                foreach (var spawn in obj.Spawns)
                {
                    if (spawn.Code > 0)
                        result.Add(spawn.Code);
                }
            }

            return result;
        }

        private static int GetMapDungeonOwner(LstFile maplst, int mapId)
        {
            if (maplst == null || mapId <= 0)
                return -1;

            return MapDungeonOwnerCache.GetOrAdd(
                mapId,
                id =>
                {
                    try
                    {
                        return DungeonMapCatalog
                            .GetMapFile(id)
                            .DungeonId;
                    }
                    catch
                    {
                        return -1;
                    }
                });
        }

        private static bool HasDungeonStartArea(LstFile maplst, int mapId)
        {
            if (maplst == null || mapId <= 0)
                return false;

            return MapDungeonStartAreaCache.GetOrAdd(
                mapId,
                id =>
                {
                    try
                    {
                        var startArea = DungeonMapCatalog
                            .GetMapFile(id)
                            .DungeonStartArea;
                        return startArea != null && startArea.Length >= 4;
                    }
                    catch
                    {
                        return false;
                    }
                });
        }

        private static void AddMonsterCodes(
            HashSet<int> result,
            IReadOnlyList<MonsterInfo> monsters)
        {
            if (result == null || monsters == null)
                return;

            foreach (var monster in monsters)
            {
                var code = monster?.MonsterId.GetValueOrDefault() ?? 0;
                if (code > 0)
                    result.Add(code);
            }
        }

        // --- Helpers ---

        private static int GetMapDirectoryPriority(
            string filePath,
            IReadOnlyList<string> mapDirCandidates)
        {
            if (string.IsNullOrEmpty(filePath)) return -1;
            if (mapDirCandidates == null || mapDirCandidates.Count == 0) return 0;

            var normalizedPath = filePath.Replace('\\', '/');
            for (var i = 0; i < mapDirCandidates.Count; i++)
            {
                var dir = mapDirCandidates[i];
                if (string.IsNullOrEmpty(dir)) continue;
                dir = dir.Replace('\\', '/').TrimEnd('/');
                if (normalizedPath.Equals(dir, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        internal static bool IsQuestVariantFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            return stem.StartsWith("q_", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("quest_", StringComparison.OrdinalIgnoreCase)
                || (stem.Length > 1
                    && char.ToLowerInvariant(stem[0]) == 'q'
                    && char.IsDigit(stem[1]));
        }

        internal static bool IsBossVariantFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            var stem = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
            if (stem.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (stem.EndsWith("B", StringComparison.OrdinalIgnoreCase))
            {
                var prev = stem.Length >= 2 ? stem[stem.Length - 2] : '\0';
                return char.IsDigit(prev) || prev == ')';
            }
            return false;
        }

        private static int CountIndexEntries(DungeonMapDirectoryIndex index)
        {
            int count = 0;
            foreach (var kv in index.ByType)
                count += kv.Value.Count;
            return count;
        }

        // --- Backward-compatible fallback API for selftests ---

        internal static int SelectFallbackMapIdForUnresolvedRoom(
            int dungeonId, int mazeIndex, int x, int y,
            IReadOnlyList<MapSpecificationItem> mapSpecifications,
            IReadOnlyList<LstEntry> mapEntries,
            IReadOnlyList<string> mapDirCandidates,
            bool preferQuestVariant,
            out string reason)
        {
            reason = string.Empty;

            // Build a temporary index from the provided entries
            var maplst = new LstFile();
            if (mapEntries != null)
                foreach (var e in mapEntries)
                    if (e != null) maplst.Entries.Add(e);

            var index = BuildIndex(maplst, mapDirCandidates as List<string> ?? new List<string>(mapDirCandidates ?? Array.Empty<string>()));

            // Quest variant preference
            if (preferQuestVariant)
            {
                var key = DungeonMapDirectoryIndex.CoordKey(x, y);
                if (index.ByCoordinate.TryGetValue(key, out var coordEntries))
                {
                    var questHit = PickByType(coordEntries, MapFileType.Quest);
                    if (questHit > 0) { reason = "quest-variant coordinate map"; return questHit; }
                }
                // Any quest file
                if (index.ByType.TryGetValue(MapFileType.Quest, out var questPool))
                {
                    foreach (var e in questPool)
                    {
                        if (e.MapId > 0) { reason = "quest-variant map"; return e.MapId; }
                    }
                }
            }

            // Coordinate-based lookup (nearest coordinate, not just exact)
            var bestId = -1;
            var bestDistance = int.MaxValue;
            var bestX = 0;
            var bestY = 0;
            foreach (var kv in index.ByCoordinate)
            {
                foreach (var entry in kv.Value)
                {
                    if (entry.FileType == MapFileType.Boss) continue;
                    if (entry.FileType == MapFileType.Quest && !preferQuestVariant) continue;
                    var dist = Math.Abs(entry.CoordX - x) + Math.Abs(entry.CoordY - y);
                    if (dist < bestDistance || (dist == bestDistance && entry.MapId < bestId))
                    {
                        bestDistance = dist;
                        bestId = entry.MapId;
                        bestX = entry.CoordX;
                        bestY = entry.CoordY;
                    }
                }
            }
            if (bestId > 0) { reason = $"nearest coordinate map ({bestX},{bestY})"; return bestId; }

            // First map spec
            if (mapSpecifications != null)
            {
                foreach (var item in mapSpecifications)
                {
                    if (item == null || item.Index <= 0) continue;
                    reason = "first map spec";
                    if (item.MapCandidates != null && item.MapCandidates.Length > 0)
                        return item.MapCandidates[Infrastructure.ServerRandom.Next(item.MapCandidates.Length)];
                    return item.Index;
                }
            }

            // First non-quest candidate
            if (index.ByType.TryGetValue(MapFileType.Normal, out var normalPool) && normalPool.Count > 0)
            {
                reason = "first non-quest candidate map";
                return normalPool[0].MapId;
            }

            return -1;
        }
    }
}
