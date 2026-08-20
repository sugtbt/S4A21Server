using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mercenary
{
    // 0x01E5 / 0x019F 技能表：支援角色当前技能树页。
    // wire combo 是 SkillInfo 槽位 Slot，不是 striker.etc 第四字段（预览视频 ID）。
    public static class StrikerSupportSkillListSource
    {
        public const byte MinimumDisplayedLevel = 1;

        public static IReadOnlyList<StrikerSupportSkillWireEntry> Load(
            int characterId,
            byte job,
            byte growType,
            byte level,
            IGameDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            var page = StrikerSupportSkillLevelSource.LoadActiveSkillPageEntries(
                characterId,
                database,
                knownPageIndex: null,
                learnedOnly: false);
            if (page.Count == 0)
                page = BuildFallbackSkillPage(job, growType, level);

            return FromSkillPage(page);
        }

        public static IReadOnlyList<StrikerSupportSkillWireEntry> FromSkillPage(
            IReadOnlyList<SkillInfoEntrySnapshot> page)
        {
            if (page == null || page.Count == 0)
                return Array.Empty<StrikerSupportSkillWireEntry>();

            var result = new List<StrikerSupportSkillWireEntry>(page.Count);
            for (var i = 0; i < page.Count; i++)
            {
                var entry = page[i];
                if (entry == null || entry.SkillId == 0)
                    continue;

                result.Add(new StrikerSupportSkillWireEntry(
                    entry.Slot,
                    entry.SkillId,
                    entry.Level > 0 ? entry.Level : MinimumDisplayedLevel));
            }

            if (result.Count > byte.MaxValue)
            {
                throw new InvalidOperationException(
                    $"support skill page exceeds u8 count: {result.Count}");
            }

            return result;
        }

        private static IReadOnlyList<SkillInfoEntrySnapshot> BuildFallbackSkillPage(
            byte job,
            byte growType,
            byte level)
        {
            try
            {
                CharacterStatComputer.DecodeGrowType(growType, out var firstGrow, out var secondGrow);
                var snapshot = CharacterSkillProfile.BuildSnapshot(job, firstGrow, secondGrow, level);
                if (snapshot?.Pages == null || snapshot.Pages.Count == 0 || snapshot.Pages[0].Entries == null)
                    return Array.Empty<SkillInfoEntrySnapshot>();

                return snapshot.Pages[0].Entries;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[StrikerSupport] fallback skill page failed job={job} grow={growType}: {ex.Message}");
                return Array.Empty<SkillInfoEntrySnapshot>();
            }
        }
    }
}
