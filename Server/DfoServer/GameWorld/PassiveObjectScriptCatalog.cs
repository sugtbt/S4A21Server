using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class PassiveObjectScriptCatalog
    {
        private static readonly Lazy<LstFile> PassiveObjectList =
            new Lazy<LstFile>(() => DungeonCatalog.LoadListFile(
                Path.Combine("passiveobject", "passiveobject.lst")));

        private static readonly ConcurrentDictionary<int, bool>
            SceneOwnedMonsterWaveByObjectCode =
                new ConcurrentDictionary<int, bool>();

        internal static bool HasSceneOwnedTimedMonsterWave(int objectCode)
        {
            if (objectCode <= 0)
                return false;

            return SceneOwnedMonsterWaveByObjectCode.GetOrAdd(
                objectCode,
                ResolveSceneOwnedTimedMonsterWave);
        }

        internal static IReadOnlyList<TimedMonsterSpawnInfo>
            GetTimedMonsterSpawns(int objectCode)
        {
            if (objectCode <= 0)
                return Array.Empty<TimedMonsterSpawnInfo>();

            try
            {
                var entry = PassiveObjectList.Value.GetById(objectCode);
                if (entry == null || string.IsNullOrWhiteSpace(entry.FilePath))
                    return Array.Empty<TimedMonsterSpawnInfo>();

                var objectFile = ObjectFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("passiveobject", entry.FilePath)));
                return objectFile.TimedMonsterSpawns
                    ?? Array.Empty<TimedMonsterSpawnInfo>();
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[PassiveObjectScriptCatalog] object={objectCode} " +
                    $"timed spawn load failed: {ex.Message}");
                return Array.Empty<TimedMonsterSpawnInfo>();
            }
        }

        private static bool ResolveSceneOwnedTimedMonsterWave(int objectCode)
        {
            var spawns = GetTimedMonsterSpawns(objectCode);
            if (spawns.Count == 0)
                return false;

            foreach (var spawn in spawns)
            {
                if (spawn == null || spawn.MonsterId <= 0)
                    return false;
            }

            FileLogger.Log(
                $"[PassiveObjectScriptCatalog] scene-owned timed monster wave: " +
                $"object={objectCode} spawns={spawns.Count}");
            return true;
        }
    }
}
