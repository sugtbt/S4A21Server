using System;
using DfoServer.Game.Inventory;
using PvfLib;

namespace DfoServer.SelfTests
{
    // expupbycrackofdimension 修复的聚焦自测。
    // 异次元裂缝经验与普通固定经验共用同一套角色经验数学核,
    // 修复后从"一律拒绝"变为"按 stk 文件中的数值作为固定经验"。
    // 纯内存解析, 不需要 PVF_ARCHIVE_PATH。
    public static class ExperienceItemDefinitionSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== EXPERIENCE_ITEM_DEFINITION selftest ===");
            var failures = 0;

            VerifyCrackOfDimensionSupported(ref failures);
            VerifyZeroValueRejected(ref failures);
            VerifyMissingValueRejected(ref failures);
            VerifyOrdinaryExpUpStillSupported(ref failures);

            Console.WriteLine(failures == 0
                ? "EXPERIENCE_ITEM_DEFINITION selftest passed"
                : $"EXPERIENCE_ITEM_DEFINITION selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyCrackOfDimensionSupported(ref int failures)
        {
            var stackable = StackableItemFile.Parse(@"
[name]
`异次元裂缝成长胶囊`
[explain]
`经验值增加 %s`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[minimum level]
55
[maximum level]
84
[increase status type]
`[expUpByCrackOfDimension]` 500000
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10100300, stackable);
            Check(
                "expupbycrackofdimension with positive value resolves as supported",
                definition.IsSupported,
                ref failures);
            Check(
                "expupbycrackofdimension grants fixed experience kind",
                definition.GrantKind == ExperienceItemGrantKind.Fixed,
                ref failures);
            Check(
                "expupbycrackofdimension reads the stk value as fixed amount",
                definition.Value == 500000,
                ref failures);
            Check(
                "expupbycrackofdimension fixed gain is returned by CalculateGain",
                definition.CalculateGain(70) == 500000,
                ref failures);
        }

        private static void VerifyZeroValueRejected(ref int failures)
        {
            var stackable = StackableItemFile.Parse(@"
[name]
`异次元裂缝成长胶囊`
[explain]
`经验值增加 %s`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[minimum level]
55
[maximum level]
84
[increase status type]
`[expUpByCrackOfDimension]` 0
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10100300, stackable);
            Check(
                "expupbycrackofdimension with zero value stays rejected",
                !definition.IsSupported,
                ref failures);
            Check(
                "zero-value reject reason is the fixed-experience value guard",
                definition.UnsupportedReason == "invalid fixed experience value",
                ref failures);
        }

        private static void VerifyMissingValueRejected(ref int failures)
        {
            var stackable = StackableItemFile.Parse(@"
[name]
`未填数值的成长胶囊`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[increase status type]
`[expUpByCrackOfDimension]`
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10146833, stackable);
            Check(
                "expupbycrackofdimension with no value stays rejected",
                !definition.IsSupported,
                ref failures);
            Check(
                "missing-value reject reason is the fixed-experience value guard",
                definition.UnsupportedReason == "invalid fixed experience value",
                ref failures);
        }

        private static void VerifyOrdinaryExpUpStillSupported(ref int failures)
        {
            var stackable = StackableItemFile.Parse(@"
[name]
`普通经验胶囊`
[stackable type]
`[etc]` 0
[usable job]
`[all]`
[/usable job]
[minimum level]
1
[maximum level]
70
[increase status type]
`[expUp]` 15000
[/increase status type]
");

            var definition = ExperienceItemDataProvider.Resolve(10089614, stackable);
            Check(
                "ordinary expUp item still resolves as fixed experience",
                definition.IsSupported
                && definition.GrantKind == ExperienceItemGrantKind.Fixed
                && definition.Value == 15000,
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
