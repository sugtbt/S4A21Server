namespace DfoServer.Game.Quests
{
    internal enum QuestClientTriggerDisposition
    {
        EchoOnly = 0,
        Mutate = 1,
        Reject = 2,
        Recompute = 3,
    }

    internal static class QuestClientTriggerAuthority
    {
        internal static QuestClientTriggerDisposition Resolve(
            ushort questId,
            byte triggerType,
            bool increment = false)
        {
            var quest = GameWorld.QuestData.GetQuestFile(questId);
            if (quest == null || !IsSupportedTriggerType(triggerType))
                return QuestClientTriggerDisposition.Reject;

            var type = GameWorld.QuestData.NormalizeQuestTag(quest.Type);
            if (type == "question")
            {
                return triggerType == 0
                    ? QuestClientTriggerDisposition.Mutate
                    : QuestClientTriggerDisposition.Reject;
            }

            if (type == "seek n meet npc")
            {
                return triggerType == 0x20
                    ? QuestClientTriggerDisposition.Mutate
                    : QuestClientTriggerDisposition.Recompute;
            }

            switch (type)
            {
                case "seeking":
                    return QuestClientTriggerDisposition.Recompute;

                case "hunt monster":
                    if (GameWorld.QuestData.IsWorldMapHuntMonsterQuest(questId))
                    {
                        return GameWorld.QuestData
                            .IsClientWorldMapHuntMonsterTriggerAuthorized(
                                questId,
                                triggerType,
                                increment)
                            ? QuestClientTriggerDisposition.Mutate
                            : QuestClientTriggerDisposition.Reject;
                    }
                    return QuestClientTriggerDisposition.EchoOnly;

                case "clear map":
                case "clear quest":
                case "quest clear":
                    return QuestClientTriggerDisposition.EchoOnly;

                case "hunt enemy":
                {
                    var source = GameWorld.QuestData.GetHuntEnemyProgressSource(
                        questId);
                    if (source == GameWorld.HuntEnemyProgressSource.Server)
                        return QuestClientTriggerDisposition.EchoOnly;
                    if (source == GameWorld.HuntEnemyProgressSource.Client)
                    {
                        return GameWorld.QuestData
                            .IsClientHuntEnemyTriggerAuthorized(
                                questId,
                                triggerType)
                            ? QuestClientTriggerDisposition.Mutate
                            : QuestClientTriggerDisposition.Reject;
                    }
                    return QuestClientTriggerDisposition.Reject;
                }

                case "meet npc":
                case "seeking repeat":
                case "get item":
                case "condition under clear":
                case "condition under clear2":
                case "normal clear":
                case "use item":
                case "get score":
                case "custom quest":
                case "send chatting":
                case "check life":
                case "amplify item":
                case "disjoint item":
                case "equipped item":
                case "check time":
                case "use fortune coin":
                case "meet secret npc":
                case "turn gold card":
                case "ui click":
                case "assault count":
                case "mobile":
                case "accumulate play":
                case "powerwar win":
                case "belong to winning power":
                case "powerwar point":
                case "clear quest by grade":
                case "clear a dungeon":
                case "use skill":
                case "pvp quest":
                case "pvp match":
                case "pvp rank":
                case "raid phase clear":
                case "level up":
                case "first grow":
                case "awakening":
                case "get item check index":
                case "quest accept":
                    return QuestClientTriggerDisposition.Mutate;

                default:
                    return QuestClientTriggerDisposition.EchoOnly;
            }
        }

        private static bool IsSupportedTriggerType(byte triggerType)
        {
            if (triggerType == 0 || triggerType == 1)
                return true;
            return (triggerType & ~0x70) == 0 && (triggerType & 0x70) != 0;
        }
    }
}
