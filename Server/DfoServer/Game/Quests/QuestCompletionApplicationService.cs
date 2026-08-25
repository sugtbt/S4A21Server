using System;
using System.Collections.Generic;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestCompletionApplicationService
    {
        private readonly string _connectionString;
        private readonly QuestRepository _repository;

        internal QuestCompletionApplicationService(
            string connectionString,
            QuestRepository repository)
        {
            _connectionString = connectionString;
            _repository = repository;
        }

        internal QuestFinishResult Apply(
            QuestCommandOwnerContext owner,
            QuestFinishCommand command)
        {
            var characterId = owner.CharacterId;
            var currentExp = owner.CurrentExp;
            var questId = command.QuestId;
            var rewardSelectionIndex = command.RewardSelectionIndex;
            var hasRewardSelection = command.HasRewardSelection;
            var completionCount = command.CompletionCount;
            var lease = owner.InventoryLease;
            if (!owner.IsCurrentInventoryOwner()
                || lease.AccountId != owner.AccountId)
            {
                return QuestFinishResult.Fail(22);
            }

            var active = _repository.LoadActiveQuests(characterId);
            var activeQuest = QuestActiveListRules.FindByQuestId(active, questId);
            var isDailyChallengeCompletion = false;
            DailyChallengeEntryRewardState dailyChallengeEntry = null;

            if (activeQuest == null)
            {
                if (GameWorld.QuestData.IsDailyChallengeQuest(questId))
                {
                    dailyChallengeEntry = DailyChallengeRepository.LoadEntryRewardState(
                        _connectionString,
                        characterId,
                        questId);
                    isDailyChallengeCompletion = dailyChallengeEntry.CanClaim;
                }

                if (!isDailyChallengeCompletion)
                {
                    FileLogger.Log(
                        $"[QuestCompletionApplicationService] FINISH rejected: " +
                        $"quest={questId} has no claimable activation, cid={characterId} " +
                        $"dailyFound={dailyChallengeEntry?.Found ?? false} " +
                        $"dailyRemaining={dailyChallengeEntry?.RemainingValue ?? uint.MaxValue} " +
                        $"dailyClaimed={dailyChallengeEntry?.Claimed ?? false}");
                    return QuestFinishResult.Fail(22);
                }
            }

            if (!GameWorld.QuestData.TryResolveCompletionDefinition(
                    questId,
                    out var completionDefinition,
                    out var completionDefinitionError))
            {
                FileLogger.Log(
                    $"[QuestCompletionApplicationService] FINISH rejected invalid " +
                    $"completion definition: quest={questId} cid={characterId} " +
                    $"error={completionDefinitionError}");
                return QuestFinishResult.Fail(22);
            }

            if (completionCount == 0
                || (isDailyChallengeCompletion && completionCount != 1)
                || (completionCount != 1
                    && !completionDefinition.SupportsBatchCompletion))
            {
                FileLogger.Log(
                    $"[QuestCompletionApplicationService] FINISH rejected count: " +
                    $"quest={questId} cid={characterId} count={completionCount} " +
                    $"grade={completionDefinition.Grade} " +
                    $"type={completionDefinition.Type}");
                return QuestFinishResult.Fail(22);
            }

            var isQuestionQuest = GameWorld.QuestData.IsQuestionQuest(questId);
            var clearedFlagValue = 1;
            if (isDailyChallengeCompletion)
            {
                clearedFlagValue = 1;
            }
            else if (GameWorld.QuestData.IsQuestClearQuest(questId))
            {
                if (!QuestClearProgressRules.CanFinish(
                        _connectionString,
                        characterId,
                        questId))
                {
                    return QuestFinishResult.Fail(22);
                }

            }
            else if (isQuestionQuest)
            {
                if (!TryResolveQuestionQuestClearFlagValue(
                        questId,
                        activeQuest,
                        hasRewardSelection,
                        rewardSelectionIndex,
                        out clearedFlagValue))
                {
                    return QuestFinishResult.Fail(22);
                }
            }
            else if (activeQuest.TriggerValue != 0)
            {
                return QuestFinishResult.Fail(22);
            }

            var playerLevel = GetCharacterScalar(characterId, "level", 1);
            var playerJob = GetCharacterScalar(characterId, "job", -1);
            var playerGrowType = GetCharacterScalar(characterId, "grow_type", 0);
            var rewardResolution = GameWorld.QuestRewardProjector.Resolve(
                completionDefinition.RewardDefinition,
                hasRewardSelection && !isQuestionQuest,
                hasRewardSelection && !isQuestionQuest
                    ? rewardSelectionIndex
                    : -1,
                playerLevel,
                playerJob,
                playerGrowType);
            if (!rewardResolution.IsValid)
            {
                FileLogger.Log(
                    $"[QuestCompletionApplicationService] FINISH rejected invalid reward " +
                    $"definition: quest={questId} cid={characterId} " +
                    $"error={rewardResolution.Error}");
                return QuestFinishResult.Fail(22);
            }

            var resolvedReward = rewardResolution.Reward;
            var rewardKind = completionDefinition.RewardDefinition.Kind;
            var reward = ApplyCompletionRewardPolicy(
                resolvedReward,
                rewardKind);
            if (rewardKind == GameWorld.QuestRewardKind.CircleDungeon)
            {
                FileLogger.Log(
                    $"[QuestCompletionApplicationService] circle reward filtered: " +
                    $"quest={questId} exp={reward.Exp} " +
                    $"gold={resolvedReward.Gold}->0 " +
                    $"items={resolvedReward.Items?.Count ?? 0}->0 " +
                    $"chain={resolvedReward.ChainType}->0");
            }
            var isTitleRewardQuest = GameWorld.QuestData.IsTitleRewardQuest(questId);
            var consumedEntries = new List<ConsumedItemEntry>();
            var insertedEntries = new List<InsertedItemEntry>();
            SelectCharacter.SkillInfoSnapshot finishSkillSnapshot = null;
            uint goldReward = 0;
            uint expReward = 0;
            uint honorExpReward = 0;
            ulong totalHonorExp = 0;
            uint growthCapsuleExpReward = 0;
            uint totalGrowthCapsuleExp = 0;
            var newLevel = (byte)Math.Max(1, Math.Min(byte.MaxValue, playerLevel));
            uint newExp = currentExp ?? 0;
            var petEvolution = PetCreatureEvolutionResult.Noop;
            SelectCharacter.SelectCharacterInitializationSnapshot
                dailyChallengeSnapshot = null;
            var dailyChallengeProgressChanged = false;
            var accountId = owner.AccountId;
            IReadOnlyCollection<GameWorld.QuestRewardItem> seekItems =
                string.Equals(
                    completionDefinition.Type,
                    "seeking",
                    StringComparison.Ordinal)
                    ? completionDefinition.SeekingItems
                    : GameWorld.QuestData.GetSeekingConsumeItems(questId);
            var eventItems = GameWorld.QuestData.GetEventItems(questId);
            IReadOnlyCollection<GameWorld.QuestRewardItem>
                carryForwardEventItems =
                rewardKind == GameWorld.QuestRewardKind.CircleDungeon
                    ? Array.Empty<GameWorld.QuestRewardItem>()
                    : GameWorld.QuestData.GetCarryForwardEventItems(questId);
            // Circle completion only delivers EXP and this independent package.
            IReadOnlyCollection<GameWorld.QuestRewardItem>
                circleDungeonWorldmapRewardItems =
                rewardKind == GameWorld.QuestRewardKind.CircleDungeon
                    ? GameWorld.QuestData
                        .GetCircleDungeonWorldmapRewardItems(questId)
                    : Array.Empty<GameWorld.QuestRewardItem>();

            lock (lease.SyncRoot)
            {
                if (!owner.IsCurrentInventoryOwner())
                    return QuestFinishResult.Fail(22);

                var inventory = lease.Inventory;
                if (completionCount > 1)
                {
                    var maximumCompletionCount = GetMaximumCompletionCount(
                        inventory,
                        completionDefinition.SeekingItems);
                    if (completionCount > maximumCompletionCount)
                    {
                        FileLogger.Log(
                            $"[QuestCompletionApplicationService] FINISH rejected " +
                            $"batch upper bound: quest={questId} cid={characterId} " +
                            $"requested={completionCount} max={maximumCompletionCount}");
                        return QuestFinishResult.Fail(22);
                    }
                }

                if (!TryBuildInventoryPlan(
                    inventory,
                    reward,
                    seekItems,
                    eventItems,
                    carryForwardEventItems,
                    circleDungeonWorldmapRewardItems,
                    completionCount,
                    isTitleRewardQuest,
                    questId,
                    out var inventoryPlan,
                    out var planError))
                {
                    FileLogger.Log(
                        $"[QuestCompletionApplicationService] FINISH planning rejected: " +
                        $"quest={questId} cid={characterId} count={completionCount} " +
                        $"error={planError}");
                    return QuestFinishResult.Fail(22);
                }

                expReward = inventoryPlan.ExpReward;
                var rollback = QuestCompletionInventoryRollback.Capture(
                    inventory,
                    inventoryPlan);
                var inventoryMutated = false;
                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        {
                            if (!owner.IsCurrentInventoryOwner())
                                return QuestFinishResult.Fail(22);

                            ActiveQuest transactionQuest = null;
                            var transactionClearedFlagValue = clearedFlagValue;
                            if (isDailyChallengeCompletion)
                            {
                                var transactionEntry = DailyChallengeRepository
                                    .LoadEntryRewardState(
                                        connection,
                                        transaction,
                                        characterId,
                                        questId);
                                if (!transactionEntry.CanClaim
                                    || transactionEntry.GroupIndex != dailyChallengeEntry.GroupIndex
                                    || transactionEntry.EntryIndex != dailyChallengeEntry.EntryIndex
                                    || transactionEntry.TargetValue != dailyChallengeEntry.TargetValue
                                    || !DailyChallengeRepository.TryMarkEntryRewardClaimed(
                                        connection,
                                        transaction,
                                        characterId,
                                        transactionEntry))
                                {
                                    return QuestFinishResult.Fail(22);
                                }

                                QuestRepository.MarkQuestCleared(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId,
                                    clearedFlagValue);
                            }
                            else
                            {
                                var transactionActive = QuestRepository.LoadActiveQuests(
                                    connection,
                                    transaction,
                                    characterId);
                                transactionQuest = QuestActiveListRules.FindByQuestId(
                                    transactionActive,
                                    questId);
                                if (transactionQuest == null
                                    || !transactionQuest.ActivationId.Equals(
                                        activeQuest.ActivationId))
                                {
                                    return QuestFinishResult.Fail(22);
                                }

                                transactionClearedFlagValue = 1;
                                if (GameWorld.QuestData.IsQuestClearQuest(questId))
                                {
                                    if (!QuestClearProgressRules.CanFinish(
                                            connection,
                                            transaction,
                                            characterId,
                                            questId))
                                    {
                                        return QuestFinishResult.Fail(22);
                                    }
                                }
                                else if (isQuestionQuest)
                                {
                                    if (!TryResolveQuestionQuestClearFlagValue(
                                            questId,
                                            transactionQuest,
                                            hasRewardSelection,
                                            rewardSelectionIndex,
                                            out transactionClearedFlagValue))
                                    {
                                        return QuestFinishResult.Fail(22);
                                    }
                                }
                                else if (transactionQuest.TriggerValue != 0)
                                {
                                    return QuestFinishResult.Fail(22);
                                }

                                if (!QuestRepository.TryDeleteActiveQuestCas(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId,
                                    transactionQuest.ActivationId,
                                    transactionQuest.Version,
                                    transactionQuest.TriggerValue))
                                {
                                    return QuestFinishResult.Fail(22);
                                }

                                var challengeProgress = DailyChallengeRepository
                                    .ApplyQuestCompletion(
                                        connection,
                                        transaction,
                                        characterId,
                                        completionDefinition.Grade,
                                        completionCount);
                                dailyChallengeProgressChanged =
                                    challengeProgress.ChangedEntries > 0;
                                if (challengeProgress.ChangedEntries > 0)
                                {
                                    FileLogger.Log(
                                        $"[DailyChallenge] QUEST_COMPLETION cid={characterId} " +
                                        $"quest={questId} grade={completionDefinition.Grade} " +
                                        $"count={completionCount} " +
                                        $"changed={challengeProgress.ChangedEntries}");
                                }
                            }
                            clearedFlagValue = transactionClearedFlagValue;

                            var goldCarryLimit = CharacterGoldLimitRepository
                                .LoadEffectiveGoldCarryLimit(
                                    connection,
                                    transaction,
                                    characterId);

                            inventoryMutated = true;
                            if (!TryApplyConsumptionPlan(
                                    inventory,
                                    inventoryPlan.RequiredConsumptions,
                                    consumedEntries)
                                || !TryApplyConsumptionPlan(
                                    inventory,
                                    inventoryPlan.OptionalConsumptions,
                                    consumedEntries))
                            {
                                throw new InvalidOperationException(
                                    "quest completion consumption plan diverged");
                            }

                            if (inventoryPlan.RequiresPetEvolution)
                            {
                                petEvolution = PetCreatureEvolutionRuntimeService
                                    .TryCompletePetCreatureEvolutionQuest(
                                        inventory,
                                        reward.CreatureKind,
                                        reward.CreatureLevel,
                                        reward.GrowNumber);
                                if (!petEvolution.Changed)
                                {
                                    throw new InvalidOperationException(
                                        "quest pet evolution precondition failed");
                                }
                            }

                            if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                                    inventory,
                                    inventoryPlan.RewardPlan,
                                    out var rewardGrant)
                                || !rewardGrant.Success)
                            {
                                throw new InvalidOperationException(
                                    $"quest reward apply failed: {rewardGrant?.Error}");
                            }
                            if (rewardGrant.ActivatedPremiums.Count > 0)
                            {
                                throw new InvalidOperationException(
                                    "quest premium reward bypassed completion policy");
                            }

                            AppendInsertedEntries(
                                rewardGrant,
                                0,
                                inventoryPlan.CarryForwardGrantCount,
                                insertedEntries);

                            if (inventoryPlan.RequestedGoldReward > 0)
                            {
                                if (!inventory.TryGrantGold(
                                        (int)inventoryPlan.RequestedGoldReward,
                                        goldCarryLimit,
                                        out var grantedGold,
                                        out _))
                                {
                                    throw new InvalidOperationException(
                                        "quest gold grant failed");
                                }

                                goldReward = (uint)Math.Max(0, grantedGold);
                                if (goldReward > 0)
                                {
                                    insertedEntries.Add(new InsertedItemEntry
                                    {
                                        SlotIndex = 0,
                                        ItemId = 0,
                                        GrantedCount = goldReward,
                                    });
                                }
                            }

                            AppendInsertedEntries(
                                rewardGrant,
                                inventoryPlan.CarryForwardGrantCount,
                                rewardGrant.Results.Count,
                                insertedEntries);

                            if (reward.ChainType == 1 || reward.ChainType == 2)
                            {
                                finishSkillSnapshot = UpdateGrowType(
                                    connection,
                                    transaction,
                                    characterId,
                                    reward.ChainType,
                                    reward.GrowNumber);
                            }
                            else if (reward.ChainType == 20)
                            {
                                finishSkillSnapshot = UpdateExpertJob(
                                    connection,
                                    transaction,
                                    characterId,
                                    reward.GrowNumber);
                            }
                            else if (reward.ChainType
                                     == GameWorld.QuestData.ChainTypeSlotExpansion)
                            {
                                UpdateSlotExpansion(
                                    connection,
                                    transaction,
                                    characterId,
                                    reward.GrowNumber);
                            }

                            if (!isDailyChallengeCompletion
                                && !completionDefinition.IsRepeatable)
                            {
                                QuestRepository.MarkQuestCleared(
                                    connection,
                                    transaction,
                                    characterId,
                                    questId,
                                    clearedFlagValue);
                            }
                            if (!isDailyChallengeCompletion)
                            {
                                QuestClearProgressRules.SynchronizeActiveParents(
                                    connection,
                                    transaction,
                                    characterId);
                            }

                            newExp = currentExp
                                ?? GetCharacterExp(
                                    connection,
                                    transaction,
                                    characterId);
                            if (expReward > 0)
                            {
                                var experienceGrant = Progression
                                    .CharacterExperienceService
                                    .GrantInTransaction(
                                        connection,
                                        transaction,
                                        characterId,
                                        accountId,
                                        newLevel,
                                        newExp,
                                        expReward);
                                newLevel = experienceGrant.NewLevel;
                                newExp = experienceGrant.NewExp;
                                honorExpReward = experienceGrant.HonorExpGain;
                                totalHonorExp = experienceGrant.TotalHonorExp;
                                growthCapsuleExpReward =
                                    experienceGrant.GrowthCapsuleExpGain;
                                totalGrowthCapsuleExp =
                                    experienceGrant.TotalGrowthCapsuleExp;
                            }

                            if (isDailyChallengeCompletion
                                || dailyChallengeProgressChanged)
                            {
                                dailyChallengeSnapshot = DailyChallengeRepository
                                    .LoadSnapshot(
                                        connection,
                                        transaction,
                                        characterId);
                            }

                            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                    connection,
                                    transaction,
                                    lease))
                            {
                                throw new InvalidOperationException(
                                    "quest inventory persistence returned false");
                            }
                            if (!owner.IsCurrentInventoryOwner())
                            {
                                throw new InvalidOperationException(
                                    "quest finish inventory lease was replaced");
                            }

                            transaction.Commit();
                        }
                    }

                    inventory.ClearDirtyState();
                    inventoryMutated = false;
                }
                catch (Exception ex)
                {
                    if (inventoryMutated)
                    {
                        QuestCompletionInventoryRollback.Restore(
                            inventory,
                            rollback,
                            inventoryPlan.RewardPlan);
                    }

                    FileLogger.Log(
                        $"[QuestCompletionApplicationService] FINISH failed before " +
                        $"atomic commit: quest={questId} cid={characterId} " +
                        $"count={completionCount} error={ex.Message}");
                    return QuestFinishResult.Fail(22);
                }
            }

            FileLogger.Log(
                $"[QuestCompletionApplicationService] FINISH quest={questId} " +
                $"source={(isDailyChallengeCompletion ? "daily-challenge" : "active-quest")} " +
                $"rewardIdx={rewardSelectionIndex} count={completionCount} " +
                $"flag={clearedFlagValue} gold={goldReward} " +
                $"consumed={consumedEntries.Count} rewarded={insertedEntries.Count}");
            return new QuestFinishResult
            {
                QuestId = questId,
                FinishType = ProjectFinishType(completionDefinition.Type),
                Exp = expReward,
                HonorExp = honorExpReward,
                TotalHonorExp = totalHonorExp,
                GrowthCapsuleExp = growthCapsuleExpReward,
                TotalGrowthCapsuleExp = totalGrowthCapsuleExp,
                CompletionCount = (uint)completionCount,
                NewLevel = newLevel,
                NewExp = newExp,
                ChainType = reward.ChainType,
                RewardAcquiredAtUnixTime = insertedEntries.Count > 0
                    ? unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    : 0,
                GrowNumber = reward.GrowNumber,
                PetCreatureEvolution = petEvolution,
                ConsumedEntries = consumedEntries,
                InsertedEntries = insertedEntries,
                DailyChallengeSnapshot = dailyChallengeSnapshot,
                SkillPages = reward.ChainType == 1
                    || reward.ChainType == 2
                    || reward.ChainType == 20
                    ? CaptureFinishSkillPages(finishSkillSnapshot)
                    : new List<QuestFinishSkillPage>(),
            };
        }

        internal static QuestFinishType ProjectFinishType(string normalizedQuestType)
        {
            switch (normalizedQuestType ?? string.Empty)
            {
                case "seeking":
                    return QuestFinishType.Seeking;
                case "condition under clear":
                    return QuestFinishType.ConditionUnderClear;
                case "hunt monster":
                    return QuestFinishType.HuntMonster;
                case "meet npc":
                    return QuestFinishType.MeetNpc;
                case "hunt enemy":
                    return QuestFinishType.HuntEnemy;
                case "custom quest":
                    return QuestFinishType.CustomQuest;
                case "use item":
                    return QuestFinishType.UseItem;
                default:
                    return QuestFinishType.Seeking;
            }
        }

        internal static GameWorld.QuestReward ApplyCompletionRewardPolicy(
            GameWorld.QuestReward reward,
            GameWorld.QuestRewardKind rewardKind)
        {
            if (rewardKind != GameWorld.QuestRewardKind.CircleDungeon)
                return reward;

            return new GameWorld.QuestReward
            {
                Exp = reward.Exp,
                Gold = 0,
                ChainType = 0,
                GrowNumber = 0,
                CreatureKind = 0,
                CreatureLevel = 0,
                Items = new List<GameWorld.QuestRewardItem>(),
                ConsumeItems = reward.ConsumeItems
                    ?? new List<GameWorld.QuestRewardItem>(),
            };
        }

        private static void AddMissingCarryForwardEventItemRequests(
            InventoryService inventory,
            IReadOnlyCollection<GameWorld.QuestRewardItem> eventItems,
            ICollection<InventoryRewardGrantRequest> requests)
        {
            if (inventory == null || eventItems == null || requests == null)
                return;

            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;
                var held = Math.Max(0, inventory.CountMainItem(eventItem.ItemId));
                var missing = Math.Max(0, eventItem.Count - held);
                if (missing <= 0)
                    continue;
                requests.Add(InventoryRewardGrantRequest.CreateQuestEventItem(
                    eventItem.ItemId,
                    missing,
                    ItemCreateReason.QuestReward));
            }
        }

        private static bool TryBuildInventoryPlan(
            InventoryService inventory,
            GameWorld.QuestReward reward,
            IReadOnlyCollection<GameWorld.QuestRewardItem> seekItems,
            IReadOnlyCollection<GameWorld.QuestRewardItem> eventItems,
            IReadOnlyCollection<GameWorld.QuestRewardItem> carryForwardEventItems,
            IReadOnlyCollection<GameWorld.QuestRewardItem>
                circleDungeonWorldmapRewardItems,
            ushort completionCount,
            bool isTitleRewardQuest,
            ushort questId,
            out QuestCompletionInventoryPlan plan,
            out string error)
        {
            plan = null;
            error = string.Empty;
            if (inventory == null || completionCount == 0)
            {
                error = "invalid inventory or completion count";
                return false;
            }

            if (!TryMultiplyUInt32(reward.Exp, completionCount, out var expReward)
                || !TryMultiplyUInt32(
                    reward.Gold,
                    completionCount,
                    out var requestedGoldReward)
                || requestedGoldReward > int.MaxValue)
            {
                error = "scaled exp/gold reward exceeds protocol range";
                return false;
            }

            if (!TryScaleQuestItems(
                    reward.ConsumeItems,
                    completionCount,
                    out var scaledRewardConsumptions,
                    out error)
                || !TryScaleQuestItems(
                    seekItems,
                    completionCount,
                    out var scaledSeekingConsumptions,
                    out error))
            {
                return false;
            }

            var planningInventory = InventoryCompoundPlanning.CloneInventory(inventory);
            var requiredConsumptions = new List<QuestInventoryConsumePlanEntry>();
            if (!TryPlanConsumptions(
                    planningInventory,
                    scaledRewardConsumptions,
                    requiredConsumptions)
                || !TryPlanConsumptions(
                    planningInventory,
                    scaledSeekingConsumptions,
                    requiredConsumptions))
            {
                error = "required quest items are insufficient";
                return false;
            }

            var optionalConsumptions = new List<QuestInventoryConsumePlanEntry>();
            if (!TryPlanOptionalEventConsumptions(
                    planningInventory,
                    eventItems,
                    seekItems,
                    carryForwardEventItems,
                    optionalConsumptions))
            {
                error = "event-item cleanup plan diverged";
                return false;
            }

            var carryForwardRequests = new List<InventoryRewardGrantRequest>();
            AddMissingCarryForwardEventItemRequests(
                inventory,
                carryForwardEventItems,
                carryForwardRequests);

            var rewardRequests = new List<InventoryRewardGrantRequest>();
            if (reward.ChainType == 0
                && !TryAddQuestRewardRequests(
                    rewardRequests,
                    reward.Items,
                    completionCount,
                    isTitleRewardQuest,
                    questId,
                    out error))
            {
                return false;
            }

            if (reward.ChainType == 0
                && !TryAddQuestRewardRequests(
                    rewardRequests,
                    circleDungeonWorldmapRewardItems,
                    completionCount,
                    isTitleRewardQuest: false,
                    questId,
                    out error))
            {
                return false;
            }

            var allGrantRequests = new List<InventoryRewardGrantRequest>(
                carryForwardRequests.Count + rewardRequests.Count);
            allGrantRequests.AddRange(carryForwardRequests);
            allGrantRequests.AddRange(rewardRequests);
            if (!InventoryRewardGrantService.TryPlanBatch(
                    planningInventory,
                    allGrantRequests,
                    out var rewardPlan))
            {
                error = $"reward plan failed: {rewardPlan?.Error}";
                return false;
            }
            if (!TryValidateCompletionRewardPlan(rewardPlan, out error))
                return false;

            if (requiredConsumptions.Count + optionalConsumptions.Count
                    > byte.MaxValue
                || CountProjectedInsertedEntries(rewardPlan, requestedGoldReward)
                    > byte.MaxValue)
            {
                error = "completion projection exceeds ACK entry capacity";
                return false;
            }

            plan = new QuestCompletionInventoryPlan(
                requiredConsumptions,
                optionalConsumptions,
                rewardPlan,
                carryForwardRequests.Count,
                expReward,
                requestedGoldReward,
                reward.ChainType == 10 || reward.ChainType == 25);
            return true;
        }

        internal static bool TryValidateCompletionRewardPlan(
            InventoryRewardGrantBatchPlan rewardPlan,
            out string error)
        {
            error = string.Empty;
            if (rewardPlan == null)
            {
                error = "reward plan is missing";
                return false;
            }

            foreach (var entry in rewardPlan.Entries)
            {
                if (entry.Kind != InventoryRewardGrantKind.Premium)
                    continue;
                error = "premium quest rewards require transactional activation";
                return false;
            }
            return true;
        }

        private static int GetMaximumCompletionCount(
            InventoryService inventory,
            IReadOnlyCollection<GameWorld.QuestRewardItem> items)
        {
            if (inventory == null || items == null || items.Count == 0)
                return 0;

            var maximum = int.MaxValue;
            foreach (var item in items)
            {
                if (item.ItemId < 0 || item.Count <= 0)
                    return 0;
                maximum = Math.Min(
                    maximum,
                    inventory.CountMainItem(item.ItemId) / item.Count);
            }
            return Math.Max(0, maximum);
        }

        private static bool TryScaleQuestItems(
            IReadOnlyCollection<GameWorld.QuestRewardItem> items,
            ushort completionCount,
            out List<GameWorld.QuestRewardItem> scaled,
            out string error)
        {
            scaled = new List<GameWorld.QuestRewardItem>();
            error = string.Empty;
            if (items == null || items.Count == 0)
                return true;

            var order = new List<int>();
            var itemIds = new Dictionary<int, int>();
            var totals = new Dictionary<int, int>();
            foreach (var item in items)
            {
                if (item.ItemId < 0 || item.Count <= 0)
                {
                    error = "quest item requirement contains a negative item or non-positive count";
                    return false;
                }

                var scaledCount = (long)item.Count * completionCount;
                if (scaledCount <= 0 || scaledCount > int.MaxValue)
                {
                    error = "scaled quest item requirement exceeds int32";
                    return false;
                }

                var identity = GetMainItemIdentityKey(item.ItemId);
                if (!totals.TryGetValue(identity, out var current))
                {
                    order.Add(identity);
                    itemIds[identity] = item.ItemId;
                    current = 0;
                }

                var combined = (long)current + scaledCount;
                if (combined > int.MaxValue)
                {
                    error = "combined quest item requirement exceeds int32";
                    return false;
                }
                totals[identity] = (int)combined;
            }

            foreach (var identity in order)
            {
                scaled.Add(new GameWorld.QuestRewardItem
                {
                    ItemId = itemIds[identity],
                    Count = totals[identity],
                });
            }
            return true;
        }

        private static bool TryPlanConsumptions(
            InventoryService planningInventory,
            IReadOnlyCollection<GameWorld.QuestRewardItem> items,
            ICollection<QuestInventoryConsumePlanEntry> entries)
        {
            if (planningInventory == null || entries == null)
                return false;
            if (items == null || items.Count == 0)
                return true;

            foreach (var item in items)
            {
                if (!TryPlanMainItemConsumption(
                        planningInventory,
                        item.ItemId,
                        item.Count,
                        entries))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryPlanOptionalEventConsumptions(
            InventoryService planningInventory,
            IReadOnlyCollection<GameWorld.QuestRewardItem> eventItems,
            IReadOnlyCollection<GameWorld.QuestRewardItem> seekItems,
            IReadOnlyCollection<GameWorld.QuestRewardItem> carryForwardEventItems,
            ICollection<QuestInventoryConsumePlanEntry> entries)
        {
            if (planningInventory == null || entries == null)
                return false;
            if (eventItems == null || eventItems.Count == 0)
                return true;

            var seekItemIds = ToItemIdentitySet(seekItems);
            var carryForwardItemIds = ToItemIdentitySet(carryForwardEventItems);
            foreach (var eventItem in eventItems)
            {
                if (eventItem.ItemId <= 0 || eventItem.Count <= 0)
                    continue;
                var identity = GetMainItemIdentityKey(eventItem.ItemId);
                if (seekItemIds.Contains(identity)
                    || carryForwardItemIds.Contains(identity))
                {
                    continue;
                }

                var available = planningInventory.CountMainItem(eventItem.ItemId);
                var consumeCount = Math.Min(eventItem.Count, available);
                if (consumeCount > 0
                    && !TryPlanMainItemConsumption(
                        planningInventory,
                        eventItem.ItemId,
                        consumeCount,
                        entries))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryPlanMainItemConsumption(
            InventoryService planningInventory,
            int itemId,
            int count,
            ICollection<QuestInventoryConsumePlanEntry> entries)
        {
            if (planningInventory == null
                || entries == null
                || itemId < 0
                || count <= 0)
            {
                return false;
            }

            if (InventoryService.TryResolveMainVirtualSlotByItemId(
                    itemId,
                    out var virtualSlot,
                    out var virtualItemId))
            {
                var current = planningInventory.GetMainVirtualCount(virtualSlot);
                if (current == null || current.Count < count)
                    return false;
                if (!planningInventory.SetMainVirtualCount(
                        virtualSlot,
                        virtualItemId,
                        current.Count - count))
                {
                    return false;
                }
                entries.Add(new QuestInventoryConsumePlanEntry(
                    InventoryListType.Main,
                    virtualSlot,
                    virtualItemId,
                    count,
                    isVirtual: true));
                return true;
            }

            var remaining = count;
            foreach (var pair in planningInventory.GetItems(InventoryListType.Main))
            {
                var source = pair.Value;
                if (source == null || source.ItemId != itemId)
                    continue;

                var available = InventoryStackRuleService.IsStackable(source)
                    ? Math.Max(0, source.Count)
                    : 1;
                var consumeCount = Math.Min(remaining, available);
                if (consumeCount <= 0)
                    continue;
                if (!InventoryDeleteService.TryConsumeFromSlot(
                        planningInventory,
                        InventoryListType.Main,
                        pair.Key,
                        itemId,
                        consumeCount,
                        out var consumed)
                    || !consumed.Success)
                {
                    return false;
                }

                entries.Add(new QuestInventoryConsumePlanEntry(
                    InventoryListType.Main,
                    pair.Key,
                    itemId,
                    consumed.DeletedCount,
                    isVirtual: false));
                remaining -= consumed.DeletedCount;
                if (remaining == 0)
                    return true;
            }
            return false;
        }

        private static bool TryApplyConsumptionPlan(
            InventoryService inventory,
            IReadOnlyList<QuestInventoryConsumePlanEntry> plan,
            ICollection<ConsumedItemEntry> consumedEntries)
        {
            if (inventory == null || plan == null || consumedEntries == null)
                return false;

            foreach (var entry in plan)
            {
                if (entry.IsVirtual)
                {
                    var current = inventory.GetMainVirtualCount(entry.SlotIndex);
                    if (current == null
                        || current.ItemId != entry.ItemId
                        || current.Count < entry.Count
                        || !inventory.SetMainVirtualCount(
                            entry.SlotIndex,
                            entry.ItemId,
                            current.Count - entry.Count))
                    {
                        return false;
                    }
                }
                else if (!InventoryDeleteService.TryConsumeFromSlot(
                        inventory,
                        entry.ListType,
                        entry.SlotIndex,
                        entry.ItemId,
                        entry.Count,
                        out var consumed)
                    || !consumed.Success
                    || consumed.DeletedCount != entry.Count)
                {
                    return false;
                }

                consumedEntries.Add(new ConsumedItemEntry
                {
                    UpdateType = 0,
                    SlotIndex = (ushort)entry.SlotIndex,
                    ConsumedCount = (uint)entry.Count,
                });
            }
            return true;
        }

        private static bool TryAddQuestRewardRequests(
            ICollection<InventoryRewardGrantRequest> requests,
            IReadOnlyCollection<GameWorld.QuestRewardItem> items,
            ushort completionCount,
            bool isTitleRewardQuest,
            ushort questId,
            out string error)
        {
            error = string.Empty;
            if (requests == null)
            {
                error = "missing reward request collection";
                return false;
            }
            if (items == null || items.Count == 0)
                return true;

            foreach (var item in items)
            {
                if (item.ItemId <= 0 || item.Count <= 0)
                {
                    error = "quest reward contains a non-positive item/count";
                    return false;
                }
                if (isTitleRewardQuest)
                {
                    FileLogger.Log(
                        $"[QuestCompletionApplicationService] FINISH title reward " +
                        $"skipped from inventory: quest={questId} item={item.ItemId}");
                    continue;
                }

                var scaledCount = (long)item.Count * completionCount;
                if (scaledCount <= 0 || scaledCount > int.MaxValue)
                {
                    error = "scaled quest reward count exceeds int32";
                    return false;
                }
                requests.Add(InventoryRewardGrantRequest.Create(
                    item.ItemId,
                    (int)scaledCount,
                    ItemCreateReason.QuestReward));
            }
            return true;
        }

        private static bool TryMultiplyUInt32(
            uint value,
            ushort multiplier,
            out uint result)
        {
            var scaled = (ulong)value * multiplier;
            result = scaled <= uint.MaxValue ? (uint)scaled : 0;
            return scaled <= uint.MaxValue;
        }

        private static int CountProjectedInsertedEntries(
            InventoryRewardGrantBatchPlan rewardPlan,
            uint requestedGoldReward)
        {
            var count = requestedGoldReward > 0 ? 1 : 0;
            if (rewardPlan == null)
                return count;
            foreach (var entry in rewardPlan.Entries)
            {
                if (entry.Kind == InventoryRewardGrantKind.InventoryItem
                    || entry.Kind == InventoryRewardGrantKind.MainVirtualCount)
                {
                    count++;
                }
            }
            return count;
        }

        private static HashSet<int> ToItemIdentitySet(
            IReadOnlyCollection<GameWorld.QuestRewardItem> items)
        {
            var identities = new HashSet<int>();
            if (items == null)
                return identities;
            foreach (var item in items)
            {
                if (item.ItemId >= 0 && item.Count > 0)
                    identities.Add(GetMainItemIdentityKey(item.ItemId));
            }
            return identities;
        }

        private static void AppendInsertedEntries(
            InventoryRewardGrantBatchResult rewardGrant,
            int startIndex,
            int endIndexExclusive,
            ICollection<InsertedItemEntry> insertedEntries)
        {
            if (rewardGrant == null || insertedEntries == null)
                return;
            var start = Math.Max(0, startIndex);
            var end = Math.Min(rewardGrant.Results.Count, endIndexExclusive);
            for (var index = start; index < end; index++)
            {
                var entry = ToInsertedItemEntry(rewardGrant.Results[index]);
                if (entry != null)
                    insertedEntries.Add(entry);
            }
        }

        private static InsertedItemEntry ToInsertedItemEntry(
            InventoryRewardGrantResult grant)
        {
            if (grant == null || !grant.Success || grant.SlotIndex < 0)
                return null;
            if (grant.Kind == InventoryRewardGrantKind.Premium)
                return null;

            return new InsertedItemEntry
            {
                SlotIndex = (ushort)grant.SlotIndex,
                ItemId = grant.ItemTemplateId,
                GrantedCount = (uint)Math.Max(0, grant.GrantedCount),
            };
        }

        private readonly struct QuestInventoryConsumePlanEntry
        {
            internal QuestInventoryConsumePlanEntry(
                InventoryListType listType,
                short slotIndex,
                int itemId,
                int count,
                bool isVirtual)
            {
                ListType = listType;
                SlotIndex = slotIndex;
                ItemId = itemId;
                Count = count;
                IsVirtual = isVirtual;
            }

            internal InventoryListType ListType { get; }
            internal short SlotIndex { get; }
            internal int ItemId { get; }
            internal int Count { get; }
            internal bool IsVirtual { get; }
        }

        private sealed class QuestCompletionInventoryPlan
        {
            internal QuestCompletionInventoryPlan(
                IReadOnlyList<QuestInventoryConsumePlanEntry> requiredConsumptions,
                IReadOnlyList<QuestInventoryConsumePlanEntry> optionalConsumptions,
                InventoryRewardGrantBatchPlan rewardPlan,
                int carryForwardGrantCount,
                uint expReward,
                uint requestedGoldReward,
                bool requiresPetEvolution)
            {
                RequiredConsumptions = new List<QuestInventoryConsumePlanEntry>(
                    requiredConsumptions ?? Array.Empty<QuestInventoryConsumePlanEntry>())
                    .AsReadOnly();
                OptionalConsumptions = new List<QuestInventoryConsumePlanEntry>(
                    optionalConsumptions ?? Array.Empty<QuestInventoryConsumePlanEntry>())
                    .AsReadOnly();
                RewardPlan = rewardPlan
                    ?? throw new ArgumentNullException(nameof(rewardPlan));
                CarryForwardGrantCount = carryForwardGrantCount;
                ExpReward = expReward;
                RequestedGoldReward = requestedGoldReward;
                RequiresPetEvolution = requiresPetEvolution;
            }

            internal IReadOnlyList<QuestInventoryConsumePlanEntry>
                RequiredConsumptions { get; }

            internal IReadOnlyList<QuestInventoryConsumePlanEntry>
                OptionalConsumptions { get; }

            internal InventoryRewardGrantBatchPlan RewardPlan { get; }
            internal int CarryForwardGrantCount { get; }
            internal uint ExpReward { get; }
            internal uint RequestedGoldReward { get; }
            internal bool RequiresPetEvolution { get; }
        }

        private sealed class QuestCompletionInventoryRollback
        {
            private readonly Dictionary<InventorySlotKey, ItemCore> _items =
                new Dictionary<InventorySlotKey, ItemCore>();
            private readonly Dictionary<short, VirtualCountItem> _virtualCounts =
                new Dictionary<short, VirtualCountItem>();
            private int _pendingHappyTokenCeraGrant;

            internal static QuestCompletionInventoryRollback Capture(
                InventoryService inventory,
                QuestCompletionInventoryPlan plan)
            {
                if (inventory == null)
                    throw new ArgumentNullException(nameof(inventory));
                if (plan == null)
                    throw new ArgumentNullException(nameof(plan));

                var snapshot = new QuestCompletionInventoryRollback
                {
                    _pendingHappyTokenCeraGrant =
                        inventory.PendingHappyTokenCeraGrant,
                };
                foreach (var entry in plan.RequiredConsumptions)
                    snapshot.CaptureConsumption(inventory, entry);
                foreach (var entry in plan.OptionalConsumptions)
                    snapshot.CaptureConsumption(inventory, entry);

                foreach (var entry in plan.RewardPlan.Entries)
                {
                    if (entry.Kind == InventoryRewardGrantKind.InventoryItem)
                    {
                        snapshot.CaptureItem(
                            inventory,
                            entry.ListType,
                            entry.SlotIndex);
                    }
                    else if (entry.Kind
                             == InventoryRewardGrantKind.MainVirtualCount)
                    {
                        snapshot.CaptureVirtual(inventory, entry.SlotIndex);
                    }
                }

                if (plan.RequestedGoldReward > 0)
                {
                    snapshot.CaptureVirtual(
                        inventory,
                        InventoryService.MainVirtualCurrencySlotStart);
                }
                if (plan.RequiresPetEvolution)
                {
                    snapshot.CaptureItem(
                        inventory,
                        InventoryListType.Equipment,
                        PetInventoryLayout.CreatureEquipSlot);
                }
                return snapshot;
            }

            internal static void Restore(
                InventoryService inventory,
                QuestCompletionInventoryRollback snapshot,
                InventoryRewardGrantBatchPlan rewardPlan)
            {
                if (inventory == null || snapshot == null)
                    return;

                if (rewardPlan != null)
                {
                    foreach (var entry in rewardPlan.Entries)
                    {
                        InventoryCreateService.DetachCreatedDetails(
                            inventory,
                            entry.CreateResult);
                    }
                }

                foreach (var pair in snapshot._items)
                {
                    if (pair.Value == null)
                    {
                        if (inventory.GetItem(
                                pair.Key.ListType,
                                pair.Key.SlotIndex) != null)
                        {
                            inventory.RemoveItem(
                                pair.Key.ListType,
                                pair.Key.SlotIndex);
                        }
                    }
                    else
                    {
                        inventory.SetItem(
                            pair.Key.ListType,
                            pair.Key.SlotIndex,
                            pair.Value.Copy());
                    }
                }

                foreach (var pair in snapshot._virtualCounts)
                {
                    if (pair.Value == null)
                        inventory.SetMainVirtualCount(pair.Key, 0);
                    else
                        inventory.SetMainVirtualCount(
                            pair.Key,
                            pair.Value.ItemId,
                            pair.Value.Count);
                }
                inventory.RestorePendingHappyTokenCeraGrant(
                    snapshot._pendingHappyTokenCeraGrant);
            }

            private void CaptureConsumption(
                InventoryService inventory,
                QuestInventoryConsumePlanEntry entry)
            {
                if (entry.IsVirtual)
                    CaptureVirtual(inventory, entry.SlotIndex);
                else
                    CaptureItem(inventory, entry.ListType, entry.SlotIndex);
            }

            private void CaptureItem(
                InventoryService inventory,
                InventoryListType listType,
                short slotIndex)
            {
                if (slotIndex < 0)
                    return;
                var key = new InventorySlotKey(listType, slotIndex);
                if (_items.ContainsKey(key))
                    return;
                _items[key] = inventory.GetItem(listType, slotIndex)?.Copy();
            }

            private void CaptureVirtual(
                InventoryService inventory,
                short slotIndex)
            {
                if (slotIndex < 0 || _virtualCounts.ContainsKey(slotIndex))
                    return;
                _virtualCounts[slotIndex] =
                    inventory.GetMainVirtualCount(slotIndex);
            }
        }

        private readonly struct InventorySlotKey : IEquatable<InventorySlotKey>
        {
            internal InventorySlotKey(
                InventoryListType listType,
                short slotIndex)
            {
                ListType = listType;
                SlotIndex = slotIndex;
            }

            internal InventoryListType ListType { get; }
            internal short SlotIndex { get; }

            public bool Equals(InventorySlotKey other) =>
                ListType == other.ListType && SlotIndex == other.SlotIndex;

            public override bool Equals(object obj) =>
                obj is InventorySlotKey other && Equals(other);

            public override int GetHashCode() =>
                ((int)ListType * 397) ^ SlotIndex;
        }

        private static bool TryResolveQuestionQuestClearFlagValue(
            ushort questId,
            ActiveQuest activeQuest,
            bool hasRewardSelection,
            ushort rewardSelectionIndex,
            out int flagValue)
        {
            flagValue = 1;
            var answerCount = GameWorld.QuestData.GetQuestionAnswerCount(questId);
            if (answerCount <= 0)
                return activeQuest == null || activeQuest.TriggerValue == 0;
            if (activeQuest != null
                && TryResolveQuestionQuestFlagValueFromTrigger(
                    activeQuest.TriggerValue,
                    answerCount,
                    out flagValue))
            {
                return true;
            }
            if (hasRewardSelection && rewardSelectionIndex < answerCount)
            {
                flagValue = GameWorld.QuestData.GetRequiredQuestAnswerFlagValue(
                    rewardSelectionIndex);
                return true;
            }

            var trigger = activeQuest != null
                ? activeQuest.TriggerValue
                : uint.MaxValue;
            FileLogger.Log(
                $"[QuestCompletionApplicationService] Question quest finish rejected: " +
                $"quest={questId} trigger={trigger} answerCount={answerCount}");
            return false;
        }

        private static bool TryResolveQuestionQuestFlagValueFromTrigger(
            uint trigger,
            int answerCount,
            out int flagValue)
        {
            if (trigger == 0)
            {
                flagValue = GameWorld.QuestData.GetRequiredQuestAnswerFlagValue(0);
                return true;
            }
            if (trigger <= (uint)answerCount)
            {
                flagValue = (int)trigger;
                return true;
            }
            flagValue = 1;
            return false;
        }

        private int GetCharacterScalar(
            int characterId,
            string column,
            int fallback)
        {
            // Column is selected only from this class's fixed call sites.
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = new SqliteCommand(
                           $"SELECT {column} FROM characters WHERE character_id=@cid",
                           connection))
                {
                    command.Parameters.AddWithValue("@cid", characterId);
                    var result = command.ExecuteScalar();
                    return result != null ? Convert.ToInt32(result) : fallback;
                }
            }
        }

        private static uint GetCharacterExp(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = new SqliteCommand(
                       "SELECT exp FROM characters WHERE character_id=@cid",
                       connection,
                       transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                var result = command.ExecuteScalar();
                return result != null ? (uint)Convert.ToInt64(result) : 0u;
            }
        }

        internal static SelectCharacter.SkillInfoSnapshot UpdateGrowType(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int chainType,
            int growNumber)
        {
            byte currentGrowType = 0;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT grow_type FROM characters WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                if (value != null)
                    currentGrowType = (byte)Convert.ToInt32(value);
            }

            var firstGrow = currentGrowType & 0xF;
            var secondGrow = (currentGrowType >> 4) & 0xF;
            if (chainType == 1)
                firstGrow = growNumber;
            else if (chainType == 2)
                secondGrow = growNumber;
            var newGrowType = (byte)((secondGrow << 4) | (firstGrow & 0xF));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE characters SET grow_type = @grow WHERE character_id = @cid";
                command.Parameters.AddWithValue("@grow", (int)newGrowType);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
            FileLogger.Log(
                $"[QuestCompletionApplicationService] UpdateGrowType: cid={characterId} " +
                $"chain={chainType} growNumber={growNumber} " +
                $"old=0x{currentGrowType:X2} new=0x{newGrowType:X2}");

            byte job;
            byte characterLevel;
            uint characterExp;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT job, level, exp FROM characters WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        throw new InvalidOperationException(
                            $"character not found: cid={characterId}");
                    }
                    job = (byte)reader.GetInt32(0);
                    characterLevel = (byte)Math.Max(1, Math.Min(255, reader.GetInt32(1)));
                    var expValue = reader.GetInt64(2);
                    characterExp = (uint)Math.Max(
                        0L,
                        Math.Min(uint.MaxValue, expValue));
                }
            }

            var progressRepository = CharacterData.SqliteCharacterProgressRepository
                .FromConnectionString(connection.ConnectionString);
            SelectCharacter.SkillInfoSnapshot skills;
            if (chainType == 1)
            {
                skills = Skills.CharacterSkillProfile.BuildSnapshot(
                    job,
                    firstGrow,
                    0,
                    characterLevel);
                progressRepository.SaveSkillProgress(
                    connection,
                    transaction,
                    characterId,
                    skills);
            }
            else if (chainType == 2)
            {
                skills = progressRepository.LoadSkills(
                    connection,
                    transaction,
                    characterId);
                var grants = Skills.CharacterSkillProfile.GetGrowTypeGrants(
                    job,
                    firstGrow,
                    secondGrow);
                Skills.CharacterSkillProfile.MergeGrants(
                    skills,
                    grants,
                    job,
                    characterLevel,
                    firstGrow,
                    secondGrow);
                progressRepository.SaveSkillProgress(
                    connection,
                    transaction,
                    characterId,
                    skills);
            }
            else
            {
                throw new InvalidOperationException(
                    $"invalid grow type reward chain: {chainType}");
            }

            if (!Progression.CharacterProgressService.PersistLevelAndExp(
                    connection,
                    transaction,
                    characterId,
                    characterLevel,
                    characterExp))
            {
                throw new InvalidOperationException(
                    $"combat stat refresh failed after grow type update: " +
                    $"cid={characterId}");
            }

            return skills;
        }

        internal static SelectCharacter.SkillInfoSnapshot UpdateGrowTypeExact(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int firstGrow,
            int secondGrow)
        {
            byte currentGrowType = 0;
            byte job;
            byte characterLevel;
            uint characterExp;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT job, level, exp, grow_type
FROM characters
WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        throw new InvalidOperationException(
                            $"character not found: cid={characterId}");
                    }

                    job = (byte)reader.GetInt32(0);
                    characterLevel = (byte)Math.Max(1, Math.Min(255, reader.GetInt32(1)));
                    var expValue = reader.GetInt64(2);
                    characterExp = (uint)Math.Max(
                        0L,
                        Math.Min(uint.MaxValue, expValue));
                    currentGrowType = (byte)Math.Max(
                        0,
                        Math.Min(255, reader.GetInt32(3)));
                }
            }

            firstGrow = Math.Max(0, Math.Min(0x0F, firstGrow));
            secondGrow = Math.Max(0, Math.Min(0x0F, secondGrow));
            var newGrowType = (byte)((secondGrow << 4) | (firstGrow & 0x0F));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE characters SET grow_type = @grow WHERE character_id = @cid";
                command.Parameters.AddWithValue("@grow", (int)newGrowType);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
            FileLogger.Log(
                $"[QuestCompletionApplicationService] UpdateGrowTypeExact: cid={characterId} " +
                $"first={firstGrow} second={secondGrow} " +
                $"old=0x{currentGrowType:X2} new=0x{newGrowType:X2}");

            var progressRepository = CharacterData.SqliteCharacterProgressRepository
                .FromConnectionString(connection.ConnectionString);
            var skills = Skills.CharacterSkillProfile.BuildSnapshot(
                job,
                firstGrow,
                secondGrow,
                characterLevel);
            progressRepository.SaveSkillProgress(
                connection,
                transaction,
                characterId,
                skills);

            if (!Progression.CharacterProgressService.PersistLevelAndExp(
                    connection,
                    transaction,
                    characterId,
                    characterLevel,
                    characterExp))
            {
                throw new InvalidOperationException(
                    $"combat stat refresh failed after exact grow type update: " +
                    $"cid={characterId}");
            }

            return skills;
        }

        internal static SelectCharacter.SkillInfoSnapshot UpdateExpertJob(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int expertJobType)
        {
            if (expertJobType <= 0
                || expertJobType > byte.MaxValue
                || !CharacterData.SqliteSubtype0FieldsRepository
                    .SetExpertJobInTransaction(
                        connection,
                        transaction,
                        characterId,
                        (byte)expertJobType))
                throw new InvalidOperationException(
                    $"invalid expert job reward: cid={characterId} type={expertJobType}");

            SqliteExpertJobStateRepository.InitializeInTransaction(
                connection,
                transaction,
                characterId,
                expertJobType);

            byte job;
            byte characterLevel;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "SELECT job, level FROM characters WHERE character_id = @cid";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        throw new InvalidOperationException(
                            $"character not found: cid={characterId}");
                    }

                    job = (byte)reader.GetInt32(0);
                    characterLevel = (byte)Math.Max(1, Math.Min(255, reader.GetInt32(1)));
                }
            }

            var progressRepository = CharacterData.SqliteCharacterProgressRepository
                .FromConnectionString(connection.ConnectionString);
            var skills = progressRepository.LoadSkills(
                connection,
                transaction,
                characterId);
            if (ExpertJobGiveupConfigProvider.TryGet(expertJobType, out var config)
                && config.SkillGrants != null
                && config.SkillGrants.Count > 0)
            {
                var grants = new List<Skills.CharacterSkillProfile.SkillGrant>(
                    config.SkillGrants.Count);
                foreach (var grant in config.SkillGrants)
                {
                    grants.Add(new Skills.CharacterSkillProfile.SkillGrant
                    {
                        SkillIndex = grant.SkillId,
                        Level = grant.Level,
                    });
                }

                Skills.CharacterSkillProfile.MergeGrants(
                    skills,
                    grants,
                    job,
                    characterLevel);
                progressRepository.SaveSkillProgress(
                    connection,
                    transaction,
                    characterId,
                    skills);
            }

            return skills;
        }

        internal static List<QuestFinishSkillPage> CaptureFinishSkillPages(
            SelectCharacter.SkillInfoSnapshot skills)
        {
            var pages = new List<QuestFinishSkillPage>(2);
            for (var pageIndex = 0; pageIndex < 2; pageIndex++)
            {
                var page = new QuestFinishSkillPage();
                var source = skills != null && pageIndex < skills.Pages.Count
                    ? skills.Pages[pageIndex]
                    : null;
                if (source?.Entries != null)
                {
                    foreach (var entry in source.Entries)
                    {
                        if (entry == null)
                            continue;
                        page.Entries.Add(new QuestFinishSkillEntry
                        {
                            Slot = entry.Slot,
                            SkillId = entry.SkillId,
                            Level = entry.Level,
                        });
                    }
                }

                pages.Add(page);
            }

            return pages;
        }

        internal static void UpdateSlotExpansion(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int slotType)
        {
            var flag = ResolveSlotExpansionFlag(slotType);
            if (flag == 0)
                return;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE characters
                    SET ex_equip_slot_stat = (ex_equip_slot_stat | @flag),
                        updated_at = CURRENT_TIMESTAMP
                    WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@flag", flag);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        private static int ResolveSlotExpansionFlag(int slotType)
        {
            switch (slotType)
            {
                case 0:
                    return 1;
                case 1:
                    return 2;
                default:
                    return 0;
            }
        }

        private static int GetMainItemIdentityKey(int itemId)
        {
            return InventoryService.TryResolveMainVirtualSlotByItemId(
                itemId,
                out var slotIndex,
                out _)
                ? -100000 - slotIndex
                : itemId;
        }

    }
}
