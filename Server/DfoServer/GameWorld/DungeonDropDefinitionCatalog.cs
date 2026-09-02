using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Concurrent;

namespace DfoServer.GameWorld
{
    internal static class DungeonDropDefinitionCatalog
    {
        private static readonly ConcurrentDictionary<int, DungeonDropDefinition> Cache =
            new ConcurrentDictionary<int, DungeonDropDefinition>();

        internal static DungeonDropDefinition Resolve(int dungeonId)
        {
            if (dungeonId <= 0)
                return DungeonDropDefinition.Standard;

            return Cache.GetOrAdd(dungeonId, ResolveUncached);
        }

        private static DungeonDropDefinition ResolveUncached(int dungeonId)
        {
            try
            {
                var loaded = Dungeon.LoadDungeonFileWithPath(dungeonId);
                var file = loaded.File;
                if (file == null)
                    return CreateStandard(dungeonId, loaded.FilePath);

                if (LicensedDungeonCatalog.TryGetDefinition(
                        dungeonId,
                        out _))
                {
                    FileLogger.Log(
                        $"[DungeonDropDefinition] licensed whitelist policy: " +
                        $"dungeon={dungeonId} path={loaded.FilePath}");
                    return DungeonDropDefinition.CreateLicensed(
                        dungeonId,
                        loaded.FilePath);
                }

                if (file.ImpossibleDungeonClassification >= 0)
                {
                    return new DungeonDropDefinition(
                        dungeonId,
                        file.SharedDifficultDungeonIndex,
                        file.ImpossibleDungeonClassification,
                        loaded.FilePath,
                        DungeonDropDefinitionKind.ImpossibleParty,
                        DungeonDropPolicy.Impossible);
                }

                if (IsImpossibleSoloDefinition(dungeonId, file))
                {
                    var counterpart = Dungeon.LoadDungeonFileWithPath(
                        file.SharedDifficultDungeonIndex);
                    return new DungeonDropDefinition(
                        dungeonId,
                        file.SharedDifficultDungeonIndex,
                        counterpart.File.ImpossibleDungeonClassification,
                        loaded.FilePath,
                        DungeonDropDefinitionKind.ImpossibleSolo,
                        DungeonDropPolicy.Impossible);
                }

                return CreateStandard(dungeonId, loaded.FilePath);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonDropDefinition] resolution failed open " +
                    $"dungeon={dungeonId}: {ex.Message}");
                return CreateStandard(dungeonId, string.Empty);
            }
        }

        private static bool IsImpossibleSoloDefinition(
            int dungeonId,
            DungeonFile file)
        {
            if (file == null
                || file.SharedDifficultDungeonIndex <= 0
                || file.LimitPartyCount != 1
                || file.Difficulty != 0
                || ParseFirstInt(file.NecessaryParty) != 1
                || file.OnGuideMovieDungeon == null)
            {
                return false;
            }

            try
            {
                var counterpart = Dungeon.LoadDungeonFileWithPath(
                    file.SharedDifficultDungeonIndex).File;
                return counterpart != null
                    && counterpart.ImpossibleDungeonClassification >= 0
                    && counterpart.SharedDifficultDungeonIndex == dungeonId;
            }
            catch
            {
                return false;
            }
        }

        private static int ParseFirstInt(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return -1;

            var tokens = value.Split(
                new[] { ' ', '\t', '\r', '\n', '`' },
                StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 && int.TryParse(tokens[0], out var parsed)
                ? parsed
                : -1;
        }

        private static DungeonDropDefinition CreateStandard(
            int dungeonId,
            string sourcePath)
            => DungeonDropDefinition.CreateStandard(dungeonId, sourcePath);
    }
}
