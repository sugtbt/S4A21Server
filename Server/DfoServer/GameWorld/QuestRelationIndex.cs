using System;
using System.Collections.Generic;
using PvfLib;

namespace DfoServer.GameWorld
{
    internal static class QuestRelationIndex
    {
        private static readonly Lazy<Dictionary<int, int>> QuestionAnswerCounts =
            new Lazy<Dictionary<int, int>>(BuildQuestionAnswerCounts);
        private static readonly Lazy<Dictionary<int, int[]>> SuccessorQuestIds =
            new Lazy<Dictionary<int, int[]>>(BuildSuccessorQuestIds);

        internal static List<int> GetPreRequiredQuests(int questId)
        {
            var result = new List<int>();
            var definition = QuestPrerequisiteCatalog.Get(questId);
            if (definition == null || !definition.IsValid)
                return result;

            var seen = new HashSet<int>();
            foreach (var group in definition.CompletedQuestGroups)
            {
                foreach (var prerequisiteQuestId in group)
                {
                    if (seen.Add(prerequisiteQuestId))
                        result.Add(prerequisiteQuestId);
                }
            }
            return result;
        }

        internal static List<int> GetCollisionQuests(int questId)
        {
            var definition = QuestPrerequisiteCatalog.Get(questId);
            return definition != null && definition.IsValid
                ? new List<int>(definition.CollisionQuestIds)
                : new List<int>();
        }

        internal static bool IsQuestionQuest(int questId)
            => QuestData.NormalizeQuestTag(QuestCatalog.Get(questId)?.Type)
                == "question";

        internal static int GetQuestionAnswerCount(int questId)
            => QuestionAnswerCounts.Value.TryGetValue(questId, out var count)
                ? count
                : 0;

        internal static int GetRequiredQuestAnswerFlagValue(int answerIndex)
            => answerIndex >= 0 ? answerIndex + 1 : 0;

        internal static bool DoesClearedFlagMatchRequiredQuestAnswer(
            IReadOnlyDictionary<int, int> clearedFlags,
            int requiredQuestId,
            int requiredAnswerIndex)
        {
            if (requiredQuestId <= 0)
                return true;

            var requiredFlag = GetRequiredQuestAnswerFlagValue(
                requiredAnswerIndex);
            return requiredFlag > 0
                && clearedFlags != null
                && clearedFlags.TryGetValue(requiredQuestId, out var flagValue)
                && flagValue == requiredFlag;
        }

        internal static List<ushort> ComputeAcceptableQuests(
            int characterLevel,
            int characterJob,
            int growType,
            HashSet<int> clearedQuestIds,
            Dictionary<int, int> clearedFlags,
            ISet<int> allowedCreatureKinds)
        {
            var result = new List<ushort>();
            var prerequisiteState = new QuestPrerequisiteEvaluationState(
                clearedQuestIds,
                clearedFlags);
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                if (questId <= 0 || questId > 29999)
                    continue;

                var quest = QuestCatalog.Get(questId);
                if (quest == null
                    || ParseExposedValue(quest.ExposedByNpc) == 0
                    || quest.IsEvent)
                {
                    continue;
                }

                if (quest.CreatureKind >= 0
                    && (allowedCreatureKinds == null
                        || !allowedCreatureKinds.Contains(quest.CreatureKind)))
                {
                    continue;
                }

                if (quest.ExpertJobType >= 0 && quest.ExpertJobLevel >= 0)
                    continue;

                var grade = (quest.Grade ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                if (grade == "[training]"
                    && !QuestData.IsThereDailyTrainingQuestList(
                        characterLevel,
                        quest.NpcIndex))
                {
                    continue;
                }

                if (!IsSelectableGrade(grade))
                    continue;

                if (!MeetsCharacterRestrictions(
                        quest,
                        characterLevel,
                        characterJob,
                        growType))
                {
                    continue;
                }

                var repeatable = grade == "[daily]"
                    || grade == "[normaly repeat]"
                    || grade == "[special daily]";
                if (!repeatable && clearedQuestIds.Contains(questId))
                    continue;
                var prerequisiteDefinition = QuestPrerequisiteCatalog.Get(questId);
                if (prerequisiteDefinition == null
                    || !prerequisiteDefinition.Evaluate(
                        prerequisiteState).IsAllowed)
                {
                    continue;
                }

                result.Add((ushort)questId);
            }

            FileLogger.Log(
                $"[QuestRelationIndex] acceptable={result.Count} " +
                $"job={characterJob} lv={characterLevel} grow={growType}");
            return result;
        }

        internal static bool MeetsCharacterRestrictions(
            int questId,
            int characterLevel,
            int characterJob,
            int growType)
            => MeetsCharacterRestrictions(
                QuestCatalog.Get(questId),
                characterLevel,
                characterJob,
                growType);

        private static bool MeetsCharacterRestrictions(
            QuestFile quest,
            int characterLevel,
            int characterJob,
            int growType)
        {
            if (quest == null)
                return false;

            var targetCharacter = (quest.TargetCharacter ?? string.Empty)
                .Trim()
                .ToLowerInvariant();
            if (targetCharacter.Length > 0
                && !MatchesCharacterTag(targetCharacter, characterJob))
            {
                return false;
            }

            var minimumLevel = quest.Level != null && quest.Level.Length > 0
                ? quest.Level[0]
                : 1;
            var maximumLevel = quest.Level != null && quest.Level.Length > 1
                ? quest.Level[1]
                : 99;
            if (characterLevel < minimumLevel || characterLevel > maximumLevel)
                return false;

            var job = (quest.Job ?? string.Empty).Trim().ToLowerInvariant();
            if (job.Length > 0
                && job != "[all]"
                && !MatchesCharacterTag(job, characterJob))
            {
                return false;
            }

            var jobChangeQuest = quest.JobChangeQuestValue;
            var firstGrow = growType & 0xF;
            var secondGrow = (growType >> 4) & 0xF;
            if (jobChangeQuest == 1
                && QuestData.IsCareerTransferQuest(quest)
                && firstGrow != 0)
            {
                // A GM may change the persisted profession without clearing
                // the old final transfer quest. The quest is no longer a
                // valid stage once the character already has a first grow.
                return false;
            }

            if (jobChangeQuest == 2)
            {
                // First awakening is available only after transfer and before
                // the first awakening high nibble is recorded.
                if (firstGrow <= 0 || secondGrow != 0)
                    return false;
                if (quest.GrowType != -1 && quest.GrowType != firstGrow)
                    return false;
            }
            else if (jobChangeQuest == 3)
            {
                // Second awakening is available only after first awakening and
                // must disappear as soon as the second high-nibble stage is
                // persisted.
                if (firstGrow <= 0 || secondGrow != 1)
                    return false;
                if (quest.GrowType != -1 && quest.GrowType != firstGrow)
                    return false;
            }
            else if (quest.GrowType != -1
                && jobChangeQuest != 1
                && jobChangeQuest != 10
                && jobChangeQuest != 20
                && growType >= 0
                && quest.GrowType != growType)
            {
                return false;
            }

            return true;
        }

        internal static bool IsQuestClearQuest(int questId)
            => IsQuestClearQuest(QuestCatalog.Get(questId));

        internal static bool IsQuestClearQuest(QuestFile quest)
        {
            var tag = QuestData.NormalizeQuestTag(
                quest?.Type);
            return tag == "quest clear" || tag == "clear quest";
        }

        internal static List<int> GetQuestClearRequiredQuestIds(int questId)
        {
            var quest = QuestCatalog.Get(questId);
            if (!IsQuestClearQuest(questId) || quest == null)
                return new List<int>();

            var values = QuestData.ParseIntList(quest.IntData);
            values.RemoveAll(id => id <= 0);
            return values;
        }

        internal static List<QuestRewardItem> GetCarryForwardEventItems(
            int questId)
        {
            var eventItems = QuestData.GetEventItems(questId);
            if (eventItems.Count == 0)
                return new List<QuestRewardItem>();

            var eventItemIds = new HashSet<int>();
            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId > 0 && eventItem.Count > 0)
                    eventItemIds.Add(eventItem.ItemId);
            }
            if (eventItemIds.Count == 0)
                return new List<QuestRewardItem>();

            var carryForward = new Dictionary<int, int>();
            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;
                if (!HasDownstreamSeekingConsumer(
                        questId,
                        eventItem.ItemId))
                    continue;

                if (!carryForward.TryGetValue(
                        eventItem.ItemId,
                        out var currentCount)
                    || currentCount < eventItem.Count)
                {
                    carryForward[eventItem.ItemId] = eventItem.Count;
                }
            }

            var result = new List<QuestRewardItem>();
            foreach (var pair in carryForward)
            {
                result.Add(new QuestRewardItem
                {
                    ItemId = pair.Key,
                    Count = pair.Value,
                });
            }
            return result;
        }

        private static bool HasDownstreamSeekingConsumer(
            int questId,
            int itemId)
        {
            if (questId <= 0 || itemId <= 0)
                return false;

            var pending = new Queue<int>();
            var visited = new HashSet<int> { questId };
            EnqueueSuccessors(questId, pending);
            while (pending.Count > 0)
            {
                var nextQuestId = pending.Dequeue();
                if (!visited.Add(nextQuestId))
                    continue;

                if (ContainsItem(
                        QuestData.GetEventItems(nextQuestId),
                        itemId))
                {
                    continue;
                }
                if (ContainsItem(
                        QuestTargetIndex.GetSeekingConsumeItems(nextQuestId),
                        itemId))
                {
                    return true;
                }

                EnqueueSuccessors(nextQuestId, pending);
            }
            return false;
        }

        private static void EnqueueSuccessors(
            int questId,
            Queue<int> pending)
        {
            if (!SuccessorQuestIds.Value.TryGetValue(
                    questId,
                    out var successors))
            {
                return;
            }

            foreach (var successor in successors)
                pending.Enqueue(successor);
        }

        private static Dictionary<int, int[]> BuildSuccessorQuestIds()
        {
            var mutable = new Dictionary<int, List<int>>();
            foreach (var nextQuestId in QuestCatalog.OrderedIds)
            {
                var nextQuest = QuestCatalog.Get(nextQuestId);
                if (nextQuest == null)
                    continue;

                var definition = QuestPrerequisiteCatalog.Get(nextQuestId);
                if (definition == null || !definition.IsValid)
                    continue;

                foreach (var prerequisiteId in GetPreRequiredQuests(nextQuestId))
                {
                    if (prerequisiteId <= 0
                        || prerequisiteId == nextQuestId)
                    {
                        continue;
                    }
                    if (!mutable.TryGetValue(
                            prerequisiteId,
                            out var successors))
                    {
                        successors = new List<int>();
                        mutable[prerequisiteId] = successors;
                    }
                    if (!successors.Contains(nextQuestId))
                        successors.Add(nextQuestId);
                }
            }

            var result = new Dictionary<int, int[]>(mutable.Count);
            foreach (var pair in mutable)
                result[pair.Key] = pair.Value.ToArray();
            return result;
        }

        private static Dictionary<int, int> BuildQuestionAnswerCounts()
        {
            var result = new Dictionary<int, int>();
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                var quest = QuestCatalog.Get(questId);
                if (quest == null)
                    continue;

                var definition = QuestPrerequisiteCatalog.Get(questId);
                if (definition == null || !definition.IsValid)
                    continue;

                foreach (var requiredAnswer in definition.RequiredAnswers)
                {
                    var questionQuestId = requiredAnswer.QuestId;
                    var answerIndex = requiredAnswer.AnswerIndex;
                    if (questionQuestId <= 0 || answerIndex < 0)
                        continue;

                    var nextCount = answerIndex + 1;
                    if (!result.TryGetValue(
                            questionQuestId,
                            out var currentCount)
                        || nextCount > currentCount)
                    {
                        result[questionQuestId] = nextCount;
                    }
                }
            }

            FileLogger.Log(
                $"[QuestRelationIndex] question quests={result.Count}");
            return result;
        }

        private static bool IsSelectableGrade(string grade)
            => grade == string.Empty
                || grade == "[normal]"
                || grade == "[side]"
                || grade == "[sub]"
                || grade == "[epic]"
                || grade == "[training]"
                || grade == "[achievement]"
                || grade == "[daily]"
                || grade == "[daily random]"
                || grade == "[normaly repeat]"
                || grade == "[special daily]"
                || grade == "[common unique]"
                || grade == "[system]";

        private static int ParseExposedValue(string value)
            => int.TryParse((value ?? string.Empty).Trim(), out var parsed)
                ? parsed
                : -1;

        private static bool MatchesCharacterTag(
            string configuredTags,
            int characterJob)
        {
            if (characterJob < 0 || characterJob >= CharacterJobTags.Length)
                return false;

            var expectedTag = CanonicalizeCharacterTag(
                CharacterJobTags[characterJob]);
            if (expectedTag.Length == 0)
                return false;

            var value = configuredTags ?? string.Empty;
            var foundToken = false;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '[')
                    continue;

                var tokenEnd = value.IndexOf(']', index + 1);
                if (tokenEnd < 0)
                    break;

                foundToken = true;
                var token = CanonicalizeCharacterTag(
                    value.Substring(index, tokenEnd - index + 1));
                if (token == expectedTag)
                    return true;

                index = tokenEnd;
            }

            // QuestFile normally preserves bracketed PVF tokens. Keep a
            // strict fallback for hand-built tests or legacy definitions
            // that expose one unbracketed tag.
            return !foundToken
                && CanonicalizeCharacterTag(value) == expectedTag;
        }

        private static string CanonicalizeCharacterTag(string value)
        {
            var normalized = QuestData.NormalizeQuestTag(value);
            if (normalized.Length == 0)
                return string.Empty;

            var buffer = new char[normalized.Length];
            var length = 0;
            foreach (var character in normalized)
            {
                if (!char.IsLetterOrDigit(character))
                    continue;
                buffer[length++] = char.ToLowerInvariant(character);
            }
            return new string(buffer, 0, length);
        }

        private static bool ContainsItem(
            IReadOnlyCollection<QuestRewardItem> items,
            int itemId)
        {
            foreach (var item in items)
            {
                if (item.ItemId == itemId && item.Count > 0)
                    return true;
            }
            return false;
        }

        // A21 character.lst 的 job id 必须直接映射到 QST 使用的职业标签。
        // 暗黑武士/缔造者是外传职业，不是 AT 鬼剑士/男法师的别名。
        private static readonly string[] CharacterJobTags =
        {
            "[swordman]",
            "[fighter]",
            "[gunner]",
            "[mage]",
            "[priest]",
            "[at gunner]",
            "[thief]",
            "[at fighter]",
            "[at mage]",
            "[demonic swordman]",
            "[creator mage]",
            "[at swordman]",
            "[knight]",
            "[demonic lancer]",
        };
    }
}
