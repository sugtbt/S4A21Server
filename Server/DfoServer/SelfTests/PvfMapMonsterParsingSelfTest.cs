using DfoServer.GameWorld;
using DfoServer.Game.Quests;
using DfoServer.Game.Dungeon;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class PvfMapMonsterParsingSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== PVF_MAP_MONSTER_PARSING selftest ===");
            var failures = 0;

            VerifyDummyBossAlignment(ref failures);
            VerifyNpcBossAlignment(ref failures);
            VerifyNpcDummyBossAlignment(ref failures);
            VerifyRealArdenBossMap(ref failures);
            VerifyRealDimensionBossMaps(ref failures);
            VerifyCrackOfDimensionSelection(ref failures);
            VerifyAnotherAradQuestRuntime(ref failures);
            VerifySceneOwnedTimedMonsterWave(ref failures);
            VerifyWorldMapHuntMonsterQuestLayout(ref failures);

            Console.WriteLine(failures == 0
                ? "PVF_MAP_MONSTER_PARSING selftest passed"
                : $"PVF_MAP_MONSTER_PARSING selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyDummyBossAlignment(ref int failures)
        {
            var map = MapFile.Parse(@"
[monster]
63007 1 0 -184 -349 0 1 1 `[fixed]` `[dummy]` `[boss]` 66312 1 0 687 228 0 1 1 `[fixed]` `[boss]`
[/monster]
");

            Check(
                "dummy boss marker consumes one extra tag without dropping the next actor",
                map.Monsters.Count == 2
                && map.Monsters[0].MonsterId == 63007
                && map.Monsters[0].Type == MonsterType.Boss
                && map.Monsters[1].MonsterId == 66312
                && map.Monsters[1].Type == MonsterType.Boss,
                ref failures);
        }

        private static void VerifyNpcBossAlignment(ref int failures)
        {
            var map = MapFile.Parse(@"
[monster]
62000 1 0 100 200 0 1 1 `[fixed]` `[NPC]` 1000 `[boss]` 62001 1 0 300 400 0 1 1 `[fixed]` `[normal]`
[/monster]
");

            Check(
                "NPC boss marker keeps the existing twelve-field record shape",
                map.Monsters.Count == 2
                && map.Monsters[0].MonsterId == 62000
                && map.Monsters[0].NpcId == 1000
                && map.Monsters[0].Type == MonsterType.Boss
                && map.Monsters[1].MonsterId == 62001,
                ref failures);
        }

        private static void VerifyRealArdenBossMap(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath))
            {
                Console.WriteLine(
                    "real PVF 57541 check skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            try
            {
                var map = MapFile.Parse(PvfArchiveAccessor.ReadText(
                    "map/arden/2282/57541_2282(3.2).map"));
                var target = map.Monsters.FirstOrDefault(
                    monster => monster.MonsterId == 66312);
                var dummy = map.Monsters.FirstOrDefault(
                    monster => monster.MonsterId == 63007);

                Check(
                    "Arden boss map keeps hunt target 66312 and dummy boss 63007",
                    map.Monsters.Count == 9
                    && target != null
                    && target.Type == MonsterType.Boss
                    && dummy != null
                    && dummy.Type == MonsterType.Boss,
                    ref failures);

                var projected = Dungeon.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 93,
                    x: 3,
                    y: 2,
                    mazeIndex: -1,
                    overrideMapId: 57541,
                    bossPos: new[] { 3, 2 });
                var projectedTarget = projected.Monsters.FirstOrDefault(
                    monster => monster.Code == 66312);
                Check(
                    "Arden boss projection keeps 66312 as a blocking Boss actor",
                    projected.Monsters.Count == 9
                    && projectedTarget.Code == 66312
                    && projectedTarget.Type == 3
                    && projectedTarget.IsBlocking,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"real PVF 57541 check failed with exception: {ex.Message}");
                failures++;
            }
        }

        private static void VerifyRealDimensionBossMaps(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath))
            {
                Console.WriteLine(
                    "real dimension boss-map check skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            try
            {
                var dogsMaze = Dungeon.GetDungeonMaze(64, 0);
                var dogsStart = DungeonMapResolver.ResolveMapId(
                    64,
                    0,
                    0,
                    dogsMaze,
                    0,
                    new[] { 5, 0 });
                var dogsBoss = DungeonMapResolver.ResolveMapId(
                    64,
                    5,
                    0,
                    dogsMaze,
                    0,
                    new[] { 5, 0 });
                Check(
                    "Ranjerus Dogs uses its PVF boss MAP instead of the last normal MAP",
                    dogsStart == 16300 && dogsBoss == 16305,
                    ref failures);

                var wrapperMaze = Dungeon.GetDungeonMaze(7100, 0);
                var wrapperStart = DungeonMapResolver.ResolveMapId(
                    7100,
                    0,
                    0,
                    wrapperMaze,
                    0,
                    new[] { 5, 0 });
                var wrapperBoss = DungeonMapResolver.ResolveMapId(
                    7100,
                    5,
                    0,
                    wrapperMaze,
                    0,
                    new[] { 5, 0 });
                Check(
                    "Fusion dimension uses its PVF boss MAP instead of the last normal MAP",
                    wrapperStart == 53070 && wrapperBoss == 53075,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"real dimension boss-map check failed with exception: {ex.Message}");
                failures++;
            }
        }

        private static void VerifyCrackOfDimensionSelection(ref int failures)
        {
            var body = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(64u), 0, body, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(14125u), 0, body, 4, 4);
            Check(
                "02B6 parses little-endian historical dungeon and quest IDs",
                Network.Parsers.Dungeon.CrackOfDimensionRequest.TryParse(
                    body,
                    out var parsed)
                && parsed.HistoricalDungeonId == 64
                && parsed.CrackQuestId == 14125
                && parsed.TrailingLength == 0,
                ref failures);

            Check(
                "02B6 rejects a body shorter than the two-u32 pair",
                !Network.Parsers.Dungeon.CrackOfDimensionRequest.TryParse(
                    new byte[7],
                    out _),
                ref failures);

            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH")))
            {
                Console.WriteLine(
                    "02B6 PVF pair validation skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            var validPair = FindCrackPair();
            if (!validPair.HasValue)
            {
                Check(
                    "current PVF contains a valid Crack-of-Dimension pair",
                    false,
                    ref failures);
                return;
            }

            var pair = validPair.Value;
            Check(
                "PVF-valid 02B6 pair is accepted",
                AnotherAradSelectionResolver.TryResolve(
                    pair.DungeonId,
                    pair.QuestId,
                    out var selection,
                    out _)
                && selection.HistoricalDungeonId == pair.DungeonId
                && selection.CrackQuestId == pair.QuestId
                && selection.WrapperDungeonId > 0,
                ref failures);

            var invalidQuestId = pair.QuestId + 1;
            Check(
                "02B6 rejects a quest that does not pair with the historical dungeon",
                !AnotherAradSelectionResolver.TryResolve(
                    pair.DungeonId,
                    invalidQuestId,
                    out _,
                    out _),
                ref failures);
        }

        private static void VerifySceneOwnedTimedMonsterWave(ref int failures)
        {
            var parsed = ObjectFile.Parse(@"
[int data]
2 10000 67 62014 216 180 500 0 25000 67 62015 240 300 500 0
[/int data]
");
            Check(
                "object int-data parses a complete timed monster schedule",
                parsed.TimedMonsterSpawns.Count == 2
                    && parsed.TimedMonsterSpawns[0].DelayMilliseconds == 10000
                    && parsed.TimedMonsterSpawns[0].MonsterId == 62014
                    && parsed.TimedMonsterSpawns[1].MonsterId == 62015,
                ref failures);

            var malformed = ObjectFile.Parse(@"
[int data]
2 10000 67 62014 216 180 500 0
[/int data]
");
            Check(
                "incomplete object int-data does not become a timed schedule",
                malformed.TimedMonsterSpawns.Count == 0,
                ref failures);

            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH")))
            {
                Console.WriteLine(
                    "Suspicious Village timed-wave check skipped: " +
                    "PVF_ARCHIVE_PATH is not set");
                return;
            }

            try
            {
                var schedule = PassiveObjectScriptCatalog
                    .GetTimedMonsterSpawns(1112);
                var room = Dungeon.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 53,
                    x: 2,
                    y: 5,
                    mazeIndex: 0,
                    overrideMapId: 16408,
                    bossPos: new[] { 3, 0 });
                Check(
                    "Suspicious Village owns all eleven thief waves in its PVF object script",
                    schedule.Count == 11
                        && schedule.Count(spawn => spawn.MonsterId == 62014) == 6
                        && schedule.Count(spawn => spawn.MonsterId == 62015) == 5,
                    ref failures);
                Check(
                    "scene-owned timed waves keep one blocking aggregate actor",
                    room.Monsters.Count(actor => actor.Code == 56716) == 1
                        && room.Monsters.Count(actor =>
                            actor.Code == 56716 && actor.IsBlocking) == 1
                        && room.Monsters.All(actor =>
                            actor.Code != 62014 && actor.Code != 62015),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Suspicious Village timed-wave check failed: {ex.Message}");
                failures++;
            }
        }

        private static (int DungeonId, int QuestId)? FindCrackPair()
        {
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                var quest = QuestData.GetQuestFile(questId);
                if (quest == null
                    || !string.Equals(
                        QuestData.NormalizeQuestTag(quest.RewardType),
                        "crack of dimension",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (AnotherAradConfigCatalog.TryGetHistoricalDungeonId(
                        questId,
                        out var historicalDungeonId)
                    && AnotherAradSelectionResolver.TryResolve(
                        historicalDungeonId,
                        questId,
                        out _,
                        out _))
                {
                    return (historicalDungeonId, questId);
                }
            }

            return null;
        }

        private static void VerifyAnotherAradQuestRuntime(ref int failures)
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH")))
            {
                Console.WriteLine(
                    "Another Arad quest runtime check skipped: " +
                    "PVF_ARCHIVE_PATH is not set");
                return;
            }

            var singleHunt = FindCrackQuestDefinition("crack_002.qst");
            var modeledCounts = new int[5];
            var modeledTotal = 0;
            var unmodeled = new System.Collections.Generic.List<string>();
            foreach (var questId in AnotherAradConfigCatalog.ConfiguredQuestIds)
            {
                if (!AnotherAradConfigCatalog.TryGetHistoricalDungeonId(
                        questId,
                        out var historicalDungeonId))
                {
                    unmodeled.Add($"{questId}:config_missing");
                    continue;
                }
                if (!AnotherAradQuestDefinition.TryCreate(
                    questId,
                    historicalDungeonId,
                    out var definition,
                    out var definitionReason))
                {
                    unmodeled.Add($"{questId}:{definitionReason}");
                    continue;
                }

                modeledTotal++;
                modeledCounts[(int)definition.Kind]++;
            }
            Console.WriteLine(
                $"Another Arad catalog: configured=" +
                $"{AnotherAradConfigCatalog.ConfiguredQuestIds.Count} " +
                $"modeled={modeledTotal} kinds=" +
                $"{string.Join(",", modeledCounts)} unmodeled=" +
                $"{string.Join(",", unmodeled)}");
            Check(
                "current crack config models every exposed Another Arad mission",
                AnotherAradConfigCatalog.ConfiguredQuestIds.Count == 393
                    && modeledTotal == 393
                    && modeledCounts[(int)AnotherAradQuestKind.Hunt] == 190
                    && modeledCounts[(int)AnotherAradQuestKind.Clear] == 106
                    && modeledCounts[(int)AnotherAradQuestKind.TimedClear] == 51
                    && modeledCounts[(int)AnotherAradQuestKind.Locations] == 46
                    && unmodeled.Count == 0,
                ref failures);
            Check(
                "current crack config resolves its level reward from ETC",
                AnotherAradConfigCatalog.TryResolveReward(
                    55,
                    out var configuredRewardItemId,
                    out var configuredRewardCount)
                    && configuredRewardItemId == 10100301
                    && configuredRewardCount == 1,
                ref failures);
            Check(
                "Another Arad single hunt keeps the PVF initial count",
                singleHunt != null
                    && singleHunt.Kind == AnotherAradQuestKind.Hunt
                    && singleHunt.InitialTrigger == 10,
                ref failures);

            var unscopedHunt = FindCrackQuestDefinition("crack_057.qst");
            Check(
                "Another Arad ETC pairing scopes a wildcard hunt mission",
                unscopedHunt != null
                    && unscopedHunt.Kind == AnotherAradQuestKind.Hunt
                    && unscopedHunt.HuntRequirements.Count == 1
                    && unscopedHunt.HuntRequirements[0].DungeonSelector == -1
                    && unscopedHunt.HuntRequirements[0].ActorSelector == 1043
                    && unscopedHunt.InitialTrigger == 1,
                ref failures);
            if (singleHunt != null)
            {
                var runtime = new AnotherAradQuestRuntime(singleHunt);
                var beforeAccept = runtime.TryRecordActorDeath(
                    Guid.NewGuid(),
                    singleHunt.HistoricalDungeonId,
                    4,
                    1,
                    0,
                    1,
                    isHostile: true,
                    isBlocking: true,
                    out var beforeAcceptTrigger);
                runtime.TryAccept(
                    DateTime.UnixEpoch,
                    out var acceptedTrigger,
                    out var duplicateAccept);
                var deathEventId = Guid.NewGuid();
                var changed = runtime.TryRecordActorDeath(
                    deathEventId,
                    singleHunt.HistoricalDungeonId,
                    4,
                    1,
                    0,
                    1,
                    isHostile: true,
                    isBlocking: true,
                    out var changedTrigger);
                var duplicateDeath = runtime.TryRecordActorDeath(
                    deathEventId,
                    singleHunt.HistoricalDungeonId,
                    4,
                    1,
                    0,
                    1,
                    isHostile: true,
                    isBlocking: true,
                    out var duplicateTrigger);
                Check(
                    "Another Arad hunt starts after accept and deduplicates deaths",
                    !beforeAccept
                        && beforeAcceptTrigger == 10
                        && !duplicateAccept
                        && acceptedTrigger == 10
                        && changed
                        && changedTrigger == 9
                        && !duplicateDeath
                        && duplicateTrigger == 9,
                    ref failures);
            }

            var multiHunt = FindCrackQuestDefinition("crack_015.qst");
            Check(
                "Another Arad multi-target hunt keeps independent trigger channels",
                multiHunt != null
                    && multiHunt.InitialTrigger == (15u | (15u << 9)),
                ref failures);
            if (multiHunt != null)
            {
                var runtime = new AnotherAradQuestRuntime(multiHunt);
                runtime.TryAccept(DateTime.UnixEpoch, out _, out _);
                runtime.TryRecordActorDeath(
                    Guid.NewGuid(),
                    multiHunt.HistoricalDungeonId,
                    4,
                    210,
                    0,
                    1,
                    isHostile: true,
                    isBlocking: true,
                    out var trigger);
                Check(
                    "Another Arad multi-target hunt decrements only its matched channel",
                    QuestData.GetTriggerChannel(trigger, 0) == 14
                        && QuestData.GetTriggerChannel(trigger, 1) == 15,
                    ref failures);
            }

            var clear = FindCrackQuestDefinition("crack_001.qst");
            Check(
                "Another Arad clear mission uses its historical dungeon scope",
                clear != null && clear.Kind == AnotherAradQuestKind.Clear,
                ref failures);
            if (clear != null)
            {
                var success = new AnotherAradQuestRuntime(clear);
                success.TryAccept(DateTime.UnixEpoch, out _, out _);
                var completed = success.EvaluateSettlement(
                    clear.HistoricalDungeonId,
                    4,
                    DateTime.UnixEpoch.AddSeconds(1),
                    out var completedTrigger);
                var reservation = success.TryReserveRewardClaim();
                success.CommitRewardClaim();
                Check(
                    "Another Arad clear mission completes only after settlement",
                    completed
                        && completedTrigger == 0
                        && reservation
                            == AnotherAradQuestClaimDisposition.Reserved
                        && success.TryReserveRewardClaim()
                            == AnotherAradQuestClaimDisposition.AlreadyClaimed,
                    ref failures);

                var mismatch = new AnotherAradQuestRuntime(clear);
                mismatch.TryAccept(DateTime.UnixEpoch, out _, out _);
                Check(
                    "Another Arad clear mission rejects another dungeon context",
                    !mismatch.EvaluateSettlement(
                        clear.HistoricalDungeonId + 1,
                        4,
                        DateTime.UnixEpoch.AddSeconds(1),
                        out _),
                    ref failures);
            }

            var locations = FindFirstCrackQuestDefinition(
                definition => definition.Kind == AnotherAradQuestKind.Locations);
            Check(
                "current PVF contains a modeled Another Arad location mission",
                locations != null,
                ref failures);
            if (locations != null)
            {
                var runtime = new AnotherAradQuestRuntime(locations);
                runtime.TryAccept(DateTime.UnixEpoch, out _, out _);
                for (var index = 0;
                    index < locations.RequiredLocationCount;
                    index++)
                {
                    runtime.TryRecordRoomClear(index, 0, 1000 + index, out _);
                }
                runtime.TryRecordRoomClear(
                    locations.RequiredLocationCount - 1,
                    0,
                    1000 + locations.RequiredLocationCount - 1,
                    out var duplicateRoomTrigger);
                Check(
                    "Another Arad location mission counts unique rooms",
                    duplicateRoomTrigger == 0
                        && runtime.EvaluateSettlement(
                            locations.HistoricalDungeonId,
                            Math.Max(4, locations.MinimumDifficulty),
                            DateTime.UnixEpoch.AddSeconds(1),
                            out var locationTrigger)
                        && locationTrigger == 0,
                    ref failures);
            }

            var longTimed = FindFirstCrackQuestDefinition(
                definition => definition.Kind == AnotherAradQuestKind.TimedClear
                    && definition.TimeLimitSeconds > 0x1FF);
            Check(
                "Another Arad timed mission seconds are not packed as a trigger channel",
                longTimed != null
                    && longTimed.InitialTrigger == 1
                    && longTimed.TimeLimitSeconds > 0x1FF,
                ref failures);
            if (longTimed != null)
            {
                var inside = new AnotherAradQuestRuntime(longTimed);
                inside.TryAccept(DateTime.UnixEpoch, out _, out _);
                var insideLimit = inside.EvaluateSettlement(
                    longTimed.HistoricalDungeonId,
                    Math.Max(4, longTimed.MinimumDifficulty),
                    DateTime.UnixEpoch.AddSeconds(longTimed.TimeLimitSeconds),
                    out var insideTrigger);
                var expired = new AnotherAradQuestRuntime(longTimed);
                expired.TryAccept(DateTime.UnixEpoch, out _, out _);
                var outsideLimit = expired.EvaluateSettlement(
                    longTimed.HistoricalDungeonId,
                    Math.Max(4, longTimed.MinimumDifficulty),
                    DateTime.UnixEpoch.AddSeconds(longTimed.TimeLimitSeconds + 1),
                    out var expiredTrigger);
                Check(
                    "Another Arad timed mission honors its PVF boundary",
                    insideLimit
                        && insideTrigger == 0
                        && !outsideLimit
                        && expiredTrigger == 1,
                    ref failures);
            }

            var clearMap = FindFirstCrackQuestDefinition(
                definition => definition.Kind == AnotherAradQuestKind.ClearMap);
            if (clearMap != null)
            {
                var runtime = new AnotherAradQuestRuntime(clearMap);
                runtime.TryAccept(DateTime.UnixEpoch, out _, out _);
                var wrongMap = runtime.TryRecordRoomClear(
                    0,
                    0,
                    clearMap.ClearTargetId + 1,
                    out var wrongMapTrigger);
                var targetMap = runtime.TryRecordRoomClear(
                    1,
                    0,
                    clearMap.ClearTargetId,
                    out var targetMapTrigger);
                Check(
                    "Another Arad clear-map mission completes on its PVF MAP",
                    !wrongMap
                        && wrongMapTrigger == 1
                        && targetMap
                        && targetMapTrigger == 0
                        && runtime.EvaluateSettlement(
                            clearMap.HistoricalDungeonId,
                            4,
                            DateTime.UnixEpoch.AddSeconds(1),
                            out _),
                    ref failures);
            }
            else
            {
                Console.WriteLine(
                    "configured Another Arad clear-map mission check skipped: " +
                    "current crack info list exposes none");
            }

            var noRevive = FindFirstCrackQuestDefinition(
                definition => definition.Kind == AnotherAradQuestKind.Clear
                    && definition.RequireNoRevive);
            Check(
                "current PVF contains a modeled Another Arad no-revive mission",
                noRevive != null,
                ref failures);
            if (noRevive != null)
            {
                var runtime = new AnotherAradQuestRuntime(noRevive);
                runtime.TryAccept(DateTime.UnixEpoch, out _, out _);
                runtime.MarkReviveUsed();
                Check(
                    "Another Arad no-revive mission fails after a revive fact",
                    !runtime.EvaluateSettlement(
                        noRevive.HistoricalDungeonId,
                        Math.Max(4, noRevive.MinimumDifficulty),
                        DateTime.UnixEpoch.AddSeconds(1),
                        out _),
                    ref failures);
            }
        }

        private static AnotherAradQuestDefinition FindCrackQuestDefinition(
            string fileName)
        {
            var suffix = "/" + (fileName ?? string.Empty).ToLowerInvariant();
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                if (!QuestCatalog.TryGetPath(questId, out var path)
                    || !(path ?? string.Empty)
                        .Replace('\\', '/')
                        .ToLowerInvariant()
                        .EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (AnotherAradConfigCatalog.TryGetHistoricalDungeonId(
                        questId,
                        out var historicalDungeonId)
                    && AnotherAradQuestDefinition.TryCreate(
                        questId,
                        historicalDungeonId,
                        out var definition,
                        out _))
                {
                    return definition;
                }
            }

            return null;
        }

        private static AnotherAradQuestDefinition FindFirstCrackQuestDefinition(
            Func<AnotherAradQuestDefinition, bool> predicate)
        {
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                if (!QuestCatalog.TryGetPath(questId, out var path)
                    || !(path ?? string.Empty)
                        .Replace('\\', '/')
                        .StartsWith(
                            "n_quest/crackofdimension/",
                            StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!AnotherAradConfigCatalog.TryGetHistoricalDungeonId(
                        questId,
                        out var historicalDungeonId)
                    || !AnotherAradQuestDefinition.TryCreate(
                        questId,
                        historicalDungeonId,
                        out var definition,
                        out _)
                    || predicate?.Invoke(definition) == false)
                {
                    continue;
                }

                return definition;
            }

            return null;
        }

        private static void VerifyWorldMapHuntMonsterQuestLayout(
            ref int failures)
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH")))
            {
                Console.WriteLine(
                    "world-map hunt-monster check skipped: " +
                    "PVF_ARCHIVE_PATH is not set");
                return;
            }

            const ushort quest3Of5 = 14396;
            const ushort quest4Of5 = 14397;
            Check(
                "regional hunt quests read their residual from the fifth field",
                QuestData.IsWorldMapHuntMonsterQuest(quest3Of5)
                    && QuestData.IsWorldMapHuntMonsterQuest(quest4Of5)
                    && QuestData.GetInitTrigger(quest3Of5) == 5
                    && QuestData.GetInitTrigger(quest4Of5) == 5,
                ref failures);

            var repairedLegacy = QuestData.TryRepairWorldMapHuntMonsterTrigger(
                quest3Of5,
                509,
                out var repaired509);
            var preservedProgress = QuestData.TryRepairWorldMapHuntMonsterTrigger(
                quest3Of5,
                4,
                out var repaired4);
            Check(
                "regional hunt repair clamps legacy selector residue only",
                repairedLegacy
                    && repaired509 == 5
                    && preservedProgress
                    && repaired4 == 4,
                ref failures);

            Check(
                "regional hunts use client decrement without entering ordinary kill matching",
                QuestData.GetHuntMonsterTargets(quest3Of5).Count == 0
                    && QuestClientTriggerAuthority.Resolve(
                        quest3Of5,
                        0,
                        increment: false)
                        == QuestClientTriggerDisposition.Mutate
                    && QuestClientTriggerAuthority.Resolve(
                        quest3Of5,
                        0,
                        increment: true)
                        == QuestClientTriggerDisposition.Reject
                    && QuestClientTriggerAuthority.Resolve(
                        quest3Of5,
                        0x20,
                        increment: false)
                        == QuestClientTriggerDisposition.Reject,
                ref failures);

            var evaluation = QuestObjectiveEvaluator.Evaluate(
                new ActiveQuest
                {
                    QuestId = quest3Of5,
                    TriggerValue = 5,
                },
                new QuestProgressApplicationRequest
                {
                    CharacterId = 1,
                    Operation = QuestProgressOperation.ClientTrigger,
                    QuestId = quest3Of5,
                    TriggerType = 0,
                    Increment = false,
                },
                null);
            Check(
                "regional hunt client progress decrements the remaining count",
                evaluation.Matched
                    && evaluation.Trigger.PackedValue == 4
                    && evaluation.Changes.Count == 1,
                ref failures);

            var ordinary = QuestCatalog.OrderedIds
                .Select(id => new
                {
                    QuestId = id,
                    Targets = QuestData.GetHuntMonsterTargets(id),
                })
                .FirstOrDefault(entry => entry.Targets.Count > 0);
            Check(
                "ordinary four-field hunt quests remain server authoritative",
                ordinary != null
                    && QuestClientTriggerAuthority.Resolve(
                        (ushort)ordinary.QuestId,
                        0,
                        increment: false)
                        == QuestClientTriggerDisposition.EchoOnly
                    && QuestData.GetTriggerChannel(
                        QuestData.GetInitTrigger(ordinary.QuestId),
                        0) == ordinary.Targets[0].RequiredCount,
                ref failures);

            VerifyWorldMapHuntMonsterPersistenceRepair(
                quest3Of5,
                ref failures);
        }

        private static void VerifyWorldMapHuntMonsterPersistenceRepair(
            ushort questId,
            ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo-worldmap-hunt-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={path}";
            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
CREATE TABLE character_active_quests (
    character_id INTEGER NOT NULL,
    slot INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    trigger_value INTEGER NOT NULL,
    version INTEGER NOT NULL DEFAULT 0,
    activation_id TEXT NOT NULL,
    PRIMARY KEY (character_id, slot),
    UNIQUE (character_id, quest_id)
);";
                        command.ExecuteNonQuery();
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO character_active_quests
    (character_id, slot, quest_id, trigger_value, version, activation_id)
VALUES
    (1, 0, @questId, 509, 0, @activationId);";
                        command.Parameters.AddWithValue("@questId", questId);
                        command.Parameters.AddWithValue(
                            "@activationId",
                            QuestActivationId.New().ToStorageString());
                        command.ExecuteNonQuery();
                    }
                }

                var service = new QuestActiveTriggerRepairService(
                    connectionString);
                var repairs = service.RepairWorldMapHuntMonsterTriggers(1);
                var repaired = ReadTrigger(connectionString, questId);
                Check(
                    "login repair persists the corrected regional hunt trigger",
                    repairs.Count == 1
                        && repairs[0].PreviousTriggerValue == 509
                        && repairs[0].TriggerValue == 5
                        && repaired == 5,
                    ref failures);

                WriteTrigger(connectionString, questId, 4);
                var partialRepairs = service
                    .RepairWorldMapHuntMonsterTriggers(1);
                Check(
                    "login repair preserves persisted partial progress",
                    partialRepairs.Count == 0
                        && ReadTrigger(connectionString, questId) == 4,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"world-map hunt persistence check failed: {ex.Message}");
                failures++;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static uint ReadTrigger(string connectionString, ushort questId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT trigger_value
FROM character_active_quests
WHERE character_id=1 AND quest_id=@questId;";
                    command.Parameters.AddWithValue("@questId", questId);
                    return Convert.ToUInt32(command.ExecuteScalar());
                }
            }
        }

        private static void WriteTrigger(
            string connectionString,
            ushort questId,
            uint trigger)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE character_active_quests
SET trigger_value=@trigger
WHERE character_id=1 AND quest_id=@questId;";
                    command.Parameters.AddWithValue("@trigger", trigger);
                    command.Parameters.AddWithValue("@questId", questId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void VerifyNpcDummyBossAlignment(ref int failures)
        {
            var map = MapFile.Parse(@"
[monster]
62000 1 0 100 200 0 1 1 `[fixed]` `[NPC]` 1000 `[dummy]` `[boss]` 62001 1 0 300 400 0 1 1 `[fixed]` `[normal]`
[/monster]
");

            Check(
                "NPC and dummy modifiers can precede the actual Boss type",
                map.Monsters.Count == 2
                && map.Monsters[0].MonsterId == 62000
                && map.Monsters[0].NpcId == 1000
                && map.Monsters[0].Type == MonsterType.Boss
                && map.Monsters[1].MonsterId == 62001,
                ref failures);
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"PASS: {name}");
                return;
            }

            Console.WriteLine($"FAIL: {name}");
            failures++;
        }
    }
}
