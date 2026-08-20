using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Mercenary
{
    // 读取支援角色当前技能树页。0x01E5/0x019F 用 Slot 当 wire combo；已学等级仍以本页为准。
    internal static class StrikerSupportSkillLevelSource
    {
        public static IReadOnlyList<SkillInfoEntrySnapshot> LoadActiveSkillPageEntries(
            int characterId,
            IGameDatabase database,
            byte? knownPageIndex = null,
            bool learnedOnly = true)
        {
            if (characterId <= 0)
                return Array.Empty<SkillInfoEntrySnapshot>();
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            try
            {
                var snapshot = new SqliteCharacterProgressRepository(database)
                    .LoadSkills(characterId);
                if (snapshot?.Pages == null || snapshot.Pages.Count == 0)
                    return Array.Empty<SkillInfoEntrySnapshot>();

                var stored = knownPageIndex
                    ?? new SqliteSubtype1Repository(database)
                        .LoadSkillTreeIndex(characterId)
                    ?? 0;
                byte pageIndex = 0;
                if (SkillTreeExpansionState.IsUnlocked(stored))
                    pageIndex = stored;
                if (pageIndex >= snapshot.Pages.Count)
                    pageIndex = 0;

                return snapshot.Pages[pageIndex].Entries
                    .Where(entry => entry != null
                        && entry.SkillId != 0
                        && (!learnedOnly || entry.Level > 0))
                    .OrderBy(entry => entry.Slot)
                    .ToList();
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[StrikerSupport] load active skill page failed cid={characterId}: {ex.Message}");
                return Array.Empty<SkillInfoEntrySnapshot>();
            }
        }
    }
}
