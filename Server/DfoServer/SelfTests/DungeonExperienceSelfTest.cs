using System;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    // 快捷栏纹章 [exp advantage] 杀怪经验加成 + 秘药/纹章的 0x0023 槽位投影。
    // 纹章扫描用注入解析器, 不加载 PVF。
    public static class DungeonExperienceSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_EXPERIENCE selftest ===");
            var failures = 0;

            VerifyParticipantRuntimeEquipmentBonus(ref failures);
            VerifyCharmExpAdvantageScan(ref failures);
            VerifyClearRewardSlotProjection(ref failures);

            Console.WriteLine(failures == 0
                ? "DUNGEON_EXPERIENCE selftest passed"
                : $"DUNGEON_EXPERIENCE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyParticipantRuntimeEquipmentBonus(ref int failures)
        {
            var participantRuntime = new DungeonParticipantExperienceRuntime();
            participantRuntime.RecordMonster(
                2469,
                493,
                isBoss: false,
                isChampion: true,
                isSuperChampion: false,
                isNamedMonster: false);
            participantRuntime.RecordMonster(
                4939,
                987,
                isBoss: false,
                isChampion: true,
                isSuperChampion: false,
                isNamedMonster: false,
                equipmentBonusExperience: 60);
            var participantSnapshot = participantRuntime.Capture();
            Check(
                "participant runtime separates base, bonus, total, and type projection",
                participantSnapshot.MonsterBaseExperience == 7408
                    && participantSnapshot.MonsterGrowthContractBonusExperience == 1480
                    && participantSnapshot.MonsterEquipmentBonusExperience == 60
                    && participantSnapshot.MonsterTotalExperience == 8948
                    && participantSnapshot.ChampionBaseExperience == 7408,
                ref failures);

            // 进位零头(decimal): 逐只 floor 会在小基数下吞掉全部加成,
            // 零头带进下一只, 整局合计 ≈ 基础总额 × 百分比。
            var carryRuntime = new DungeonParticipantExperienceRuntime();
            var firstGrant = carryRuntime.ApplyEquipmentBonusRate(3, 30);
            var secondGrant = carryRuntime.ApplyEquipmentBonusRate(3, 30);
            uint carryTotal = firstGrant + secondGrant;
            for (var k = 2; k < 10; k++)
                carryTotal += carryRuntime.ApplyEquipmentBonusRate(3, 30);
            Check(
                "equipment bonus carry keeps tiny bases from flooring to zero",
                firstGrant == 0 && secondGrant == 1 && carryTotal == 9,
                ref failures);
            Check(
                "equipment bonus carry ignores zero base and zero rate",
                carryRuntime.ApplyEquipmentBonusRate(0, 30) == 0
                    && new DungeonParticipantExperienceRuntime()
                        .ApplyEquipmentBonusRate(100, 0) == 0,
                ref failures);
            Check(
                "equipment bonus saturates at uint max",
                new DungeonParticipantExperienceRuntime()
                    .ApplyEquipmentBonusRate(uint.MaxValue, 100) == uint.MaxValue,
                ref failures);
        }

        // 快捷栏区间与 CharmExpAdvantageService 一致: Main 背包槽位 3-8。
        private static void VerifyCharmExpAdvantageScan(ref int failures)
        {
            const int CharmItemId = 400360007;
            EquipmentType TypeOf(int itemId)
                => itemId == CharmItemId ? EquipmentType.Charm : EquipmentType.Unknown;
            int AdvantageOf(int itemId)
                => itemId == CharmItemId ? 30 : 0;

            var inventory = new InventoryService(
                characterId: 990882,
                accountId: 990882);
            inventory.SetItem(
                InventoryListType.Main,
                slotIndex: 5,
                ItemCore.Create(ItemCore.KindEquipment, itemId: CharmItemId));
            Check(
                "charm in a quick slot exposes its exp advantage percent",
                CharmExpAdvantageService.ScanQuickSlot(inventory, TypeOf, AdvantageOf) == 30,
                ref failures);

            var outsideQuickSlot = new InventoryService(
                characterId: 990883,
                accountId: 990883);
            outsideQuickSlot.SetItem(
                InventoryListType.Main,
                slotIndex: 12,
                ItemCore.Create(ItemCore.KindEquipment, itemId: CharmItemId));
            Check(
                "charm outside quick slots gives no bonus",
                CharmExpAdvantageService.ScanQuickSlot(outsideQuickSlot, TypeOf, AdvantageOf) == 0,
                ref failures);

            var noCharm = new InventoryService(
                characterId: 990884,
                accountId: 990884);
            noCharm.SetItem(
                InventoryListType.Main,
                slotIndex: 4,
                ItemCore.Create(ItemCore.KindConsumable, itemId: 1001));
            Check(
                "consumables in quick slots are ignored",
                CharmExpAdvantageService.ScanQuickSlot(noCharm, TypeOf, AdvantageOf) == 0,
                ref failures);

            Check(
                "charm exp bonus scales with the monster base (30%)",
                DungeonExperienceCalculator.FloorToUInt32(4939 * 0.30) == 1481,
                ref failures);
        }

        // 0x0023 主区块: 4u32 + 1u8 头之后每槽位一个 i32, 槽位 i 偏移 = 17 + i*4。
        private static void VerifyClearRewardSlotProjection(ref int failures)
        {
            const int MonsterGrowthBonus = 39484;
            const int MonsterEquipmentBonus = 59268;
            var body = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 78261,
                partyClearBreakdownExp: 39130,
                avatarExp: 3913,
                growthContractExp: 15652,
                monsterGrowthContractExp: MonsterGrowthBonus,
                experiencePotionExp: 7777,
                monsterEquipmentExp: MonsterEquipmentBonus);

            const int AvatarBonusOffset = 3 * sizeof(int);
            const int ExperiencePotionBonusOffset = 17 + 1 * sizeof(int);
            const int MonsterGrowthBonusOffset = 17 + 18 * sizeof(int);
            const int MonsterEquipmentBonusOffset = 17 + 20 * sizeof(int);
            Check(
                "0x0023 projects avatar bonus in slot four",
                BitConverter.ToInt32(body, AvatarBonusOffset) == 3913,
                ref failures);
            Check(
                "0x0023 projects experience potion bonus in slot two",
                BitConverter.ToInt32(body, ExperiencePotionBonusOffset) == 7777,
                ref failures);
            Check(
                "0x0023 projects monster growth contract bonus in slot 19",
                BitConverter.ToInt32(body, MonsterGrowthBonusOffset) == MonsterGrowthBonus,
                ref failures);
            Check(
                "0x0023 projects equipment(charm) bonus in slot 21",
                BitConverter.ToInt32(body, MonsterEquipmentBonusOffset) == MonsterEquipmentBonus,
                ref failures);
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine($"[FAIL] {name}");
        }
    }
}
