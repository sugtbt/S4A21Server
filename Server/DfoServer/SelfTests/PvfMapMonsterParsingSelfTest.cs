using DfoServer.GameWorld;
using PvfLib;
using System;
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
