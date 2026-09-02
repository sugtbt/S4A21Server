using System;

namespace DfoServer.GameWorld
{
    internal readonly struct AnotherAradSelection
    {
        internal AnotherAradSelection(
            int wrapperDungeonId,
            int historicalDungeonId,
            int crackQuestId,
            AnotherAradQuestDefinition questDefinition)
        {
            WrapperDungeonId = wrapperDungeonId;
            HistoricalDungeonId = historicalDungeonId;
            CrackQuestId = crackQuestId;
            QuestDefinition = questDefinition;
        }

        internal int WrapperDungeonId { get; }
        internal int HistoricalDungeonId { get; }
        internal int CrackQuestId { get; }
        internal AnotherAradQuestDefinition QuestDefinition { get; }
    }

    internal static class AnotherAradSelectionResolver
    {
        private static readonly Lazy<int> WrapperDungeonId =
            new Lazy<int>(FindWrapperDungeonId);

        internal static bool TryResolve(
            int historicalDungeonId,
            int crackQuestId,
            out AnotherAradSelection selection,
            out string reason)
        {
            selection = default;
            reason = string.Empty;

            if (historicalDungeonId <= 0 || historicalDungeonId > ushort.MaxValue)
            {
                reason = "historical_dungeon_out_of_range";
                return false;
            }
            if (crackQuestId <= 0 || crackQuestId > ushort.MaxValue)
            {
                reason = "crack_quest_out_of_range";
                return false;
            }

            try
            {
                if (!TryFindWrapperDungeonId(
                        out var wrapperDungeonId,
                        out var wrapper))
                {
                    reason = "wrapper_pvf_missing";
                    return false;
                }
                if (historicalDungeonId == wrapperDungeonId)
                {
                    reason = "historical_dungeon_is_wrapper";
                    return false;
                }

                var quest = QuestData.GetQuestFile(crackQuestId);
                if (quest == null)
                {
                    reason = "crack_quest_missing";
                    return false;
                }

                var rewardType = QuestData.NormalizeQuestTag(quest.RewardType);
                if (!string.Equals(
                        rewardType,
                        "crack of dimension",
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason = "quest_not_crack_of_dimension";
                    return false;
                }
                if (!QuestCatalog.TryGetPath(
                        crackQuestId,
                        out var questPath)
                    || !IsCrackQuestPath(questPath))
                {
                    reason = "quest_not_crack_of_dimension_path";
                    return false;
                }

                if (!AnotherAradConfigCatalog.MatchesQuestDungeon(
                        crackQuestId,
                        historicalDungeonId))
                {
                    reason = "crack_pair_mismatch";
                    return false;
                }

                AnotherAradQuestDefinition.TryCreate(
                    crackQuestId,
                    historicalDungeonId,
                    out var questDefinition,
                    out var questDefinitionReason);

                var historical = Dungeon.LoadDungeonFileWithPath(historicalDungeonId).File;
                if (historical?.Mazes == null || historical.Mazes.Count == 0)
                {
                    reason = "historical_dungeon_has_no_maze";
                    return false;
                }

                selection = new AnotherAradSelection(
                    wrapperDungeonId,
                    historicalDungeonId,
                    crackQuestId,
                    questDefinition);
                reason = questDefinition != null
                    ? "ok"
                    : "ok_unmodeled_mission:" + questDefinitionReason;
                return true;
            }
            catch (Exception ex)
            {
                reason = "pvf_validation_error:" + ex.GetType().Name;
                return false;
            }
        }

        internal static int ResolveMaximumDifficulty(int dungeonId)
        {
            var count = Dungeon.GetMaxDifficultyCount(dungeonId);
            return count > 0
                ? Math.Max(0, Math.Min(byte.MaxValue, count - 1))
                : 4;
        }

        private static bool TryFindWrapperDungeonId(
            out int wrapperDungeonId,
            out PvfLib.DungeonFile wrapper)
        {
            wrapperDungeonId = WrapperDungeonId.Value;
            wrapper = null;
            if (wrapperDungeonId <= 0)
                return false;

            try
            {
                wrapper = Dungeon.LoadDungeonFileWithPath(wrapperDungeonId).File;
                return wrapper != null && wrapper.CrackOfDimensionDungeon;
            }
            catch
            {
                wrapper = null;
                return false;
            }
        }

        private static int FindWrapperDungeonId()
        {
            try
            {
                foreach (var entry in DungeonCatalog.LoadDungeonList().Entries)
                {
                    if (entry == null || entry.Id <= 0)
                        continue;

                    try
                    {
                        var file = Dungeon.LoadDungeonFileWithPath(entry.Id).File;
                        if (file != null && file.CrackOfDimensionDungeon)
                            return entry.Id;
                    }
                    catch
                    {
                        // A malformed unrelated DGN must not hide the wrapper.
                    }
                }
            }
            catch
            {
                // Keep validation fail-closed when the PVF index is unavailable.
            }

            return 0;
        }

        private static bool IsCrackQuestPath(string path)
        {
            var normalized = (path ?? string.Empty)
                .Replace('\\', '/')
                .TrimStart('/')
                .ToLowerInvariant();
            return normalized.StartsWith(
                "n_quest/crackofdimension/",
                StringComparison.Ordinal)
                || normalized.StartsWith(
                    "n_quest/crack_of_dimension/",
                    StringComparison.Ordinal);
        }
    }
}
