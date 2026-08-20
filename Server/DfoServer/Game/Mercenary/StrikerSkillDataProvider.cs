using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Mercenary
{
    // 支援兵技能配置的一条记录。
    public sealed class StrikerSkillEntry
    {
        internal StrikerSkillEntry(int job, int growType, int skillIndex, int comboIndex, int requiredLevel)
        {
            Job = job;
            GrowType = growType;
            SkillIndex = skillIndex;
            ComboIndex = comboIndex;
            RequiredLevel = requiredLevel;
        }

        public int Job { get; }
        public int GrowType { get; }
        public int SkillIndex { get; }
        // PVF [striker skill] 第四字段：预览视频/脚本 ID，值常大于 255。
        // 不是 0x01E5 / 0x019F 的 wire combo；wire combo 来自技能树 Slot。
        public int ComboIndex { get; }
        public int RequiredLevel { get; }
    }

    public static class StrikerSkillDataProvider
    {
        private sealed class ProviderState
        {
            public ProviderState(
                IReadOnlyList<StrikerSkillEntry> entries,
                int minimumSupportLevel,
                int maxActiveSupportCount)
            {
                Entries = entries;
                MinimumSupportLevel = minimumSupportLevel;
                MaxActiveSupportCount = maxActiveSupportCount;
            }

            public IReadOnlyList<StrikerSkillEntry> Entries { get; }
            public int MinimumSupportLevel { get; }
            public int MaxActiveSupportCount { get; }
        }

        private static readonly Lazy<ProviderState> State = new Lazy<ProviderState>(LoadState);

        public static void Warmup()
        {
            _ = State.Value;
        }

        public static IReadOnlyList<StrikerSkillEntry> GetAvailableSkills(int job, int growType, int level)
        {
            var entries = State.Value.Entries;
            // 转职值可能带有打包信息，低四位对应支援兵配置。
            var normalizedGrowType = NormalizeGrowType(growType);
            var result = new List<StrikerSkillEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Job != job || entry.GrowType != normalizedGrowType)
                    continue;
                if (entry.RequiredLevel > level)
                    continue;

                result.Add(entry);
            }
            return result.AsReadOnly();
        }

        internal static IReadOnlyList<StrikerSkillEntry> GetAll()
        {
            return State.Value.Entries;
        }

        public static StrikerSkillEntry FindBySkill(int job, int growType, int skillIndex)
        {
            var entries = State.Value.Entries;
            var normalizedGrowType = NormalizeGrowType(growType);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Job != job || entry.GrowType != normalizedGrowType)
                    continue;
                if (entry.SkillIndex == skillIndex)
                    return entry;
            }

            return null;
        }

        public static int NormalizeGrowType(int growType)
        {
            return growType > 0x0F ? growType & 0x0F : growType;
        }

        public static int GetMinimumSupportLevel()
        {
            return State.Value.MinimumSupportLevel;
        }

        public static int GetMaxActiveSupportCount()
        {
            return State.Value.MaxActiveSupportCount;
        }

        private static ProviderState LoadState()
        {
            var strikerText = PvfArchiveAccessor.ReadText("etc/linksystem/striker.etc");
            var entries = Parse(strikerText);
            var maxActiveSupportCount = ParseFirstPositiveInt(
                ExtractSection(strikerText, "striker combo"));

            var linkText = PvfArchiveAccessor.ReadText("etc/characlinksystem.etc");
            var minimumSupportLevel = ParseLinkCharacterMinimumLevel(
                ExtractSection(linkText, "1st link character info"));
            if (minimumSupportLevel <= 0 || maxActiveSupportCount <= 0)
                throw new InvalidOperationException(
                    $"invalid linksystem rules: minimumLevel={minimumSupportLevel} maxActive={maxActiveSupportCount}");

            return new ProviderState(entries, minimumSupportLevel, maxActiveSupportCount);
        }

        private static int ParseFirstPositiveInt(string text)
        {
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"-?\d+"))
            {
                if (int.TryParse(match.Value, out var value) && value > 0)
                    return value;
            }
            return 0;
        }

        private static int ParseLinkCharacterMinimumLevel(string text)
        {
            var values = new List<int>();
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"-?\d+"))
            {
                if (int.TryParse(match.Value, out var value))
                    values.Add(value);
            }
            // [1st link character info] 为七元组，末字段是支援候选最低等级。
            return values.Count == 7 && values[6] > 0 ? values[6] : 0;
        }

        private static IReadOnlyList<StrikerSkillEntry> Parse(string text)
        {
            var section = ExtractSection(text, "striker skill");
            var tokens = Tokenize(section);
            if (tokens.Count == 0)
                throw new InvalidOperationException("PVF striker skill section is empty");
            var entries = new List<StrikerSkillEntry>();

            int offset = 0;
            while (offset < tokens.Count)
            {
                var recordOffset = offset;
                if (!TryReadInt(tokens, ref offset, out var job)
                    || !TryReadInt(tokens, ref offset, out var growType)
                    || !TryReadInt(tokens, ref offset, out var skillIndex)
                    || !TryReadInt(tokens, ref offset, out var comboIndex)
                    || !TryReadInt(tokens, ref offset, out _)
                    || !TryReadString(tokens, ref offset, out _)
                    || !TryReadInt(tokens, ref offset, out var componentCount))
                {
                    throw new InvalidOperationException(
                        $"invalid striker skill record header at token {recordOffset}");
                }

                if (componentCount < 0 || componentCount > 128)
                    throw new InvalidOperationException(
                        $"invalid striker component count {componentCount} at token {recordOffset}");
                if (job < 0 || job > byte.MaxValue
                    || growType < 0 || growType > 0x0F
                    || skillIndex <= 0 || skillIndex > ushort.MaxValue
                    || comboIndex < 0 || comboIndex > ushort.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"out-of-range striker record at token {recordOffset}: job={job} grow={growType} skill={skillIndex} combo={comboIndex}");
                }

                for (int i = 0; i < componentCount; i++)
                {
                    if (!TryReadInt(tokens, ref offset, out _))
                        throw new InvalidOperationException(
                            $"truncated striker component list at token {recordOffset}");
                }

                var data = SkillDataProvider.GetSkill(job, skillIndex);
                if (data == null)
                    throw new InvalidOperationException(
                        $"striker PVF references missing static skill job={job} skill={skillIndex}");
                entries.Add(new StrikerSkillEntry(
                    job,
                    growType,
                    skillIndex,
                    comboIndex,
                    data.RequiredLevel));
            }

            var duplicate = entries
                .GroupBy(entry => (entry.Job, entry.GrowType, entry.SkillIndex))
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                throw new InvalidOperationException(
                    $"duplicate striker skill job={duplicate.Key.Job} grow={duplicate.Key.GrowType} skill={duplicate.Key.SkillIndex}");
            return entries.AsReadOnly();
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

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            if (string.IsNullOrWhiteSpace(text))
                return tokens;

            foreach (Match m in Regex.Matches(text, @"`([^`]*)`|[-]?\d+"))
            {
                if (m.Value.StartsWith("`", StringComparison.Ordinal))
                    tokens.Add(new Token { Text = m.Groups[1].Value, IsString = true });
                else if (int.TryParse(m.Value, out var value))
                    tokens.Add(new Token { Number = value });
            }

            return tokens;
        }

        private static bool TryReadInt(List<Token> tokens, ref int offset, out int value)
        {
            value = 0;
            if (offset >= tokens.Count || tokens[offset].IsString)
                return false;
            value = tokens[offset++].Number;
            return true;
        }

        private static bool TryReadString(List<Token> tokens, ref int offset, out string value)
        {
            value = null;
            if (offset >= tokens.Count || !tokens[offset].IsString)
                return false;
            value = tokens[offset++].Text;
            return true;
        }

        private struct Token
        {
            public bool IsString;
            public int Number;
            public string Text;
        }
    }
}
