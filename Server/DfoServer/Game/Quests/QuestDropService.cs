using DfoServer.Game.Inventory;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Premium;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Game.Quests
{
    // 击杀怪物/摧毁被动物体后的任务掉落判定与发放。
    // 规则数据来自 QuestDropProvider(PVF)，发放走在线背包服务，发放后同步寻物任务进度。
    // 原先寄居在副本共享服务里, 拆出归任务域。
    public sealed class QuestDropService
    {
        private const string ProtocolLogName = "GameProtocol";
        private const int MaxCommitAttempts = 4;

        private readonly QuestDropNotificationBatcher _notificationBatcher;
        private readonly string _connectionString;
        private readonly IGameDatabase _database;
        private readonly Func<QuestDropCandidate, int, int> _rollDrop;
        private readonly Func<int> _rollQuestAssistantBonus;
        private readonly DungeonItemAcquisitionService _itemAcquisition;

        public QuestDropService(
            InventoryRefreshSender inventoryRefresh,
            string connectionString = null,
            Func<QuestDropCandidate, int, int> rollDrop = null,
            Func<int> rollQuestAssistantBonus = null)
            : this(
                inventoryRefresh,
                connectionString,
                rollDrop,
                itemAcquisition: null,
                database: null,
                rollQuestAssistantBonus: rollQuestAssistantBonus)
        {
        }

        internal QuestDropService(
            InventoryRefreshSender inventoryRefresh,
            string connectionString,
            Func<QuestDropCandidate, int, int> rollDrop,
            DungeonItemAcquisitionService itemAcquisition,
            IGameDatabase database = null,
            Func<int> rollQuestAssistantBonus = null)
        {
            if (inventoryRefresh == null) throw new ArgumentNullException(nameof(inventoryRefresh));
            _notificationBatcher = new QuestDropNotificationBatcher(inventoryRefresh);
            _database = database;
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : database?.ConnectionString;
            _rollDrop = rollDrop ?? QuestDropProvider.RollDrop;
            _rollQuestAssistantBonus = rollQuestAssistantBonus
                ?? (() => ServerRandom.Next(100));
            _itemAcquisition = itemAcquisition
                ?? new DungeonItemAcquisitionService(new DropService());
        }

        public Task CheckMonsterDrop(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            DungeonEventEnvelope source,
            int monsterCode)
        {
            if (expectedRun == null || expectedRun.DungeonId <= 0 || monsterCode <= 0)
                return Task.CompletedTask;

            return CheckDrop(
                session,
                expectedRun,
                source,
                monsterCode,
                "monster",
                requireRoomIdentity: true,
                getCandidates: activeQuestIds =>
                    QuestDropProvider.CheckMonsterDrop(
                        activeQuestIds,
                        expectedRun.DungeonId,
                        expectedRun.Difficulty,
                        monsterCode));
        }

        public IReadOnlyList<DropInfo> CheckMonsterCaptureDrop(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            DungeonEventEnvelope source,
            DfoServer.GameWorld.Dungeon.MonsterSumInfo monster)
        {
            var empty = Array.Empty<DropInfo>();
            var characterId = session?.Player?.CharacterId ?? 0;
            if (expectedRun == null
                || expectedRun.Tower != null
                || monster.Code <= 0
                || monster.CaptureItems == null
                || monster.CaptureItems.Count == 0
                || source == null
                || source.SourcePlayerId != characterId
                || !MatchesExpectedRun(
                    session?.Player?.CurrentRun,
                    expectedRun,
                    characterId,
                    source,
                    requireRoomIdentity: true)
                || !expectedRun.RewardPolicy.AllowsQuestDrops)
            {
                return empty;
            }

            if (!expectedRun.CaptureDrops.TryBegin(
                    source.SourceEventId,
                    out var reservation,
                    out var committedDrops))
            {
                return committedDrops ?? empty;
            }

            try
            {
                if (!TryLoadEligibleQuestIds(
                        session,
                        expectedRun,
                        $"capture-monster={monster.Code}",
                        out var activeQuestIds))
                {
                    expectedRun.CaptureDrops.TryFail(reservation);
                    return empty;
                }

                var candidates = QuestDropProvider.CheckMonsterCaptureDrop(
                    activeQuestIds,
                    monster.CaptureItems);
                if (candidates == null || candidates.Count == 0)
                {
                    expectedRun.CaptureDrops.TryCommit(reservation, empty);
                    return empty;
                }

                if (!InventoryContext.TryGetOwnedLease(
                        session.SessionId,
                        characterId,
                        out var lease))
                {
                    expectedRun.CaptureDrops.TryFail(reservation);
                    return empty;
                }

                var heldCounts = new Dictionary<int, int>();
                lock (lease.SyncRoot)
                {
                    if (!InventoryContext.IsCurrentLease(
                            lease,
                            session.SessionId,
                            characterId))
                    {
                        expectedRun.CaptureDrops.TryFail(reservation);
                        return empty;
                    }

                    foreach (var candidate in candidates)
                    {
                        if (!heldCounts.ContainsKey(candidate.ItemId))
                        {
                            heldCounts[candidate.ItemId] =
                                lease.Inventory.CountMainItem(candidate.ItemId);
                        }
                    }
                }

                if (!MatchesExpectedRun(
                        session.Player.CurrentRun,
                        expectedRun,
                        characterId,
                        source,
                        requireRoomIdentity: true))
                {
                    expectedRun.CaptureDrops.TryFail(reservation);
                    return empty;
                }

                var generated = new List<DropInfo>();
                var projectedCounts = new Dictionary<int, int>();
                var questAssistantActive = IsQuestAssistantActive(
                    session,
                    expectedRun);
                foreach (var candidate in candidates)
                {
                    heldCounts.TryGetValue(candidate.ItemId, out var held);
                    projectedCounts.TryGetValue(
                        candidate.ItemId,
                        out var projected);
                    var pending = CountPendingGroundItems(
                        expectedRun,
                        candidate.ItemId);
                    var current = SaturatingAdd(
                        SaturatingAdd(held, pending),
                        projected);
                    var count = ClampDropCount(
                        candidate,
                        current,
                        RollDrop(
                            candidate,
                            current,
                            questAssistantActive));
                    if (count <= 0)
                        continue;

                    if (!_itemAcquisition.TryRegisterGroundDrop(
                            expectedRun,
                            candidate.ItemId,
                            count,
                            out var drop))
                    {
                        FileLogger.Log(
                            $"[{ProtocolLogName}] QUEST_CAPTURE_DROP: " +
                            $"registration failed monster={monster.Code} " +
                            $"item={candidate.ItemId} count={count}");
                        continue;
                    }

                    generated.Add(drop);
                    projectedCounts[candidate.ItemId] =
                        SaturatingAdd(projected, count);
                }

                if (!expectedRun.CaptureDrops.TryCommit(
                        reservation,
                        generated))
                {
                    throw new InvalidOperationException(
                        "capture drop reservation was lost before commit");
                }

                return generated.Count == 0
                    ? empty
                    : generated.AsReadOnly();
            }
            catch (Exception ex)
            {
                expectedRun.CaptureDrops.TryFail(reservation);
                FileLogger.Log(
                    $"[{ProtocolLogName}] QUEST_CAPTURE_DROP: " +
                    $"monster={monster.Code} error={ex.Message}");
                return empty;
            }
        }

        public Task CheckPassiveObjectDrop(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            DungeonEventEnvelope source,
            int objectCode)
        {
            if (expectedRun == null || expectedRun.DungeonId <= 0 || objectCode <= 0)
                return Task.CompletedTask;

            return CheckDrop(
                session,
                expectedRun,
                source,
                objectCode,
                "passive",
                requireRoomIdentity: true,
                getCandidates: activeQuestIds =>
                    QuestDropProvider.CheckEnemyDrop(
                        activeQuestIds,
                        expectedRun.DungeonId,
                        expectedRun.Difficulty,
                        objectCode,
                        QuestDropProvider.EnemyTypePassiveObject));
        }

        public Task CheckAiCharacterDrop(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            DungeonEventEnvelope source,
            int aiCharacterCode)
        {
            if (expectedRun == null
                || expectedRun.DungeonId <= 0
                || aiCharacterCode <= 0)
            {
                return Task.CompletedTask;
            }

            return CheckDrop(
                session,
                expectedRun,
                source,
                aiCharacterCode,
                "ai-character",
                requireRoomIdentity: true,
                getCandidates: activeQuestIds =>
                    QuestDropProvider.CheckEnemyDrop(
                        activeQuestIds,
                        expectedRun.DungeonId,
                        expectedRun.Difficulty,
                        aiCharacterCode,
                        QuestDropProvider.EnemyTypeAiCharacter));
        }

        public Task CheckDungeonClearReward(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            DungeonEventEnvelope source)
        {
            if (expectedRun == null || expectedRun.DungeonId <= 0)
                return Task.CompletedTask;

            return CheckDrop(
                session,
                expectedRun,
                source,
                expectedRun.DungeonId,
                "dungeon-clear",
                requireRoomIdentity: false,
                getCandidates: activeQuestIds => QuestDropProvider.CheckClearReward(
                    activeQuestIds,
                    expectedRun.DungeonId,
                    expectedRun.Difficulty));
        }

        private Task CheckDrop(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            DungeonEventEnvelope source,
            int sourceCode,
            string sourceName,
            bool requireRoomIdentity,
            Func<ICollection<int>, List<QuestDropCandidate>> getCandidates)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (!MatchesExpectedRun(
                    session?.Player?.CurrentRun,
                    expectedRun,
                    characterId,
                    source,
                    requireRoomIdentity))
            {
                LogStaleEvent(session, expectedRun, source, sourceName, sourceCode);
                return Task.CompletedTask;
            }
            if (!expectedRun.RewardPolicy.AllowsQuestDrops)
                return Task.CompletedTask;

            var activeQuestIds = LoadEligibleQuestIds(
                session,
                expectedRun,
                $"{sourceName}={sourceCode}");
            if (activeQuestIds == null || activeQuestIds.Count == 0)
                return Task.CompletedTask;

            var candidates = getCandidates(activeQuestIds);
            if (candidates == null) return Task.CompletedTask;

            if (!MatchesExpectedRun(
                    session.Player.CurrentRun,
                    expectedRun,
                    characterId,
                    source,
                    requireRoomIdentity))
            {
                LogStaleEvent(session, expectedRun, source, sourceName, sourceCode);
                return Task.CompletedTask;
            }

            if (!InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    session.Player.CharacterId,
                    out var lease))
            {
                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP: skipped because online inventory is missing cid={session.Player.CharacterId}");
                return Task.CompletedTask;
            }

            var committed = CommitAutomaticDrops(
                lease,
                session.SessionId,
                characterId,
                source.SourceEventId,
                expectedRun.QuestSnapshot,
                candidates,
                () => MatchesExpectedRun(
                    session.Player.CurrentRun,
                    expectedRun,
                    characterId,
                    source,
                    requireRoomIdentity),
                IsQuestAssistantActive(session, expectedRun));
            if (!committed.Committed)
            {
                if (!string.IsNullOrEmpty(committed.Error))
                {
                    FileLogger.Log(
                        $"[{ProtocolLogName}] QUEST_DROP: atomic grant skipped " +
                        $"{sourceName}={sourceCode} error={committed.Error}");
                }
                return Task.CompletedTask;
            }

            var grantedSlots = new HashSet<short>();
            foreach (var entry in committed.Grants.Entries)
            {
                var grant = entry?.Grant;
                if (entry?.Request == null
                    || grant == null
                    || grant.SlotIndex < 0)
                {
                    continue;
                }

                grantedSlots.Add(grant.SlotIndex);
            }

            if (grantedSlots.Count <= 0)
                return Task.CompletedTask;

            // Coalesce only client projections after online inventory and quest state settle.
            _notificationBatcher.Queue(session, grantedSlots);
            return Task.CompletedTask;
        }

        internal QuestDropCommitResult CommitAutomaticDrops(
            InventoryLease lease,
            Guid sessionId,
            int characterId,
            Guid sourceEventId,
            QuestRunSnapshot snapshot,
            IReadOnlyList<QuestDropCandidate> candidates,
            Func<bool> isStillCurrent,
            bool questAssistantActive = false)
        {
            if (lease == null
                || sessionId == Guid.Empty
                || characterId <= 0
                || sourceEventId == Guid.Empty
                || snapshot == null
                || candidates == null
                || isStillCurrent == null)
            {
                return QuestDropCommitResult.Fail("invalid atomic drop request");
            }

            var connectionString = ResolveConnectionString();
            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        sessionId,
                        characterId)
                    || !isStillCurrent())
                {
                    return QuestDropCommitResult.Fail(
                        "inventory owner or dungeon run changed");
                }

                var inventory = lease.Inventory;
                var requests = BuildGrantRequests(
                    inventory,
                    snapshot,
                    candidates,
                    questAssistantActive);
                if (requests.Count == 0)
                    return QuestDropCommitResult.Noop;

                for (var attempt = 0; attempt < MaxCommitAttempts; attempt++)
                {
                    DungeonItemGrantBatchPlan plan = null;
                    DungeonItemGrantMutationSnapshot rollback = null;
                    var committed = false;
                    try
                    {
                        using (var connection = new SqliteConnection(connectionString))
                        {
                            connection.Open();
                            using (var transaction = connection.BeginTransaction(
                                       deferred: false))
                            {
                                var active = QuestRepository.LoadActiveQuests(
                                    connection,
                                    transaction,
                                    characterId);
                                if (!TryBuildEligibleActivations(
                                        active,
                                        requests,
                                        out var eligibleActivations))
                                {
                                    return QuestDropCommitResult.Fail(
                                        "quest activation changed before grant");
                                }

                                if (!_itemAcquisition.TryPlanItems(
                                        inventory,
                                        requests,
                                        out plan))
                                {
                                    return QuestDropCommitResult.Fail(
                                        $"inventory planning failed: {plan?.Error}");
                                }
                                if (!DungeonItemGrantMutationSnapshot.TryCapture(
                                        inventory,
                                        plan,
                                        out rollback))
                                {
                                    return QuestDropCommitResult.Fail(
                                        "inventory rollback snapshot failed");
                                }
                                if (!_itemAcquisition.TryApplyPlannedItems(
                                        inventory,
                                        plan,
                                        out var grants))
                                {
                                    rollback.Restore(inventory, plan);
                                    return QuestDropCommitResult.Fail(
                                        $"inventory apply failed: {grants?.Error}");
                                }

                                var itemFilter = CollectGrantedItemIds(requests);
                                var heldCounts = BuildSeekingHeldCounts(
                                    inventory,
                                    active,
                                    eligibleActivations,
                                    itemFilter);
                                var progress = new QuestProgressApplicationService(
                                    connectionString).ApplyInTransaction(
                                    connection,
                                    transaction,
                                    new QuestProgressApplicationRequest
                                    {
                                        CharacterId = characterId,
                                        Operation = QuestProgressOperation.SeekingItems,
                                        SourceEventId = sourceEventId,
                                        EligibleQuestActivations = eligibleActivations,
                                        ItemFilter = itemFilter,
                                        HeldItemCounts = heldCounts,
                                    });
                                if (progress.RetryRequired)
                                {
                                    rollback.Restore(inventory, plan);
                                    transaction.Rollback();
                                    if (attempt + 1 < MaxCommitAttempts)
                                        continue;
                                    return QuestDropCommitResult.Fail(
                                        progress.Error ??
                                        "quest progress CAS retry exhausted");
                                }
                                if (!progress.Success || progress.DuplicateEvent)
                                {
                                    rollback.Restore(inventory, plan);
                                    transaction.Rollback();
                                    return progress.DuplicateEvent
                                        ? QuestDropCommitResult.Duplicate
                                        : QuestDropCommitResult.Fail(
                                            progress.Error ??
                                            "quest progress update failed");
                                }

                                if (!InventoryPersistenceService
                                        .SaveDirtyInTransaction(
                                            connection,
                                            transaction,
                                            lease))
                                {
                                    throw new InvalidOperationException(
                                        "inventory persistence returned false");
                                }
                                if (!InventoryContext.IsCurrentLease(
                                        lease,
                                        sessionId,
                                        characterId)
                                    || !isStillCurrent())
                                {
                                    throw new InvalidOperationException(
                                        "inventory owner or dungeon run changed before commit");
                                }

                                transaction.Commit();
                                committed = true;
                                inventory.ClearDirtyState();
                                return QuestDropCommitResult.Success(grants);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!committed && rollback != null)
                            rollback.Restore(inventory, plan);
                        return QuestDropCommitResult.Fail(ex.Message);
                    }
                }
            }

            return QuestDropCommitResult.Fail(
                "quest drop commit retry exhausted");
        }

        private List<DungeonItemGrantRequest> BuildGrantRequests(
            InventoryService inventory,
            QuestRunSnapshot snapshot,
            IReadOnlyList<QuestDropCandidate> candidates,
            bool questAssistantActive)
        {
            var requests = new List<DungeonItemGrantRequest>();
            var projectedHeldCounts = new Dictionary<int, int>();
            foreach (var candidate in candidates)
            {
                if (candidate.QuestId <= 0
                    || candidate.QuestId > ushort.MaxValue
                    || !snapshot.TryGet(
                        (ushort)candidate.QuestId,
                        out var frozen)
                    || !frozen.ActivationId.IsValid)
                {
                    continue;
                }

                if (!projectedHeldCounts.TryGetValue(
                        candidate.ItemId,
                        out var currentHeld))
                {
                    currentHeld = inventory.CountMainItem(candidate.ItemId);
                }

                var dropCount = ClampDropCount(
                    candidate,
                    currentHeld,
                    RollDrop(
                        candidate,
                        currentHeld,
                        questAssistantActive));
                if (dropCount <= 0)
                    continue;

                requests.Add(new DungeonItemGrantRequest
                {
                    QuestId = candidate.QuestId,
                    QuestActivationId = frozen.ActivationId,
                    ItemTemplateId = candidate.ItemId,
                    Count = dropCount,
                    Source = DungeonItemAcquisitionSource.QuestAutomaticDrop,
                });
                projectedHeldCounts[candidate.ItemId] =
                    currentHeld > int.MaxValue - dropCount
                        ? int.MaxValue
                        : currentHeld + dropCount;
            }
            return requests;
        }

        private int RollDrop(
            QuestDropCandidate candidate,
            int currentHeld,
            bool questAssistantActive)
        {
            var baseCount = _rollDrop(candidate, currentHeld);
            return questAssistantActive
                ? QuestAssistantDropPolicy.ApplyBonus(
                    candidate,
                    currentHeld,
                    baseCount,
                    _rollQuestAssistantBonus)
                : baseCount;
        }

        private bool IsQuestAssistantActive(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null || run == null)
                return false;

            lock (run.SyncRoot)
            {
                if (run.QuestBridge.QuestAssistantActive.HasValue)
                    return run.QuestBridge.QuestAssistantActive.Value;
            }

            var active = PremiumService.HasActiveDevilContract(
                ResolveConnectionString(),
                session.Account?.AccountId ?? 0,
                DevilContractUsagePolicy.QuestAssistantSlot);
            lock (run.SyncRoot)
            {
                run.QuestBridge.QuestAssistantActive ??= active;
                return run.QuestBridge.QuestAssistantActive.Value;
            }
        }

        private static bool TryBuildEligibleActivations(
            IReadOnlyList<ActiveQuest> active,
            IReadOnlyList<DungeonItemGrantRequest> requests,
            out IReadOnlyDictionary<ushort, QuestActivationId> eligible)
        {
            var expected = new Dictionary<ushort, QuestActivationId>();
            foreach (var request in requests)
            {
                if (request == null
                    || request.QuestId <= 0
                    || request.QuestId > ushort.MaxValue
                    || !request.QuestActivationId.IsValid)
                {
                    eligible = null;
                    return false;
                }

                var questId = (ushort)request.QuestId;
                if (expected.TryGetValue(questId, out var existing)
                    && !existing.Equals(request.QuestActivationId))
                {
                    eligible = null;
                    return false;
                }
                expected[questId] = request.QuestActivationId;
            }

            foreach (var pair in expected)
            {
                var matched = false;
                if (active != null)
                {
                    foreach (var quest in active)
                    {
                        if (quest != null
                            && quest.QuestId == pair.Key
                            && quest.ActivationId.Equals(pair.Value))
                        {
                            matched = true;
                            break;
                        }
                    }
                }
                if (!matched)
                {
                    eligible = null;
                    return false;
                }
            }

            eligible = expected;
            return expected.Count > 0;
        }

        private static HashSet<int> CollectGrantedItemIds(
            IReadOnlyList<DungeonItemGrantRequest> requests)
        {
            var itemIds = new HashSet<int>();
            foreach (var request in requests)
            {
                if (request != null && request.ItemTemplateId > 0)
                    itemIds.Add(request.ItemTemplateId);
            }
            return itemIds;
        }

        private static IReadOnlyDictionary<int, int> BuildSeekingHeldCounts(
            InventoryService inventory,
            IReadOnlyList<ActiveQuest> active,
            IReadOnlyDictionary<ushort, QuestActivationId> eligible,
            ISet<int> itemFilter)
        {
            var relevantItemIds = new HashSet<int>();
            if (active != null && eligible != null)
            {
                foreach (var quest in active)
                {
                    if (quest == null
                        || !eligible.TryGetValue(
                            quest.QuestId,
                            out var expectedActivation)
                        || !expectedActivation.Equals(quest.ActivationId))
                    {
                        continue;
                    }

                    var seeking = QuestData.GetSeekingConsumeItems(quest.QuestId);
                    var matchesFilter = itemFilter == null || itemFilter.Count == 0;
                    foreach (var item in seeking)
                    {
                        if (item.ItemId < 0 || item.Count <= 0)
                            continue;
                        if (!matchesFilter && itemFilter.Contains(item.ItemId))
                            matchesFilter = true;
                    }
                    if (!matchesFilter)
                        continue;
                    foreach (var item in seeking)
                    {
                        if (item.ItemId >= 0 && item.Count > 0)
                            relevantItemIds.Add(item.ItemId);
                    }
                }
            }

            var heldCounts = new Dictionary<int, int>();
            foreach (var itemId in relevantItemIds)
                heldCounts[itemId] = inventory.CountMainItem(itemId);
            return heldCounts;
        }

        private string ResolveConnectionString()
        {
            return !string.IsNullOrWhiteSpace(_connectionString)
                ? _connectionString
                : (_database ?? GameDatabase.CreateDefault()).ConnectionString;
        }

        internal static int ClampDropCount(
            QuestDropCandidate candidate,
            int currentHeld,
            int requestedCount)
        {
            if (requestedCount <= 0)
                return 0;

            var effectiveLimit = QuestDropProvider.GetEffectiveHeldLimit(candidate);
            if (effectiveLimit < 0)
                return requestedCount;
            if (currentHeld >= effectiveLimit)
                return 0;
            return Math.Min(requestedCount, effectiveLimit - currentHeld);
        }

        internal static bool MatchesExpectedRun(
            DungeonRun currentRun,
            DungeonRun expectedRun,
            int characterId,
            DungeonEventEnvelope source,
            bool requireRoomIdentity)
        {
            if (currentRun == null
                || expectedRun == null
                || source == null
                || characterId <= 0
                || !ReferenceEquals(currentRun, expectedRun)
                || !expectedRun.Matches(source.RunIdentity)
                || (source.AffectedPlayerId.HasValue
                    && source.AffectedPlayerId.Value != characterId))
            {
                return false;
            }

            return !requireRoomIdentity
                || (source.RoomInstanceId.HasValue
                    && source.RoomInstanceId.Value > 0
                    && expectedRun.CurrentRoomInstanceId
                        == source.RoomInstanceId.Value);
        }

        internal static HashSet<int> FilterEligibleQuestActivations(
            QuestRunSnapshot snapshot,
            IEnumerable<ActiveQuest> activeQuests)
        {
            if (snapshot == null || snapshot.Count == 0 || activeQuests == null)
                return null;

            var eligible = new HashSet<int>();
            foreach (var activeQuest in activeQuests)
            {
                if (activeQuest == null
                    || !snapshot.TryGet(activeQuest.QuestId, out var frozen))
                {
                    continue;
                }

                if (!frozen.ActivationId.IsValid
                    || !activeQuest.ActivationId.IsValid
                    || !frozen.ActivationId.Equals(activeQuest.ActivationId))
                {
                    continue;
                }

                eligible.Add(activeQuest.QuestId);
            }
            return eligible.Count > 0 ? eligible : null;
        }

        private HashSet<int> LoadEligibleQuestIds(
            EnhancedClientSession session,
            DungeonRun run,
            string source)
        {
            return TryLoadEligibleQuestIds(
                session,
                run,
                source,
                out var eligible)
                ? eligible
                : null;
        }

        private bool TryLoadEligibleQuestIds(
            EnhancedClientSession session,
            DungeonRun run,
            string source,
            out HashSet<int> eligible)
        {
            eligible = null;
            var snapshot = run?.QuestSnapshot ?? QuestRunSnapshot.Empty;
            if (snapshot.Count == 0)
                return true;

            try
            {
                var connStr = ResolveConnectionString();
                var quests = QuestService.LoadActiveQuests(
                    connStr,
                    session.Player.CharacterId);
                eligible = FilterEligibleQuestActivations(snapshot, quests);
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] QUEST_DROP ERROR: active quest load failed, drop check skipped: {source}: {ex.Message}");
                return false;
            }
        }

        private static int CountPendingGroundItems(
            DungeonRun run,
            int itemId)
        {
            var total = 0;
            lock (run.SyncRoot)
            {
                foreach (var drop in run.Drops.Values)
                {
                    if (drop.IsGold
                        || drop.TemplateId != unchecked((uint)itemId))
                    {
                        continue;
                    }

                    var count = drop.StackCount > int.MaxValue
                        ? int.MaxValue
                        : (int)drop.StackCount;
                    total = SaturatingAdd(total, Math.Max(1, count));
                }
            }
            return total;
        }

        private static int SaturatingAdd(int left, int right)
        {
            var sum = (long)Math.Max(0, left) + Math.Max(0, right);
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        private static void LogStaleEvent(
            EnhancedClientSession session,
            DungeonRun expectedRun,
            DungeonEventEnvelope source,
            string sourceName,
            int sourceCode)
        {
            var current = session?.Player?.CurrentRun;
            FileLogger.Log(
                $"[{ProtocolLogName}] QUEST_DROP stale event rejected: " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"source={sourceName}:{sourceCode} " +
                $"expectedDungeon={expectedRun?.DungeonId ?? 0} " +
                $"expectedRun={expectedRun?.RunId ?? 0}/{expectedRun?.RunGeneration ?? 0} " +
                $"currentDungeon={current?.DungeonId ?? 0} " +
                $"currentRun={current?.RunId ?? 0}/{current?.RunGeneration ?? 0} " +
                $"event={source?.SourceEventId.ToString("N") ?? "none"}");
        }

    }

    internal sealed class QuestDropCommitResult
    {
        internal static QuestDropCommitResult Noop { get; } =
            new QuestDropCommitResult();
        internal static QuestDropCommitResult Duplicate { get; } =
            new QuestDropCommitResult { DuplicateEvent = true };

        internal bool Committed { get; private set; }
        internal bool DuplicateEvent { get; private set; }
        internal DungeonItemGrantBatchResult Grants { get; private set; }
        internal string Error { get; private set; } = string.Empty;

        internal static QuestDropCommitResult Success(
            DungeonItemGrantBatchResult grants)
            => new QuestDropCommitResult
            {
                Committed = true,
                Grants = grants,
            };

        internal static QuestDropCommitResult Fail(string error)
            => new QuestDropCommitResult
            {
                Error = error ?? string.Empty,
            };
    }
}
