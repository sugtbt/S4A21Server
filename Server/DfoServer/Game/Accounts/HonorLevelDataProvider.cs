using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Accounts
{
    public sealed class HonorLevelSummary
    {
        public ulong TotalHonorExp { get; set; }
        public uint HonorExp { get; set; }
        public byte HonorLevel { get; set; }
        public byte HonorGrade { get; set; }
        public int FullLevelCharacterCount { get; set; }
    }

    public static class HonorLevelDataProvider
    {
        private static readonly object Sync = new object();
        private static HonorLevelTables _tables;

        public static void Warmup()
        {
            EnsureLoaded();
        }

        public static HonorLevelSummary CalculateFromHonorExp(ulong honorExp, IEnumerable<CharacterRecord> characters)
        {
            var fullLevelCount = 0;
            if (characters != null)
            {
                foreach (var character in characters)
                {
                    if (character == null || character.Deleted)
                        continue;
                    if (character.Level >= ExpTableProvider.MaxLevel)
                        fullLevelCount++;
                }
            }

            return CalculateFromHonorExp(honorExp, fullLevelCount);
        }

        public static HonorLevelSummary CalculateFromHonorExp(ulong totalHonorExp, int fullLevelCount)
        {
            EnsureLoaded();
            var cappedTotalExp = Math.Min(totalHonorExp, MaxTotalHonorExp);
            var resolved = ResolveHonorProgress(cappedTotalExp);
            return new HonorLevelSummary
            {
                TotalHonorExp = cappedTotalExp,
                HonorExp = resolved.CurrentLevelExp,
                HonorLevel = (byte)Math.Max(0, Math.Min(byte.MaxValue, resolved.Level)),
                HonorGrade = ResolveHonorGrade(resolved.Level),
                FullLevelCharacterCount = fullLevelCount,
            };
        }

        // HONOR_LEVEL_INFO (0x0289) is the honor-level-up banner.
        // USERINFO subtype0/1 still carry account honor for every character,
        // including non-max-level alts that show honor UI and honor effects.
        public static bool ShouldSendHonorLevelUpBanner(int characterLevel)
        {
            return characterLevel >= ExpTableProvider.MaxLevel;
        }

        public static uint CalculateHonorExpGain(byte previousLevel, uint previousExp, uint gainedExp)
        {
            if (gainedExp == 0)
                return 0;

            if (previousLevel >= ExpTableProvider.MaxLevel)
                return gainedExp;

            var maxLevelEntryExp = (uint)Math.Max(0, ExpTableProvider.GetLevelThreshold(ExpTableProvider.MaxLevel - 1));
            var newExp = previousExp > uint.MaxValue - gainedExp ? uint.MaxValue : previousExp + gainedExp;
            if (newExp <= maxLevelEntryExp)
                return 0;

            return newExp - Math.Max(previousExp, maxLevelEntryExp);
        }

        public static void ApplyToUserInfoAddition(UserInfoAdditionSnapshot addition, HonorLevelSummary summary)
        {
            if (addition == null || summary == null)
                return;

            addition.Progress1 = summary.HonorLevel;
            addition.Progress2 = summary.HonorExp;
        }

        public static void ApplyToSubtype0Tail(UserInfoMinimumTailSnapshot tail, HonorLevelSummary summary)
        {
            if (tail == null || summary == null)
                return;

            tail.ProgressA = summary.HonorLevel;
            tail.ProgressB = summary.HonorExp;
        }

        public static void ApplyToCharacterRecord(CharacterRecord record, HonorLevelSummary summary)
        {
            if (record == null || summary == null)
                return;

            if (record.Subtype0Tail == null)
                record.Subtype0Tail = new UserInfoMinimumTailSnapshot();

            ApplyToSubtype0Tail(record.Subtype0Tail, summary);
        }

        public static int MaxExpOnMaxLevel
        {
            get
            {
                EnsureLoaded();
                return _tables.MaxExpOnMaxLevel;
            }
        }

        public static ulong MaxTotalHonorExp
        {
            get
            {
                EnsureLoaded();
                ulong total = 0;
                foreach (var pair in _tables.LevelExpByLevel)
                {
                    if (pair.Key <= 1)
                        continue;
                    total += (ulong)Math.Max(0, pair.Value);
                }
                total += (ulong)Math.Max(0, _tables.MaxExpOnMaxLevel);
                return total;
            }
        }

        public static ulong GetTotalExpAtLevelStart(int level)
        {
            EnsureLoaded();
            if (level <= 1)
                return 0;
            ulong total = 0;
            foreach (var pair in _tables.LevelExpByLevel)
            {
                if (pair.Key <= 1)
                    continue;
                if (pair.Key >= level)
                    break;
                total += (ulong)Math.Max(0, pair.Value);
            }
            return total;
        }

        public static uint GetRequiredExpForLevelUpTo(int level)
        {
            EnsureLoaded();
            return (uint)Math.Max(0, GetLevelSegmentRequirement(level));
        }

        private static HonorProgress ResolveHonorProgress(ulong totalHonorExp)
        {
            var level = 1;
            ulong remaining = totalHonorExp;
            foreach (var pair in _tables.LevelExpByLevel)
            {
                var nextLevel = pair.Key;
                if (nextLevel <= 1)
                    continue;

                var required = (ulong)Math.Max(0, pair.Value);
                if (remaining < required)
                    break;

                remaining -= required;
                level = nextLevel;
            }

            var currentCap = level >= _tables.MaxLevel
                ? (uint)Math.Max(0, _tables.MaxExpOnMaxLevel)
                : (uint)Math.Max(0, GetNextLevelRequirement(level));
            var currentLevelExp = (uint)Math.Min(remaining, currentCap);
            return new HonorProgress(level, currentLevelExp);
        }

        private static int GetNextLevelRequirement(int currentLevel)
        {
            return GetLevelSegmentRequirement(currentLevel + 1);
        }

        private static int GetLevelSegmentRequirement(int level)
        {
            foreach (var pair in _tables.LevelExpByLevel)
                if (pair.Key == level)
                    return pair.Value;
            return 0;
        }

        private static byte ResolveHonorGrade(int honorLevel)
        {
            if (honorLevel <= 0)
                return 0;
            return _tables.GradeByLevel.TryGetValue(honorLevel, out var grade)
                ? (byte)Math.Max(0, Math.Min(byte.MaxValue, grade))
                : (byte)0;
        }

        private static void EnsureLoaded()
        {
            if (_tables != null)
                return;

            lock (Sync)
            {
                if (_tables != null)
                    return;

                _tables = Parse(PvfArchiveAccessor.ReadText("etc/honorlevel.etc"));
            }
        }

        private static HonorLevelTables Parse(string text)
        {
            var tables = new HonorLevelTables();
            foreach (Match section in Regex.Matches(text ?? string.Empty, @"\[grade\](.*?)\[/grade\]", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                ParseGradeSection(section.Groups[1].Value, tables);

            var maxExpSection = ExtractSection(text, "maxexp on maxlevel");
            var maxNumbers = ParseInts(maxExpSection);
            tables.MaxExpOnMaxLevel = maxNumbers.Count > 0 ? Math.Max(0, maxNumbers[0]) : 0;
            tables.LevelExpByLevel.Sort((a, b) => a.Key.CompareTo(b.Key));
            if (tables.MaxExpOnMaxLevel <= 0 && tables.LevelExpByLevel.Count > 0)
                tables.MaxExpOnMaxLevel = tables.LevelExpByLevel[tables.LevelExpByLevel.Count - 1].Value;
            if (tables.LevelExpByLevel.Count > 0)
                tables.MaxLevel = tables.LevelExpByLevel[tables.LevelExpByLevel.Count - 1].Key;
            return tables;
        }

        private static void ParseGradeSection(string section, HonorLevelTables tables)
        {
            var withoutStrings = Regex.Replace(section ?? string.Empty, @"`[^`]*`", " ");
            var numbers = ParseInts(withoutStrings);
            if (numbers.Count < 5)
                return;

            var grade = numbers[0];
            for (var i = 3; i + 1 < numbers.Count; i += 2)
            {
                var level = numbers[i];
                var requiredExp = numbers[i + 1];
                if (level <= 0 || requiredExp < 0)
                    continue;

                tables.LevelExpByLevel.Add(new KeyValuePair<int, int>(level, requiredExp));
                tables.GradeByLevel[level] = grade;
            }
        }

        private static List<int> ParseInts(string text)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (Match match in Regex.Matches(text, @"[-]?\d+"))
            {
                if (long.TryParse(match.Value, out var value))
                    result.Add(value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value);
            }

            return result;
        }

        private static string ExtractSection(string text, string tag)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var startTag = "[" + tag + "]";
            var endTag = "[/" + tag + "]";
            var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;
            start += startTag.Length;

            var end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = text.Length;

            return text.Substring(start, end - start);
        }

        private readonly struct HonorProgress
        {
            public HonorProgress(int level, uint currentLevelExp)
            {
                Level = level;
                CurrentLevelExp = currentLevelExp;
            }

            public int Level { get; }
            public uint CurrentLevelExp { get; }
        }

        private sealed class HonorLevelTables
        {
            public List<KeyValuePair<int, int>> LevelExpByLevel { get; } = new List<KeyValuePair<int, int>>();
            public Dictionary<int, int> GradeByLevel { get; } = new Dictionary<int, int>();
            public int MaxExpOnMaxLevel { get; set; }
            public int MaxLevel { get; set; }
        }
    }
}


