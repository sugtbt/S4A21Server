using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.Joust
{
    internal sealed class JoustConfig
    {
        internal const ushort EventId = 2365;
        internal const string PvfPath = "event/chn_event/chn_joust.evt";

        public int MinLevel { get; set; }

        public int MaxBetting { get; set; }

        public int RewardItemId { get; set; }

        public int BettingRewardItemId { get; set; }

        public IReadOnlyList<int> MaterialItemIds { get; set; } =
            Array.Empty<int>();

        public IReadOnlyList<JoustKnightDefinition> Knights { get; set; } =
            Array.Empty<JoustKnightDefinition>();

        public JoustKnightDefinition GetKnight(int knightIndex)
        {
            return Knights.FirstOrDefault(knight => knight.Index == knightIndex);
        }

        public static JoustConfig Load()
        {
            return Parse(PvfArchiveAccessor.ReadText(PvfPath));
        }

        internal static JoustConfig Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("chn_joust.evt is empty.", nameof(text));

            var config = new JoustConfig
            {
                MinLevel = ReadSingleInt(text, "min level"),
                MaxBetting = ReadSingleInt(text, "max betting"),
                RewardItemId = ReadSingleInt(text, "reward"),
                BettingRewardItemId = ReadSingleInt(text, "betting reward"),
                MaterialItemIds = ReadIntBlock(text, "material"),
                Knights = ParseKnights(text),
            };

            Validate(config);
            return config;
        }

        internal static JoustConfig CreateFallback()
        {
            var text = @"
[min level]
17
[max betting]
1000
[reward]
490002916
[betting reward]
490002925
[material]
490002916 490700609
[/material]
[knight info]
[knight]
[index]
0
[attack type]
1
[knight name]
`爱德华`
[/knight]
[knight]
[index]
1
[attack type]
1
[knight name]
`理查德`
[/knight]
[knight]
[index]
2
[attack type]
0
[knight name]
`罗兰`
[/knight]
[knight]
[index]
3
[attack type]
0
[knight name]
`贝奥武夫`
[/knight]
[knight]
[index]
4
[attack type]
1
[knight name]
`莱奥`
[/knight]
[knight]
[index]
5
[attack type]
27
[knight name]
`伊萨尔`
[/knight]
[knight]
[index]
6
[attack type]
27
[knight name]
`吉利特`
[/knight]
[knight]
[index]
7
[attack type]
0
[knight name]
`席恩`
[/knight]
[knight]
[index]
8
[attack type]
1
[knight name]
`湖上骑士兰斯洛特`
[/knight]
[knight]
[index]
9
[attack type]
0
[knight name]
`机动队长苏雷德`
[/knight]
[knight]
[index]
10
[attack type]
28
[knight name]
`骷髅骑士`
[/knight]
[knight]
[index]
11
[attack type]
28
[knight name]
`无头骑士`
[/knight]
[/knight info]";
            return Parse(text);
        }

        private static IReadOnlyList<JoustKnightDefinition> ParseKnights(string text)
        {
            var knights = new List<JoustKnightDefinition>();
            foreach (Match match in Regex.Matches(
                         text,
                         @"\[knight\](?<body>.*?)\[/knight\]",
                         RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var body = match.Groups["body"].Value;
                knights.Add(new JoustKnightDefinition
                {
                    Index = ReadSingleInt(body, "index"),
                    AttackType = ReadSingleInt(body, "attack type"),
                    Name = ReadName(body, "knight name"),
                    WinTable = ReadIntBlock(body, "win"),
                    LossTable = ReadIntBlock(body, "loss"),
                });
            }

            return knights
                .OrderBy(knight => knight.Index)
                .ToList();
        }

        private static int ReadSingleInt(string text, string tag)
        {
            var values = ReadIntBlock(text, tag);
            if (values.Count == 0)
                throw new FormatException($"Missing [{tag}] in {PvfPath}.");
            return values[0];
        }

        private static IReadOnlyList<int> ReadIntBlock(string text, string tag)
        {
            var pattern = @"\[" + Regex.Escape(tag) + @"\](?<body>.*?)(\[/"
                + Regex.Escape(tag) + @"\]|\[[^\]]+\]|$)";
            var match = Regex.Match(
                text,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
                return Array.Empty<int>();

            return Regex.Matches(match.Groups["body"].Value, @"-?\d+")
                .Cast<Match>()
                .Select(value => int.Parse(value.Value))
                .ToList();
        }

        private static string ReadName(string text, string tag)
        {
            var match = Regex.Match(
                text,
                @"\[" + Regex.Escape(tag) + @"\]\s*`(?<name>[^`]*)`",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups["name"].Value : string.Empty;
        }

        private static void Validate(JoustConfig config)
        {
            if (config.MinLevel <= 0
                || config.MaxBetting <= 0
                || config.RewardItemId <= 0
                || config.BettingRewardItemId <= 0
                || config.MaterialItemIds.Count == 0
                || config.Knights.Count < 12)
            {
                throw new FormatException("chn_joust.evt has incomplete joust config.");
            }

            for (var index = 0; index < 12; index++)
            {
                if (config.GetKnight(index) == null)
                    throw new FormatException(
                        $"chn_joust.evt missing knight index {index}.");
            }
        }
    }

    internal sealed class JoustKnightDefinition
    {
        public int Index { get; set; }

        public int AttackType { get; set; }

        public string Name { get; set; } = string.Empty;

        public IReadOnlyList<int> WinTable { get; set; } = Array.Empty<int>();

        public IReadOnlyList<int> LossTable { get; set; } = Array.Empty<int>();
    }

    internal sealed class JoustConfigProvider
    {
        private static readonly Lazy<JoustConfig> SharedConfig =
            new Lazy<JoustConfig>(LoadShared);

        internal static JoustConfigProvider Instance { get; } =
            new JoustConfigProvider();

        private JoustConfigProvider()
        {
        }

        internal JoustConfig Current => SharedConfig.Value;

        internal void Warmup()
        {
            _ = Current;
        }

        private static JoustConfig LoadShared()
        {
            try
            {
                var loaded = JoustConfig.Load();
                FileLogger.Log(
                    $"[Joust] loaded {JoustConfig.PvfPath} knights={loaded.Knights.Count}");
                return loaded;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Joust] failed to load PVF config, using fallback: {ex.Message}");
                return JoustConfig.CreateFallback();
            }
        }
    }
}
