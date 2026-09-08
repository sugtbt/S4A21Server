using DfoServer.Game.Characters;
using System.Collections.Generic;

namespace DfoServer.Game.Mercenary
{
    // 冒险团支援兵名单：wire 低字节是账号角色表下标，不是持久化槽位。
    internal static class StrikerSupportRoster
    {
        internal static CharacterRecord FindByWireIndex(
            IReadOnlyList<CharacterRecord> roster,
            byte wireIndex)
        {
            if (roster == null || wireIndex >= roster.Count)
                return null;

            var candidate = roster[wireIndex];
            if (candidate == null || candidate.CharacterId <= 0)
                return null;

            return candidate;
        }

        internal static bool IsEligibleSupport(CharacterRecord candidate, int activeCharacterId)
        {
            return candidate != null
                && candidate.CharacterId > 0
                && candidate.CharacterId <= ushort.MaxValue
                && candidate.CharacterId != activeCharacterId
                && candidate.Level >= StrikerSkillDataProvider.GetMinimumSupportLevel()
                // 游戏内提示: 支援兵需 Lv50+ 且完成第一次觉醒。
                // grow_type 高四位记录觉醒段 (0x10=一觉, 0x20=二觉)。
                && (candidate.GrowType & 0xF0) >= 0x10;
        }

        // 城镇点当前角色自己 = 取消支援。
        internal static bool IsTownClearSelection(CharacterRecord candidate, int activeCharacterId)
        {
            return candidate != null
                && candidate.CharacterId > 0
                && candidate.CharacterId == activeCharacterId;
        }
    }
}
