using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.DailyAttendanceAnytime
{
    internal sealed class DailyAttendanceAnytimeConfigProvider
    {
        private static readonly Lazy<DailyAttendanceAnytimeConfig> SharedConfig =
            new Lazy<DailyAttendanceAnytimeConfig>(LoadShared);

        internal static DailyAttendanceAnytimeConfigProvider Instance { get; } =
            new DailyAttendanceAnytimeConfigProvider();

        private DailyAttendanceAnytimeConfigProvider()
        {
        }

        internal DailyAttendanceAnytimeConfig Current => SharedConfig.Value;

        internal void Warmup()
        {
            _ = Current;
        }

        private static DailyAttendanceAnytimeConfig LoadShared()
        {
            try
            {
                var loaded = DailyAttendanceAnytimeConfigParser.Parse(
                    PvfArchiveAccessor.ReadText(
                        DailyAttendanceAnytimeConfig.PvfPath));
                FileLogger.Log(
                    "[DailyAttendanceAnytime] loaded "
                    + $"{DailyAttendanceAnytimeConfig.PvfPath} "
                    + $"daily={loaded.DailyRewards.Count} "
                    + $"accumulate={loaded.AccumulateRewards.Count}");
                return loaded;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[DailyAttendanceAnytime] failed to load PVF config, "
                    + "using fallback: " + ex.Message);
                return DailyAttendanceAnytimeConfigParser.CreateFallback();
            }
        }
    }

    internal static class DailyAttendanceAnytimeConfigParser
    {
        internal static DailyAttendanceAnytimeConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException(
                    "chn_dailyattendanceanytimeevent.evt is empty.",
                    nameof(text));
            }

            var config = new DailyAttendanceAnytimeConfig
            {
                DailyRewards = ParseDailyRewards(
                    ReadClosedBlock(text, "daily attendance reward")),
                AccumulateRewards = ParseAccumulateRewards(
                    ReadClosedBlock(text, "accumulate attendance reward")),
            };
            Validate(config);
            return config;
        }

        internal static DailyAttendanceAnytimeConfig CreateFallback()
        {
            var itemIds = new[]
            {
                490003342, 490003343, 490003344, 490003346,
                490003345, 490003348, 490003347,
            };
            var daily = new List<DailyAttendanceAnytimeReward>();
            for (var index = 0; index < 28; index++)
            {
                daily.Add(new DailyAttendanceAnytimeReward
                {
                    StageIndex = index,
                    DayIndex = index,
                    ItemId = itemIds[index % itemIds.Length],
                    ItemCount = 1,
                });
            }

            return new DailyAttendanceAnytimeConfig
            {
                DailyRewards = daily,
                AccumulateRewards = new[]
                {
                    new DailyAttendanceAnytimeReward
                    {
                        StageIndex = 0,
                        RequiredAttendanceCount = 5,
                        ItemId = 490003353,
                        ItemCount = 1,
                    },
                    new DailyAttendanceAnytimeReward
                    {
                        StageIndex = 1,
                        RequiredAttendanceCount = 15,
                        ItemId = 490003354,
                        ItemCount = 1,
                    },
                    new DailyAttendanceAnytimeReward
                    {
                        StageIndex = 2,
                        RequiredAttendanceCount = 20,
                        ItemId = 490003355,
                        ItemCount = 1,
                    },
                },
            };
        }

        private static IReadOnlyList<DailyAttendanceAnytimeReward>
            ParseDailyRewards(string block)
        {
            var rewards = new List<DailyAttendanceAnytimeReward>();
            var values = ReadNumericValues(block);
            for (var offset = 0; offset + 2 < values.Count; offset += 3)
            {
                rewards.Add(new DailyAttendanceAnytimeReward
                {
                    StageIndex = values[offset],
                    DayIndex = values[offset],
                    ItemId = values[offset + 1],
                    ItemCount = values[offset + 2],
                });
            }

            return rewards
                .OrderBy(reward => reward.DayIndex)
                .ToList();
        }

        private static IReadOnlyList<DailyAttendanceAnytimeReward>
            ParseAccumulateRewards(string block)
        {
            var rewards = new List<DailyAttendanceAnytimeReward>();
            var values = ReadNumericValues(block);
            for (var offset = 0; offset + 2 < values.Count; offset += 3)
            {
                rewards.Add(new DailyAttendanceAnytimeReward
                {
                    StageIndex = rewards.Count,
                    RequiredAttendanceCount = values[offset],
                    ItemId = values[offset + 1],
                    ItemCount = values[offset + 2],
                });
            }

            return rewards
                .OrderBy(reward => reward.StageIndex)
                .ToList();
        }

        private static IReadOnlyList<int> ReadNumericValues(string block)
        {
            if (string.IsNullOrWhiteSpace(block))
                return Array.Empty<int>();

            var values = new List<int>();
            foreach (var rawLine in block.Split(
                         new[] { '\r', '\n' },
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

        private static string StripComment(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var index = line.IndexOf('#');
            return index >= 0 ? line.Substring(0, index) : line;
        }

        private static void Validate(DailyAttendanceAnytimeConfig config)
        {
            if (config.DailyRewards.Count != 28
                || config.AccumulateRewards.Count != 3)
            {
                throw new FormatException(
                    "chn_dailyattendanceanytimeevent.evt must define 28 daily "
                    + "rewards and three accumulate rewards.");
            }

            for (var index = 0; index < config.DailyRewards.Count; index++)
            {
                var reward = config.DailyRewards[index];
                if (reward.DayIndex != index
                    || reward.ItemId <= 0
                    || reward.ItemCount <= 0)
                {
                    throw new FormatException(
                        "chn_dailyattendanceanytimeevent.evt contains an "
                        + "invalid daily reward.");
                }
            }

            foreach (var reward in config.AccumulateRewards)
            {
                if (reward.StageIndex < 0
                    || reward.StageIndex >= 3
                    || reward.RequiredAttendanceCount <= 0
                    || reward.ItemId <= 0
                    || reward.ItemCount <= 0)
                {
                    throw new FormatException(
                        "chn_dailyattendanceanytimeevent.evt contains an "
                        + "invalid accumulate reward.");
                }
            }
        }
    }
}
