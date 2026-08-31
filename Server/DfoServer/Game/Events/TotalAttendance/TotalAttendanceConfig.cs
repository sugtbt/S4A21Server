using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.TotalAttendance
{
    internal sealed class TotalAttendanceConfigProvider
    {
        private static readonly Lazy<TotalAttendanceConfig> SharedConfig =
            new Lazy<TotalAttendanceConfig>(LoadShared);

        internal static TotalAttendanceConfigProvider Instance { get; } =
            new TotalAttendanceConfigProvider();

        private TotalAttendanceConfigProvider()
        {
        }

        internal TotalAttendanceConfig Current => SharedConfig.Value;

        internal void Warmup()
        {
            _ = Current;
        }

        private static TotalAttendanceConfig LoadShared()
        {
            try
            {
                var loaded = TotalAttendanceConfigParser.Parse(
                    PvfArchiveAccessor.ReadText(TotalAttendanceConfig.PvfPath));
                FileLogger.Log(
                    "[TotalAttendance] loaded "
                    + $"{TotalAttendanceConfig.PvfPath} "
                    + $"weeks={loaded.WeeklyRewards.Count} "
                    + $"total={loaded.TotalRewards.Count}");
                return loaded;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[TotalAttendance] failed to load PVF config, "
                    + "using fallback: " + ex.Message);
                return TotalAttendanceConfigParser.CreateFallback();
            }
        }
    }

    internal static class TotalAttendanceConfigParser
    {
        internal static TotalAttendanceConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException(
                    "chn_totalattendance.evt is empty.",
                    nameof(text));
            }

            var config = new TotalAttendanceConfig
            {
                EventDurationWeeks = ReadFirstNumber(
                    ReadBlock(text, "event duration"),
                    TotalAttendanceConfig.DefaultEventDurationWeeks),
                RecommendClearTarget = ReadFirstNumber(
                    ReadBlock(text, "attendance condition"),
                    TotalAttendanceConfig.DefaultRecommendClearTarget),
                WeeklyRewards = ParseRewards(
                    ReadBlock(text, "attendance week"),
                    weekReward: true),
                TotalRewards = ParseRewards(
                    ReadBlock(text, "total attendance week"),
                    weekReward: false),
            };
            Validate(config);
            return config;
        }

        internal static TotalAttendanceConfig CreateFallback()
        {
            return new TotalAttendanceConfig
            {
                EventDurationWeeks =
                    TotalAttendanceConfig.DefaultEventDurationWeeks,
                RecommendClearTarget =
                    TotalAttendanceConfig.DefaultRecommendClearTarget,
                WeeklyRewards = new[]
                {
                    Reward(0, 1, 490003187, 1),
                    Reward(1, 2, 490003188, 1),
                    Reward(2, 3, 490003189, 1),
                    Reward(3, 4, 490003196, 1),
                    Reward(4, 5, 490003190, 1),
                    Reward(5, 6, 490003191, 1),
                    Reward(6, 7, 490003192, 1),
                    Reward(7, 8, 490003197, 1),
                    Reward(8, 9, 490003193, 1),
                    Reward(9, 10, 490003194, 1),
                    Reward(10, 11, 490003195, 1),
                    Reward(11, 12, 490003198, 1),
                },
                TotalRewards = new[]
                {
                    Reward(0, 4, 490003219, 1),
                    Reward(1, 8, 490003220, 1),
                    Reward(2, 11, 490003221, 1),
                },
            };
        }

        private static TotalAttendanceReward Reward(
            int stageIndex,
            int requiredAttendanceCount,
            int itemId,
            int itemCount)
            => new TotalAttendanceReward
            {
                StageIndex = stageIndex,
                RequiredAttendanceCount = requiredAttendanceCount,
                ItemId = itemId,
                ItemCount = itemCount,
            };

        private static IReadOnlyList<TotalAttendanceReward> ParseRewards(
            string block,
            bool weekReward)
        {
            var values = ReadNumericValues(block);
            var rewards = new List<TotalAttendanceReward>();
            for (var offset = 0; offset + 2 < values.Count; offset += 3)
            {
                rewards.Add(new TotalAttendanceReward
                {
                    StageIndex = weekReward ? values[offset] - 1 : rewards.Count,
                    RequiredAttendanceCount = values[offset],
                    ItemId = values[offset + 1],
                    ItemCount = values[offset + 2],
                });
            }

            return rewards
                .OrderBy(reward => reward.RequiredAttendanceCount)
                .ToList();
        }

        private static int ReadFirstNumber(string block, int fallback)
        {
            var values = ReadNumericValues(block);
            return values.Count > 0 && values[0] > 0 ? values[0] : fallback;
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

        private static string ReadBlock(string text, string tag)
        {
            var lines = text.Split(new[] { "\r\n", "\n" },
                StringSplitOptions.None);
            var inBlock = false;
            var result = new List<string>();
            var openTag = "[" + tag + "]";
            var closeTag = "[/" + tag + "]";
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!inBlock)
                {
                    if (string.Equals(
                            trimmed,
                            openTag,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        inBlock = true;
                    }
                    continue;
                }

                if (string.Equals(
                        trimmed,
                        closeTag,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal)
                    && !trimmed.StartsWith("[/", StringComparison.Ordinal))
                {
                    break;
                }

                result.Add(line);
            }

            return string.Join(Environment.NewLine, result);
        }

        private static string StripComment(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var index = line.IndexOf('#');
            return index >= 0 ? line.Substring(0, index) : line;
        }

        private static void Validate(TotalAttendanceConfig config)
        {
            if (config.EventDurationWeeks <= 0
                || config.RecommendClearTarget <= 0
                || config.WeeklyRewards.Count != 12
                || config.TotalRewards.Count != 3)
            {
                throw new FormatException(
                    "chn_totalattendance.evt must define duration, "
                    + "12 weekly rewards and three total rewards.");
            }

            for (var index = 0; index < config.WeeklyRewards.Count; index++)
            {
                var reward = config.WeeklyRewards[index];
                if (reward.RequiredAttendanceCount != index + 1
                    || reward.StageIndex != index
                    || reward.ItemId <= 0
                    || reward.ItemCount <= 0)
                {
                    throw new FormatException(
                        "chn_totalattendance.evt contains an invalid weekly reward.");
                }
            }

            foreach (var reward in config.TotalRewards)
            {
                if (reward.StageIndex < 0
                    || reward.StageIndex >= 3
                    || reward.RequiredAttendanceCount <= 0
                    || reward.ItemId <= 0
                    || reward.ItemCount <= 0)
                {
                    throw new FormatException(
                        "chn_totalattendance.evt contains an invalid total reward.");
                }
            }
        }
    }
}
