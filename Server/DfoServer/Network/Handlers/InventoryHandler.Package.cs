using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Mailbox;
using DfoServer.Game.TitleBook;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        internal async Task<bool> TryHandleDungeonUseStackable(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (body == null || body.Length < 7)
                return false;

            var rewardPolicy = session?.Player?.CurrentRun?.RewardPolicy;
            if (DungeonInteractionPolicy.Resolve(rewardPolicy)
                .ConsumesStackableItems)
            {
                return false;
            }

            var slotIndex = BitConverter.ToInt16(body, 0);
            var listType = (InventoryListType)body[2];
            var instanceValue = BitConverter.ToInt32(body, 3);
            var itemCode = body.Length >= 11 ? BitConverter.ToInt32(body, 7) : 0;
            var (characterId, _) = ResolveOwner(session);

            InventoryLease lease = null;
            TryGetOwnedInventoryLease(session, characterId, out lease);
            UseStackableResponsePlan responsePlan;
            if (lease == null)
            {
                TryBuildDungeonUseStackableResponsePlan(
                    rewardPolicy,
                    null,
                    listType,
                    slotIndex,
                    instanceValue,
                    itemCode,
                    out responsePlan);
            }
            else
            {
                lock (lease.SyncRoot)
                {
                    TryBuildDungeonUseStackableResponsePlan(
                        rewardPolicy,
                        lease.Inventory,
                        listType,
                        slotIndex,
                        instanceValue,
                        itemCode,
                        out responsePlan);
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                responsePlan.AckBody));
            if (responsePlan.RefreshSourceSlot)
                await _refresh.SendUpdateItemList(session, listType, slotIndex);
            FileLogger.Log(
                $"[{ProtocolName}] USE_STACKABLE training: " +
                $"cid={characterId} list={listType} slot={slotIndex} " +
                $"item=0x{itemCode:X8} accepted={responsePlan.Accepted} " +
                "persistentCountUnchanged=true");
            return true;
        }

        internal static bool TryBuildDungeonUseStackableResponsePlan(
            DungeonRewardPolicy rewardPolicy,
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode,
            out UseStackableResponsePlan responsePlan)
        {
            responsePlan = null;
            if (DungeonInteractionPolicy.Resolve(rewardPolicy)
                .ConsumesStackableItems)
            {
                return false;
            }

            var valid = InventoryDeleteService.CanUseStackableForClient(
                inventory,
                listType,
                slotIndex,
                itemCode,
                out var resolvedItemId);
            var responseItemCode = itemCode > 0 ? itemCode : resolvedItemId;
            responsePlan = new UseStackableResponsePlan
            {
                AckBody = valid
                    ? UseStackableAckBuilder.BuildPracticeSuccess(
                        (byte)listType,
                        instanceValue,
                        responseItemCode)
                    : UseStackableAckBuilder.BuildError(
                        (byte)listType,
                        instanceValue,
                        responseItemCode),
                ItemListUpdateBody = null,
                StalePetConsumable = false,
                RefreshSourceSlot = false,
                Accepted = valid,
            };
            return true;
        }

        public async Task Handle_ENUM_CMDPACKET_USE_STACKABLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {

            if (body == null || body.Length < 7)
                return;

            var slotIndex = BitConverter.ToInt16(body, 0);
            var listType = (InventoryListType)body[2];
            var instanceValue = BitConverter.ToInt32(body, 3);
            var itemCode = body.Length >= 11 ? BitConverter.ToInt32(body, 7) : 0;

            var (cid, aid) = ResolveOwner(session);

            if (await TryRejectExpiredStackableSourceAsync(
                    session,
                    header,
                    cid,
                    listType,
                    slotIndex,
                    instanceValue,
                    itemCode))
            {
                return;
            }

            if (await TryRejectChannelRestrictedTeleportConsumableAsync(
                    session,
                    header,
                    cid,
                    listType,
                    slotIndex,
                    instanceValue,
                    itemCode))
            {
                return;
            }

            if (await TryRejectUsableCountLimitExceededAsync(
                    session,
                    header,
                    cid,
                    listType,
                    slotIndex,
                    instanceValue,
                    itemCode))
            {
                return;
            }

            if (await TryHandleExpertJobRecipeLearning(
                    session, header.type, cid, listType, slotIndex, instanceValue, itemCode))
                return;

            if (await TryHandleQuestCompletionTicketAsync(
                    session,
                    header.type,
                    cid,
                    listType,
                    slotIndex,
                    instanceValue,
                    itemCode))
                return;

            AccountCargoUpgradeToolResult accountCargoToolResult = null;
            bool accountCargoToolHandled = false;
            InventoryLease lease = null;
            if (TryGetOwnedInventoryLease(session, cid, out lease))
            {
                lock (lease.SyncRoot)
                    accountCargoToolHandled = InventoryCargoRuntimeService.TryUseAccountCargoUpgradeTool(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        out accountCargoToolResult)
                        && accountCargoToolResult.Handled;
            }

            if (accountCargoToolHandled)
            {
                if (accountCargoToolResult.Success
                    && !OnlineInventoryMutationCommitCoordinator.TryCommit(
                        lease,
                        "account-cargo-upgrade-tool"))
                {
                    FileLogger.Log($"[{ProtocolName}] USE_STACKABLE account-cargo-upgrade: commit failed item=0x{itemCode:X8} slot={slotIndex}");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        header.type,
                        UseStackableAckBuilder.BuildError((byte)listType, instanceValue, itemCode)));
                    return;
                }

                await SendAccountCargoUpgradeToolResponse(
                    session,
                    header.type,
                    listType,
                    slotIndex,
                    instanceValue,
                    itemCode,
                    accountCargoToolResult);
                return;
            }

            PersonalCargoUpgradeTicketResult cargoTicketResult = null;
            bool cargoTicketHandled = false;
            if (lease != null)
            {
                lock (lease.SyncRoot)
                    cargoTicketHandled = InventoryCargoRuntimeService.TryUsePersonalCargoUpgradeTicket(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        itemCode,
                        out cargoTicketResult)
                        && cargoTicketResult.Handled;
            }

            if (cargoTicketHandled)
            {
                if (cargoTicketResult.Success
                    && !OnlineInventoryMutationCommitCoordinator.TryCommit(
                        lease,
                        "personal-cargo-upgrade-ticket"))
                {
                    FileLogger.Log($"[{ProtocolName}] USE_STACKABLE upgrade-cargo: commit failed item=0x{itemCode:X8} slot={slotIndex}");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        header.type,
                        UseStackableAckBuilder.BuildError((byte)listType, instanceValue, itemCode)));
                    return;
                }

                await SendPersonalCargoUpgradeTicketResponse(
                    session,
                    header.type,
                    listType,
                    slotIndex,
                    instanceValue,
                    itemCode,
                    cargoTicketResult);
                return;
            }

            var runeRequest = new EquipmentEffectRuneUseRequest
            {
                SourceListType = listType,
                SourceSlotIndex = slotIndex,
                SourceInstanceValue = instanceValue,
                ExpectedSourceItemTemplateId = itemCode,
                RawBody = body,
            };
            if (TryUseOnlineEquipmentEffectRune(session, cid, runeRequest, out var runeResult)
                && runeResult.Handled)
            {
                await SendEquipmentEffectRuneResponse(
                    session,
                    listType,
                    slotIndex,
                    instanceValue,
                    itemCode,
                    runeResult,
                    header.type);
                return;
            }

            // 远古精灵秘药 [exp bonus rate]：服务端权威使用链路（副本内限制、
            // 共享冷却、效果持久化），成功/拦截都要回 ACK 阻断通用消耗流程。
            // 客户端对该道具类型不做本地扣减预测（PVF 侧原为"非消耗品"），
            // 成功后必须补发槽位更新，否则物品要到回城刷新背包才消失。
            InventoryMutationResult experiencePotionMutation = null;
            string experiencePotionDetail = null;
            var experiencePotionHandled = false;
            if (TryGetOwnedInventoryLease(session, cid, out lease))
            {
                lock (lease.SyncRoot)
                {
                    experiencePotionHandled = ExperienceBonusPotionService.TryUse(
                        lease,
                        cid,
                        listType,
                        slotIndex,
                        itemCode,
                        session.Player?.CurrentRun != null,
                        out experiencePotionMutation,
                        out experiencePotionDetail);
                }
            }

            if (experiencePotionHandled)
            {
                var potionSuccess = experiencePotionMutation != null;
                var potionPlan = BuildUseStackableResponsePlan(
                    potionSuccess,
                    experiencePotionMutation,
                    listType,
                    slotIndex,
                    instanceValue,
                    itemCode);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    potionPlan.AckBody));
                if (potionSuccess)
                    await _refresh.SendUpdateItemList(session, listType, slotIndex);
                FileLogger.Log(
                    $"[{ProtocolName}] USE_STACKABLE exp-bonus potion: "
                    + $"cid={cid} item=0x{itemCode:X8} success={potionSuccess} "
                    + experiencePotionDetail);
                return;
            }

            InventoryMutationResult result = null;
            InventoryStackableUseCommitResult stackableUseResult = null;
            if (TryGetOwnedInventoryLease(session, cid, out lease))
            {
                stackableUseResult = InventoryDeleteCommitService.TryCommitStackableUseDetailed(
                    lease,
                    listType,
                    slotIndex,
                    itemCode);
                result = stackableUseResult?.Mutation;
            }

            var consumed = stackableUseResult != null && stackableUseResult.Consumed;
            var responsePlan = BuildUseStackableResponsePlan(consumed, result, listType, slotIndex, instanceValue, itemCode);
            if (!consumed)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, responsePlan.AckBody));
                if (stackableUseResult?.SourceExpiredDeleted == true)
                {
                    await _refresh.SendUpdateItemList(session, listType, slotIndex);
                    FileLogger.Log($"[{ProtocolName}] USE_STACKABLE: expired source removed item 0x{stackableUseResult.ItemTemplateId:X8} at listType={listType} slot={slotIndex}");
                    return;
                }

                FileLogger.Log(responsePlan.StalePetConsumable
                    ? $"[{ProtocolName}] USE_STACKABLE: stale pet consumable use acknowledged item 0x{itemCode:X8} at listType={listType} slot={slotIndex}"
                    : $"[{ProtocolName}] USE_STACKABLE: failed to consume item 0x{itemCode:X8} at listType={listType} slot={slotIndex}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, responsePlan.AckBody));
            if (EpicBuffPotionDefinition.IsItem(stackableUseResult.ItemTemplateId))
            {
                await EpicBuffPotionBuffNotifier.SendAddAsync(session);
                EpicBuffPotionBuffNotifier.ScheduleRemoveForCurrentEffect(
                    session,
                    cid);
            }
            if (result.UsableCountState != null)
                await SendUsableCountLimitUpdateAsync(session, result.UsableCountState);
            session.GameSession?.QuestManager
                ?.RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
                    lease,
                    result);

            var petSatietyLog = result.PetSatietyChanged
                ? $" petSatiety key={result.PetCreatureKey} {result.PetSatietyBefore}->{result.PetSatietyAfter}"
                : string.Empty;
            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE: consumed 1x item 0x{itemCode:X8} from slot {slotIndex}, remaining={result.RemainingStackCount}{petSatietyLog}");
        }

        private async Task<bool> TryRejectExpiredStackableSourceAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int expectedItemTemplateId)
        {
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            int responseItemTemplateId;
            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        session.SessionId,
                        characterId))
                {
                    return false;
                }

                var source = lease.Inventory.GetItem(listType, slotIndex);
                if (!InventoryItemLifecycleService.IsExpired(
                        source,
                        InventoryItemLifecycleService.UtcNowUnixSeconds())
                    || (expectedItemTemplateId > 0
                        && source.ItemId != expectedItemTemplateId))
                {
                    return false;
                }

                responseItemTemplateId = source.ItemId;
            }

            InventoryMutationResult mutation = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "use-stackable-expired-source",
                (connection, transaction) =>
                    InventoryItemLifecycleService.TryRemoveExpiredSource(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        responseItemTemplateId,
                        InventoryItemLifecycleService.UtcNowUnixSeconds(),
                        out mutation));
            if (!committed || mutation == null)
                return false;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                UseStackableAckBuilder.BuildError(
                    (byte)listType,
                    instanceValue,
                    responseItemTemplateId)));
            await _refresh.SendUpdateItemList(session, listType, slotIndex);
            FileLogger.Log(
                $"[{ProtocolName}] USE_STACKABLE: expired source removed " +
                $"cid={characterId} item=0x{responseItemTemplateId:X8} " +
                $"listType={listType} slot={slotIndex}");
            return true;
        }

        private async Task<bool>
            TryRejectChannelRestrictedTeleportConsumableAsync(
                EnhancedClientSession session,
                GamePacketHeader header,
                int characterId,
                InventoryListType listType,
                short slotIndex,
                int instanceValue,
                int expectedItemTemplateId)
        {
            if (!GameNetworkConfig.IsChannel100Listener(
                    session.ListenerPort)
                || !TryGetOwnedInventoryLease(
                    session,
                    characterId,
                    out var lease))
            {
                return false;
            }

            var itemTemplateId = 0;
            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        session.SessionId,
                        characterId))
                {
                    return false;
                }

                var source = lease.Inventory.GetItem(
                    listType,
                    slotIndex);
                if (source == null
                    || source.ItemId <= 0
                    || expectedItemTemplateId > 0
                    && source.ItemId != expectedItemTemplateId)
                {
                    return false;
                }

                itemTemplateId = source.ItemId;
            }

            if (!TeleportConsumableDefinitionProvider.TryResolve(
                    itemTemplateId,
                    out var definition)
                || GameChannelTeleportPolicy.CanUseConsumable(
                    session.ListenerPort,
                    definition))
            {
                return false;
            }

            FileLogger.Log(
                $"[{ProtocolName}] USE_STACKABLE teleport rejected by " +
                $"channel policy: cid={characterId} " +
                $"listener={session.ListenerPort} " +
                $"item=0x{itemTemplateId:X8} kind={definition.Kind} " +
                $"targetTown={definition.TargetTownId?.ToString() ?? "dynamic"} " +
                $"validDefinition={definition.IsValid}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                UseStackableAckBuilder.BuildError(
                    (byte)listType,
                    instanceValue,
                    itemTemplateId)));
            if (_refresh != null)
            {
                await _refresh.SendUpdateItemList(
                    session,
                    listType,
                    slotIndex);
            }
            await ChannelTownRestrictionSender.SendAsync(session);
            return true;
        }

        private async Task<bool> TryRejectUsableCountLimitExceededAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int expectedItemTemplateId)
        {
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            int resolvedItemId;
            string connectionString;
            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        session.SessionId,
                        characterId))
                {
                    return false;
                }

                if (!InventoryDeleteService.CanUseStackableForClient(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        expectedItemTemplateId,
                        out resolvedItemId))
                {
                    return false;
                }

                connectionString = lease.Inventory.Database?.ConnectionString
                    ?? _database?.ConnectionString;
            }

            if (string.IsNullOrWhiteSpace(connectionString)
                || UsableCountLimitService.CanUse(
                    connectionString,
                    characterId,
                    resolvedItemId))
            {
                return false;
            }

            FileLogger.Log(
                $"[{ProtocolName}] USE_STACKABLE usable-count limit reached: " +
                $"cid={characterId} item=0x{resolvedItemId:X8} " +
                $"listType={listType} slot={slotIndex}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                UseStackableAckBuilder.BuildError(
                    (byte)listType,
                    instanceValue,
                    resolvedItemId)));
            return true;
        }

        private async Task<bool> TryHandleExpertJobRecipeLearning(
            EnhancedClientSession session,
            ushort responseType,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode)
        {
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            var sourceItemId = itemCode;
            lock (lease.SyncRoot)
                sourceItemId = lease.Inventory.GetItem(listType, slotIndex)?.ItemId ?? sourceItemId;
            if (!ExpertJobConfigRegistry.TryResolveRecipe(
                    sourceItemId,
                    out var recipeConfig))
                return false;
            var recipeExpertJobType = recipeConfig.ExpertJobType;
            if (session.Player?.Subtype0Tail?.ExpertJobType != recipeExpertJobType)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    responseType,
                    UseStackableAckBuilder.BuildError(
                        ExpertJobRecipeLearningService.ErrorRequirementsNotMet,
                        (byte)listType,
                        instanceValue,
                        sourceItemId)));
                return true;
            }

            var operationGate = _expertJobOperations.GetGate(characterId);
            await operationGate.WaitAsync();
            try
            {
                var state = _expertJobStates.Load(
                    characterId,
                    recipeExpertJobType);
                ExpertJobRecipeLearningResult result;
                lock (lease.SyncRoot)
                {
                    result = ExpertJobRecipeLearningService.TryLearn(
                        lease.Inventory,
                        listType,
                        slotIndex,
                        sourceItemId,
                        session.Player.Subtype0Tail.ExpertJobExp,
                        state,
                        recipeConfig);
                }
                if (!result.Handled)
                    return false;

                if (!result.Success)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        responseType,
                        UseStackableAckBuilder.BuildError(
                            result.ErrorCode != 0
                                ? result.ErrorCode
                                : ExpertJobRecipeLearningService.ErrorRequirementsNotMet,
                            (byte)listType,
                            instanceValue,
                            sourceItemId)));
                    return true;
                }

                var ack = UseStackableAckBuilder.BuildSuccess(
                    slotIndex,
                    (byte)listType,
                    instanceValue,
                    sourceItemId);

                if (!_expertJobPersistence.Save(
                        lease,
                        lease,
                        (connection, transaction) => _expertJobStates.SaveRecipeInTransaction(
                            connection,
                            transaction,
                            characterId,
                            result.RecipeId)))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        responseType,
                        UseStackableAckBuilder.BuildError(
                            ExpertJobRecipeLearningService.ErrorRequirementsNotMet,
                            (byte)listType,
                            instanceValue,
                            sourceItemId)));
                    return true;
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType, ack));
                await _refresh.SendUpdateItemList(session, listType, slotIndex);
                await SendExpertJobRecipeInfo(
                    session,
                    recipeExpertJobType,
                    state);
                FileLogger.Log(
                    $"[{ProtocolName}] EXPERT_JOB_RECIPE cid={characterId} " +
                    $"type={recipeExpertJobType} " +
                    $"item={sourceItemId} recipe={result.RecipeId} " +
                    $"remaining={result.RemainingCount}");
                return true;
            }
            finally
            {
                operationGate.Release();
            }
        }

        private static async Task SendExpertJobRecipeInfo(
            EnhancedClientSession session,
            int expertJobType,
            ExpertJobState state)
        {
            var body = ExpertJobInfoBodyBuilder.BuildProjectedBody(
                expertJobType,
                state,
                session.Player.Subtype0Tail.ExpertJobExp);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00CD, body));
        }

        public async Task Handle_ADD_EQUIPMENT_EFFECT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var commandType = (ushort)CmdPacketType.ADD_EQUIPMENT_EFFECT;
            FileLogger.Log($"[{ProtocolName}] ADD_EQUIPMENT_EFFECT type=0x{commandType:X4} raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!EquipmentEffectRuneUseRequest.TryParseAddEquipmentEffectBody(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00 }));
                return;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!TryUseOnlineEquipmentEffectRune(session, cid, request, out var result)
                || result == null
                || !result.Handled)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00, 0x04 }));
                FileLogger.Log($"[{ProtocolName}] ADD_EQUIPMENT_EFFECT: unhandled sourceSlot={request.SourceSlotIndex} target={request.TargetListType}:{request.TargetSlotIndex}");
                return;
            }

            await SendEquipmentEffectRuneResponse(
                session,
                request.SourceListType,
                request.SourceSlotIndex,
                request.SourceInstanceValue,
                result.SourceItemTemplateId,
                result,
                commandType,
                result.Success ? BuildAddEquipmentEffectAck(body) : new byte[] { 0x00, 0x04 });
        }

        private bool TryUseOnlineEquipmentEffectRune(
            EnhancedClientSession session,
            int characterId,
            EquipmentEffectRuneUseRequest request,
            out EquipmentEffectRuneUseResult result)
        {
            result = null;
            if (request == null)
                return false;

            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
            {
                if (InventoryEquipmentMutationService.IsEquipmentEffectRuneItem(request.ExpectedSourceItemTemplateId))
                {
                    result = new EquipmentEffectRuneUseResult
                    {
                        Status = EquipmentEffectRuneStatus.MissingSource,
                        SourceListType = request.SourceListType,
                        SourceSlotIndex = request.SourceSlotIndex,
                        SourceInstanceValue = request.SourceInstanceValue,
                        SourceItemTemplateId = request.ExpectedSourceItemTemplateId,
                    };
                    return true;
                }

                return false;
            }

            lock (lease.SyncRoot)
                return InventoryEquipmentMutationService.TryUseEquipmentEffectRune(lease.Inventory, request, out result);
        }

        private async Task SendEquipmentEffectRuneResponse(
            EnhancedClientSession session,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode,
            EquipmentEffectRuneUseResult result,
            ushort responseType = 0x002C,
            byte[] ackOverride = null)
        {
            var responseItemCode = itemCode != 0 ? itemCode : result.SourceItemTemplateId;
            var responseInstanceValue = instanceValue != 0 ? instanceValue : result.SourceInstanceValue;
            var ackBody = ackOverride ?? (result.Success
                ? UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, responseInstanceValue, responseItemCode)
                : UseStackableAckBuilder.BuildError((byte)listType, responseInstanceValue, responseItemCode));

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType, ackBody));

            if (!result.Success)
            {
                FileLogger.Log($"[{ProtocolName}] USE_STACKABLE equipment-effect: failed status={result.Status} item=0x{result.SourceItemTemplateId:X8} listType={listType} slot={slotIndex}");
                return;
            }

            await _refresh.SendUpdateItemList(session, result.SourceListType, result.SourceSlotIndex);
            await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);

            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE equipment-effect: rune=0x{result.SourceItemTemplateId:X8} effect={result.AppliedEffectId} target=0x{result.TargetItemTemplateId:X8}@{result.TargetListType}:{result.TargetSlotIndex} remaining={result.SourceRemainingStackCount}");
        }

        private static byte[] BuildAddEquipmentEffectAck(byte[] requestBody)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            if (requestBody != null && requestBody.Length > 0)
                writer.WriteBytes(requestBody);
            return writer.ToArray();
        }

        private async Task SendPersonalCargoUpgradeTicketResponse(
            EnhancedClientSession session,
            ushort responseType,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode,
            PersonalCargoUpgradeTicketResult result)
        {
            var responseItemCode = itemCode != 0 ? itemCode : result.ItemTemplateId;
            var ackBody = result.Success
                ? UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, instanceValue, responseItemCode)
                : UseStackableAckBuilder.BuildError((byte)listType, instanceValue, responseItemCode);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType, ackBody));

            if (!result.Success)
            {
                FileLogger.Log($"[{ProtocolName}] USE_STACKABLE upgrade-cargo: failed status={result.Status} item=0x{result.ItemTemplateId:X8} listType={listType} slot={slotIndex} current={result.PreviousListParam16}");
                return;
            }

            if (result.ConsumedItem != null)
                await _refresh.SendUpdateItemList(session, result.ConsumedItem.ListType, result.ConsumedItem.SlotIndex);

            await _refresh.SendItemListRefresh(session, InventoryListType.PersonalCargo);
            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE upgrade-cargo: item=0x{result.ItemTemplateId:X8} slot={slotIndex} personalCargo={result.PreviousListParam16}->{result.NewListParam16} remaining={result.ConsumedItem?.RemainingStackCount ?? 0}");
        }

        private async Task SendAccountCargoUpgradeToolResponse(
            EnhancedClientSession session,
            ushort responseType,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode,
            AccountCargoUpgradeToolResult result)
        {
            var responseItemCode = itemCode != 0 ? itemCode : result.ItemTemplateId;
            var ackBody = result.Success
                ? UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, instanceValue, responseItemCode)
                : UseStackableAckBuilder.BuildError((byte)listType, instanceValue, responseItemCode);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType, ackBody));

            if (!result.Success)
            {
                FileLogger.Log($"[{ProtocolName}] USE_STACKABLE account-cargo-upgrade: failed status={result.Status} item=0x{result.ItemTemplateId:X8} listType={listType} slot={slotIndex} current={result.PreviousSelectionKey}");
                return;
            }

            if (result.ConsumedItem != null)
                await _refresh.SendUpdateItemList(session, result.ConsumedItem.ListType, result.ConsumedItem.SlotIndex);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132,
                CommonPacketBodyBuilder.BuildSuccessAck()));
            await _refresh.SendItemListRefresh(session, InventoryListType.AccountCargo);
            FileLogger.Log($"[{ProtocolName}] USE_STACKABLE account-cargo-upgrade: item=0x{result.ItemTemplateId:X8} slot={slotIndex} accountCargo={result.PreviousSelectionKey}->{result.NewSelectionKey} remaining={result.ConsumedItem?.RemainingStackCount ?? 0}");
        }

        internal static UseStackableResponsePlan BuildUseStackableResponsePlan(
            bool consumed,
            InventoryMutationResult result,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode)
        {
            var stalePetConsumable = !consumed && IsPetConsumableSlot(listType, slotIndex);
            var responseItemCode = itemCode != 0 ? itemCode : result?.ItemTemplateId ?? 0;
            var responseInstanceValue = instanceValue != 0 ? instanceValue : result?.InstanceValue ?? 0;
            var ackBody = consumed || stalePetConsumable
                ? UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, responseInstanceValue, responseItemCode)
                : UseStackableAckBuilder.BuildError((byte)listType, responseInstanceValue, responseItemCode);

            return new UseStackableResponsePlan
            {
                AckBody = ackBody,
                ItemListUpdateBody = null,
                StalePetConsumable = stalePetConsumable,
                Accepted = consumed || stalePetConsumable,
            };
        }

        private static bool IsPetConsumableSlot(InventoryListType listType, short slotIndex)
        {
            return listType == InventoryListType.Pet
                && slotIndex >= 189
                && slotIndex <= InventoryService.CreatureSlotEnd;
        }

        internal sealed class UseStackableResponsePlan
        {
            public byte[] AckBody { get; set; }

            public byte[] ItemListUpdateBody { get; set; }

            public bool StalePetConsumable { get; set; }

            public bool RefreshSourceSlot { get; set; }

            public bool Accepted { get; set; }
        }

        public async Task Handle_OPEN_AVATAR_PACKAGE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            var parsedAvatar = AvatarPackageOpenRequest.TryParse(body, out var request);
            if (!parsedAvatar)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: parse failed");
            }
            else
            {
                var (cid, _) = ResolveOwner(session);
                if (TryOpenOnlineAvatarPackage(
                        session,
                        cid,
                        request,
                        out var result,
                        out var persistenceFailed))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207, AvatarPackageAckBuilder.BuildSuccess(result.SlotIndex)));
                    if (result.GrantedItems.Count > 0)
                    {
                        await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                            SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems)));
                    }

                    await SendPackageMainItemUpdates(session, result.SlotIndex, result.SourceRemainingStackCount, result.GrantedItems);
                    await SendSpecialUpdateListForGrantedItems(session, result.GrantedItems);

                    if (result.ActivatedPremiums.Count > 0)
                        await Game.Premium.PremiumService.ActivateAndNotify(
                            session,
                            result.ActivatedPremiums,
                            _sqliteSelectCharacterDataSource,
                            _database);

                    FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: OK slot={result.SlotIndex} item=0x{result.PackageItemTemplateId:X8} avatar={result.AddedAvatarCount} main={result.AddedMainItemCount} pet={result.AddedPetCount}");
                    return;
                }

                if (persistenceFailed)
                {
                    FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: persistence failed slot={request.SlotIndex}");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207, new byte[] { 0x00 }));
                    return;
                }

                FileLogger.Log($"[{ProtocolName}] OPEN_AVATAR_PACKAGE: avatar path failed slot={request.SlotIndex} choices={request.Choices.Count}, trying general package 0x0207");
            }

            if (await TryHandleOpenPackage0207(session, header, body))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207, new byte[] { 0x00 }));
        }

        public async Task Handle_OPEN_SELECTABLE_PACKAGE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            var parsedSelectable = SelectablePackageOpenRequest.TryParse(body, out var request);
            if (!parsedSelectable)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: parse failed");
            }
            else
            {
                var (cid, _) = ResolveOwner(session);
                if (TryOpenOnlineSelectablePackage(
                        session,
                        cid,
                        request,
                        out var result,
                        out var persistenceFailed))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0, SelectablePackageAckBuilder.BuildSuccess(result.SlotIndex, result.GrantedItems)));

                    await SendPackageMainItemUpdates(session, result.SlotIndex, result.SourceRemainingStackCount, result.GrantedItems);
                    await SendSpecialUpdateListForGrantedItems(session, result.GrantedItems);

                    if (result.ActivatedPremiums.Count > 0)
                        await Game.Premium.PremiumService.ActivateAndNotify(
                            session,
                            result.ActivatedPremiums,
                            _sqliteSelectCharacterDataSource,
                            _database);

                    FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: OK slot={result.SlotIndex} item=0x{result.PackageItemTemplateId:X8} reward=0x{result.RewardItemTemplateId:X8} main={result.AddedMainItemCount} avatar={result.AddedAvatarCount} pet={result.AddedPetCount} ackItems={result.GrantedItems.Count}");
                    return;
                }

                if (persistenceFailed)
                {
                    FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: persistence failed slot={request.SlotIndex} selected=0x{request.SelectedItemTemplateId:X8}");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        0x00A0,
                        BuildOpenSelectablePackageFallbackErrorBody()));
                    return;
                }

                FileLogger.Log($"[{ProtocolName}] OPEN_SELECTABLE_PACKAGE: selectable path failed slot={request.SlotIndex} selected=0x{request.SelectedItemTemplateId:X8}, trying general booster");
            }

            if (await TryHandleBoosterOpen(session, header, body))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x00A0,
                BuildOpenSelectablePackageFallbackErrorBody()));
        }

        public async Task Handle_USE_BOOSTER_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!await TryHandleBoosterOpen(session, header, body))
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
        }

        internal static byte[] BuildOpenSelectablePackageFallbackErrorBody()
        {
            return CommonPacketBodyBuilder.BuildCmdError(BoosterUseResult.ErrorInvalidRequest);
        }

        public async Task Handle_OPEN_MAGIC_BOX(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var elapsed = Stopwatch.StartNew();
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!MagicBoxOpenRequest.TryParse(body, out var request) || request.ListType != InventoryListType.Main)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: parse/list failed");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var materialSlotIndex = request.MaterialSlotIndex >= 0
                ? (short?)request.MaterialSlotIndex
                : null;
            var expectedMaterialItemTemplateId = request.MaterialItemTemplateId > 0
                ? request.MaterialItemTemplateId
                : 0;

            var (cid, aid) = ResolveOwner(session);
            if (!TryUseOnlineBoosterItem(
                    session,
                    cid,
                    new BoosterUseRequest
                    {
                        SlotIndex = request.SlotIndex,
                        SelectedItemTemplateIds = Array.Empty<int>(),
                        ExpectedItemTemplateId = request.ItemTemplateId,
                        MaterialSlotIndex = materialSlotIndex,
                        ExpectedMaterialItemTemplateId = expectedMaterialItemTemplateId,
                        RequestedCount = request.RequestedCount,
                    },
                    out var result,
                    out var persistenceFailed))
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: failed cid={cid} aid={aid} slot={request.SlotIndex} item=0x{request.ItemTemplateId:X8} material=0x{request.MaterialItemTemplateId:X8}@{request.MaterialSlotIndex} requested={request.RequestedCount} persistenceFailed={persistenceFailed} elapsed={elapsed.ElapsedMilliseconds}ms");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    BuildMagicBoxFailureAckBody(header.type, result)));
                await SendBoosterMaterialNotice(session, result);
                await RefreshExpiredBoosterSourceAsync(session, result);
                return;
            }

            result.MagicBoxClientType = request.RawListType;
            await SendBoosterUseResult(session, header.type, result);
            if (result.UsableCountState != null)
                await SendUsableCountLimitUpdateAsync(session, result.UsableCountState);
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} requested={request.RequestedCount} applied={result.ConsumedSourceCount} remaining={result.SourceRemainingStackCount} material=0x{result.ConsumedMaterialItemTemplateId:X8}x{result.ConsumedMaterialCount}@{result.ConsumedMaterialSlotIndex} materialRemaining={result.ConsumedMaterialRemainingStackCount}{FormatBoosterOpenState(result)} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))} elapsed={elapsed.ElapsedMilliseconds}ms");
        }

        public async Task Handle_OPEN_MAGIC_BOX_SINGLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var elapsed = Stopwatch.StartNew();
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!MagicBoxOpenRequest.TryParseSingle(body, out var request) || request.ListType != InventoryListType.Main)
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: parse/list failed");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var materialSlotIndex = request.MaterialSlotIndex >= 0
                ? (short?)request.MaterialSlotIndex
                : null;
            var expectedMaterialItemTemplateId = request.MaterialItemTemplateId > 0
                ? request.MaterialItemTemplateId
                : 0;

            var (cid, aid) = ResolveOwner(session);
            if (!TryUseOnlineBoosterItem(
                    session,
                    cid,
                    new BoosterUseRequest
                    {
                        SlotIndex = request.SlotIndex,
                        SelectedItemTemplateIds = Array.Empty<int>(),
                        ExpectedItemTemplateId = request.ItemTemplateId,
                        MaterialSlotIndex = materialSlotIndex,
                        ExpectedMaterialItemTemplateId = expectedMaterialItemTemplateId,
                        RequestedCount = request.RequestedCount,
                    },
                    out var result,
                    out var persistenceFailed))
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: failed cid={cid} aid={aid} slot={request.SlotIndex} materialSlot={(materialSlotIndex.HasValue ? materialSlotIndex.Value.ToString() : "auto")} requested={request.RequestedCount} persistenceFailed={persistenceFailed} elapsed={elapsed.ElapsedMilliseconds}ms");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    BuildMagicBoxFailureAckBody(header.type, result)));
                await SendBoosterMaterialNotice(session, result);
                await RefreshExpiredBoosterSourceAsync(session, result);
                return;
            }

            result.MagicBoxClientType = request.RawListType;
            await SendBoosterUseResult(session, header.type, result);
            if (result.UsableCountState != null)
                await SendUsableCountLimitUpdateAsync(session, result.UsableCountState);
            FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX_SINGLE: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} requested={request.RequestedCount} applied={result.ConsumedSourceCount} remaining={result.SourceRemainingStackCount} material=0x{result.ConsumedMaterialItemTemplateId:X8}x{result.ConsumedMaterialCount}@{result.ConsumedMaterialSlotIndex} materialRemaining={result.ConsumedMaterialRemainingStackCount}{FormatBoosterOpenState(result)} rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))} elapsed={elapsed.ElapsedMilliseconds}ms");
        }

        private async Task<bool> TryHandleBoosterOpen(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var elapsed = Stopwatch.StartNew();
            short? slotIndex = body != null && body.Length >= 2
                ? BitConverter.ToInt16(body, 0)
                : (short?)null;
            var selectedItemTemplateIds = ParseBoosterSelectionItemIds(body);
            var selectedText = selectedItemTemplateIds.Count == 0
                ? "none"
                : string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"));
            FileLogger.Log($"[{ProtocolName}] USE_BOOSTER raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} slot={(slotIndex.HasValue ? slotIndex.Value.ToString() : "auto")} selected={selectedText}");

            if (TryBuildCrystalContractBodyFromUpdateRequest(header.type, body, out var crystalContractBody))
            {
                var owner = ResolveOwner(session);
                if (!_sqliteSelectCharacterDataSource.TrySaveCrystalContractSelection(owner.characterId, crystalContractBody))
                {
                    FileLogger.Log($"[{ProtocolName}] UPDATE_CONTRACT_OF_CUBE_INFO: failed cid={owner.characterId} body={BitConverter.ToString(crystalContractBody)}");
                    return false;
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
                FileLogger.Log($"[{ProtocolName}] UPDATE_CONTRACT_OF_CUBE_INFO: saved cid={owner.characterId} body={BitConverter.ToString(crystalContractBody)}");
                return true;
            }

            if (slotIndex == 0 && header.type == 0x0218)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
                FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: confirm ack type=0x{header.type:X4}");
                return true;
            }

            var (cid, aid) = ResolveOwner(session);
            if (!TryUseOnlineBoosterItem(session, cid, new BoosterUseRequest
            {
                SlotIndex = slotIndex,
                SelectedItemTemplateIds = selectedItemTemplateIds,
            }, out var result, out var persistenceFailed))
            {
                if (result != null && result.ErrorCode != 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        header.type,
                        BuildBoosterFailureAckBody(result)));
                    await SendBoosterMaterialNotice(session, result);
                    await RefreshExpiredBoosterSourceAsync(session, result);
                    FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: rejected cid={cid} aid={aid} slot={(slotIndex.HasValue ? slotIndex.Value.ToString() : "auto")} error=0x{result.ErrorCode:X2} elapsed={elapsed.ElapsedMilliseconds}ms");
                    return true;
                }

                FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: failed cid={cid} aid={aid} slot={(slotIndex.HasValue ? slotIndex.Value.ToString() : "auto")} persistenceFailed={persistenceFailed} elapsed={elapsed.ElapsedMilliseconds}ms");
                return false;
            }

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] USE_BOOSTER: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} remaining={result.SourceRemainingStackCount}, rewards={string.Join(",", result.Rewards.Select(r => $"{r.ListType}:0x{r.ItemTemplateId:X8}x{r.GrantedCount}@{r.SlotIndex}"))}, elapsed={elapsed.ElapsedMilliseconds}ms");
            return true;
        }

        private async Task<bool> TryHandleQuestCompletionTicketAsync(
            EnhancedClientSession session,
            ushort responseType,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            int instanceValue,
            int itemCode)
        {
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            var service = new QuestCompletionTicketService(_database.ConnectionString);
            QuestCompletionTicketUseResult result;
            lock (lease.SyncRoot)
            {
                result = service.UseBySlot(new QuestCompletionTicketUseRequest
                {
                    SessionId = session.SessionId,
                    CharacterId = characterId,
                    AccountId = lease.AccountId,
                    Lease = lease,
                    ListType = listType,
                    SlotIndex = slotIndex,
                    ExpectedItemTemplateId = itemCode,
                });
            }

            if (!result.Handled)
                return false;

            var responseItemCode = itemCode != 0 ? itemCode : result.ItemTemplateId;
            if (!result.Success)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    responseType,
                    UseStackableAckBuilder.BuildError(
                        (byte)listType,
                        instanceValue,
                        responseItemCode)));

                if (result.Status == QuestCompletionTicketUseStatus.MissingSource
                    || result.Status == QuestCompletionTicketUseStatus.ConsumeFailed)
                {
                    await _refresh.SendUpdateItemList(session, listType, slotIndex);
                }

                FileLogger.Log(
                    $"[{ProtocolName}] USE_STACKABLE quest-ticket: failed " +
                    $"status={result.Status} item=0x{result.ItemTemplateId:X8} " +
                    $"action={result.ActionKind} listType={listType} slot={slotIndex} " +
                    $"detail={result.Detail}");
                return true;
            }

            var questManager = session.GameSession?.QuestManager;
            if (questManager != null)
            {
                foreach (var finish in result.FinishResults)
                    await questManager.SendPreFinishAckNotificationsAsync(finish);
                foreach (var finish in result.FinishResults)
                    await questManager.ProjectFinishedQuestAsync(finish, false);
            }

            await SendQuestCompletionTicketQuestStateAsync(
                session,
                questManager,
                characterId);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                responseType,
                UseStackableAckBuilder.BuildSuccess(
                    slotIndex,
                    (byte)listType,
                    instanceValue,
                    responseItemCode)));

            foreach (var achievement in result.AchievementResults)
                await SendQuestCompletionTicketAchievementAsync(session, characterId, achievement);

            if (result.ConsumedItem?.UsableCountState != null)
                await SendUsableCountLimitUpdateAsync(session, result.ConsumedItem.UsableCountState);

            if (result.ConsumedItem != null)
                await _refresh.SendUpdateItemList(session, result.ConsumedItem.ListType, result.ConsumedItem.SlotIndex);

            FileLogger.Log(
                $"[{ProtocolName}] USE_STACKABLE quest-ticket: item=0x{result.ItemTemplateId:X8} " +
                $"action={result.ActionKind} completed={result.CompletedQuestIds.Count} " +
                $"slot={slotIndex} remaining={result.ConsumedItem?.RemainingStackCount ?? 0}");
            return true;
        }

        private async Task SendQuestCompletionTicketQuestStateAsync(
            EnhancedClientSession session,
            QuestManager questManager,
            int characterId)
        {
            try
            {
                if (questManager != null)
                {
                    await questManager.SendActiveQuestListAsync();
                    await questManager.SendAcceptableQuestListAsync();
                }
                await SendLevelUpTicketClearQuestListAsync(session, characterId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] USE_STACKABLE quest-ticket quest-state refresh failed: " +
                    $"{ex.Message}");
            }
        }

        private async Task SendQuestCompletionTicketAchievementAsync(
            EnhancedClientSession session,
            int characterId,
            AchievementTriggerResult result)
        {
            if (session == null
                || result == null
                || !result.Success
                || !result.Completed
                || result.TitleItemId <= 0)
                return;

            try
            {
                var writer = new GamePacketWriter();
                writer.WriteInt32(result.QuestId);
                writer.WriteInt32(result.Category);
                writer.WriteInt32(result.BookIndex);
                writer.WriteInt32(result.TitleItemId);
                writer.WriteUInt16((ushort)Math.Max(0, result.BookIndex));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.ACHIEVEMENT_COMPLETE,
                    writer.ToArray()));
                await SendTitleBookCategoryRefresh(session, characterId, result.Category);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] USE_STACKABLE quest-ticket achievement notify failed: " +
                    $"{ex.Message}");
            }
        }

        internal static bool TryBuildCrystalContractBodyFromUpdateRequest(ushort requestType, byte[] body, out byte[] crystalContractBody)
        {
            crystalContractBody = null;
            if (requestType != 0x0218 || body == null || body.Length != 2)
                return false;

            if (body[0] != 0x00 || (body[1] > 0x05 && body[1] != 0xFF))
                return false;

            crystalContractBody = new byte[] { body[0], body[1] };
            return true;
        }

        private async Task<bool> TryHandleOpenPackage0207(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 3)
                return false;

            var slotIndex = BitConverter.ToInt16(body, 0);
            var selectedItemTemplateIds = Parse0207ItemIds(body);
            FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207 raw({body.Length}B): {BitConverter.ToString(body)} slot={slotIndex} selected={string.Join(",", selectedItemTemplateIds.Select(id => $"0x{id:X8}"))}");

            var (cid, _) = ResolveOwner(session);
            if (!TryOpenOnlinePackage0207(
                    session,
                    cid,
                    slotIndex,
                    selectedItemTemplateIds,
                    out var result,
                    out var persistenceFailed))
            {
                FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207: failed slot={slotIndex} persistenceFailed={persistenceFailed}");
                return false;
            }

            await SendBoosterUseResult(session, header.type, result);
            FileLogger.Log($"[{ProtocolName}] OPEN_PACKAGE_0207: source=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex} rewards={result.Rewards.Count}");
            return true;
        }

        private bool TryUseOnlineBoosterItem(
            EnhancedClientSession session,
            int characterId,
            BoosterUseRequest request,
            out BoosterUseResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            return InventorySpecialConsumableCommitService.TryCommitBoosterItem(
                lease,
                request,
                ResolveCharacterJobLabel(characterId),
                _overflowRewardSink,
                out result,
                out persistenceFailed);
        }

        private bool TryOpenOnlinePackage0207(
            EnhancedClientSession session,
            int characterId,
            short slotIndex,
            IReadOnlyList<int> selectedItemTemplateIds,
            out BoosterUseResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            return InventorySpecialConsumableCommitService.TryCommitPackage0207(
                lease,
                slotIndex,
                selectedItemTemplateIds,
                _overflowRewardSink,
                out result,
                out persistenceFailed);
        }

        private bool TryOpenOnlineAvatarPackage(
            EnhancedClientSession session,
            int characterId,
            AvatarPackageOpenRequest request,
            out AvatarPackageOpenResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            return InventorySpecialConsumableCommitService.TryCommitAvatarPackage(
                lease,
                request,
                _overflowRewardSink,
                out result,
                out persistenceFailed);
        }

        private bool TryOpenOnlineSelectablePackage(
            EnhancedClientSession session,
            int characterId,
            SelectablePackageOpenRequest request,
            out SelectablePackageOpenResult result,
            out bool persistenceFailed)
        {
            result = null;
            persistenceFailed = false;
            if (!TryGetOwnedInventoryLease(session, characterId, out var lease))
                return false;

            return InventorySpecialConsumableCommitService.TryCommitSelectablePackage(
                lease,
                request,
                _overflowRewardSink,
                out result,
                out persistenceFailed);
        }

        private string ResolveCharacterJobLabel(int characterId)
        {
            var record = _characterRepository.GetById(characterId);
            return record != null
                ? InventorySpecialConsumableService.ResolveCharacterJobLabel(record.Job)
                : null;
        }

        private async Task SendBoosterUseResult(EnhancedClientSession session, ushort responseType, BoosterUseResult result)
        {
            var wallet = PersistHappyTokenCeraRewards(session, result);
            var magicBoxBatchType = (ushort)CmdPacketType.USE_RANDOMBOX_ITEM_EXPAND;
            var useNativeMagicBoxBatchAck = responseType == magicBoxBatchType && ShouldUseNativeMagicBoxBatchAck(result);
            var grantedItems = responseType == magicBoxBatchType && !useNativeMagicBoxBatchAck
                ? ToPackageGrantedItems(result)
                : ToBoosterPopupGrantedItems(result);

            if (responseType == 0x00A0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                    SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
            }
            else if (responseType == 0x0207)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0207,
                    AvatarPackageAckBuilder.BuildSuccess(result.SourceSlotIndex)));
                if (grantedItems.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
            }
            else if (responseType == magicBoxBatchType)
            {
                if (useNativeMagicBoxBatchAck)
                {
                    var ackBody = MagicBoxOpenAckBuilder.BuildBatch(result);
                    FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX ACK: type=0x{responseType:X4} bodyLen={ackBody.Length} head={FormatPacketHead(ackBody, 24)}{FormatBoosterOpenState(result)} {FormatBoosterRows(result, false)}");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType, ackBody));
                }
                else if (grantedItems.Count > 0)
                {
                    FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX legacy popup: type=0x{responseType:X4} rows={grantedItems.Count}{FormatBoosterOpenState(result)}");
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
            }
            else if (responseType == 0x00D0)
            {
                var ackBody = MagicBoxOpenAckBuilder.BuildSingle(result);
                FileLogger.Log($"[{ProtocolName}] OPEN_MAGIC_BOX ACK: type=0x00D0 bodyLen={ackBody.Length} head={FormatPacketHead(ackBody, 24)}{FormatBoosterOpenState(result)} {FormatBoosterRows(result, true)}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00D0, ackBody));
            }
            else
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType, CommonPacketBodyBuilder.BuildSuccessAck()));
                if (grantedItems.Count > 0)
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00A0,
                        SelectablePackageAckBuilder.BuildSuccess(result.SourceSlotIndex, grantedItems)));
                }
            }

            if (result.SourceRemainingStackCount <= 0)
                await _refresh.SendEmptyUpdateItemList(session, InventoryListType.Main, result.SourceSlotIndex);

            await SendBoosterMainItemUpdates(session, result, result.SourceRemainingStackCount > 0);
            if (wallet != null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0035,
                    CeraUpdateBuilder.Build(
                        wallet.Cera,
                        wallet.TokenCera,
                        wallet.HappyTokenCera)));
                FileLogger.Log(
                    $"[{ProtocolName}] HAPPY_TOKEN_CERA_UPDATE: cera={wallet.Cera} " +
                    $"token={wallet.TokenCera} happy={wallet.HappyTokenCera}");
            }
            await SendSpecialUpdateListForBoosterRewards(session, result);
            if (ShouldSendCreatureListRefreshForBoosterRewards(result))
                await _refresh.SendCreatureItemListRefresh(session);

            if (result.ActivatedPremiums.Count > 0)
                await Game.Premium.PremiumService.ActivateAndNotify(
                    session,
                    result.ActivatedPremiums,
                    _sqliteSelectCharacterDataSource,
                    _database);
        }

        private static WalletSnapshot PersistHappyTokenCeraRewards(
            EnhancedClientSession session,
            BoosterUseResult result)
        {
            if (result?.Rewards?.Any(
                    reward => reward?.SpecialOutcome?.Kind == SpecialRewardKind.HappyTokenCera) != true)
                return null;

            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId)
                || !InventoryPersistenceService.SaveDirtyAndLoadWallet(lease, out var wallet))
            {
                FileLogger.Log($"[{nameof(InventoryHandler)}] happy-token reward persistence failed cid={characterId}");
                return null;
            }

            return wallet;
        }

        private async Task RefreshExpiredBoosterSourceAsync(
            EnhancedClientSession session,
            BoosterUseResult result)
        {
            if (result == null || !result.SourceExpiredDeleted)
                return;

            await _refresh.SendUpdateItemList(
                session,
                InventoryListType.Main,
                result.SourceSlotIndex);
            FileLogger.Log(
                $"[{ProtocolName}] USE_BOOSTER: expired source removed " +
                $"item=0x{result.SourceItemTemplateId:X8} slot={result.SourceSlotIndex}");
        }

        private static async Task SendBoosterMaterialNotice(EnhancedClientSession session, BoosterUseResult result)
        {
            var message = BuildBoosterMaterialNoticeMessage(result);
            if (string.IsNullOrEmpty(message))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE,
                ServerNoticeMessageBuilder.Build(message)));
            FileLogger.Log($"[{nameof(InventoryHandler)}] SERVER_NOTICE_MESSAGE: {message}");
        }

        internal static byte[] BuildBoosterFailureAckBody(BoosterUseResult result)
        {
            var errorCode = result?.ErrorCode == BoosterUseResult.ErrorMaterialNotEnough
                ? (byte)0x16
                : result?.ErrorCode ?? BoosterUseResult.ErrorInvalidRequest;
            return CommonPacketBodyBuilder.BuildCmdError(errorCode);
        }

        internal static byte[] BuildMagicBoxFailureAckBody(ushort responseType, BoosterUseResult result)
        {
            if (responseType == 0x00D0 && result?.ErrorCode == BoosterUseResult.ErrorMaterialNotEnough)
                return MagicBoxOpenAckBuilder.BuildSingleSilentCompletion();

            return BuildBoosterFailureAckBody(result);
        }

        internal static string BuildBoosterMaterialNoticeMessage(BoosterUseResult result)
        {
            if (result == null
                || result.ErrorCode != BoosterUseResult.ErrorMaterialNotEnough
                || result.RequiredMaterialItemTemplateId <= 0
                || result.RequiredMaterialCount <= 0)
                return null;

            var materialName = string.IsNullOrWhiteSpace(result.RequiredMaterialName)
                ? "鎸囧畾鏉愭枡"
                : result.RequiredMaterialName.Trim();
            return $"\u6750\u6599\u4E0D\u8DB3: \u9700\u8981[{materialName}] x{result.RequiredMaterialCount}, \u5F53\u524D x{Math.Max(0, result.AvailableMaterialCount)}.";
        }

        private static bool ShouldSendCreatureListRefreshForBoosterRewards(BoosterUseResult result)
        {
            if (result?.Rewards == null)
                return false;

            return result.Rewards.Any(x =>
                x != null
                && x.ListType == InventoryListType.Pet
                && ItemMetadataResolver.IsCreatureItem(x.ItemTemplateId));
        }

        internal static bool ShouldSendSourceAckForBoosterResponse(ushort responseType)
        {
            return responseType != (ushort)CmdPacketType.USE_RANDOMBOX_ITEM
                && responseType != (ushort)CmdPacketType.USE_RANDOMBOX_ITEM_EXPAND;
        }

        internal static bool ShouldUseNativeMagicBoxBatchAck(BoosterUseResult result)
        {
            return result != null && result.IsSeriaLuckValueSource;
        }

        internal static bool ShouldSendObtainedItemsPopupForBoosterResponse(ushort responseType)
        {
            return responseType != (ushort)CmdPacketType.USE_RANDOMBOX_ITEM
                && responseType != (ushort)CmdPacketType.USE_RANDOMBOX_ITEM_EXPAND;
        }

        internal static bool ShouldSendObtainedItemsPopupForBoosterResponse(ushort responseType, BoosterUseResult result)
        {
            if (responseType == (ushort)CmdPacketType.USE_RANDOMBOX_ITEM)
                return false;

            if (responseType == (ushort)CmdPacketType.USE_RANDOMBOX_ITEM_EXPAND)
                return !ShouldUseNativeMagicBoxBatchAck(result);

            return true;
        }

        private static string FormatPacketHead(byte[] body, int maxBytes)
        {
            if (body == null || body.Length == 0)
                return string.Empty;

            var count = Math.Min(body.Length, Math.Max(0, maxBytes));
            return BitConverter.ToString(body, 0, count);
        }

        private static string FormatBoosterOpenState(BoosterUseResult result)
        {
            if (result == null)
                return string.Empty;

            var state = $" clientType=0x{result.MagicBoxClientType:X2} displayRows={result.DisplayRewards.Count} doubleRows={result.DoubleRewards.Count}";
            if (!result.IsSeriaLuckValueSource)
                return state;

            return state + $" seriaLuck={result.SeriaLuckValueBefore}->{result.SeriaLuckValueAfter}/{result.SeriaLuckValueMax} doubleTriggered={result.SeriaLuckDoubleTriggered}";
        }

        private async Task SendGrantedMainItemUpdates(
            EnhancedClientSession session,
            IReadOnlyList<PackageGrantedItem> grantedItems,
            short? sourceSlotIndex)
        {
            var slots = new HashSet<short>();
            if (sourceSlotIndex.HasValue)
                slots.Add(sourceSlotIndex.Value);

            if (grantedItems != null)
            {
            foreach (var reward in grantedItems)
            {
                if (reward != null
                    && reward.ListType == InventoryListType.Main
                    && reward.SlotIndex >= 0
                    && reward.SpecialOutcome?.Kind != SpecialRewardKind.EpicPiece)
                    slots.Add(reward.SlotIndex);
            }
            }

            if (slots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, slots);
        }

        private async Task SendPackageMainItemUpdates(
            EnhancedClientSession session,
            short sourceSlotIndex,
            int sourceRemainingStackCount,
            IReadOnlyList<PackageGrantedItem> grantedItems)
        {
            if (sourceRemainingStackCount <= 0)
            {
                await _refresh.SendEmptyUpdateItemList(session, InventoryListType.Main, sourceSlotIndex);
                await SendGrantedMainItemUpdates(session, grantedItems, null);
                return;
            }

            await SendGrantedMainItemUpdates(session, grantedItems, sourceSlotIndex);
        }

        private async Task SendBoosterMainItemUpdates(
            EnhancedClientSession session,
            BoosterUseResult result,
            bool includeSourceUpdate)
        {
            if (result == null)
                return;

            var slots = new HashSet<short>();
            if (includeSourceUpdate)
                slots.Add(result.SourceSlotIndex);
            if (result.ConsumedMaterialItemTemplateId > 0)
                slots.Add(result.ConsumedMaterialSlotIndex);

            foreach (var reward in result.Rewards)
            {
                if (reward != null
                    && reward.ListType == InventoryListType.Main
                    && reward.SlotIndex >= 0
                    && (reward.SpecialOutcome == null
                        || reward.SpecialOutcome.Kind == SpecialRewardKind.ReviveCoin))
                    slots.Add(reward.SlotIndex);
            }

            if (slots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, slots);
        }

        private async Task SendSpecialUpdateListForGrantedItems(EnhancedClientSession session, IReadOnlyList<PackageGrantedItem> grantedItems)
        {
            if (grantedItems == null)
                return;

            var avatarSlots = new HashSet<short>();
            var petSlots = new HashSet<short>();
            var guildMedalSlots = new HashSet<short>();
            foreach (var item in grantedItems)
            {
                if (item == null)
                    continue;

                if (item.SpecialOutcome?.Kind == SpecialRewardKind.EpicPiece)
                {
                    await InventoryRefreshSender.SendEpicPieceInfo(
                        session,
                        item.ItemTemplateId,
                        item.SpecialOutcome.WalletNewTotal);
                }
                else if (item.ListType == InventoryListType.Avatar)
                    avatarSlots.Add(item.SlotIndex);
                else if (item.ListType == InventoryListType.Pet)
                    petSlots.Add(item.SlotIndex);
                else if (item.ListType == InventoryListType.GuildMedal)
                    guildMedalSlots.Add(item.SlotIndex);
            }

            if (avatarSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Avatar, avatarSlots);
            if (petSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Pet, petSlots);
            if (guildMedalSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.GuildMedal, guildMedalSlots);
        }

        private async Task SendSpecialUpdateListForBoosterRewards(EnhancedClientSession session, BoosterUseResult result)
        {
            if (result?.Rewards == null)
                return;

            var avatarSlots = new HashSet<short>();
            var petSlots = new HashSet<short>();
            var guildMedalSlots = new HashSet<short>();
            foreach (var item in result.Rewards)
            {
                if (item == null)
                    continue;

                if (item.SpecialOutcome?.Kind == SpecialRewardKind.EpicPiece)
                {
                    await InventoryRefreshSender.SendEpicPieceInfo(
                        session,
                        item.ItemTemplateId,
                        item.StackCount);
                }
                else if (item.ListType == InventoryListType.Avatar)
                    avatarSlots.Add(item.SlotIndex);
                else if (item.ListType == InventoryListType.Pet)
                    petSlots.Add(item.SlotIndex);
                else if (item.ListType == InventoryListType.GuildMedal)
                    guildMedalSlots.Add(item.SlotIndex);
            }

            if (avatarSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Avatar, avatarSlots);
            if (petSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.Pet, petSlots);
            if (guildMedalSlots.Count > 0)
                await _refresh.SendUpdateItemList(session, InventoryListType.GuildMedal, guildMedalSlots);
        }

        private static string FormatBoosterRows(BoosterUseResult result, bool singleAckRows)
        {
            if (result == null)
                return string.Empty;

            if (singleAckRows)
                return $"ackDisplay={FormatPackageRows(result.DisplayRewards, 16)} raw={FormatBoosterRewardRows(result.Rewards, 16)} double={FormatPackageRows(result.DoubleRewards, 16)}";

            return $"ack={FormatPackageRows(result.DisplayRewards, 16)} double={FormatPackageRows(result.DoubleRewards, 16)}";
        }

        private static string FormatPackageRows(IReadOnlyList<PackageGrantedItem> rows, int maxRows)
        {
            if (rows == null || rows.Count == 0)
                return "none";

            var limit = Math.Min(rows.Count, Math.Max(0, maxRows));
            var parts = new List<string>();
            for (var i = 0; i < limit; i++)
            {
                var row = rows[i];
                parts.Add($"{row.ListType}:0x{row.ItemTemplateId:X8}x{row.DisplayCount}@{row.SlotIndex}");
            }

            if (rows.Count > limit)
                parts.Add($"...+{rows.Count - limit}");

            return string.Join(",", parts);
        }

        private static string FormatBoosterRewardRows(IReadOnlyList<BoosterRewardResult> rows, int maxRows)
        {
            if (rows == null || rows.Count == 0)
                return "none";

            var limit = Math.Min(rows.Count, Math.Max(0, maxRows));
            var parts = new List<string>();
            for (var i = 0; i < limit; i++)
            {
                var row = rows[i];
                parts.Add($"{row.ListType}:0x{row.ItemTemplateId:X8}x{row.GrantedCount}@{row.SlotIndex}");
            }

            if (rows.Count > limit)
                parts.Add($"...+{rows.Count - limit}");

            return string.Join(",", parts);
        }

        private static IReadOnlyList<int> ParseBoosterSelectionItemIds(byte[] body)
        {
            var selected = new List<int>();
            if (body == null || body.Length < 6)
                return selected;

            AddAlignedInt32Candidates(body, 4, 4, selected);
            if (body.Length >= 3)
                AddRecordCandidates(body, 3, body[2], 5, selected);
            AddAlignedInt32Candidates(body, 2, 4, selected);

            return selected;
        }

        private static IReadOnlyList<int> Parse0207ItemIds(byte[] body)
        {
            var selected = new List<int>();
            if (body == null || body.Length < 3)
                return selected;

            var itemCount = body[2];
            for (var i = 0; i < itemCount; i++)
            {
                var offset = 3 + i * 5;
                if (offset + 4 > body.Length)
                    break;

                AddItemCandidate(BitConverter.ToInt32(body, offset), selected);
            }

            return selected;
        }

        private static void AddAlignedInt32Candidates(byte[] body, int startOffset, int stride, List<int> selected)
        {
            for (var offset = startOffset; offset + 4 <= body.Length; offset += stride)
                AddItemCandidate(BitConverter.ToInt32(body, offset), selected);
        }

        private static void AddRecordCandidates(byte[] body, int startOffset, int count, int recordSize, List<int> selected)
        {
            for (var i = 0; i < count; i++)
            {
                var offset = startOffset + i * recordSize;
                if (offset + 4 > body.Length)
                    break;

                AddItemCandidate(BitConverter.ToInt32(body, offset), selected);
            }
        }

        private static void AddItemCandidate(int itemTemplateId, List<int> selected)
        {
            if (itemTemplateId >= 1000 && !selected.Contains(itemTemplateId))
                selected.Add(itemTemplateId);
        }

        public async Task Handle_COMPOUND_AVATAR(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {

            if (body == null || body.Length < 22)
            {
                var shortErr = new GamePacketWriter();
                shortErr.WriteByte(0x00);
                shortErr.WriteByte(0x16);
                shortErr.WriteByte(0x00);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, shortErr.ToArray()));
                return;
            }

            short consumeSlot = BitConverter.ToInt16(body, 0);
            short slot1 = BitConverter.ToInt16(body, 2);
            short slot2 = BitConverter.ToInt16(body, 8);
            int reqItemId = BitConverter.ToInt32(body, 14);
            ushort abilityNo = ReadCompoundAvatarAbilityNo(body, 18);

            var (cid, _) = ResolveOwner(session);
            var job = _characterRepository.GetById(cid)?.Job ?? 0;

            var request = new InventoryAvatarCompoundRequest
            {
                ConsumeSlot = consumeSlot,
                Slot1 = slot1,
                Slot2 = slot2,
                RequestedItemId = reqItemId,
                AbilityNo = abilityNo,
            };

            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, BuildCompoundAvatarErrorBody(includeTailByte: true)));
                return;
            }

            var committed = InventoryAvatarCompoundCommitService.TryCommit(
                lease,
                request,
                (old1, old2, materialId) =>
                {
                    var prob = CompoundAvatarProbabilityService.Resolve(job, old1, old2, materialId, reqItemId);
                    return prob.Success ? prob.NewItemIds : new List<int> { reqItemId };
                },
                out var result,
                out var persistenceFailed);

            if (!committed || result == null || !result.Success)
            {
                FileLogger.Log($"  [CompoundAvatar] REJECT: error={result?.Error} persistenceFailed={persistenceFailed} slot1={slot1} slot2={slot2} consumeSlot={consumeSlot} abilityNo={abilityNo}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, BuildCompoundAvatarErrorBody(includeTailByte: true)));
                return;
            }

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteByte(0x03);
            w.WriteByte(0x01);
            w.WriteInt16(slot1);
            w.WriteInt32(1);
            w.WriteByte(0x01);
            w.WriteInt16(slot2);
            w.WriteInt32(1);
            w.WriteByte(0x00);
            w.WriteInt16(consumeSlot);
            w.WriteInt32(1);
            for (int i = 0; i < 2; i++)
            {
                bool hasItem = i < result.NewItemIds.Count;
                w.WriteInt16(hasItem ? result.NewSlots[i] : (short)-1);
                w.WriteInt32(hasItem ? result.NewItemIds[i] : 0);
                w.WriteInt32(0);
                w.WriteUInt16(abilityNo);
                w.WriteInt32(30);
                w.WriteZeroBytes(30);
                w.WriteInt32(4);
                w.WriteZeroBytes(4);
            }

            var respBody = w.ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0063, respBody));
            FileLogger.Log($"  [CompoundAvatar] OK: deleted slot{slot1}(item {result.OldItemId1}) + slot{slot2}(item {result.OldItemId2}) + " +
                           $"1x slot{consumeSlot}(template {result.ConsumedItemTemplateId}, remain {result.ConsumedItemRemainingCount}), " +
                           $"abilityNo={abilityNo}, added items [{string.Join(",", result.NewItemIds)}] at slots [{string.Join(",", result.NewSlots)}]");
        }


        public async Task Handle_COMPOUND_AVATAR_SET(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 72)
                return;

            var commandType = (ushort)CmdPacketType.BIND_PLUS;
            short consumeStackableSlot = body[13];
            int requestedItemId = BitConverter.ToInt32(body, 16);
            ushort option = BitConverter.ToUInt16(body, 20);

            var consumeSlots = new short[8];
            var consumeSlotItemIds = new int[8];
            int off = 24;
            for (int i = 0; i < 8; i++)
            {
                consumeSlots[i] = BitConverter.ToInt16(body, off);
                consumeSlotItemIds[i] = BitConverter.ToInt32(body, off + 2);
                off += 6;
            }

            if (consumeSlots.Distinct().Count() != consumeSlots.Length)
            {
                var dupErr = new GamePacketWriter();
                dupErr.WriteByte(0x00);
                dupErr.WriteByte(0x16);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, dupErr.ToArray()));
                return;
            }

            var (cid, _) = ResolveOwner(session);
            var job = _characterRepository.GetById(cid)?.Job ?? 0;

            int ResolveNewItemId(int consumeMaterialId)
            {
                var cube = AbsoluteBindCubeService.Resolve(consumeMaterialId, job);
                if (!cube.Success)
                {
                    return -1;
                }

                foreach (var kv in cube.PartToItemId)
                {
                    if (kv.Value == requestedItemId)
                        return requestedItemId;
                }
                return -1;
            }

            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, BuildCompoundAvatarErrorBody(includeTailByte: false)));
                return;
            }

            var request = new InventoryAvatarCompoundSetRequest
            {
                ConsumeSlot = consumeStackableSlot,
                ConsumeSlots = consumeSlots,
                ExpectedItemIds = consumeSlotItemIds,
                RequestedItemId = requestedItemId,
                AbilityNo = option,
            };

            var committed = InventoryAvatarCompoundCommitService.TryCommitSet(
                lease,
                request,
                ResolveNewItemId,
                out var result,
                out var persistenceFailed);

            if (!committed || result == null || !result.Success)
            {
                FileLogger.Log($"  [CompoundAvatarSet] REJECT: error={result?.Error} persistenceFailed={persistenceFailed} consumeSlot={consumeStackableSlot} requested=0x{requestedItemId:X8} abilityNo={option}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, BuildCompoundAvatarErrorBody(includeTailByte: false)));
                return;
            }

            var w2 = new GamePacketWriter();
            w2.WriteByte(0x01);
            w2.WriteByte(0x01); w2.WriteByte(0x00); w2.WriteByte(0x03); w2.WriteByte(0x00);
            w2.WriteByte(0x01); w2.WriteByte(0x00); w2.WriteByte(0x00); w2.WriteByte(0x00);
            w2.WriteInt16(result.NewSlots[0]);
            w2.WriteInt32(result.NewItemIds[0]);
            w2.WriteUInt16(option);
            w2.WriteInt16(1);
            for (int i = 0; i < 8; i++)
                w2.WriteInt16(consumeSlots[i]);
            w2.WriteZeroBytes(24);

            var respBody2 = w2.ToArray();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, respBody2));
            await _refresh.SendUpdateItemList(session, InventoryListType.Main, result.ConsumeSlot);
            FileLogger.Log($"  [CompoundAvatarSet] OK: consumed {consumeSlots.Length} avatar items + 1x slot {consumeStackableSlot}(template {result.ConsumedItemTemplateId}), abilityNo={option}, added item {result.NewItemIds[0]} at slot {result.NewSlots[0]}");

        }

        private static ushort ReadCompoundAvatarAbilityNo(byte[] body, int offset)
        {
            if (body == null || body.Length < offset + 2)
                return 0;

            var value = BitConverter.ToUInt16(body, offset);
            if (value <= 0)
                return 0;
            return value;
        }

        private static byte[] BuildCompoundAvatarErrorBody(bool includeTailByte)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x00);
            writer.WriteByte(0x16);
            if (includeTailByte)
                writer.WriteByte(0x00);
            return writer.ToArray();
        }
    }
}
