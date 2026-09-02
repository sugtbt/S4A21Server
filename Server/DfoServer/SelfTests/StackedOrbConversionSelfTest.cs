using DfoServer.Game.Inventory;
using System;

namespace DfoServer.SelfTests
{
    public static class StackedOrbConversionSelfTest
    {
        public static int Run()
        {
            var limitedCube = new InventoryTitleChangeResolution { IsLimitedCube = true };
            var titleChange = new InventoryTitleChangeResolution { IsLimitedCube = false };
            var stackedOrb = ItemCore.Create(ItemCore.KindMaterial, 10008645);
            stackedOrb.Count = 2;
            var singleOrb = stackedOrb.Copy();
            singleOrb.Count = 1;
            var title = ItemCore.Create(ItemCore.KindEquipment, 100330789);
            title.Count = 2;

            var passed = InventoryTitleChangeService.ShouldSplitStackedTarget(limitedCube, stackedOrb)
                && !InventoryTitleChangeService.ShouldSplitStackedTarget(titleChange, title)
                && !InventoryTitleChangeService.ShouldSplitStackedTarget(limitedCube, singleOrb);

            Console.WriteLine(passed
                ? "STACKED_ORB_CONVERSION selftest passed"
                : "STACKED_ORB_CONVERSION selftest failed");
            return passed ? 0 : 1;
        }
    }
}
