using System;
using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal static class QuestObjectiveEvaluator
    {
        internal static QuestProgressEvaluation Evaluate(
            ActiveQuest quest,
            QuestProgressApplicationRequest request,
            Func<ushort, int, int, bool> clearMapMatcher)
        {
            var current = new QuestTrigger(quest.TriggerValue);
            var result = new QuestProgressEvaluation
            {
                Trigger = current,
            };

            switch (request.Operation)
            {
                case QuestProgressOperation.ClientTrigger:
                {
                    var authority = QuestClientTriggerAuthority.Resolve(
                        quest.QuestId,
                        request.TriggerType,
                        request.Increment);
                    if (authority == QuestClientTriggerDisposition.Reject)
                        return result;
                    if (authority == QuestClientTriggerDisposition.Recompute)
                    {
                        var recomputed = EvaluateSeekingItems(
                            quest,
                            request,
                            current);
                        if (recomputed.Matched && recomputed.Changes.Count == 0)
                        {
                            recomputed.AddChange(
                                quest.QuestId,
                                current,
                                current);
                        }
                        return recomputed;
                    }
                    if (authority == QuestClientTriggerDisposition.EchoOnly)
                    {
                        result.Matched = true;
                        result.AddChange(quest.QuestId, current, current);
                        return result;
                    }

                    var next = QuestProgressReducer.ApplyClientMutation(
                        current,
                        request.TriggerType,
                        request.Increment);
                    result.Matched = true;
                    result.Trigger = next;
                    result.AddChange(quest.QuestId, current, next);
                    return result;
                }

                case QuestProgressOperation.HuntMonster:
                    return EvaluateHuntMonster(quest, request, current);

                case QuestProgressOperation.HuntEnemy:
                    return EvaluateHuntEnemy(quest, request, current);

                case QuestProgressOperation.ClearMap:
                case QuestProgressOperation.ClearDungeon:
                {
                    if (current.IsComplete)
                        return result;
                    var matcher = clearMapMatcher
                        ?? ((questId, dungeonId, mapId) =>
                            GameWorld.QuestData.MatchesClearMapTarget(
                                questId,
                                dungeonId,
                                mapId));
                    if (!matcher(quest.QuestId, request.DungeonId, request.MapId))
                        return result;

                    var complete = QuestProgressReducer.Complete(current);
                    result.Matched = true;
                    result.Trigger = complete;
                    result.AddChange(quest.QuestId, current, complete);
                    return result;
                }

                case QuestProgressOperation.SeekingItems:
                    return EvaluateSeekingItems(quest, request, current);

                default:
                    return result;
            }
        }

        private static QuestProgressEvaluation EvaluateHuntMonster(
            ActiveQuest quest,
            QuestProgressApplicationRequest request,
            QuestTrigger current)
        {
            var result = new QuestProgressEvaluation { Trigger = current };
            if (current.IsComplete)
                return result;

            var targets = GameWorld.QuestData.GetHuntMonsterTargets(quest.QuestId);
            foreach (var target in targets)
            {
                if (!GameWorld.QuestData.MatchesHuntMonsterTarget(
                        target,
                        request.DungeonId,
                        request.Difficulty,
                        request.MonsterCode,
                        request.MonsterType))
                {
                    continue;
                }

                result.Matched = true;
                var previous = result.Trigger;
                result.Trigger = QuestProgressReducer.DecrementChannel(
                    result.Trigger,
                    target.ChannelIndex);
                if (!result.Trigger.Equals(previous))
                    result.AddChange(quest.QuestId, previous, result.Trigger);
            }
            return result;
        }

        private static QuestProgressEvaluation EvaluateHuntEnemy(
            ActiveQuest quest,
            QuestProgressApplicationRequest request,
            QuestTrigger current)
        {
            var result = new QuestProgressEvaluation { Trigger = current };
            if (current.IsComplete)
                return result;

            var targets = GameWorld.QuestData.GetHuntEnemyTargets(quest.QuestId);
            foreach (var target in targets)
            {
                if (target.ProgressSource
                        != GameWorld.HuntEnemyProgressSource.Server
                    || !GameWorld.QuestData.MatchesHuntEnemyTarget(
                        target,
                        request.DungeonId,
                        request.Difficulty,
                        request.MonsterCode,
                        request.EnemyType))
                {
                    continue;
                }

                result.Matched = true;
                var previous = result.Trigger;
                result.Trigger = QuestProgressReducer.DecrementChannel(
                    result.Trigger,
                    target.ChannelIndex);
                if (!result.Trigger.Equals(previous))
                    result.AddChange(quest.QuestId, previous, result.Trigger);
            }
            return result;
        }

        private static QuestProgressEvaluation EvaluateSeekingItems(
            ActiveQuest quest,
            QuestProgressApplicationRequest request,
            QuestTrigger current)
        {
            var result = new QuestProgressEvaluation { Trigger = current };
            var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(quest.QuestId);
            if (seekItems.Count == 0)
                return result;

            var filter = request.ItemFilter == null
                ? null
                : new HashSet<int>(request.ItemFilter);
            var relevant = new List<GameWorld.QuestRewardItem>();
            var matchesFilter = filter == null || filter.Count == 0;
            foreach (var item in seekItems)
            {
                if (item.ItemId < 0 || item.Count <= 0)
                    continue;
                relevant.Add(item);
                if (!matchesFilter && filter.Contains(item.ItemId))
                    matchesFilter = true;
            }

            if (relevant.Count == 0 || !matchesFilter)
                return result;

            result.Matched = true;
            var next = QuestProgressReducer.ApplySeekingItems(
                current,
                relevant,
                itemId => request.HeldItemCounts != null
                    && request.HeldItemCounts.TryGetValue(itemId, out var held)
                        ? held
                        : 0);
            result.Trigger = next;
            if (!next.Equals(current))
                result.AddChange(quest.QuestId, current, next);
            return result;
        }
    }
}
