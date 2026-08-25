using DfoServer.Game.Dungeon.Tournament;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;

namespace DfoServer.SelfTests
{
    public static class A21SpecialDungeonProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_SPECIAL_DUNGEON_PROTOCOL selftest ===");
            var failures = 0;

            VerifyTournamentPayloads(ref failures);
            VerifyBloodAltarPayloads(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_SPECIAL_DUNGEON_PROTOCOL selftest passed."
                    : $"A21_SPECIAL_DUNGEON_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyTournamentPayloads(ref int failures)
        {
            var candidates = new List<TournamentActorDefinition>();
            for (var index = 0; index < 15; index++)
            {
                candidates.Add(new TournamentActorDefinition(
                    partyCount: 1,
                    TournamentActorKind.Monster,
                    code: 56000 + index,
                    strength: 100 + index,
                    name: string.Empty,
                    level: 70,
                    actorType: 0));
            }

            var definition = new TournamentDungeonDefinition(
                dungeonId: 120,
                mapId: 17100,
                basicLevel: 70,
                partyLimit: 1,
                coinLimit: 3,
                roundFatigue: 0,
                clearRewardGoldRate: 1f,
                experienceByRound: null,
                resultCards: null,
                rewardItemRates: Array.Empty<TournamentRewardItemRateDefinition>(),
                candidates,
                startAreas: Array.Empty<TournamentStartAreaDefinition>(),
                entryItems: Array.Empty<TournamentEntryItemDefinition>());
            if (!TournamentDungeonRuntimeFactory.TryCreate(
                    definition,
                    partyCount: 1,
                    _ => 0,
                    out var runtime,
                    out var failureReason))
            {
                Check(
                    $"tournament runtime can be created: {failureReason}",
                    false,
                    ref failures);
                return;
            }

            var info = TournamentPacketBuilder.BuildTournamentInfo(
                runtime,
                difficulty: 2,
                firstMonsterSequence: 0x2711);
            Check(
                "TOURNAMENT_INFO uses the captured 260-byte body",
                info.Length == 260,
                ref failures);
            Check(
                "TOURNAMENT_INFO starts with u32 dungeon id, difficulty and party limit",
                ReadUInt32(info, 0) == 120
                && info[4] == 2
                && info[5] == 1,
                ref failures);
            Check(
                "TOURNAMENT_INFO path actor sequence remains aligned after the bracket",
                info[224] == 1
                && ReadUInt16(info, 225) == 0x2711,
                ref failures);

            const uint seed = 0xDD23D90E;
            var map = TournamentPacketBuilder.BuildTournamentMapInfo(
                x: 0,
                y: 0,
                seed,
                mapId: 17100,
                revisit: false);
            Check(
                "TOURNAMENT_MAP_INFO uses the captured 15-byte body",
                map.Length == 15,
                ref failures);
            Check(
                "TOURNAMENT_MAP_INFO writes u32 map id and explicit tail fields",
                ReadUInt32(map, 2) == seed
                && map[6] == 0
                && map[7] == 1
                && ReadUInt32(map, 8) == 17100
                && map[12] == 0
                && map[13] == 0
                && map[14] == 0,
                ref failures);
        }

        private static void VerifyBloodAltarPayloads(ref int failures)
        {
            var endlessInfo = BloodAltarPacketBuilder.BuildInfo(
                11006,
                BloodAltarDungeonKind.Endless);
            var ultimateInfo = BloodAltarPacketBuilder.BuildInfo(
                11007,
                BloodAltarDungeonKind.Ultimate);
            Check(
                "BLOOD_INFO keeps the captured 12-byte body for both altar kinds",
                endlessInfo.Length == 12 && ultimateInfo.Length == 12,
                ref failures);
            Check(
                "BLOOD_INFO maps the PVF altar kind to the captured mode word",
                ReadUInt32(endlessInfo, 0) == 11006
                && ReadUInt16(endlessInfo, 4) == 0
                && ReadUInt16(endlessInfo, 6) == 2
                && ReadUInt32(endlessInfo, 8) == 0
                && ReadUInt32(ultimateInfo, 0) == 11007
                && ReadUInt16(ultimateInfo, 4) == 0
                && ReadUInt16(ultimateInfo, 6) == 0
                && ReadUInt32(ultimateInfo, 8) == 0,
                ref failures);

            var firstMap = BloodAltarPacketBuilder.BuildStartMap(
                x: 0,
                y: 0,
                seed: 0x028080C0,
                mapId: 16348);
            var movedMap = BloodAltarPacketBuilder.BuildStartMap(
                x: 1,
                y: 0,
                seed: 0x0002084D,
                mapId: 16353);
            Check(
                "START_BLOOD_MAP remains 15 bytes on entry and map transitions",
                firstMap.Length == 15 && movedMap.Length == 15,
                ref failures);
            Check(
                "START_BLOOD_MAP writes the captured mode, u32 map id and zero tails",
                movedMap[0] == 1
                && movedMap[1] == 0
                && ReadUInt32(movedMap, 2) == 0x0002084D
                && movedMap[6] == 0
                && movedMap[7] == 1
                && ReadUInt32(movedMap, 8) == 16353
                && movedMap[12] == 0
                && movedMap[13] == 0
                && movedMap[14] == 0,
                ref failures);
        }

        private static ushort ReadUInt16(byte[] data, int offset)
            => BitConverter.ToUInt16(data, offset);

        private static uint ReadUInt32(byte[] data, int offset)
            => BitConverter.ToUInt32(data, offset);

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
