using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.ExpertJob
{
    internal readonly struct ExpertJobSkillGrant
    {
        internal ExpertJobSkillGrant(ushort skillId, byte level)
        {
            SkillId = skillId;
            Level = level;
        }

        internal ushort SkillId { get; }

        internal byte Level { get; }
    }

    internal sealed class ExpertJobGiveupConfig
    {
        internal byte ExpertJobType { get; set; }

        internal IReadOnlyList<ushort> ClearQuestIds { get; set; }

        internal IReadOnlyList<ushort> ConnectQuestIds { get; set; }

        internal IReadOnlyList<int> GiveupCosts { get; set; }

        internal IReadOnlyList<ushort> SkillIds { get; set; }

        internal IReadOnlyList<ExpertJobSkillGrant> SkillGrants { get; set; }

        internal int DeleteItemId { get; set; }

        internal bool TryResolveCost(int giveupCount, out int cost, out byte nextGiveupCount)
        {
            cost = 0;
            nextGiveupCount = 0;
            if (giveupCount < 0
                || GiveupCosts == null
                || giveupCount >= GiveupCosts.Count)
                return false;

            cost = GiveupCosts[giveupCount];
            nextGiveupCount = (byte)Math.Min(byte.MaxValue, Math.Min(
                GiveupCosts.Count - 1,
                giveupCount + 1));
            return cost >= 0;
        }
    }

    internal static class ExpertJobGiveupConfigProvider
    {
        private static readonly Dictionary<byte, string> Paths =
            new Dictionary<byte, string>
            {
                [ExpertJobStateCodec.EnchanterType] = "character/expertjob/enchanter.exj",
                [ExpertJobStateCodec.AlchemistType] = "character/expertjob/alchemist.exj",
                [ExpertJobStateCodec.DisjointerType] = "character/expertjob/disjointer.exj",
                [ExpertJobStateCodec.DollControllerType] = "character/expertjob/doll_controller.exj",
            };

        private static readonly Dictionary<byte, Lazy<ExpertJobGiveupConfig>> Configs =
            Paths.ToDictionary(
                pair => pair.Key,
                pair => new Lazy<ExpertJobGiveupConfig>(
                    () => Load(pair.Key, pair.Value)));

        internal static bool TryGet(
            int expertJobType,
            out ExpertJobGiveupConfig config)
        {
            config = null;
            if (expertJobType <= 0 || expertJobType > byte.MaxValue
                || !Configs.TryGetValue((byte)expertJobType, out var lazy))
                return false;

            try
            {
                config = lazy.Value;
                return config != null;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ExpertJobGiveupConfig] load failed type={expertJobType}: " +
                    ex.Message);
                return false;
            }
        }

        private static ExpertJobGiveupConfig Load(byte expertJobType, string jobPath)
        {
            var content = PvfArchiveAccessor.ReadText(jobPath);
            var root = new ScriptParser().Parse(content);

            var clearQuestIds = ParseQuestIds(
                ExpertJobPvfValueReader.ReadTokens(root, content, "clear quests"),
                jobPath,
                "clear quests");
            var connectQuestIds = ParseQuestIds(
                ExpertJobPvfValueReader.ReadTokens(root, content, "connect quest list"),
                jobPath,
                "connect quest list");
            var costs = ParsePositiveInts(
                ExpertJobPvfValueReader.ReadTokens(root, content, "giveup cost"),
                jobPath,
                "giveup cost");
            var skillTokens = ExpertJobPvfValueReader.ReadTokens(root, content, "skill");
            if (skillTokens.Length == 0 || skillTokens.Length % 2 != 0)
                throw new InvalidOperationException(
                    $"PVF {jobPath} [skill] row width is not 2");
            var skillIds = new List<ushort>();
            var skillGrants = new List<ExpertJobSkillGrant>();
            for (var index = 0; index < skillTokens.Length; index += 2)
            {
                var skillId = ExpertJobPvfValueReader.ParseInt(skillTokens[index]);
                if (skillId <= 0 || skillId > ushort.MaxValue)
                    throw new InvalidOperationException(
                        $"PVF {jobPath} has invalid skill id");
                var skillLevel = ExpertJobPvfValueReader.ParseInt(skillTokens[index + 1]);
                if (skillLevel < 0 || skillLevel > byte.MaxValue)
                    throw new InvalidOperationException(
                        $"PVF {jobPath} has invalid skill level");
                skillIds.Add((ushort)skillId);
                skillGrants.Add(new ExpertJobSkillGrant(
                    (ushort)skillId,
                    (byte)Math.Max(1, skillLevel)));
            }

            var deleteTokens = ExpertJobPvfValueReader.ReadTokens(
                root,
                content,
                "giveup delete item");
            if (deleteTokens.Length > 1)
                throw new InvalidOperationException(
                    $"PVF {jobPath} [giveup delete item] must contain at most one item");
            var deleteItemId = deleteTokens.Length == 0
                ? 0
                : ExpertJobPvfValueReader.ParseInt(deleteTokens[0]);
            if (deleteItemId < 0)
                throw new InvalidOperationException(
                    $"PVF {jobPath} has invalid giveup delete item");

            return new ExpertJobGiveupConfig
            {
                ExpertJobType = expertJobType,
                ClearQuestIds = clearQuestIds,
                ConnectQuestIds = connectQuestIds,
                GiveupCosts = costs,
                SkillIds = skillIds,
                SkillGrants = skillGrants,
                DeleteItemId = deleteItemId,
            };
        }

        private static List<ushort> ParseQuestIds(
            string[] tokens,
            string pvfPath,
            string tag)
        {
            if (tokens.Length == 0)
                throw new InvalidOperationException($"PVF {pvfPath} [{tag}] is empty");

            var result = new List<ushort>();
            foreach (var token in tokens)
            {
                var questId = ExpertJobPvfValueReader.ParseInt(token);
                if (questId <= 0 || questId > ushort.MaxValue || result.Contains((ushort)questId))
                    throw new InvalidOperationException(
                        $"PVF {pvfPath} has invalid {tag} quest id");
                result.Add((ushort)questId);
            }
            return result;
        }

        private static List<int> ParsePositiveInts(
            string[] tokens,
            string pvfPath,
            string tag)
        {
            if (tokens.Length == 0)
                throw new InvalidOperationException($"PVF {pvfPath} [{tag}] is empty");

            var result = new List<int>();
            foreach (var token in tokens)
            {
                var value = ExpertJobPvfValueReader.ParseInt(token);
                if (value < 0)
                    throw new InvalidOperationException(
                        $"PVF {pvfPath} has invalid {tag} value");
                result.Add(value);
            }
            return result;
        }
    }
}
