using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory;
using DfoServer.Game.KnightShield;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public sealed class QuestListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var character = snapshot.CharacterRecord;
            int level = (character != null) ? character.Level : 1;
            int job = (character != null) ? character.Job : 0;
            int growType = (character != null) ? character.GrowType : -1;

            var clearedFlags = new Dictionary<int, int>();
            foreach (var entry in init.CharacInvisibleFalgs)
            {
                if (entry.FlagValue != 0)
                    clearedFlags[entry.SlotIndex] = entry.FlagValue;
            }

            var allowedCreatureKinds = character != null
                && character.CharacterId > 0
                && InventoryContext.TryGetLease(character.CharacterId, out var lease)
                    ? PetCreatureEvolutionRuntimeService.LoadEligiblePetCreatureEvolutionQuestKinds(lease.Inventory)
                    : null;

            body = BuildBody(level, job, growType, clearedFlags, allowedCreatureKinds);
            return true;
        }

        // 可接任务列表(NOTI 0x0015)包体的唯一构建点 --
        // 选角初始化、交任务后的刷新、副本返城后的刷新三条路径共用。
        public static byte[] BuildBody(int level, int job, int growType, Dictionary<int, int> clearedFlags)
            => BuildBody(level, job, growType, clearedFlags, null);

        public static byte[] BuildBody(
            int level,
            int job,
            int growType,
            Dictionary<int, int> clearedFlags,
            ISet<int> allowedCreatureKinds)
        {
            var clearedSet = new HashSet<int>(clearedFlags.Keys);
            var questIds = GameWorld.QuestData.ComputeAcceptableQuests(
                level,
                job,
                growType,
                clearedSet,
                clearedFlags,
                allowedCreatureKinds);
            AppendGuardianShieldOpenQuests(
                questIds,
                level,
                job,
                growType,
                clearedSet,
                clearedFlags);

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)level);
            writer.WriteUInt16((ushort)questIds.Count);
            foreach (var questId in questIds)
                writer.WriteUInt16(questId);
            return writer.ToArray();
        }

        // 图鉴 [open quest index] 常带 [exposed by npc]=0，主循环不会列出；只补当前可接的 catalog open。
        private static void AppendGuardianShieldOpenQuests(
            List<ushort> questIds,
            int level,
            int job,
            int growType,
            HashSet<int> clearedSet,
            Dictionary<int, int> clearedFlags)
        {
            if (job != KnightShieldDataProvider.GuardianJob)
                return;

            var seen = new HashSet<int>();
            for (var index = 0; index < questIds.Count; index++)
                seen.Add(questIds[index]);

            var prerequisiteState = new QuestPrerequisiteEvaluationState(
                clearedSet,
                clearedFlags ?? new Dictionary<int, int>());

            var entries = KnightShieldDataProvider.GetCatalogEntries(growType);
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var openQuestId = entry.OpenQuestId;
                if (openQuestId <= 0 || openQuestId > 29999)
                    continue;
                if (clearedSet.Contains(openQuestId))
                    continue;
                if (entry.ClearQuestId > 0 && clearedSet.Contains(entry.ClearQuestId))
                    continue;

                var quest = QuestCatalog.Get(openQuestId);
                if (quest == null || quest.IsEvent)
                    continue;
                if (!QuestRelationIndex.MeetsCharacterRestrictions(
                        openQuestId,
                        level,
                        job,
                        growType))
                {
                    continue;
                }

                var prerequisiteDefinition = QuestPrerequisiteCatalog.Get(openQuestId);
                if (prerequisiteDefinition == null
                    || !prerequisiteDefinition.Evaluate(prerequisiteState).IsAllowed)
                {
                    continue;
                }

                if (!seen.Add(openQuestId))
                    continue;

                questIds.Add((ushort)openQuestId);
            }
        }
    }
}
