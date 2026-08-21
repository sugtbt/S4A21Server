using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.GameWorld
{
    internal static class QuestData
    {
        // PVF [slot expansion] 的 reward int data 是槽位位图索引：
        // 0=辅助装备，1=魔法石，对应 ex_equip_slot_stat 的 bit0/bit1。
        internal const int ChainTypeSlotExpansion =
            QuestRewardProjector.ChainTypeSlotExpansion;
        internal const int ChainTypeTitle = 5;

        private static readonly Lazy<Dictionary<int, HashSet<int>>> TrainingQuestNpcs = new Lazy<Dictionary<int, HashSet<int>>>(LoadTrainingQuestNpcs);

        internal static QuestFile GetQuestFile(int questId)
            => QuestCatalog.Get(questId);

        private static Dictionary<int, HashSet<int>> LoadTrainingQuestNpcs()
        {
            var result = new Dictionary<int, HashSet<int>>();
            try
            {
                var text = PvfArchiveAccessor.ReadText("n_Quest/TrainingQuest.lst");
                if (string.IsNullOrEmpty(text)) return result;

                int currentLevel = -1;
                foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Trim();
                    if (line == "[level]") { currentLevel = -1; continue; }
                    if (line == "[/level]") { currentLevel = -1; continue; }
                    if (line.StartsWith("[")) continue;
                    if (currentLevel < 0)
                    {
                        int lv;
                        if (int.TryParse(line, out lv) && lv >= 1 && lv <= 70)
                            currentLevel = lv;
                        continue;
                    }
                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var t in tokens)
                    {
                        int npc;
                        if (int.TryParse(t, out npc) && npc > 0)
                        {
                            HashSet<int> set;
                            if (!result.TryGetValue(currentLevel, out set))
                            {
                                set = new HashSet<int>();
                                result[currentLevel] = set;
                            }
                            set.Add(npc);
                        }
                    }
                }
                FileLogger.Log($"[QuestData] TrainingQuest: {result.Count} levels with NPC entries");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[QuestData] Failed to load TrainingQuest.lst: {ex.Message}");
            }
            return result;
        }

        internal static bool IsThereDailyTrainingQuestList(int level, int npcIndex)
        {
            if (level <= 0 || level > 70) return false;
            HashSet<int> npcs;
            if (!TrainingQuestNpcs.Value.TryGetValue(level, out npcs)) return false;
            return npcs.Contains(npcIndex);
        }

        public static bool IsRepeatableQuest(int questId)
        {
            var qst = GetQuestFile(questId);
            if (qst == null) return false;
            var grade = (qst.Grade ?? "").Trim().ToLowerInvariant();
            return grade == "[daily]" || grade == "[normaly repeat]" || grade == "[special daily]";
        }

        internal static bool TryResolveCompletionDefinition(
            int questId,
            out QuestCompletionDefinition definition,
            out string error)
        {
            definition = null;
            error = string.Empty;
            var quest = GetQuestFile(questId);
            if (quest == null)
            {
                error = "quest definition not found";
                return false;
            }

            if (!QuestRewardDefinition.TryCreate(
                    questId,
                    quest,
                    out var rewardDefinition,
                    out error))
            {
                return false;
            }

            return QuestCompletionDefinition.TryCreate(
                questId,
                NormalizeQuestTag(quest.Grade),
                NormalizeQuestTag(quest.Type),
                quest.IntData,
                IsRepeatableQuest(questId),
                rewardDefinition,
                out definition,
                out error);
        }

        internal static bool TryResolveRewardDefinition(
            int questId,
            out QuestRewardDefinition definition,
            out string error)
        {
            definition = null;
            error = string.Empty;
            var quest = GetQuestFile(questId);
            if (quest == null)
            {
                error = "quest definition not found";
                return false;
            }
            return QuestRewardDefinition.TryCreate(
                questId,
                quest,
                out definition,
                out error);
        }

        internal static bool IsDailyChallengeQuest(int questId)
        {
            if (!DailyChallengeData.IsConfiguredQuest(questId))
                return false;

            return NormalizeQuestTag(GetQuestFile(questId)?.Grade) == "challenge";
        }

        internal static bool TryGetSuitableDungeonClearChallengeRule(
            int questId,
            out int minimumDifficulty)
        {
            minimumDifficulty = -1;
            if (!IsDailyChallengeQuest(questId))
                return false;

            var quest = GetQuestFile(questId);
            if (quest == null
                || NormalizeQuestTag(quest.Type) != "condition under clear"
                || quest.SubType != 6)
            {
                return false;
            }

            var values = ParseIntList(quest.IntData);
            if (values.Count < 3 || values[0] != -3 || values[values.Count - 1] <= 0)
                return false;

            minimumDifficulty = values[1];
            return true;
        }

        internal static bool TryGetSuitableDungeonBossKillChallengeRule(
            int questId,
            out int minimumDifficulty,
            out int targetCount)
        {
            minimumDifficulty = -1;
            targetCount = 0;
            if (!IsDailyChallengeQuest(questId))
                return false;

            var quest = GetQuestFile(questId);
            if (quest == null
                || NormalizeQuestTag(quest.Type) != "hunt monster")
            {
                return false;
            }

            // The frozen A21 challenge catalog uses a four-int hunt target:
            //   dungeon=-3, minimumDifficulty, monster=-3, requiredCount
            // The first -3 scopes the event to a recommended-level dungeon;
            // the monster -3 is the existing "any boss" selector.
            var values = ParseIntList(quest.IntData);
            const int stride = 4;
            for (var offset = 0; offset + stride <= values.Count; offset += stride)
            {
                if (values[offset] != -3
                    || values[offset + 1] < -1
                    || values[offset + 2]
                        != HuntMonsterQuestTarget.AnyBossMonsterCode
                    || values[offset + 3] <= 0)
                {
                    continue;
                }

                minimumDifficulty = values[offset + 1];
                targetCount = values[offset + 3];
                return true;
            }

            return false;
        }

        internal static bool TryGetQuestCompletionChallengeRule(
            int questId,
            out int gradeSelector,
            out int targetCount)
        {
            gradeSelector = int.MinValue;
            targetCount = 0;
            if (!IsDailyChallengeQuest(questId))
                return false;

            var quest = GetQuestFile(questId);
            if (quest == null
                || NormalizeQuestTag(quest.Type) != "clear quest by grade")
            {
                return false;
            }

            var values = ParseIntList(quest.IntData);
            if (values.Count < 2 || values[1] <= 0)
                return false;

            gradeSelector = values[0];
            targetCount = values[1];
            return true;
        }

        internal static bool MatchesQuestGradeSelector(
            int gradeSelector,
            string normalizedGrade)
        {
            normalizedGrade = NormalizeQuestTag(normalizedGrade);
            if (normalizedGrade == "challenge")
                return false;
            if (gradeSelector < 0)
                return true;

            // Current A21 PVF uses selector 0 for challenges named
            // "完成...个主线". Mainline quests have grade [epic]. No other
            // non-negative selector exists in the frozen challenge catalog;
            // unknown future selectors stay fail-closed until evidenced.
            return gradeSelector == 0 && normalizedGrade == "epic";
        }

        public static bool IsTitleRewardQuest(int questId)
        {
            var qst = GetQuestFile(questId);
            if (qst == null) return false;
            return NormalizeQuestTag(qst.RewardType) == "title";
        }

        // The client rebuilds these native character effects from the completed
        // quest id and the QST's [special reward status] block.
        internal static bool HasSpecialRewardStatus(int questId)
        {
            if (questId <= 0)
                return false;

            var qst = GetQuestFile(questId);
            return qst != null && qst.HasTag("special reward status");
        }

        public static bool CanGiveup(int questId)
        {
            return CanGiveup(GetQuestFile(questId));
        }

        internal static bool CanGiveup(QuestFile quest)
        {
            if (quest == null || !quest.CantGiveup)
                return true;

            return quest.CantGiveupValue == 2
                && IsCareerTransferQuest(quest);
        }

        internal static bool IsCareerTransferQuest(QuestFile quest)
        {
            return quest != null
                && quest.JobChangeQuestValue == 1
                && NormalizeQuestTag(quest.RewardType) == "grow type";
        }

        public static List<int> GetPreRequiredQuests(int questId)
            => QuestRelationIndex.GetPreRequiredQuests(questId);

        internal static bool IsQuestionQuest(int questId)
            => QuestRelationIndex.IsQuestionQuest(questId);

        internal static int GetQuestionAnswerCount(int questId)
            => QuestRelationIndex.GetQuestionAnswerCount(questId);

        internal static int GetRequiredQuestAnswerFlagValue(int answerIndex)
            => QuestRelationIndex.GetRequiredQuestAnswerFlagValue(answerIndex);

        internal static bool DoesClearedFlagMatchRequiredQuestAnswer(
            Dictionary<int, int> clearedFlags,
            int requiredQuestId,
            int requiredAnswerIndex)
            => QuestRelationIndex.DoesClearedFlagMatchRequiredQuestAnswer(
                clearedFlags,
                requiredQuestId,
                requiredAnswerIndex);

        public static List<ushort> ComputeAcceptableQuests(int characterLevel, int characterJob, int growType, HashSet<int> clearedQuestIds, Dictionary<int, int> clearedFlags)
            => QuestRelationIndex.ComputeAcceptableQuests(
                characterLevel,
                characterJob,
                growType,
                clearedQuestIds,
                clearedFlags,
                allowedCreatureKinds: null);

        public static List<ushort> ComputeAcceptableQuests(
            int characterLevel,
            int characterJob,
            int growType,
            HashSet<int> clearedQuestIds,
            Dictionary<int, int> clearedFlags,
            ISet<int> allowedCreatureKinds)
            => QuestRelationIndex.ComputeAcceptableQuests(
                characterLevel,
                characterJob,
                growType,
                clearedQuestIds,
                clearedFlags,
                allowedCreatureKinds);


        public static List<int> GetCollisionQuests(int questId)
            => QuestRelationIndex.GetCollisionQuests(questId);

        internal static List<HuntMonsterQuestTarget> GetHuntMonsterTargets(
            int questId)
            => QuestTargetIndex.GetHuntMonsterTargets(questId);

        internal static List<HuntEnemyQuestTarget> GetHuntEnemyTargets(
            int questId)
            => QuestTargetIndex.GetHuntEnemyTargets(questId);

        internal static HuntEnemyProgressSource GetHuntEnemyProgressSource(
            int questId)
            => QuestTargetIndex.GetHuntEnemyProgressSource(questId);

        internal static bool IsServerDrivenHuntEnemyActorType(int enemyType)
            => QuestTargetIndex.IsServerDrivenHuntEnemyActorType(enemyType);

        internal static bool IsClientHuntEnemyTriggerAuthorized(
            int questId,
            byte triggerType)
            => QuestTargetIndex.IsClientHuntEnemyTriggerAuthorized(
                questId,
                triggerType);

        internal static List<DungeonQuestActorTarget>
            GetUnfinishedDungeonActorTargets(
                int questId,
                uint trigger,
                int dungeonId,
                int difficulty)
            => QuestTargetIndex.GetUnfinishedDungeonActorTargets(
                questId,
                trigger,
                dungeonId,
                difficulty);

        internal static bool TryGetNpcItemDropQuestTarget(
            int questId,
            int dungeonId,
            int difficulty,
            out DungeonNpcItemDropQuestTarget target)
            => QuestTargetIndex.TryGetNpcItemDropQuestTarget(
                questId,
                dungeonId,
                difficulty,
                out target);

        internal static bool MatchesHuntMonsterTarget(
            HuntMonsterQuestTarget target,
            int dungeonId,
            int difficulty,
            int monsterCode,
            byte monsterType = 0)
            => QuestTargetIndex.MatchesHuntMonsterTarget(
                target,
                dungeonId,
                difficulty,
                monsterCode,
                monsterType);

        internal static bool MatchesHuntEnemyTarget(
            HuntEnemyQuestTarget target,
            int dungeonId,
            int difficulty,
            int enemyCode,
            int enemyType)
            => QuestTargetIndex.MatchesHuntEnemyTarget(
                target,
                dungeonId,
                difficulty,
                enemyCode,
                enemyType);

        internal static bool ReferencesDungeon(int questId, int dungeonId)
            => QuestTargetIndex.ReferencesDungeon(questId, dungeonId);

        public static uint GetInitTrigger(int questId)
        {
            var qst = GetQuestFile(questId);
            return qst != null ? ComputeInitTrigger(qst) : 1;
        }

        internal static bool IsQuestClearQuest(int questId)
            => QuestRelationIndex.IsQuestClearQuest(questId);

        internal static List<int> GetQuestClearRequiredQuestIds(int questId)
            => QuestRelationIndex.GetQuestClearRequiredQuestIds(questId);

        internal static bool IsClearMapQuest(int questId)
            => QuestTargetIndex.IsClearMapQuest(questId);

        internal static bool MatchesClearMapTarget(int questId, int dungeonId, int mapId)
            => QuestTargetIndex.MatchesClearMapTarget(
                questId,
                dungeonId,
                mapId);

        internal static bool MatchesClearMapTarget(QuestFile qst, int dungeonId, int mapId)
            => QuestTargetIndex.MatchesClearMapTarget(qst, dungeonId, mapId);

        internal static bool MatchesClearMapTargetData(string intData, int dungeonId, int mapId)
            => QuestTargetIndex.MatchesClearMapTargetData(
                intData,
                dungeonId,
                mapId);

        internal static string NormalizeQuestTag(string value)
        {
            var tag = (value ?? "").Trim().ToLowerInvariant();
            if (tag.Length >= 2 && tag[0] == '[' && tag[tag.Length - 1] == ']')
                tag = tag.Substring(1, tag.Length - 2).Trim();
            return tag;
        }

        public static List<QuestRewardItem> GetEventItems(int questId)
        {
            var qst = GetQuestFile(questId);
            return qst != null ? ParseItemPairs(qst.DependGiveItem) : new List<QuestRewardItem>();
        }

        internal static IReadOnlyList<QuestRewardItem>
            GetCircleDungeonWorldmapRewardItems(
            int questId)
        {
            var qst = GetQuestFile(questId);
            if (qst == null
                || NormalizeQuestTag(qst.Grade) != "circle"
                || NormalizeQuestTag(qst.RewardType) != "circle dungeon")
            {
                return Array.Empty<QuestRewardItem>();
            }

            return CircleDungeonRewardCatalog.TryGetRewards(
                    questId,
                    out var rewards)
                ? rewards
                : Array.Empty<QuestRewardItem>();
        }

        public static List<QuestRewardItem> GetCarryForwardEventItems(int questId)
            => QuestRelationIndex.GetCarryForwardEventItems(questId);

        public static List<QuestRewardItem> GetSeekingConsumeItems(int questId)
            => QuestTargetIndex.GetSeekingConsumeItems(questId);


        internal static QuestRewardResolution ResolveReward(
            int questId,
            int rewardSelectIdx = -1,
            int playerLevel = 1,
            int playerJob = -1,
            int playerGrowType = -1)
            => ResolveReward(
                questId,
                rewardSelectIdx >= 0,
                rewardSelectIdx,
                playerLevel,
                playerJob,
                playerGrowType);

        internal static QuestRewardResolution ResolveReward(
            int questId,
            bool hasRewardSelection,
            int rewardSelectIdx,
            int playerLevel,
            int playerJob,
            int playerGrowType)
        {
            if (!TryResolveRewardDefinition(
                    questId,
                    out var definition,
                    out var error))
            {
                return QuestRewardResolution.Invalid(
                    QuestRewardProjector.CreateEmptyReward(),
                    error);
            }
            return QuestRewardProjector.Resolve(
                definition,
                hasRewardSelection,
                rewardSelectIdx,
                playerLevel,
                playerJob,
                playerGrowType);
        }

        public static QuestReward GetRewardExp(
            int questId,
            int rewardSelectIdx = -1,
            int playerLevel = 1,
            int playerJob = -1,
            int playerGrowType = -1)
            => ResolveReward(
                questId,
                rewardSelectIdx,
                playerLevel,
                playerJob,
                playerGrowType).Reward;

        internal static List<QuestRewardItem> ParseItemPairs(
            string data,
            int playerJob = -1,
            int playerGrowType = -1,
            bool preserveGoldMarker = false)
        {
            var result = new List<QuestRewardItem>();
            if (string.IsNullOrWhiteSpace(data)) return result;

            var tokens = data.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < tokens.Length)
            {
                int itemId;
                if (!int.TryParse(tokens[i], out itemId)) { i++; continue; }
                i++;

                if (i < tokens.Length && tokens[i].IndexOf("[job]", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    i++;
                    int jobId = -1;
                    int growType = -1;
                    int count = 1;
                    if (i < tokens.Length) { int.TryParse(tokens[i], out jobId); i++; }
                    if (i < tokens.Length) { int.TryParse(tokens[i], out growType); i++; }
                    if (i < tokens.Length) { int.TryParse(tokens[i], out count); i++; }

                    bool jobMatch = playerJob < 0 || jobId == playerJob;
                    bool growMatch = playerGrowType < 0 || growType == -1 || growType == (playerGrowType & 0xF);
                    if (itemId > 0 && jobMatch && growMatch)
                        result.Add(new QuestRewardItem { ItemId = itemId, Count = count });
                }
                else
                {
                    int count = 0;
                    if (i < tokens.Length) { int.TryParse(tokens[i], out count); i++; }
                    if (itemId > 0 || (preserveGoldMarker && itemId == 0))
                        result.Add(new QuestRewardItem { ItemId = itemId, Count = count });
                }
            }
            return result;
        }

        private static uint ComputeInitTrigger(QuestFile qst)
        {
            int typeCode = MapTypeString(qst.Type);
            string typeTag = NormalizeQuestTag(qst.Type);

            if (NormalizeQuestTag(qst.Grade) == "challenge"
                && TryComputeDailyChallengeInitTrigger(qst, typeTag, out var challengeTrigger))
            {
                return challengeTrigger;
            }

            if (IsSeekAndMeetNpcQuest(qst))
                return ComputeSeekAndMeetNpcInitTrigger(qst.IntData);

            if (QuestRelationIndex.IsQuestClearQuest(qst))
            {
                var requiredQuestIds = ParseIntList(qst.IntData);
                requiredQuestIds.RemoveAll(id => id <= 0);
                return requiredQuestIds.Count > 0 ? (uint)requiredQuestIds.Count : 1;
            }

            if (typeTag == "condition under clear" || typeTag == "clear map")
                return ComputeTriggerFromIntData(qst.IntData, 4);

            if (typeTag == "condition under clear2")
                return ComputeTriggerFromIntData(qst.IntData, 5);

            if (typeCode == 25)
                return PackTrigger(1, 1, 0);

            if (typeCode == 1)
            {
                if (typeTag == "hunt monster")
                    return ComputeTriggerFromIntData(qst.IntData, 4);

                if (typeTag == "hunt enemy")
                    return ComputeTriggerFromIntData(qst.IntData, 5);

                if (qst.SubType == 6)
                {
                    var values = ParseIntList(qst.IntData);
                    if (values.Count >= 3 && values[2] > 0)
                        return (uint)values[2];
                }
            }

            return 1;
        }

        private static bool TryComputeDailyChallengeInitTrigger(
            QuestFile qst,
            string typeTag,
            out uint trigger)
        {
            trigger = 0;
            var checkCountNode = qst.Root?.GetChild("check count");
            if (checkCountNode != null)
            {
                var checkCounts = ParseIntList(
                    checkCountNode.GetFirstDataContent(qst.Content));
                if (checkCounts.Count > 0 && checkCounts[0] > 0)
                {
                    trigger = (uint)checkCounts[0];
                    return true;
                }
            }

            var values = ParseIntList(qst.IntData);
            switch (typeTag)
            {
                case "clear quest by grade":
                case "use skill":
                    if (values.Count >= 2 && values[1] > 0)
                    {
                        trigger = (uint)values[1];
                        return true;
                    }
                    break;

                case "condition under clear":
                    if (qst.SubType == 6
                        && values.Count >= 3
                        && values[values.Count - 1] > 0)
                    {
                        trigger = (uint)values[values.Count - 1];
                        return true;
                    }
                    break;
            }

            return false;
        }

        internal static uint ReplaceTriggerChannel(uint trigger, int channelIndex, long value)
        {
            int shift = channelIndex * 9;
            if (shift < 0 || shift > 18)
                return trigger;

            uint channelValue;
            if (value <= 0)
                channelValue = 0;
            else if (value > 0x1FF)
                channelValue = 0x1FF;
            else
                channelValue = (uint)value;

            return (trigger & ~(0x1FFu << shift)) | (channelValue << shift);
        }

        internal static int GetTriggerChannel(uint trigger, int channelIndex)
        {
            var shift = channelIndex * 9;
            if (shift < 0 || shift > 18)
                return 0;

            return (int)((trigger >> shift) & 0x1FFu);
        }

        internal static bool IsSeekAndMeetNpcQuest(int questId)
        {
            return IsSeekAndMeetNpcQuest(GetQuestFile(questId));
        }

        internal static bool IsMeetNpcQuest(int questId)
        {
            var tag = NormalizeQuestTag(GetQuestFile(questId)?.Type);
            return tag == "meet npc" || tag == "seek n meet npc";
        }

        private static bool IsSeekAndMeetNpcQuest(QuestFile qst)
        {
            return NormalizeQuestTag(qst?.Type) == "seek n meet npc";
        }

        private static uint ComputeSeekAndMeetNpcInitTrigger(string intData)
        {
            var values = ParseIntList(intData);
            if (values.Count < 3)
                return 1;

            int itemCount = values[1] > 0 ? values[1] : 1;
            int meetNpcCount = values[2] > 0 ? 1 : 0;
            return PackTrigger(itemCount, meetNpcCount, 0);
        }

        internal static List<QuestRewardItem> ParseSeekAndMeetNpcItems(string intData)
        {
            var values = ParseIntList(intData);
            var result = new List<QuestRewardItem>();
            if (values.Count >= 2 && values[0] > 0 && values[1] > 0)
                result.Add(new QuestRewardItem { ItemId = values[0], Count = values[1] });
            return result;
        }

        private static uint ComputeTriggerFromIntData(string intData, int stride)
        {
            var values = ParseIntList(intData);
            if (values.Count == 0 || stride <= 0)
                return 1;

            int countOffset = stride - 1;

            var channels = new List<int>();
            for (int i = 0; i + stride <= values.Count; i += stride)
                channels.Add(values[i + countOffset]);

            if (channels.Count == 0)
                return 1;

            int f0 = channels.Count > 0 ? channels[0] : 0;
            int f1 = channels.Count > 1 ? channels[1] : 0;
            int f2 = channels.Count > 2 ? channels[2] : 0;
            return PackTrigger(f0, f1, f2);
        }

        private static uint PackTrigger(int f0, int f1, int f2)
        {
            return (uint)(((f2 & 0x1FF) << 18) | ((f1 & 0x1FF) << 9) | (f0 & 0x1FF));
        }

        internal static List<int> ParseIntList(string data)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(data)) return result;
            foreach (var token in data.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int val;
                if (int.TryParse(token, out val))
                    result.Add(val);
            }
            return result;
        }

        private static int MapTypeString(string typeStr)
        {
            if (string.IsNullOrEmpty(typeStr)) return 0;
            var t = typeStr.Trim().ToLowerInvariant();
            switch (t)
            {
                case "[seeking]": return 1;
                case "[condition under clear]": return 2;
                case "[accumulate play]": return 3;
                case "[seeking repeat]": return 4;
                case "[powerwar win]": return 5;
                case "[condition under clear2]": return 6;
                case "[belong to winning power]": return 7;
                case "[powerwar point]": return 8;
                case "[hunt monster]": return 1;
                case "[clear map]": return 2;
                case "[meet npc]": return 1;
                case "[hunt enemy]": return 1;
                case "[use item]": return 1;
                case "[get item]": return 1;
                case "[get score]": return 1;
                case "[clear quest]": return 1;
                case "[quest clear]": return 1;
                case "[custom quest]": return 1;
                case "[send chatting]": return 1;
                case "[check life]": return 1;
                case "[amplify item]": return 1;
                case "[disjoint item]": return 1;
                case "[equipped item]": return 1;
                case "[check time]": return 1;
                case "[use fortune coin]": return 1;
                case "[meet secret npc]": return 1;
                case "[turn gold card]": return 1;
                case "[ui click]": return 1;
                case "[seek n meet npc]": return 1;
                case "[assault count]": return 1;
                case "[mobile]": return 1;
                case "[normal clear]": return 25;
                default: return 0;
            }
        }
    }

}
