using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.PcRoomTimePoint
{
    internal sealed class PcRoomTimePointConfigProvider
    {
        private static readonly Lazy<PcRoomTimePointConfig> SharedConfig =
            new Lazy<PcRoomTimePointConfig>(LoadShared);

        internal static PcRoomTimePointConfigProvider Instance { get; } =
            new PcRoomTimePointConfigProvider();

        private PcRoomTimePointConfigProvider()
        {
        }

        internal PcRoomTimePointConfig Current => SharedConfig.Value;

        internal void Warmup()
        {
            _ = Current;
        }

        private static PcRoomTimePointConfig LoadShared()
        {
            try
            {
                var loaded = PcRoomTimePointConfigParser.Parse(
                    PvfArchiveAccessor.ReadText(PcRoomTimePointConfig.PvfPath));
                FileLogger.Log(
                    "[PcRoomTimePoint] loaded "
                    + $"{PcRoomTimePointConfig.PvfPath} "
                    + $"daily={loaded.DailyRewards.Count} "
                    + $"period={loaded.PeriodRewards.Count}");
                return loaded;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[PcRoomTimePoint] failed to load PVF config, using fallback: "
                    + ex.Message);
                return PcRoomTimePointConfigParser.CreateFallback();
            }
        }
    }

    internal static class PcRoomTimePointConfigParser
    {
        internal static PcRoomTimePointConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException(
                    "pcroomtimepoint.etc is empty.",
                    nameof(text));

            var config = new PcRoomTimePointConfig
            {
                DailyRewardAutoGet = ReadSingleBool(text, "daily reward autoget"),
                DailyRewardLoop = ReadSingleInt(text, "daily reward loop", 1),
                PeriodRewardLoop = ReadSingleInt(text, "period reward loop", 4),
                DailyRewards = ParseDailyRewards(ReadClosedBlock(text, "daily reward items")),
                PeriodRewards = ParsePeriodRewards(ReadClosedBlock(text, "period reward item")),
            };
            Validate(config);
            return config;
        }

        internal static PcRoomTimePointConfig CreateFallback()
            => Parse(@"
[daily reward autoget]
`false`
[daily reward loop]
1
[period reward loop]
4
[daily reward items]
1 490003510 1 1800000 `true` `true`
1 490003662 1 1800000 `true` `true`
1 490003514 1 3600000 `true` `true`
1 490003512 1 3600000 `true` `true`
[/daily reward items]
[period reward item]
1 490003515 1 5 `true`
2 490003516 1 5 `true`
3 490003517 1 5 `true`
4 490003518 1 5 `true`
[/period reward item]");

        private static IReadOnlyList<PcRoomTimePointRewardStage> ParseDailyRewards(
            string block)
        {
            var rewards = new List<PcRoomTimePointRewardStage>();
            long cumulative = 0;
            var values = ReadNumericValues(block);
            for (var offset = 0; offset + 3 < values.Count; offset += 4)
            {
                cumulative += Math.Max(0, values[offset + 3]);
                rewards.Add(new PcRoomTimePointRewardStage
                {
                    StageIndex = rewards.Count + 1,
                    ItemId = values[offset + 1],
                    ItemCount = values[offset + 2],
                    DurationMillis = values[offset + 3],
                    CumulativeRequiredMillis = cumulative,
                });
            }

            return rewards;
        }

        private static IReadOnlyList<PcRoomTimePointRewardStage> ParsePeriodRewards(
            string block)
        {
            var rewards = new List<PcRoomTimePointRewardStage>();
            var cumulative = 0;
            var values = ReadNumericValues(block);
            for (var offset = 0; offset + 3 < values.Count; offset += 4)
            {
                cumulative += Math.Max(0, values[offset + 3]);
                rewards.Add(new PcRoomTimePointRewardStage
                {
                    StageIndex = values[offset],
                    ItemId = values[offset + 1],
                    ItemCount = values[offset + 2],
                    CumulativeRequiredCount = cumulative,
                });
            }

            return rewards
                .OrderBy(stage => stage.StageIndex)
                .ToList();
        }

        private static IReadOnlyList<int> ReadNumericValues(string block)
        {
            if (string.IsNullOrWhiteSpace(block))
                return Array.Empty<int>();

            var values = new List<int>();
            foreach (var rawLine in block.Split(new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var line = StripComment(rawLine);
                foreach (Match match in Regex.Matches(line, @"-?\d+"))
                    values.Add(int.Parse(match.Value));
            }

            return values;
        }

        private static string ReadClosedBlock(string text, string tag)
        {
            var match = Regex.Match(
                text,
                @"\[" + Regex.Escape(tag) + @"\](?<body>.*?)\[/"
                + Regex.Escape(tag) + @"\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups["body"].Value : string.Empty;
        }

        private static int ReadSingleInt(string text, string tag, int fallback)
        {
            var match = Regex.Match(
                text,
                @"\[" + Regex.Escape(tag) + @"\](?<body>.*?)(\[[^\]]+\]|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
                return fallback;

            var number = Regex.Match(StripComment(match.Groups["body"].Value), @"-?\d+");
            return number.Success ? int.Parse(number.Value) : fallback;
        }

        private static bool ReadSingleBool(string text, string tag)
        {
            var match = Regex.Match(
                text,
                @"\[" + Regex.Escape(tag) + @"\](?<body>.*?)(\[[^\]]+\]|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
                return false;

            var body = match.Groups["body"].Value;
            return body.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string StripComment(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var index = line.IndexOf('#');
            return index >= 0 ? line.Substring(0, index) : line;
        }

        private static void Validate(PcRoomTimePointConfig config)
        {
            if (config.DailyRewards.Count != 4
                || config.PeriodRewards.Count != 4
                || config.TotalDailyRequiredMillis <= 0)
            {
                throw new FormatException(
                    "pcroomtimepoint.etc must define four daily and four period rewards.");
            }

            foreach (var reward in config.DailyRewards)
            {
                if (reward.StageIndex <= 0
                    || reward.ItemId <= 0
                    || reward.ItemCount <= 0
                    || reward.DurationMillis <= 0
                    || reward.CumulativeRequiredMillis <= 0)
                {
                    throw new FormatException(
                        "pcroomtimepoint.etc contains an invalid daily reward.");
                }
            }

            foreach (var reward in config.PeriodRewards)
            {
                if (reward.StageIndex <= 0
                    || reward.ItemId <= 0
                    || reward.ItemCount <= 0
                    || reward.CumulativeRequiredCount <= 0)
                {
                    throw new FormatException(
                        "pcroomtimepoint.etc contains an invalid period reward.");
                }
            }
        }
    }
}
