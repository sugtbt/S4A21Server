using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class A21DungeonDropItemSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_DUNGEON_DROP_ITEM selftest ===");
            var failures = 0;

            Check(
                "DROP_ITEM success ACK matches A21 client parser",
                BytesEqual(
                    DropItemBuilder.BuildDropSuccessAck(0, 3, 1),
                    new byte[] { 0x01, 0x00, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00 }),
                ref failures);

            var core = ItemCore.Create(ItemCore.KindConsumable, 3030);
            core.Count = 1;
            var drop = new DropInfo
            {
                SceneSlot = 0x0066,
                TemplateId = 3030,
                StackCount = 1,
                Core = core,
                SourceSlotIndex = 3,
                IsPlayerDropped = true,
            };

            var body = DropItemBuilder.BuildDrop(
                dropperActorId: 0x0DDB,
                positionX: 0x00BA,
                positionY: 0x00D8,
                drop: drop,
                ownerActorId: 0);

            Check(
                "DROP_ITEM ground notification carries A21 101B item entry",
                body.Length == 112
                && ReadUInt16(body, 0) == 0x0DDB
                && ReadUInt16(body, 2) == 0x00BA
                && ReadUInt16(body, 4) == 0x00D8
                && ReadUInt16(body, 6) == 0x0066
                && ReadInt16(body, 8) == 3
                && ReadInt32(body, 10) == 3030
                && ReadInt32(body, 14) == 1
                && body[109] == 0
                && ReadUInt16(body, 110) == 0,
                ref failures);

            VerifyRealPvfIndependentDrop(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_DUNGEON_DROP_ITEM selftest passed."
                    : $"A21_DUNGEON_DROP_ITEM selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static bool BytesEqual(byte[] actual, byte[] expected)
        {
            if (actual == null || expected == null || actual.Length != expected.Length)
                return false;

            for (var index = 0; index < actual.Length; index++)
            {
                if (actual[index] != expected[index])
                    return false;
            }

            return true;
        }

        private static short ReadInt16(byte[] data, int offset)
            => BitConverter.ToInt16(data, offset);

        private static ushort ReadUInt16(byte[] data, int offset)
            => BitConverter.ToUInt16(data, offset);

        private static int ReadInt32(byte[] data, int offset)
            => BitConverter.ToInt32(data, offset);

        private static void VerifyRealPvfIndependentDrop(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("real PVF independent-drop checks skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            Check(
                "real PVF independent drop table loads multiple same-monster entries",
                IndependentDropDefinitionCatalog.HasMonsterDefinition(56675)
                && IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    56675,
                    out var entries)
                && entries.Count >= 5,
                ref failures);

            var slotCounter = (ushort)0;
            var drops = IndependentDropSystem.GenerateDrops(
                monsterCode: 56675,
                difficulty: 2,
                dungeonLevel: 85,
                partyMemberCount: 1,
                chronicleDropJobGroup: -1,
                lcg: new DnfLcg(0),
                slotCounter: ref slotCounter);
            var guaranteedDrop = drops
                .Where(drop => drop.TemplateId == 10093971)
                .ToArray();

            Check(
                "real PVF independent drop count uses the solo-player count column",
                guaranteedDrop.Length == 1
                && guaranteedDrop[0].StackCount == 1,
                ref failures);
        }
    }
}
