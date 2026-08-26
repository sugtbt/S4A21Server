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
            VerifyDimensionGateParser(ref failures);
            VerifyRealPvfDimensionDrop(ref failures);

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

        private static void VerifyDimensionGateParser(ref int failures)
        {
            const string sample = @"
[chronicle grow type]
    12 0 # job and first grow
    [normal chronicle list]
        1001 1002
    [/normal chronicle list]
    [set chronicle list]
        2001 2002 // inline comment
    [/set chronicle list]
[/chronicle grow type]
[chronicle grow type]
    12 16
    [normal chronicle list]
        1003
    [/normal chronicle list]
    [set chronicle list]
        2003
    [/set chronicle list]
[/chronicle grow type]";

            var definitions =
                DimensionGateDropDefinitionCatalog.ParseDefinitions(sample);
            Check(
                "dimension gate parser keys grow type by the low 4 bits",
                definitions.Count == 1
                && definitions.TryGetValue((12, 0), out var definition)
                && definition.NormalItems.SequenceEqual(
                    new[] { 1001, 1002, 1003 })
                && definition.SetItems.SequenceEqual(
                    new[] { 2001, 2002, 2003 }),
                ref failures);
        }

        private static void VerifyRealPvfDimensionDrop(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("real PVF dimension-drop checks skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            Check(
                "real PVF marks impossible Goblin Kingdom as a dimension dungeon",
                DfoServer.GameWorld.Dungeon.IsDimensionDungeon(62),
                ref failures);

            Check(
                "real PVF dimension gate table resolves first awakening grow type",
                DimensionGateDropDefinitionCatalog.DefinitionCount > 0
                && DungeonDropPolicy.Impossible.Allows(
                    DungeonMonsterDropSource.Dimension)
                && DimensionGateDropDefinitionCatalog.TryResolve(
                    0,
                    0x11,
                    out var definition)
                && definition.HasNormalItems
                && definition.HasSetItems,
                ref failures);

            if (!DimensionGateDropDefinitionCatalog.TryResolve(
                    0,
                    0x11,
                    out var resolved)
                || !resolved.HasNormalItems
                || !resolved.HasSetItems)
            {
                return;
            }

            Check(
                "dimension free card draws one normal chronicle equipment",
                DimensionDropSystem.TryCreateFreeCard(
                    0,
                    0x11,
                    new DnfLcg(1),
                    out var freeCard)
                && freeCard.IsEquipment
                && freeCard.StackCount == 1
                && resolved.NormalItems.Contains(freeCard.ItemId),
                ref failures);

            Check(
                "dimension paid card draws one set chronicle equipment",
                DimensionDropSystem.TryCreatePaidCard(
                    0,
                    0x11,
                    new DnfLcg(2),
                    out var paidCard)
                && paidCard.IsEquipment
                && paidCard.StackCount == 1
                && resolved.SetItems.Contains(paidCard.ItemId),
                ref failures);

            var eliteSlotCounter = (ushort)0;
            var eliteDrops = DimensionDropSystem.GenerateEliteDrops(
                0,
                0x11,
                new DnfLcg(3),
                ref eliteSlotCounter);
            Check(
                "dimension elite monster drops one chronicle item and one fragment",
                eliteDrops.Count == 2
                && resolved.CombinedItems.Contains((int)eliteDrops[0].TemplateId)
                && eliteDrops[1].TemplateId == DimensionDropSystem.FragmentItemId
                && eliteDrops[1].StackCount == 1,
                ref failures);

            var bossSlotCounter = (ushort)0;
            var bossDrops = DimensionDropSystem.GenerateBossDrops(
                0,
                0x11,
                new DnfLcg(4),
                ref bossSlotCounter);
            Check(
                "dimension boss monster drops normal, set, and two separate fragments",
                bossDrops.Count == 4
                && resolved.NormalItems.Contains((int)bossDrops[0].TemplateId)
                && resolved.SetItems.Contains((int)bossDrops[1].TemplateId)
                && bossDrops[2].TemplateId == DimensionDropSystem.FragmentItemId
                && bossDrops[2].StackCount == 1
                && bossDrops[3].TemplateId == DimensionDropSystem.FragmentItemId
                && bossDrops[3].StackCount == 1,
                ref failures);

            var dimensionSlotCounter = (ushort)0;
            var dimensionMonsterDrops = DimensionDropSystem.GenerateMonsterDrops(
                dungeonId: 62,
                monsterCode: 61340,
                characterJob: 0,
                growType: 0x11,
                lcg: new DnfLcg(5),
                slotCounter: ref dimensionSlotCounter);
            Check(
                "dimension drop entry requires a dimension dungeon and matched monster",
                dimensionMonsterDrops.Count == 2
                && resolved.CombinedItems.Contains(
                    (int)dimensionMonsterDrops[0].TemplateId)
                && dimensionMonsterDrops[1].TemplateId
                    == DimensionDropSystem.FragmentItemId,
                ref failures);

            var ordinarySlotCounter = (ushort)0;
            var ordinaryMonsterDrops = DimensionDropSystem.GenerateMonsterDrops(
                dungeonId: 1,
                monsterCode: 61340,
                characterJob: 0,
                growType: 0x11,
                lcg: new DnfLcg(6),
                slotCounter: ref ordinarySlotCounter);
            Check(
                "dimension drop entry does not run outside dimension dungeons",
                ordinaryMonsterDrops.Count == 0
                && ordinarySlotCounter == 0,
                ref failures);
        }
    }
}
