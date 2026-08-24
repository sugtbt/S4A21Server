using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;

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
    }
}
