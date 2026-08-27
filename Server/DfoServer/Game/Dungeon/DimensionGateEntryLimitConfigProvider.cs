using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DimensionGateEntryLimitConfig
    {
        internal DimensionGateEntryLimitConfig(
            int dailyDefaultEnterCount,
            int dailyDefaultExtraEnterCount)
        {
            DailyDefaultEnterCount = Math.Max(0, dailyDefaultEnterCount);
            DailyDefaultExtraEnterCount = Math.Max(
                0,
                dailyDefaultExtraEnterCount);
        }

        internal int DailyDefaultEnterCount { get; }

        internal int DailyDefaultExtraEnterCount { get; }
    }

    internal static class DimensionGateEntryLimitConfigProvider
    {
        private const string ConfigPath = "etc/dimensiongate.etc";
        private const int FallbackDefaultEnterCount = 5;
        private const int FallbackDefaultExtraEnterCount = 0;

        private static readonly Lazy<DimensionGateEntryLimitConfig> Current =
            new Lazy<DimensionGateEntryLimitConfig>(Load);

        internal static DimensionGateEntryLimitConfig Get()
            => Current.Value;

        internal static DimensionGateEntryLimitConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return CreateFallback();

            var root = new ScriptParser().Parse(text);
            return new DimensionGateEntryLimitConfig(
                ReadSingleInt(
                    root,
                    text,
                    "daily default enter count",
                    FallbackDefaultEnterCount),
                ReadSingleInt(
                    root,
                    text,
                    "daily default extra enter count",
                    FallbackDefaultExtraEnterCount));
        }

        private static DimensionGateEntryLimitConfig Load()
        {
            try
            {
                return Parse(PvfArchiveAccessor.ReadText(ConfigPath));
            }
            catch (Exception ex)
            {
                DfoServer.FileLogger.Log(
                    $"[DimensionGateEntryLimitConfig] load failed " +
                    $"path={ConfigPath}: {ex.Message}");
                return CreateFallback();
            }
        }

        private static DimensionGateEntryLimitConfig CreateFallback()
            => new DimensionGateEntryLimitConfig(
                FallbackDefaultEnterCount,
                FallbackDefaultExtraEnterCount);

        private static int ReadSingleInt(
            ScriptNode root,
            string content,
            string tag,
            int fallback)
        {
            var values = ReadInts(root?.GetChild(tag), content);
            return values.Count > 0 ? Math.Max(0, values[0]) : fallback;
        }

        private static List<int> ReadInts(ScriptNode node, string content)
        {
            var result = new List<int>();
            if (node?.DataItems == null)
                return result;

            foreach (var item in node.DataItems)
            {
                var raw = item.GetContent(content) ?? string.Empty;
                foreach (Match match in Regex.Matches(raw, @"-?\d+"))
                {
                    if (int.TryParse(match.Value, out var value))
                        result.Add(value);
                }
            }

            return result;
        }
    }
}
