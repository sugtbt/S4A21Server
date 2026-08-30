using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Quests
{
    internal static class QuestAssistantDropPolicy
    {
        // 旧版服务说明为任务栏物品“获得速度加快 50%-100%”。服务端先按
        // 可验证的下限实现 +50% 期望数量：每两个基础掉落固定追加一个，
        // 奇数余项再以 50% 概率追加一个；不越过任务所需/最大持有上限。
        internal const int BonusPercent = 50;

        internal static int ApplyBonus(
            QuestDropCandidate candidate,
            int currentHeld,
            int baseCount,
            Func<int> rollPercent = null)
        {
            if (baseCount <= 0)
                return 0;

            var bonus = baseCount / 2;
            if ((baseCount & 1) != 0
                && (rollPercent ?? (() => ServerRandom.Next(100)))()
                    < BonusPercent)
            {
                bonus++;
            }

            var total = baseCount > int.MaxValue - bonus
                ? int.MaxValue
                : baseCount + bonus;
            var limit = QuestDropProvider.GetEffectiveHeldLimit(candidate);
            if (limit >= 0)
                total = Math.Min(total, Math.Max(0, limit - currentHeld));
            return Math.Max(0, total);
        }
    }
}
