using DfoServer.Game.Inventory;
using System;

namespace DfoServer.SelfTests
{
    public static class LimitedCubeRuleSelfTest
    {
        public static int Run()
        {
            var rule = InventoryTitleChangeRule.CreateLimitedCube(
                new[] { 10008645, 10008646 },
                new[]
                {
                    new InventoryTitleChangeResultOption(10008645, 120, 1),
                    new InventoryTitleChangeResultOption(10008646, 120, 1),
                },
                Array.Empty<InventoryMaterialRequirement>());

            var selected = rule.TrySelectResult(
                10008645,
                _ => 0,
                out var result,
                out _);
            var passed = selected
                && result != null
                && result.ItemId == 10008646
                && result.ResultValue == 120;

            Console.WriteLine(passed
                ? "LIMITED_CUBE_RULE selftest passed"
                : "LIMITED_CUBE_RULE selftest failed: result selection mismatch");
            return passed ? 0 : 1;
        }
    }
}
