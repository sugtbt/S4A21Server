using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Game.ReviveCoin;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class ExperienceItemUseService
    {
        private const string LevelUpTicketActionType = "[level up ticket]";
        internal const int SkillPointBook5ItemId = 1031;
        internal const int SkillPointBook20ItemId = 1038;

        private readonly string _connectionString;
        private readonly IRentalTimeProvider _timeProvider;
        private readonly SqliteCharacterProgressRepository _progressRepository;

        internal ExperienceItemUseService(
            string databasePath,
            string schemaFilePath,
            IRentalTimeProvider timeProvider)
            : this(
                new GameDatabase(databasePath, schemaFilePath),
                timeProvider)
        {
        }

        internal ExperienceItemUseService(
            IGameDatabase database,
            IRentalTimeProvider timeProvider)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
            _timeProvider = timeProvider
                ?? throw new ArgumentNullException(nameof(timeProvider));
            _progressRepository = new SqliteCharacterProgressRepository(database);
        }

        internal ExperienceItemUseResult UseBySlot(
            int characterId,
            int accountId,
            InventoryListType listType,
            short slotIndex,
            ExperienceItemUseLocation location)
        {
            if (listType != InventoryListType.Main || characterId <= 0 || slotIndex < 0)
                return Reject(ExperienceItemUseStatus.NotApplicable, 0, "invalid source slot");

            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || lease.Inventory == null)
                return Reject(ExperienceItemUseStatus.NotApplicable, 0, "online inventory is unavailable");

            if (accountId <= 0 || lease.AccountId != accountId)
                return Reject(ExperienceItemUseStatus.InvalidOwner, 0, "inventory lease/account ownership mismatch");

            var resolvedItemId = 0;
            var sourceConsumed = false;
            ItemCore sourceSnapshot = null;
            InventoryService inventory = null;
            InventoryItemLifecycleUsePlan lifecyclePlan = null;
            try
            {
                lock (lease.SyncRoot)
                {
                    inventory = lease.Inventory;
                    var source = inventory.GetItem(listType, slotIndex);
                    if (source == null || source.IsEmpty)
                        return Reject(ExperienceItemUseStatus.NotApplicable, 0, "source slot is empty");

                    sourceSnapshot = source.Copy();
                    resolvedItemId = sourceSnapshot.ItemId;
                    if (InventoryItemLifecycleService.IsExpired(
                            sourceSnapshot,
                            _timeProvider.UtcNowUnixSeconds()))
                    {
                        return CommitExpiredSourceRemoval(
                            lease,
                            listType,
                            slotIndex,
                            resolvedItemId,
                            "[ExperienceItem]");
                    }

                    // 道具42(复活币礼盒): 消耗1个礼盒 → 复活币+1
                    if (resolvedItemId == ReviveCoinService.ConsumableItemId)
                    {
                        InventoryDeleteResult deleteResult = null;
                        var consumeFailed = false;
                        var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                            lease,
                            "revive-coin-consumable",
                            (connection, transaction) =>
                            {
                                var currentInventory = lease.Inventory;
                                var lifecyclePlan = InventoryItemLifecycleService.PrepareUse(
                                    currentInventory,
                                    listType,
                                    slotIndex,
                                    resolvedItemId,
                                    _timeProvider.UtcNowUnixSeconds());
                                if (!lifecyclePlan.Success)
                                {
                                    consumeFailed = true;
                                    return false;
                                }

                                if (!InventoryDeleteService.TryConsumeFromSlot(
                                        currentInventory,
                                        listType,
                                        slotIndex,
                                        resolvedItemId,
                                        1,
                                        out deleteResult)
                                    || !deleteResult.Success
                                    || deleteResult.DeletedCount != 1)
                                {
                                    consumeFailed = true;
                                    return false;
                                }

                                var current = currentInventory.CountMainItem(
                                    ReviveCoinService.ItemId);
                                if (!currentInventory.SetMainVirtualCount(
                                    ReviveCoinService.WalletSlot,
                                    ReviveCoinService.ItemId,
                                    current + 1))
                                {
                                    return false;
                                }

                                InventoryItemLifecycleService.ApplyUseSuccess(
                                    currentInventory,
                                    lifecyclePlan);
                                return true;
                            });
                        if (!committed)
                        {
                            return Reject(
                                consumeFailed
                                    ? ExperienceItemUseStatus.ConsumeFailed
                                    : ExperienceItemUseStatus.PersistenceFailed,
                                resolvedItemId,
                                consumeFailed
                                    ? "inventory deduction failed"
                                    : "revive coin transaction failed");
                        }

                        return new ExperienceItemUseResult
                        {
                            Status = ExperienceItemUseStatus.Success,
                            AccountId = accountId,
                            ItemTemplateId = resolvedItemId,
                            ConsumedItem = BuildConsumedMutation(
                                listType, slotIndex, sourceSnapshot, deleteResult),
                        };
                    }

                    if (TryResolveSkillPointBook(
                            resolvedItemId,
                            out var grantedSkillPoints))
                    {
                        return UseSkillPointBook(
                            lease,
                            characterId,
                            accountId,
                            listType,
                            slotIndex,
                            sourceSnapshot,
                            resolvedItemId,
                            grantedSkillPoints);
                    }

                    var definition = ExperienceItemDataProvider.Resolve(resolvedItemId);
                    if (!definition.IsExperienceLike)
                    {
                        return Reject(
                            ExperienceItemUseStatus.UnsupportedDefinition,
                            resolvedItemId,
                            "source item is not ordinary character experience");
                    }

                    if (!UsableCountLimitService.CanUse(
                            _connectionString,
                            characterId,
                            resolvedItemId))
                    {
                        return Reject(
                            ExperienceItemUseStatus.ConsumeFailed,
                            resolvedItemId,
                            "usable count limit reached");
                    }

                    UsableCountLimitState usableCountState = null;
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            var currentSource = inventory.GetItem(listType, slotIndex);
                            if (currentSource == null || currentSource.ItemId != resolvedItemId)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.NotApplicable,
                                    resolvedItemId,
                                    "source slot changed during use");
                            }

                            if (currentSource.Count <= 0)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.ConsumeFailed,
                                    resolvedItemId,
                                    "source stack is empty");
                            }

                            lifecyclePlan = InventoryItemLifecycleService.PrepareUse(
                                inventory,
                                listType,
                                slotIndex,
                                resolvedItemId,
                                _timeProvider.UtcNowUnixSeconds());
                            if (lifecyclePlan.SourceExpiredDeleted)
                            {
                                if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                        connection,
                                        transaction,
                                        lease))
                                {
                                    return Reject(
                                        ExperienceItemUseStatus.PersistenceFailed,
                                        resolvedItemId,
                                        "expired source persistence failed");
                                }

                                transaction.Commit();
                                inventory.ClearDirtyState();
                                return new ExperienceItemUseResult
                                {
                                    Status = ExperienceItemUseStatus.Expired,
                                    ItemTemplateId = resolvedItemId,
                                    ConsumedItem = lifecyclePlan.SourceMutation,
                                    Detail = "source item has expired",
                                };
                            }

                            if (!lifecyclePlan.Success)
                            {
                                return Reject(
                                    MapLifecycleStatus(lifecyclePlan.Status),
                                    resolvedItemId,
                                    lifecyclePlan.Detail);
                            }

                            var character = _progressRepository.LoadProgressSnapshot(
                                connection,
                                transaction,
                                characterId);
                            if (character == null
                                || accountId <= 0
                                || character.AccountId != accountId)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.InvalidOwner,
                                    resolvedItemId,
                                    "character/account ownership mismatch");
                            }

                            var usePlan = ExperienceItemUsePolicy.Evaluate(
                                new ExperienceItemUseContext
                                {
                                    Definition = definition,
                                    Job = character.Job,
                                    Level = character.Level,
                                    Exp = character.Exp,
                                    IsHardcore = character.IsHardcore,
                                    Location = location,
                                });
                            if (!usePlan.Success)
                            {
                                return Reject(
                                    usePlan.Status,
                                    resolvedItemId,
                                    usePlan.Detail);
                            }

                            if (!UsableCountLimitService.TryRecordUseIfLimited(
                                    connection,
                                    transaction,
                                    characterId,
                                    resolvedItemId,
                                    1,
                                    out usableCountState))
                            {
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "usable count transaction failed");
                            }

                            if (!InventoryDeleteService.TryConsumeFromSlot(
                                    inventory,
                                    listType,
                                    slotIndex,
                                    resolvedItemId,
                                    1,
                                    out var deleteResult)
                                || !deleteResult.Success
                                || deleteResult.DeletedCount != 1)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.ConsumeFailed,
                                    resolvedItemId,
                                    "inventory deduction failed");
                            }

                            sourceConsumed = true;
                            var consumedItem = BuildConsumedMutation(
                                listType,
                                slotIndex,
                                sourceSnapshot,
                                deleteResult);

                            var grant = Progression.CharacterExperienceService.GrantInTransaction(
                                connection,
                                transaction,
                                characterId,
                                accountId,
                                character.Level,
                                character.Exp,
                                usePlan.GrantedExp);
                            if (!grant.Persisted)
                            {
                                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "level/experience persistence failed");
                            }

                            Characters.CharacterStatComputer.DecodeGrowType(character.GrowType, out var expFirstGrow, out var expSecondGrow);
                            var syncedSkills = SkillStateService.LoadAndSync(
                                _progressRepository,
                                connection,
                                transaction,
                                characterId,
                                character.Job,
                                grant.NewLevel,
                                character.BonusSp,
                                character.BonusTp,
                                persist: grant.LeveledUp,
                                growType: expFirstGrow,
                                secondGrowType: expSecondGrow);
                            if (syncedSkills.Points == null)
                            {
                                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "skill-point synchronization failed");
                            }

                            var totalGrowthCapsuleExp = grant.TotalGrowthCapsuleExp;
                            if (grant.HonorExpGain == 0 && grant.NewLevel >= ExpTableProvider.MaxLevel)
                            {
                                totalGrowthCapsuleExp = GrowthCapsuleProgressRepository.LoadTotalExp(
                                    connection,
                                    transaction,
                                    accountId);
                            }

                            InventoryItemLifecycleService.ApplyUseSuccess(
                                inventory,
                                lifecyclePlan);

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                InventoryItemLifecycleService.RollbackUseSuccess(
                                    inventory,
                                    lifecyclePlan);
                                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "inventory persistence failed");
                            }

                            var result = new ExperienceItemUseResult
                            {
                                Status = ExperienceItemUseStatus.Success,
                                AccountId = accountId,
                                ItemTemplateId = resolvedItemId,
                                ConsumedItem = consumedItem,
                                PreviousLevel = character.Level,
                                NewLevel = grant.NewLevel,
                                PreviousExp = character.Exp,
                                NewExp = grant.NewExp,
                                GrantedExp = usePlan.GrantedExp,
                                HonorExpGain = grant.HonorExpGain,
                                TotalHonorExp = grant.TotalHonorExp,
                                TotalGrowthCapsuleExp = totalGrowthCapsuleExp,
                                SyncedSkills = syncedSkills.Skills,
                                SkillPoints = SkillStateService.GetProtocolState(
                                    syncedSkills.Skills,
                                    syncedSkills.Points),
                                UsableCountState = usableCountState,
                            };

                            transaction.Commit();
                            inventory.ClearDirtyState();
                            sourceConsumed = false;

                            return result;
                        }
                    }
                }
            }
            catch (SqliteException ex)
            {
                if (sourceConsumed && lifecyclePlan != null)
                    InventoryItemLifecycleService.RollbackUseSuccess(
                        inventory,
                        lifecyclePlan);
                if (sourceConsumed)
                    RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);

                FileLogger.Log(
                    $"[ExperienceItem] SQLite failure item={resolvedItemId} cid={characterId} slot={slotIndex}: {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode} {ex.Message}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "database transaction failed");
            }
            catch (Exception ex) when (sourceConsumed)
            {
                if (lifecyclePlan != null)
                    InventoryItemLifecycleService.RollbackUseSuccess(
                        inventory,
                        lifecyclePlan);
                RestoreConsumedSource(inventory, listType, slotIndex, sourceSnapshot);
                FileLogger.Log(
                    $"[ExperienceItem] inventory mutation rollback item={resolvedItemId} cid={characterId} slot={slotIndex}: {ex.Message}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "inventory transaction failed");
            }
        }

        private ExperienceItemUseResult UseSkillPointBook(
            InventoryLease lease,
            int characterId,
            int accountId,
            InventoryListType listType,
            short slotIndex,
            ItemCore sourceSnapshot,
            int resolvedItemId,
            int grantedSkillPoints)
        {
            var failureStatus = ExperienceItemUseStatus.PersistenceFailed;
            var failureDetail = "skill-point book transaction failed";
            InventoryDeleteResult deleteResult = null;
            CharacterProgressSnapshot character = null;
            SkillInfoSnapshot syncedSkills = null;
            SkillPointState syncedPoints = null;

            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "skill-point-book",
                (connection, transaction) =>
                {
                    var inventory = lease.Inventory;
                    var currentSource = inventory.GetItem(listType, slotIndex);
                    if (currentSource == null
                        || currentSource.ItemId != resolvedItemId)
                    {
                        failureStatus = ExperienceItemUseStatus.NotApplicable;
                        failureDetail = "source slot changed during use";
                        return false;
                    }
                    if (currentSource.Count <= 0)
                    {
                        failureStatus = ExperienceItemUseStatus.ConsumeFailed;
                        failureDetail = "source stack is empty";
                        return false;
                    }

                    var lifecyclePlan = InventoryItemLifecycleService.PrepareUse(
                        inventory,
                        listType,
                        slotIndex,
                        resolvedItemId,
                        _timeProvider.UtcNowUnixSeconds());
                    if (!lifecyclePlan.Success)
                    {
                        failureStatus = MapLifecycleStatus(lifecyclePlan.Status);
                        failureDetail = lifecyclePlan.Detail;
                        return false;
                    }

                    character = _progressRepository.LoadProgressSnapshot(
                        connection,
                        transaction,
                        characterId);
                    if (character == null
                        || accountId <= 0
                        || character.AccountId != accountId)
                    {
                        failureStatus = ExperienceItemUseStatus.InvalidOwner;
                        failureDetail = "character/account ownership mismatch";
                        return false;
                    }

                    if (!_progressRepository.TryGrantBonusSp(
                            connection,
                            transaction,
                            characterId,
                            grantedSkillPoints,
                            out var updatedBonusSp))
                    {
                        failureDetail = "bonus SP persistence failed";
                        return false;
                    }

                    if (!InventoryDeleteService.TryConsumeFromSlot(
                            inventory,
                            listType,
                            slotIndex,
                            resolvedItemId,
                            1,
                            out deleteResult)
                        || !deleteResult.Success
                        || deleteResult.DeletedCount != 1)
                    {
                        failureStatus = ExperienceItemUseStatus.ConsumeFailed;
                        failureDetail = "inventory deduction failed";
                        return false;
                    }

                    Characters.CharacterStatComputer.DecodeGrowType(
                        character.GrowType,
                        out var firstGrow,
                        out var secondGrow);
                    var synced = SkillStateService.LoadAndSync(
                        _progressRepository,
                        connection,
                        transaction,
                        characterId,
                        character.Job,
                        character.Level,
                        updatedBonusSp,
                        character.BonusTp,
                        persist: true,
                        growType: firstGrow,
                        secondGrowType: secondGrow);
                    if (synced.Skills == null || synced.Points == null)
                    {
                        failureDetail = "skill-point synchronization failed";
                        return false;
                    }

                    syncedSkills = synced.Skills;
                    syncedPoints = synced.Points;
                    InventoryItemLifecycleService.ApplyUseSuccess(
                        inventory,
                        lifecyclePlan);
                    return true;
                });

            if (!committed
                || character == null
                || syncedSkills == null
                || syncedPoints == null
                || deleteResult == null)
            {
                return Reject(
                    failureStatus,
                    resolvedItemId,
                    failureDetail);
            }

            return new ExperienceItemUseResult
            {
                Status = ExperienceItemUseStatus.Success,
                AccountId = accountId,
                ItemTemplateId = resolvedItemId,
                ConsumedItem = BuildConsumedMutation(
                    listType,
                    slotIndex,
                    sourceSnapshot,
                    deleteResult),
                PreviousLevel = character.Level,
                NewLevel = character.Level,
                PreviousExp = character.Exp,
                NewExp = character.Exp,
                GrantedExp = 0,
                SyncedSkills = syncedSkills,
                SkillPoints = SkillStateService.GetProtocolState(
                    syncedSkills,
                    syncedPoints),
                Detail = $"bonus SP +{grantedSkillPoints}",
            };
        }

        private static bool TryResolveSkillPointBook(
            int itemTemplateId,
            out int grantedSkillPoints)
        {
            switch (itemTemplateId)
            {
                case SkillPointBook5ItemId:
                    grantedSkillPoints = 5;
                    return true;
                case SkillPointBook20ItemId:
                    grantedSkillPoints = 20;
                    return true;
                default:
                    grantedSkillPoints = 0;
                    return false;
            }
        }

        internal ExperienceItemUseResult UseLevelUpTicketBySlot(
            int characterId,
            int accountId,
            short slotIndex,
            ExperienceItemUseLocation location)
        {
            if (characterId <= 0 || slotIndex < 0)
                return Reject(ExperienceItemUseStatus.NotApplicable, 0, "invalid source slot");

            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || lease.Inventory == null)
                return Reject(ExperienceItemUseStatus.NotApplicable, 0, "online inventory is unavailable");

            if (accountId <= 0 || lease.AccountId != accountId)
                return Reject(ExperienceItemUseStatus.InvalidOwner, 0, "inventory lease/account ownership mismatch");

            var resolvedItemId = 0;
            var sourceConsumed = false;
            ItemCore sourceSnapshot = null;
            InventoryService inventory = null;
            InventoryItemLifecycleUsePlan lifecyclePlan = null;
            try
            {
                lock (lease.SyncRoot)
                {
                    inventory = lease.Inventory;
                    var source = inventory.GetItem(InventoryListType.Main, slotIndex);
                    if (source == null || source.IsEmpty)
                        return Reject(ExperienceItemUseStatus.NotApplicable, 0, "source slot is empty");

                    sourceSnapshot = source.Copy();
                    resolvedItemId = sourceSnapshot.ItemId;
                    if (InventoryItemLifecycleService.IsExpired(
                            sourceSnapshot,
                            _timeProvider.UtcNowUnixSeconds()))
                    {
                        return CommitExpiredSourceRemoval(
                            lease,
                            InventoryListType.Main,
                            slotIndex,
                            resolvedItemId,
                            "[LevelUpTicket]");
                    }

                    var stackable = StackableItemProvider.Load(resolvedItemId);
                    if (!IsLevelUpTicket(stackable))
                    {
                        return Reject(
                            ExperienceItemUseStatus.UnsupportedDefinition,
                            resolvedItemId,
                            "source item is not a level-up ticket");
                    }

                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            var currentSource = inventory.GetItem(
                                InventoryListType.Main,
                                slotIndex);
                            if (currentSource == null
                                || currentSource.ItemId != resolvedItemId)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.NotApplicable,
                                    resolvedItemId,
                                    "source slot changed during use");
                            }

                            if (currentSource.Count <= 0)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.ConsumeFailed,
                                    resolvedItemId,
                                    "source stack is empty");
                            }

                            lifecyclePlan = InventoryItemLifecycleService.PrepareUse(
                                inventory,
                                InventoryListType.Main,
                                slotIndex,
                                resolvedItemId,
                                _timeProvider.UtcNowUnixSeconds());
                            if (lifecyclePlan.SourceExpiredDeleted)
                            {
                                if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                        connection,
                                        transaction,
                                        lease))
                                {
                                    return Reject(
                                        ExperienceItemUseStatus.PersistenceFailed,
                                        resolvedItemId,
                                        "expired source persistence failed");
                                }

                                transaction.Commit();
                                inventory.ClearDirtyState();
                                return new ExperienceItemUseResult
                                {
                                    Status = ExperienceItemUseStatus.Expired,
                                    ItemTemplateId = resolvedItemId,
                                    ConsumedItem = lifecyclePlan.SourceMutation,
                                    Detail = "source item has expired",
                                };
                            }

                            if (!lifecyclePlan.Success)
                            {
                                return Reject(
                                    MapLifecycleStatus(lifecyclePlan.Status),
                                    resolvedItemId,
                                    lifecyclePlan.Detail);
                            }

                            var character = _progressRepository.LoadProgressSnapshot(
                                connection,
                                transaction,
                                characterId);
                            if (character == null
                                || accountId <= 0
                                || character.AccountId != accountId)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.InvalidOwner,
                                    resolvedItemId,
                                    "character/account ownership mismatch");
                            }

                            if (!CanUseLevelUpTicket(stackable, character, out var levelError))
                            {
                                return Reject(
                                    ExperienceItemUseStatus.LevelRestricted,
                                    resolvedItemId,
                                    levelError);
                            }

                            var targetLevel = checked((byte)(character.Level + 1));
                            var targetThreshold = ExpTableProvider.GetLevelThreshold(
                                character.Level);
                            if (targetThreshold < 0 || targetThreshold == int.MaxValue)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.NoExperienceGain,
                                    resolvedItemId,
                                    "next level threshold is unavailable");
                            }

                            if (!InventoryDeleteService.TryConsumeFromSlot(
                                    inventory,
                                    InventoryListType.Main,
                                    slotIndex,
                                    resolvedItemId,
                                    1,
                                    out var deleteResult)
                                || !deleteResult.Success
                                || deleteResult.DeletedCount != 1)
                            {
                                return Reject(
                                    ExperienceItemUseStatus.ConsumeFailed,
                                    resolvedItemId,
                                    "inventory deduction failed");
                            }

                            sourceConsumed = true;
                            var consumedItem = BuildConsumedMutation(
                                InventoryListType.Main,
                                slotIndex,
                                sourceSnapshot,
                                deleteResult);

                            var completedMainlineQuests =
                                AutoCompleteCurrentLevelMainlineQuests(
                                    connection,
                                    transaction,
                                    characterId,
                                    character.Level,
                                    character.Job,
                                    character.GrowType,
                                    inventory);

                            var targetExp = (uint)targetThreshold;
                            if (!Progression.CharacterProgressService.PersistLevelAndExp(
                                    connection,
                                    transaction,
                                    characterId,
                                    targetLevel,
                                    targetExp))
                            {
                                RestoreConsumedSource(
                                    inventory,
                                    InventoryListType.Main,
                                    slotIndex,
                                    sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "level/experience persistence failed");
                            }

                            CharacterStatComputer.DecodeGrowType(
                                character.GrowType,
                                out var firstGrow,
                                out var secondGrow);
                            var syncedSkills = SkillStateService.LoadAndSync(
                                _progressRepository,
                                connection,
                                transaction,
                                characterId,
                                character.Job,
                                targetLevel,
                                character.BonusSp,
                                character.BonusTp,
                                persist: true,
                                growType: firstGrow,
                                secondGrowType: secondGrow);
                            if (syncedSkills.Points == null)
                            {
                                RestoreConsumedSource(
                                    inventory,
                                    InventoryListType.Main,
                                    slotIndex,
                                    sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "skill-point synchronization failed");
                            }

                            var totalGrowthCapsuleExp =
                                targetLevel >= ExpTableProvider.MaxLevel
                                    ? GrowthCapsuleProgressRepository.LoadTotalExp(
                                        connection,
                                        transaction,
                                        accountId)
                                    : 0;

                            InventoryItemLifecycleService.ApplyUseSuccess(
                                inventory,
                                lifecyclePlan);

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                InventoryItemLifecycleService.RollbackUseSuccess(
                                    inventory,
                                    lifecyclePlan);
                                RestoreConsumedSource(
                                    inventory,
                                    InventoryListType.Main,
                                    slotIndex,
                                    sourceSnapshot);
                                sourceConsumed = false;
                                return Reject(
                                    ExperienceItemUseStatus.PersistenceFailed,
                                    resolvedItemId,
                                    "inventory persistence failed");
                            }

                            var grantedExp = targetExp > character.Exp
                                ? targetExp - character.Exp
                                : 0;
                            var result = new ExperienceItemUseResult
                            {
                                Status = ExperienceItemUseStatus.Success,
                                AccountId = accountId,
                                ItemTemplateId = resolvedItemId,
                                ConsumedItem = consumedItem,
                                PreviousLevel = character.Level,
                                NewLevel = targetLevel,
                                PreviousExp = character.Exp,
                                NewExp = targetExp,
                                GrantedExp = grantedExp,
                                TotalGrowthCapsuleExp = totalGrowthCapsuleExp,
                                SyncedSkills = syncedSkills.Skills,
                                SkillPoints = SkillStateService.GetProtocolState(
                                    syncedSkills.Skills,
                                    syncedSkills.Points),
                                AutoCompletedQuestIds = completedMainlineQuests,
                            };

                            transaction.Commit();
                            inventory.ClearDirtyState();
                            sourceConsumed = false;
                            return result;
                        }
                    }
                }
            }
            catch (SqliteException ex)
            {
                if (sourceConsumed && lifecyclePlan != null)
                    InventoryItemLifecycleService.RollbackUseSuccess(
                        inventory,
                        lifecyclePlan);
                if (sourceConsumed)
                    RestoreConsumedSource(
                        inventory,
                        InventoryListType.Main,
                        slotIndex,
                        sourceSnapshot);

                FileLogger.Log(
                    $"[LevelUpTicket] SQLite failure item={resolvedItemId} cid={characterId} slot={slotIndex}: {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode} {ex.Message}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "database transaction failed");
            }
            catch (Exception ex) when (sourceConsumed)
            {
                if (lifecyclePlan != null)
                    InventoryItemLifecycleService.RollbackUseSuccess(
                        inventory,
                        lifecyclePlan);
                RestoreConsumedSource(
                    inventory,
                    InventoryListType.Main,
                    slotIndex,
                    sourceSnapshot);
                FileLogger.Log(
                    $"[LevelUpTicket] inventory mutation rollback item={resolvedItemId} cid={characterId} slot={slotIndex}: {ex.Message}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "inventory transaction failed");
            }
        }

        private ExperienceItemUseResult CommitExpiredSourceRemoval(
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int resolvedItemId,
            string logPrefix)
        {
            InventoryMutationResult mutation = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "experience-expired-source",
                (connection, transaction) =>
                    InventoryItemLifecycleService.TryRemoveExpiredSource(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        resolvedItemId,
                        _timeProvider.UtcNowUnixSeconds(),
                        out mutation));
            if (!committed || mutation == null)
            {
                FileLogger.Log(
                    $"{logPrefix} expired source removal failed " +
                    $"item={resolvedItemId} cid={lease?.CharacterId ?? 0} slot={slotIndex}");
                return Reject(
                    ExperienceItemUseStatus.PersistenceFailed,
                    resolvedItemId,
                    "expired source removal failed");
            }

            return new ExperienceItemUseResult
            {
                Status = ExperienceItemUseStatus.Expired,
                ItemTemplateId = resolvedItemId,
                ConsumedItem = mutation,
                Detail = "source item has expired",
            };
        }

        private static ExperienceItemUseStatus MapLifecycleStatus(
            InventoryItemLifecycleStatus status)
        {
            switch (status)
            {
                case InventoryItemLifecycleStatus.SourceExpired:
                    return ExperienceItemUseStatus.Expired;
                case InventoryItemLifecycleStatus.CooltimeActive:
                case InventoryItemLifecycleStatus.EffectActive:
                    return ExperienceItemUseStatus.CooldownActive;
                case InventoryItemLifecycleStatus.InvalidDefinition:
                    return ExperienceItemUseStatus.UnsupportedDefinition;
                case InventoryItemLifecycleStatus.SourceMissing:
                case InventoryItemLifecycleStatus.SourceChanged:
                    return ExperienceItemUseStatus.NotApplicable;
                case InventoryItemLifecycleStatus.SourceEmpty:
                    return ExperienceItemUseStatus.ConsumeFailed;
                default:
                    return ExperienceItemUseStatus.ConsumeFailed;
            }
        }

        private static InventoryMutationResult BuildConsumedMutation(
            InventoryListType listType,
            short slotIndex,
            ItemCore source,
            InventoryDeleteResult deleteResult)
        {
            return new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = source != null ? source.ItemId : 0,
                RemainingStackCount = deleteResult != null ? deleteResult.RemainingCount : 0,
                InstanceValue = source != null && InventoryStackRuleService.IsStackable(source)
                    ? (deleteResult != null ? deleteResult.RemainingCount : 0)
                    : (source != null ? source.InstanceValue : 0),
                Durability = source != null ? source.Durability : (ushort)0,
                ExpireTime = source != null ? source.ExpireTime : 0,
                RequestedCount = 1,
                AppliedCount = (short)(deleteResult != null ? deleteResult.DeletedCount : 0),
            };
        }

        private static bool IsLevelUpTicket(PvfLib.StackableItemFile stackable)
            => string.Equals(
                StackableItemProvider.NormalizeType(stackable?.ActionTypeName),
                LevelUpTicketActionType,
                StringComparison.OrdinalIgnoreCase);

        private static bool CanUseLevelUpTicket(
            PvfLib.StackableItemFile stackable,
            CharacterProgressSnapshot character,
            out string error)
        {
            error = null;
            if (stackable == null || character == null)
            {
                error = "level-up ticket definition is unavailable";
                return false;
            }

            if (character.Level <= 0 || character.Level >= ExpTableProvider.MaxLevel)
            {
                error = $"level={character?.Level ?? 0} is outside level-up range";
                return false;
            }

            if (stackable.MinimumLevel >= 0
                && character.Level < stackable.MinimumLevel)
            {
                error =
                    $"level={character.Level} is below ticket minimum={stackable.MinimumLevel}";
                return false;
            }

            if (stackable.MaximumLevel >= 0
                && character.Level > stackable.MaximumLevel)
            {
                error =
                    $"level={character.Level} exceeds ticket maximum={stackable.MaximumLevel}";
                return false;
            }

            return true;
        }

        private static IReadOnlyList<ushort> AutoCompleteCurrentLevelMainlineQuests(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte currentLevel,
            byte characterJob,
            byte growType,
            InventoryService inventory)
        {
            var completed = new List<ushort>();
            var guard = Math.Max(1, QuestCatalog.OrderedIds.Count);
            for (var iteration = 0; iteration < guard; iteration++)
            {
                var active = QuestRepository.LoadActiveQuests(
                    connection,
                    transaction,
                    characterId);
                var clearedFlags = QuestRepository.LoadClearedFlags(
                    connection,
                    transaction,
                    characterId);
                var clearedQuestIds = new HashSet<int>(clearedFlags.Keys);

                var allowedCreatureKinds =
                    PetCreatureEvolutionRuntimeService
                        .LoadEligiblePetCreatureEvolutionQuestKinds(inventory);
                var acceptable = QuestData.ComputeAcceptableQuests(
                    currentLevel,
                    characterJob,
                    growType,
                    clearedQuestIds,
                    clearedFlags,
                    allowedCreatureKinds);
                var nextQuestId = ResolveNextCurrentLevelMainlineQuest(
                    acceptable,
                    active,
                    currentLevel,
                    clearedQuestIds);
                if (nextQuestId == 0)
                    return completed;

                QuestRepository.DeleteActiveQuestsByQuestId(
                    connection,
                    transaction,
                    characterId,
                    nextQuestId);
                QuestRepository.MarkQuestCleared(
                    connection,
                    transaction,
                    characterId,
                    nextQuestId,
                    flagValue: 1);
                completed.Add(nextQuestId);
            }

            FileLogger.Log(
                $"[LevelUpTicket] mainline auto-clear stopped by guard: cid={characterId} level={currentLevel} completed={completed.Count}");
            return completed;
        }

        private static ushort ResolveNextCurrentLevelMainlineQuest(
            IReadOnlyList<ushort> acceptableQuestIds,
            IReadOnlyList<ActiveQuest> activeQuestIds,
            int currentLevel,
            ISet<int> clearedQuestIds)
        {
            if (acceptableQuestIds != null)
            {
                foreach (var questId in acceptableQuestIds)
                {
                    if ((clearedQuestIds == null || !clearedQuestIds.Contains(questId))
                        && IsCurrentLevelMainlineQuest(questId, currentLevel))
                    {
                        return questId;
                    }
                }
            }

            if (activeQuestIds != null)
            {
                foreach (var active in activeQuestIds)
                {
                    var questId = active != null ? active.QuestId : (ushort)0;
                    if (questId != 0
                        && (clearedQuestIds == null || !clearedQuestIds.Contains(questId))
                        && IsCurrentLevelMainlineQuest(questId, currentLevel))
                    {
                        return questId;
                    }
                }
            }

            return 0;
        }

        private static bool IsCurrentLevelMainlineQuest(
            ushort questId,
            int currentLevel)
        {
            if (questId == 0 || questId > 29999)
                return false;

            var quest = QuestCatalog.Get(questId);
            if (quest == null || quest.IsEvent)
                return false;

            if (!string.Equals(
                    QuestData.NormalizeQuestTag(quest.Grade),
                    "epic",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var minimumLevel = quest.Level != null && quest.Level.Length > 0
                ? quest.Level[0]
                : 1;
            return minimumLevel == currentLevel;
        }

        private static void RestoreConsumedSource(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            ItemCore sourceSnapshot)
        {
            if (inventory == null || sourceSnapshot == null)
                return;

            inventory.SetItem(listType, slotIndex, sourceSnapshot.Copy());
        }

        private static ExperienceItemUseResult Reject(
            ExperienceItemUseStatus status,
            int itemTemplateId,
            string detail)
            => new ExperienceItemUseResult
            {
                Status = status,
                ItemTemplateId = itemTemplateId,
                Detail = detail,
            };
    }
}
