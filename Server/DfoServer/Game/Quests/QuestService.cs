using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Quests
{
    // Compatibility facade for quest application services. Network parsing,
    // objective evaluation, lifecycle transactions, and notification projection
    // are owned by dedicated collaborators.
    public sealed class QuestService
    {
        private readonly string _connectionString;
        private readonly QuestRepository _repository;
        private readonly QuestProgressApplicationService _progress;
        private readonly QuestAcceptanceApplicationService _acceptance;
        private readonly QuestGiveupApplicationService _giveup;
        private readonly QuestCompletionApplicationService _completion;
        private readonly QuestScenarioModeClearApplicationService _scenarioModeClear;
        private readonly QuestActiveTriggerRepairService _triggerRepair;

        public QuestService(string connectionString)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
            _repository = new QuestRepository(connectionString);
            _progress = new QuestProgressApplicationService(connectionString);
            _acceptance = new QuestAcceptanceApplicationService(
                connectionString);
            _giveup = new QuestGiveupApplicationService(_repository);
            _completion = new QuestCompletionApplicationService(
                connectionString,
                _repository);
            _scenarioModeClear = new QuestScenarioModeClearApplicationService(
                _repository);
            _triggerRepair = new QuestActiveTriggerRepairService(
                connectionString);
        }

        public static List<ActiveQuest> LoadActiveQuests(
            string connectionString,
            int characterId)
            => new QuestRepository(connectionString).LoadActiveQuests(characterId);

        public static void SaveActiveQuests(
            string connectionString,
            int characterId,
            List<ActiveQuest> quests)
            => new QuestRepository(connectionString)
                .SaveActiveQuests(characterId, quests);

        public static ActiveQuest FindByQuestId(
            List<ActiveQuest> active,
            ushort questId)
            => QuestActiveListRules.FindByQuestId(active, questId);

        public static int FindFreeSlot(List<ActiveQuest> active)
            => QuestActiveListRules.FindFreeSlot(active);

        public QuestAcceptResult HandleAcceptQuest(
            int characterId,
            byte[] body,
            int accountId = 0)
        {
            if (body == null || body.Length < 2)
                return QuestAcceptResult.Fail(23);
            return HandleAcceptQuest(
                ResolveCurrentOwnerContext(characterId, accountId),
                new QuestAcceptCommand(BitConverter.ToUInt16(body, 0)),
                accountId);
        }

        internal QuestAcceptResult HandleAcceptQuest(
            QuestCommandOwnerContext owner,
            QuestAcceptCommand command,
            int accountId = 0)
            => _acceptance.Apply(owner, command);

        public QuestGiveupResult HandleGiveupQuest(int characterId, byte[] body)
        {
            if (body == null || body.Length < 2)
                return QuestGiveupResult.Fail(19);
            InventoryContext.TryGetLease(characterId, out var lease);
            return HandleGiveupQuest(
                ResolveCurrentOwnerContext(characterId, lease?.AccountId ?? 0),
                new QuestGiveupCommand(BitConverter.ToUInt16(body, 0)));
        }

        internal QuestGiveupResult HandleGiveupQuest(
            QuestCommandOwnerContext owner,
            QuestGiveupCommand command)
            => _giveup.Apply(owner, command);

        internal QuestFinishResult HandleFinishQuest(
            int characterId,
            QuestFinishCommand command,
            uint? currentExp = null)
            => HandleFinishQuest(
                ResolveCurrentOwnerContext(characterId, 0, currentExp),
                command);

        internal QuestFinishResult HandleFinishQuest(
            QuestCommandOwnerContext owner,
            QuestFinishCommand command)
            => _completion.Apply(owner, command);

        internal QuestScenarioModeClearResult HandleScenarioModeClearQuest(
            QuestCommandOwnerContext owner,
            QuestScenarioModeClearCommand command,
            int characterLevel,
            int characterJob,
            int growType)
            => _scenarioModeClear.Apply(
                owner,
                command,
                characterLevel,
                characterJob,
                growType);

        private static QuestCommandOwnerContext ResolveCurrentOwnerContext(
            int characterId,
            int accountId,
            uint? currentExp = null)
        {
            if (!InventoryContext.TryGetLease(characterId, out var lease))
                return default;

            return new QuestCommandOwnerContext(
                characterId,
                accountId > 0 ? accountId : lease.AccountId,
                lease.SessionId,
                lease,
                currentExp);
        }

        public QuestSetTriggerResult HandleSetTrigger(int characterId, byte[] body)
            => HandleSetTrigger(
                ResolveCurrentOwnerContext(characterId, 0),
                body);

        internal QuestSetTriggerResult HandleSetTrigger(
            QuestCommandOwnerContext owner,
            byte[] body)
        {
            if (body == null || body.Length < 3)
                return QuestSetTriggerResult.Fail(22);
            if (!owner.IsCurrentInventoryOwner())
                return QuestSetTriggerResult.Fail(22);

            var characterId = owner.CharacterId;
            var questId = BitConverter.ToUInt16(body, 0);
            var triggerType = body[2];
            var increment = body.Length >= 4 && body[3] != 0;
            if (GameWorld.QuestData.IsWorldMapHuntMonsterQuest(questId))
            {
                try
                {
                    _triggerRepair.RepairWorldMapHuntMonsterTriggers(
                        characterId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[QuestService] regional hunt trigger repair failed: " +
                        $"cid={characterId} quest={questId} {ex.Message}");
                    return QuestSetTriggerResult.Fail(22);
                }
            }
            var activeQuest = QuestActiveListRules.FindByQuestId(
                _repository.LoadActiveQuests(characterId),
                questId);
            if (activeQuest == null)
            {
                FileLogger.Log(
                    $"[QuestService] SET_TRIGGER quest={questId} " +
                    "not in active list, echo back");
                return new QuestSetTriggerResult
                {
                    QuestId = questId,
                    TriggerValue = 0,
                };
            }

            var disposition = QuestClientTriggerAuthority.Resolve(
                questId,
                triggerType,
                increment);
            IReadOnlyCollection<int> itemFilter = null;
            IReadOnlyDictionary<int, int> heldItemCounts = null;
            if (disposition == QuestClientTriggerDisposition.Recompute
                && !TryCaptureSeekingHeldCounts(
                    owner,
                    questId,
                    out itemFilter,
                    out heldItemCounts))
            {
                return QuestSetTriggerResult.Fail(22);
            }

            var applied = _progress.Apply(new QuestProgressApplicationRequest
            {
                CharacterId = characterId,
                Operation = QuestProgressOperation.ClientTrigger,
                QuestId = questId,
                TriggerType = triggerType,
                Increment = increment,
                EligibleQuestActivations =
                    new Dictionary<ushort, QuestActivationId>
                    {
                        [questId] = activeQuest.ActivationId,
                    },
                ItemFilter = itemFilter,
                HeldItemCounts = heldItemCounts,
                CommandOwner = owner,
            });
            if (!applied.Success)
            {
                FileLogger.Log(
                    $"[QuestService] SET_TRIGGER failed quest={questId}: " +
                    applied.Error);
                return QuestSetTriggerResult.Fail(22);
            }
            if (applied.QuestNotActive)
            {
                if (applied.ActivationChanged)
                {
                    FileLogger.Log(
                        $"[QuestService] SET_TRIGGER rejected stale activation " +
                        $"quest={questId} cid={characterId}");
                    return QuestSetTriggerResult.Fail(22);
                }
                FileLogger.Log(
                    $"[QuestService] SET_TRIGGER quest={questId} " +
                    "not in active list, echo back");
                return new QuestSetTriggerResult
                {
                    QuestId = questId,
                    TriggerValue = 0,
                };
            }
            if (applied.Changes.Count == 0)
                return QuestSetTriggerResult.Fail(22);

            var change = applied.Changes[applied.Changes.Count - 1];
            FileLogger.Log(
                $"[QuestService] SET_TRIGGER quest={questId} " +
                $"type=0x{triggerType:X2} inc={increment} " +
                $"authority={disposition} " +
                $"trigger={change.PreviousTriggerValue}->{change.TriggerValue}");
            return change;
        }

        private static bool TryCaptureSeekingHeldCounts(
            QuestCommandOwnerContext owner,
            ushort questId,
            out IReadOnlyCollection<int> itemFilter,
            out IReadOnlyDictionary<int, int> heldItemCounts)
        {
            itemFilter = null;
            heldItemCounts = null;
            var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(questId);
            var itemIds = new HashSet<int>();
            foreach (var item in seekItems)
            {
                if (item.ItemId >= 0 && item.Count > 0)
                    itemIds.Add(item.ItemId);
            }
            if (itemIds.Count == 0 || !owner.IsCurrentInventoryOwner())
                return false;

            var counts = new Dictionary<int, int>();
            var lease = owner.InventoryLease;
            lock (lease.SyncRoot)
            {
                if (!owner.IsCurrentInventoryOwner())
                    return false;
                foreach (var itemId in itemIds)
                    counts[itemId] = lease.Inventory.CountMainItem(itemId);
            }

            itemFilter = itemIds;
            heldItemCounts = counts;
            return owner.IsCurrentInventoryOwner();
        }

        public bool SyncItemSeekingQuestProgress(
            int characterId,
            int accountId,
            ICollection<int> itemFilter,
            IReadOnlyDictionary<int, int> temporaryHeldCounts = null)
            => SyncItemSeekingQuestProgressChanges(
                characterId,
                accountId,
                itemFilter,
                temporaryHeldCounts).Count > 0;

        internal IReadOnlyList<QuestSetTriggerResult>
            SyncItemSeekingQuestProgressChanges(
                int characterId,
                int accountId,
                ICollection<int> itemFilter,
                IReadOnlyDictionary<int, int> temporaryHeldCounts = null)
        {
            var active = _repository.LoadActiveQuests(characterId);
            if (active.Count == 0
                || !InventoryContext.TryGetLease(characterId, out var lease))
            {
                return Array.Empty<QuestSetTriggerResult>();
            }

            var relevantItemIds = new HashSet<int>();
            var filter = itemFilter == null
                ? null
                : new HashSet<int>(itemFilter);
            foreach (var quest in active)
            {
                var seekItems = GameWorld.QuestData.GetSeekingConsumeItems(
                    quest.QuestId);
                var questMatchesFilter = filter == null || filter.Count == 0;
                foreach (var item in seekItems)
                {
                    if (item.ItemId < 0 || item.Count <= 0)
                        continue;
                    if (!questMatchesFilter && filter.Contains(item.ItemId))
                        questMatchesFilter = true;
                }
                if (!questMatchesFilter)
                    continue;
                foreach (var item in seekItems)
                {
                    if (item.ItemId >= 0 && item.Count > 0)
                        relevantItemIds.Add(item.ItemId);
                }
            }
            if (relevantItemIds.Count == 0)
                return Array.Empty<QuestSetTriggerResult>();

            var heldCounts = new Dictionary<int, int>();
            lock (lease.SyncRoot)
            {
                foreach (var itemId in relevantItemIds)
                {
                    var count = lease.Inventory.CountMainItem(itemId);
                    if (temporaryHeldCounts != null
                        && temporaryHeldCounts.TryGetValue(
                            itemId,
                            out var temporaryCount)
                        && temporaryCount > 0)
                    {
                        count = count > int.MaxValue - temporaryCount
                            ? int.MaxValue
                            : count + temporaryCount;
                    }
                    heldCounts[itemId] = count;
                }
            }

            var applied = _progress.Apply(new QuestProgressApplicationRequest
            {
                CharacterId = characterId,
                Operation = QuestProgressOperation.SeekingItems,
                ItemFilter = filter,
                HeldItemCounts = heldCounts,
            });
            if (!applied.Success)
            {
                FileLogger.Log(
                    $"[QuestService] SEEKING progress failed cid={characterId}: " +
                    applied.Error);
                return Array.Empty<QuestSetTriggerResult>();
            }
            return applied.Changes;
        }

        public bool SyncClearMapQuestProgress(
            int characterId,
            int dungeonId,
            int mapId,
            Guid sourceEventId = default,
            IReadOnlyCollection<ushort> eligibleQuestIds = null,
            IReadOnlyDictionary<ushort, QuestActivationId>
                eligibleQuestActivations = null)
        {
            var changed = SyncClearMapQuestProgressCore(
                _connectionString,
                characterId,
                dungeonId,
                mapId,
                (questId, targetDungeonId, targetMapId) =>
                    GameWorld.QuestData.MatchesClearMapTarget(
                        questId,
                        targetDungeonId,
                        targetMapId),
                sourceEventId,
                eligibleQuestIds,
                eligibleQuestActivations);
            if (changed > 0)
            {
                FileLogger.Log(
                    $"[QuestService] CLEAR_MAP progress: cid={characterId} " +
                    $"dungeon={dungeonId} map={mapId} changed={changed}");
            }
            return changed > 0;
        }

        internal IReadOnlyList<QuestSetTriggerResult>
            SyncHuntMonsterQuestProgress(
                int characterId,
                int dungeonId,
                int difficulty,
                int monsterCode,
                Guid sourceEventId = default,
                IReadOnlyCollection<ushort> eligibleQuestIds = null,
                IReadOnlyDictionary<ushort, QuestActivationId>
                    eligibleQuestActivations = null,
                byte monsterType = 0)
        {
            if (characterId <= 0
                || dungeonId <= 0
                || monsterCode <= 0
                || monsterType > 3)
                return Array.Empty<QuestSetTriggerResult>();

            var applied = _progress.Apply(new QuestProgressApplicationRequest
            {
                CharacterId = characterId,
                Operation = QuestProgressOperation.HuntMonster,
                SourceEventId = sourceEventId,
                DungeonId = dungeonId,
                Difficulty = difficulty,
                MonsterCode = monsterCode,
                MonsterType = monsterType,
                EligibleQuestIds = eligibleQuestIds,
                EligibleQuestActivations = eligibleQuestActivations,
            });
            if (!applied.Success)
            {
                FileLogger.Log(
                    $"[QuestService] HUNT_MONSTER progress failed: " +
                    $"cid={characterId} dungeon={dungeonId} " +
                    $"monster={monsterCode} type={monsterType} " +
                    $"event={sourceEventId:N} error={applied.Error}");
                return Array.Empty<QuestSetTriggerResult>();
            }
            return applied.Changes;
        }

        internal IReadOnlyList<QuestSetTriggerResult>
            SyncHuntEnemyQuestProgress(
                int characterId,
                int dungeonId,
                int difficulty,
                int enemyCode,
                int enemyType,
                Guid sourceEventId = default,
                IReadOnlyCollection<ushort> eligibleQuestIds = null,
                IReadOnlyDictionary<ushort, QuestActivationId>
                    eligibleQuestActivations = null)
        {
            if (characterId <= 0
                || dungeonId <= 0
                || enemyCode <= 0
                || enemyType < GameWorld.QuestDropProvider.EnemyTypeMonster
                || enemyType > GameWorld.QuestDropProvider.EnemyTypePassiveObject)
            {
                return Array.Empty<QuestSetTriggerResult>();
            }

            var applied = _progress.Apply(new QuestProgressApplicationRequest
            {
                CharacterId = characterId,
                Operation = QuestProgressOperation.HuntEnemy,
                SourceEventId = sourceEventId,
                DungeonId = dungeonId,
                Difficulty = difficulty,
                MonsterCode = enemyCode,
                EnemyType = enemyType,
                EligibleQuestIds = eligibleQuestIds,
                EligibleQuestActivations = eligibleQuestActivations,
            });
            if (!applied.Success)
            {
                FileLogger.Log(
                    $"[QuestService] HUNT_ENEMY progress failed: " +
                    $"cid={characterId} dungeon={dungeonId} " +
                    $"enemy={enemyCode}/{enemyType} event={sourceEventId:N} " +
                    $"error={applied.Error}");
                return Array.Empty<QuestSetTriggerResult>();
            }
            return applied.Changes;
        }

        internal static int SyncClearMapQuestProgressCore(
            string connectionString,
            int characterId,
            int dungeonId,
            int mapId,
            Func<ushort, int, int, bool> matchesClearMapQuest,
            Guid sourceEventId = default,
            IReadOnlyCollection<ushort> eligibleQuestIds = null,
            IReadOnlyDictionary<ushort, QuestActivationId>
                eligibleQuestActivations = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString)
                || characterId <= 0
                || matchesClearMapQuest == null)
            {
                return 0;
            }

            var progress = new QuestProgressApplicationService(connectionString);
            var applied = progress.Apply(
                new QuestProgressApplicationRequest
                {
                    CharacterId = characterId,
                    Operation = dungeonId > 0
                        ? QuestProgressOperation.ClearDungeon
                        : QuestProgressOperation.ClearMap,
                    SourceEventId = sourceEventId,
                    DungeonId = dungeonId,
                    MapId = mapId,
                    EligibleQuestIds = eligibleQuestIds,
                    EligibleQuestActivations = eligibleQuestActivations,
                },
                matchesClearMapQuest);
            if (!applied.Success)
            {
                FileLogger.Log(
                    $"[QuestService] CLEAR_MAP progress failed: " +
                    $"cid={characterId} dungeon={dungeonId} map={mapId} " +
                    $"event={sourceEventId:N} error={applied.Error}");
                return 0;
            }
            return applied.Changes.Count;
        }

        public bool IsQuestCleared(int characterId, ushort questId)
            => _repository.IsQuestCleared(characterId, questId);
    }
}
