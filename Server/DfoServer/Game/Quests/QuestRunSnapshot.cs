using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DfoServer.Game.Quests
{
    public sealed class QuestRunSnapshotEntry
    {
        internal QuestRunSnapshotEntry(ActiveQuest quest)
        {
            Slot = quest.Slot;
            QuestId = quest.QuestId;
            ActivationId = quest.ActivationId;
            Version = quest.Version;
            Trigger = new QuestTrigger(quest.TriggerValue);
        }

        public int Slot { get; }
        public ushort QuestId { get; }
        public QuestActivationId ActivationId { get; }
        public long Version { get; }
        public QuestTrigger Trigger { get; }
    }

    public sealed class QuestRunSnapshot
    {
        private readonly Dictionary<ushort, QuestRunSnapshotEntry> _entries;

        public static QuestRunSnapshot Empty { get; } =
            new QuestRunSnapshot(new Dictionary<ushort, QuestRunSnapshotEntry>());

        private QuestRunSnapshot(Dictionary<ushort, QuestRunSnapshotEntry> entries)
        {
            _entries = entries;
            QuestIds = new ReadOnlyCollection<ushort>(
                entries.Keys.OrderBy(id => id).ToArray());
            var activations = new Dictionary<ushort, QuestActivationId>();
            foreach (var pair in entries)
            {
                if (pair.Value.ActivationId.IsValid)
                    activations.Add(pair.Key, pair.Value.ActivationId);
            }
            Activations = new ReadOnlyDictionary<ushort, QuestActivationId>(
                activations);
        }

        public IReadOnlyCollection<ushort> QuestIds { get; }
        public IReadOnlyDictionary<ushort, QuestActivationId> Activations { get; }
        public int Count => _entries.Count;

        public bool Contains(ushort questId) => _entries.ContainsKey(questId);

        public bool TryGet(ushort questId, out QuestRunSnapshotEntry entry) =>
            _entries.TryGetValue(questId, out entry);

        // 进本后新接取的任务只把这一条并入本局快照, 不重建整份白名单:
        // 已发生的事件不会被追溯消费, 上一局/其他任务的条目也不会被带进来。
        // 同一 questId 的旧 activation 会被覆盖, 旧局事件因此无法推进新任务。
        public QuestRunSnapshot WithQuest(ActiveQuest quest)
        {
            if (quest == null || quest.QuestId == 0)
                return this;

            var entries = new Dictionary<ushort, QuestRunSnapshotEntry>(_entries)
            {
                [quest.QuestId] = new QuestRunSnapshotEntry(quest),
            };
            return new QuestRunSnapshot(entries);
        }

        public static QuestRunSnapshot Capture(IReadOnlyList<ActiveQuest> activeQuests)
        {
            if (activeQuests == null || activeQuests.Count == 0)
                return Empty;

            var entries = new Dictionary<ushort, QuestRunSnapshotEntry>();
            foreach (var quest in activeQuests)
            {
                if (quest == null || quest.QuestId == 0 || entries.ContainsKey(quest.QuestId))
                    continue;
                entries.Add(quest.QuestId, new QuestRunSnapshotEntry(quest));
            }
            return entries.Count == 0 ? Empty : new QuestRunSnapshot(entries);
        }
    }
}
