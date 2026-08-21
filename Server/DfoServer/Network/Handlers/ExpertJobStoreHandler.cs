using System;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.ExpertJob;
using DfoServer.Network.Parsers.ExpertJob;

namespace DfoServer.Network.Handlers
{
    public sealed class ExpertJobStoreHandler
    {
        private const ushort CreateCommand = (ushort)CmdPacketType.CREATE_EXPERT_JOB_STORE;
        private const ushort EnterCommand = (ushort)CmdPacketType.ENTER_EXPERT_JOB_STORE;
        private const ushort CreateExpertJobNotification =
            (ushort)NotiPacketType.CREATE_EXPERT_JOB_STORE;
        private const ushort CloseExpertJobNotification =
            (ushort)NotiPacketType.CLOSE_EXPERT_JOB_STORE;
        private const ushort DisjointCommand = (ushort)CmdPacketType.REQUEST_DISJOINT_ITEM;
        private const ushort DisjointNotification = (ushort)NotiPacketType.REQUEST_DISJOINT_ITEM;
        private const ushort RepairCommand = (ushort)CmdPacketType.REPAIR_DISJOINT_MACHINE;
        private const ushort UpgradeCommand = (ushort)CmdPacketType.UPGRADE_DISJOINT_MACHINE;
        private const ushort EnchantCommand = (ushort)CmdPacketType.USE_ENCHANT_STORE;
        private const ushort EnchantNotification = (ushort)NotiPacketType.USE_ENCHANT_STORE;

        private readonly ExpertJobStoreRuntimeService _stores;
        private readonly ExpertJobStorePlacementValidator _placement;
        private readonly PartyManager _parties;
        private readonly ISessionDirectory _sessions;
        private readonly IDisjointMachineStateRepository _disjointMachineStates;
        private readonly IEnchanterMachineStateRepository _enchanterMachineStates;
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly ExpertJobPersistenceService _persistence;
        private readonly ExpertJobOperationCoordinator _operations;
        private readonly InventoryRefreshSender _inventoryRefresh;

        internal ExpertJobStoreHandler(
            ExpertJobStoreRuntimeService stores,
            ExpertJobStorePlacementValidator placement,
            PartyManager parties,
            ISessionDirectory sessions,
            IDisjointMachineStateRepository disjointMachineStates,
            IEnchanterMachineStateRepository enchanterMachineStates,
            ICharacterRepository characterRepository,
            SqliteSubtype0FieldsRepository subtype0Repository,
            HonorLevelSyncService honorLevel,
            ExpertJobPersistenceService persistence,
            ExpertJobOperationCoordinator operations,
            InventoryRefreshSender inventoryRefresh)
        {
            _stores = stores ?? throw new ArgumentNullException(nameof(stores));
            _placement = placement ?? throw new ArgumentNullException(nameof(placement));
            _parties = parties ?? throw new ArgumentNullException(nameof(parties));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _disjointMachineStates = disjointMachineStates
                ?? throw new ArgumentNullException(nameof(disjointMachineStates));
            _enchanterMachineStates = enchanterMachineStates
                ?? throw new ArgumentNullException(nameof(enchanterMachineStates));
            _characterRepository = characterRepository
                ?? throw new ArgumentNullException(nameof(characterRepository));
            _subtype0Repository = subtype0Repository
                ?? throw new ArgumentNullException(nameof(subtype0Repository));
            _honorLevel = honorLevel ?? throw new ArgumentNullException(nameof(honorLevel));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _inventoryRefresh = inventoryRefresh
                ?? throw new ArgumentNullException(nameof(inventoryRefresh));
        }

        public async Task HandleCreate(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!CreateExpertJobStoreRequest.TryParse(body, out var command))
            {
                await SendError(session, header.type, ExpertJobStoreRuntimeService.ErrorInvalidRequest);
                return;
            }

            var player = session.Player;
            if (player == null)
            {
                await SendError(session, header.type, ExpertJobStoreRuntimeService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            var expertJobType = player?.Subtype0Tail?.ExpertJobType ?? 0;
            ExpertJobStoreSession store;
            byte errorCode;
            try
            {
                var isInParty = _parties.GetPartyByUser(player.UserId) != null;
                var state = command.Kind == ExpertJobStoreKind.DisjointMachine
                    ? _disjointMachineStates.Resolve(player.CharacterId)
                    : null;
                var enchanterState = command.Kind == ExpertJobStoreKind.EnchantShop
                    ? new EnchanterStoreState
                    {
                        Endurance = _enchanterMachineStates.ResolveEnchanter(
                            player.CharacterId).Endurance,
                        CardQualificationLevels = EnchanterConfigProvider.Config.GetCardQualificationLevels(
                            player.Subtype0Tail?.ExpertJobExp ?? 0),
                    }
                    : null;
                if (!_stores.TryValidateCreate(
                        player.CharacterId,
                        player.UserId,
                        expertJobType,
                        player.CurTownId,
                        player.CurrentRun != null,
                        isInParty,
                        command,
                        state,
                        enchanterState,
                        out errorCode))
                {
                    store = null;
                }
                else if (!_placement.TryValidate(
                             player.CurTownId,
                             player.CurAreaId,
                             command.PositionX,
                             command.PositionY,
                             out errorCode))
                {
                    store = null;
                }
                else
                {
                    if (!_stores.TryCreate(
                            session.SessionId,
                            player.CharacterId,
                            player.UserId,
                            expertJobType,
                            player.CurTownId,
                            player.CurAreaId,
                            player.CurrentRun != null,
                            isInParty,
                            command,
                            state,
                            enchanterState,
                            out store,
                            out errorCode))
                    {
                        store = null;
                    }
                }
            }
            finally
            {
                operationGate.Release();
            }

            if (store == null)
            {
                await SendError(session, header.type, errorCode);
                return;
            }

            player.CurPosX = store.PositionX;
            player.CurPosY = store.PositionY;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                CreateCommand,
                CommonPacketBodyBuilder.BuildSuccessAck()));
            var notification = BuildCreateNotification(store);
            await session.SendPacketAsync(notification);
            await _sessions.BroadcastToAreaAsync(
                store.TownId,
                store.AreaId,
                store.OwnerCharacterId,
                notification,
                session.ListenerPort);
            FileLogger.Log(
                $"[ExpertJobStore] CREATE owner={store.OwnerCharacterId} uid={store.OwnerUserId} " +
                $"kind={store.Kind} cost={store.Cost} town={store.TownId} area={store.AreaId} " +
                $"pos=({store.PositionX},{store.PositionY})");
        }

        public async Task HandleEnter(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!EnterExpertJobStoreRequest.TryParse(body, out var request))
            {
                await SendError(session, header.type, ExpertJobStoreRuntimeService.ErrorInvalidRequest);
                return;
            }

            var player = session.Player;
            if (player == null
                || player.CurrentRun != null
                || !_stores.TryEnter(
                    session.SessionId,
                    player.CharacterId,
                    player.CurTownId,
                    player.CurAreaId,
                    request.OwnerUserId,
                    out var store))
            {
                await SendError(session, header.type, ExpertJobStoreRuntimeService.ErrorInvalidState);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                EnterCommand,
                ExpertJobStorePacketBuilder.BuildEnterSuccess(store)));
            FileLogger.Log(
                $"[ExpertJobStore] ENTER user={player.CharacterId} owner={store.OwnerCharacterId} " +
                $"uid={store.OwnerUserId} kind={store.Kind}");
        }

        public bool HasEnteredStore(EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            return characterId > 0
                && _stores.TryGetEnteredStore(session.SessionId, characterId, out _);
        }

        public async Task HandleDisjoint(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!DisjointMachineRequest.TryParse(body, out var request))
            {
                await SendDisjointError(session, DisjointMachineService.ErrorInvalidItem);
                return;
            }

            var player = session.Player;
            if (player == null
                || player.CurrentRun != null
                || !_stores.TryGetEnteredStore(
                    session.SessionId,
                    player.CharacterId,
                    out var store)
                || store.OwnerUserId != request.OwnerUserId
                || store.TownId != player.CurTownId
                || store.AreaId != player.CurAreaId
                || !_sessions.TryGet(store.OwnerCharacterId, out var ownerSession)
                || ownerSession.SessionId != store.OwnerSessionId
                || !InventoryContext.TryGetLease(player.CharacterId, out var requesterLease)
                || !requesterLease.IsOwnedBy(session.SessionId)
                || !InventoryContext.TryGetLease(store.OwnerCharacterId, out var ownerLease)
                || !ownerLease.IsOwnedBy(ownerSession.SessionId))
            {
                await SendDisjointError(session, ExpertJobStoreRuntimeService.ErrorInvalidState);
                return;
            }

            DisjointMachineOperationResult result;
            bool success;
            var ownerGoldCarryLimit = ReferenceEquals(requesterLease, ownerLease)
                ? int.MaxValue
                : InventoryGoldCarryLimitLoader.Load(ownerLease.Inventory);
            _ = DisjointMachineConfigProvider.Config;
            var operationGate = _operations.GetGate(store.OwnerCharacterId);
            await operationGate.WaitAsync();
            try
            {
                var originalEndurance = store.DisjointMachine.Endurance;
                if (ReferenceEquals(requesterLease, ownerLease))
                {
                    lock (requesterLease.SyncRoot)
                    {
                        success = DisjointMachineService.TryDisjoint(
                            requesterLease.Inventory,
                            ownerLease.Inventory,
                            store,
                            request.TargetSlotIndex,
                            ownerGoldCarryLimit,
                            out result);
                    }
                }
                else
                {
                    var first = requesterLease.CharacterId < ownerLease.CharacterId
                        ? requesterLease
                        : ownerLease;
                    var second = ReferenceEquals(first, requesterLease)
                        ? ownerLease
                        : requesterLease;
                    lock (first.SyncRoot)
                    lock (second.SyncRoot)
                    {
                        success = DisjointMachineService.TryDisjoint(
                            requesterLease.Inventory,
                            ownerLease.Inventory,
                            store,
                            request.TargetSlotIndex,
                            ownerGoldCarryLimit,
                            out result);
                    }
                }

                if (!success)
                {
                    await SendDisjointError(session, result.ErrorCode);
                    return;
                }

                if (!_persistence.Save(
                        requesterLease,
                        ownerLease,
                        (connection, transaction) => _disjointMachineStates.SaveInTransaction(
                            connection,
                            transaction,
                            store.OwnerCharacterId,
                            store.DisjointMachine,
                            result.ExperienceGain)))
                {
                    store.DisjointMachine.Endurance = originalEndurance;
                    await SendDisjointError(
                        session,
                        ExpertJobStoreRuntimeService.ErrorInvalidState);
                    return;
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    DisjointCommand,
                    ExpertJobStorePacketBuilder.BuildDisjointSuccess(result)));
                if (!ReferenceEquals(session, ownerSession))
                {
                    await ownerSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        DisjointNotification,
                        ExpertJobStorePacketBuilder.BuildOwnerDisjointNotification(
                            result.OwnerGold,
                            result.Endurance)));
                }
                if (result.ExperienceGain > 0)
                {
                    await UserInfoBroadcastService.SendSubtype0Async(
                        ownerSession,
                        _characterRepository,
                        _subtype0Repository,
                        _honorLevel,
                        "EXPERT_JOB_EXP_REFRESH");
                }
                FileLogger.Log(
                    $"[ExpertJobStore] DISJOINT requester={player.CharacterId} " +
                    $"owner={store.OwnerCharacterId} slot={request.TargetSlotIndex} " +
                    $"cost={(player.CharacterId == store.OwnerCharacterId ? 0 : store.Cost)} " +
                    $"endurance={result.Endurance} exp={result.ExperienceGain}");

                if (result.Endurance <= 0
                    && _stores.TryClose(
                        store.OwnerSessionId,
                        store.OwnerCharacterId,
                        out var exhaustedStore))
                {
                    await BroadcastClose(session, exhaustedStore, includeOwner: true);
                }
                var questManager = session.GameSession?.QuestManager;
                if (questManager != null)
                {
                    await questManager.SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(
                        requesterLease,
                        result.DisjointResult.InventoryMutations);
                }
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async Task HandleEnchant(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!EnchanterStoreUseRequest.TryParse(body, out var command))
            {
                await SendEnchantError(session, EnchanterStoreUseService.ErrorInvalidItem);
                return;
            }

            var player = session.Player;
            if (player == null
                || player.CurrentRun != null
                || !_stores.TryGetEnteredStore(
                    session.SessionId, player.CharacterId, out var store)
                || store.Kind != ExpertJobStoreKind.EnchantShop
                || store.OwnerUserId != command.OwnerUserId
                || store.TownId != player.CurTownId
                || store.AreaId != player.CurAreaId
                || !_sessions.TryGet(store.OwnerCharacterId, out var ownerSession)
                || ownerSession.SessionId != store.OwnerSessionId
                || ownerSession.Player?.Subtype0Tail?.ExpertJobType
                    != ExpertJobStateCodec.EnchanterType
                || !InventoryContext.TryGetLease(player.CharacterId, out var requesterLease)
                || !requesterLease.IsOwnedBy(session.SessionId)
                || !InventoryContext.TryGetLease(store.OwnerCharacterId, out var ownerLease)
                || !ownerLease.IsOwnedBy(ownerSession.SessionId))
            {
                await SendEnchantError(session, EnchanterStoreUseService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(store.OwnerCharacterId);
            await operationGate.WaitAsync();
            var persisted = false;
            try
            {
                if (!_stores.TryGetEnteredStore(
                        session.SessionId, player.CharacterId, out var currentStore)
                    || !ReferenceEquals(currentStore, store)
                    || !_stores.TryGetOwnedStore(
                        store.OwnerSessionId, store.OwnerCharacterId, out currentStore)
                    || !ReferenceEquals(currentStore, store))
                {
                    await SendEnchantError(session, EnchanterStoreUseService.ErrorInvalidState);
                    return;
                }

                var originalEndurance = store.Enchanter.Endurance;
                var previousExperience = ownerSession.Player.Subtype0Tail.ExpertJobExp;
                var isSelfService = ReferenceEquals(requesterLease, ownerLease);
                var ownerGoldCarryLimit = isSelfService
                    ? int.MaxValue
                    : InventoryGoldCarryLimitLoader.Load(ownerLease.Inventory);
                EnchanterStoreUseResult result;
                bool success;
                if (isSelfService)
                {
                    lock (requesterLease.SyncRoot)
                    {
                        success = EnchanterStoreUseService.TryEnchant(
                            requesterLease.Inventory, ownerLease.Inventory, store, command,
                            previousExperience, ownerGoldCarryLimit, out result);
                    }
                }
                else
                {
                    var first = requesterLease.CharacterId < ownerLease.CharacterId
                        ? requesterLease
                        : ownerLease;
                    var second = ReferenceEquals(first, requesterLease)
                        ? ownerLease
                        : requesterLease;
                    lock (first.SyncRoot)
                    lock (second.SyncRoot)
                    {
                        success = EnchanterStoreUseService.TryEnchant(
                            requesterLease.Inventory, ownerLease.Inventory, store, command,
                            previousExperience, ownerGoldCarryLimit, out result);
                    }
                }

                if (!success)
                {
                    await SendEnchantError(session, result.ErrorCode);
                    return;
                }

                var config = EnchanterConfigProvider.Config;
                var machineState = new EnchanterMachineState
                {
                    Endurance = result.Endurance,
                };
                if (!_persistence.Save(
                        requesterLease,
                        ownerLease,
                        (connection, transaction) =>
                            _enchanterMachineStates.SaveEnchanterInTransaction(
                                connection, transaction, store.OwnerCharacterId, machineState,
                                result.ExperienceGain,
                                config.GetNewAutoLearnRecipeIds(
                                    previousExperience,
                                    result.FinalExperience))))
                {
                    store.Enchanter.Endurance = originalEndurance;
                    await SendEnchantError(session, EnchanterStoreUseService.ErrorInvalidState);
                    return;
                }

                ownerSession.Player.Subtype0Tail.ExpertJobExp = result.FinalExperience;
                store.Enchanter.CardQualificationLevels =
                    config.GetCardQualificationLevels(result.FinalExperience);
                persisted = true;
                await _inventoryRefresh.SendUpdateItemList(
                    session, result.CardListType, result.CardSlotIndex);
                if (result.EnchantSucceeded)
                {
                    await _inventoryRefresh.SendUpdateItemList(
                        session, result.TargetListType, result.TargetSlotIndex);
                }
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    EnchantCommand,
                    ExpertJobStorePacketBuilder.BuildEnchantSuccess(result)));
                if (!isSelfService)
                {
                    await ownerSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        EnchantNotification,
                        ExpertJobStorePacketBuilder.BuildOwnerEnchantNotification(
                            result.OwnerGold,
                            result.Endurance)));
                    await _inventoryRefresh.SendGoldUpdate(session);
                    await _inventoryRefresh.SendGoldUpdate(ownerSession);
                }
                if (result.ExperienceGain > 0)
                {
                    await UserInfoBroadcastService.SendSubtype0Async(
                        ownerSession, _characterRepository, _subtype0Repository, _honorLevel,
                        "EXPERT_JOB_EXP_REFRESH");
                }
                if (config.GetLevel(previousExperience)
                    != config.GetLevel(result.FinalExperience))
                {
                    var refreshedState = _enchanterMachineStates.Load(
                        store.OwnerCharacterId,
                        ExpertJobStateCodec.EnchanterType);
                    await ownerSession.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x00CD,
                        ExpertJobInfoBodyBuilder.BuildProjectedBody(
                            ExpertJobStateCodec.EnchanterType,
                            refreshedState,
                            result.FinalExperience)));
                }
                FileLogger.Log(
                    $"[ExpertJobStore] ENCHANT requester={player.CharacterId} " +
                    $"owner={store.OwnerCharacterId} cardSlot={result.CardSlotIndex} " +
                    $"targetSlot={result.TargetSlotIndex} success={result.EnchantSucceeded} " +
                    $"cost={(player.CharacterId == store.OwnerCharacterId ? 0 : store.Cost)} " +
                    $"endurance={result.Endurance} exp={result.ExperienceGain}");

                if (result.Endurance <= 0
                    && _stores.TryClose(
                        store.OwnerSessionId, store.OwnerCharacterId, out var exhaustedStore))
                {
                    await BroadcastClose(ownerSession, exhaustedStore, includeOwner: true);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ExpertJobStore] ENCHANT failed owner={store.OwnerCharacterId}: {ex.Message}");
                if (!persisted)
                    await SendEnchantError(session, EnchanterStoreUseService.ErrorInvalidState);
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async Task HandleRepair(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var player = session.Player;
            if (!RepairExpertJobStoreRequest.IsValid(body)
                || player == null
                || player.CurrentRun != null
                || player.Subtype0Tail?.ExpertJobType != ExpertJobStateCodec.DisjointerType
                || !InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendRepairError(session, DisjointMachineRepairService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            ExpertJobMachineRepairResult result;
            bool success;
            try
            {
                if (_stores.HasStore(player.CharacterId))
                {
                    await SendRepairError(session, DisjointMachineRepairService.ErrorInvalidState);
                    return;
                }

                var state = _disjointMachineStates.Resolve(player.CharacterId);
                lock (lease.SyncRoot)
                {
                    success = DisjointMachineRepairService.TryRepair(
                        lease.Inventory,
                        state,
                        out result);
                }

                if (!success)
                {
                    await SendRepairError(session, result.ErrorCode);
                    return;
                }

                if (!_persistence.Save(
                        lease,
                        lease,
                        (connection, transaction) => _disjointMachineStates.SaveInTransaction(
                            connection,
                            transaction,
                            player.CharacterId,
                            state,
                            0)))
                {
                    await SendRepairError(
                        session,
                        DisjointMachineRepairService.ErrorInvalidState);
                    return;
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    RepairCommand,
                    ExpertJobStorePacketBuilder.BuildRepairNotification(
                        result.Gold,
                        result.Endurance)));
                FileLogger.Log(
                    $"[ExpertJobStore] REPAIR owner={player.CharacterId} " +
                    $"grade={state.MachineGrade} cost={result.Cost} " +
                    $"endurance={result.Endurance} gold={result.Gold}");
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async Task HandleUpgrade(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var player = session.Player;
            if (!UpgradeDisjointMachineRequest.IsValid(body)
                || player == null
                || player.CurrentRun != null
                || player.Subtype0Tail?.ExpertJobType != ExpertJobStateCodec.DisjointerType
                || !InventoryContext.TryGetLease(player.CharacterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendUpgradeError(session, DisjointMachineUpgradeService.ErrorCannotUpgrade);
                return;
            }

            var operationGate = _operations.GetGate(player.CharacterId);
            await operationGate.WaitAsync();
            DisjointMachineUpgradeResult result;
            bool success;
            try
            {
                if (_stores.HasStore(player.CharacterId))
                {
                    await SendUpgradeError(session, DisjointMachineUpgradeService.ErrorCannotUpgrade);
                    return;
                }

                var progress = _subtype0Repository.Load(player.CharacterId);
                var state = _disjointMachineStates.Resolve(player.CharacterId);
                lock (lease.SyncRoot)
                {
                    success = DisjointMachineUpgradeService.TryUpgrade(
                        lease.Inventory,
                        state,
                        progress?.ExpertJobExp ?? player.Subtype0Tail.ExpertJobExp,
                        player.Level,
                        out result);
                }

                if (!success)
                {
                    await SendUpgradeError(session, result.ErrorCode);
                    return;
                }

                if (!_persistence.Save(
                        lease,
                        lease,
                        (connection, transaction) => _disjointMachineStates.SaveInTransaction(
                            connection,
                            transaction,
                            player.CharacterId,
                            state,
                            0)))
                {
                    await SendUpgradeError(
                        session,
                        DisjointMachineUpgradeService.ErrorCannotUpgrade);
                    return;
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    UpgradeCommand,
                    ExpertJobStorePacketBuilder.BuildUpgradeNotification(
                        result.Gold,
                        result.Grade,
                        result.Endurance)));
                FileLogger.Log(
                    $"[ExpertJobStore] UPGRADE owner={player.CharacterId} " +
                    $"grade={result.Grade} cost={result.Cost} " +
                    $"endurance={result.Endurance} gold={result.Gold}");
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async Task HandleClose(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var player = session.Player;
            var characterId = player?.CharacterId ?? 0;
            if (!CloseExpertJobStoreRequest.IsValid(body) || characterId <= 0)
            {
                await SendError(session, header.type, ExpertJobStoreRuntimeService.ErrorInvalidState);
                return;
            }

            var operationGate = _operations.GetGate(characterId);
            await operationGate.WaitAsync();
            ExpertJobStoreSession store = null;
            var removedVisitor = false;
            try
            {
                if (_stores.TryGetOwnedStore(session.SessionId, characterId, out _))
                    _stores.TryClose(session.SessionId, characterId, out store);
                else
                    removedVisitor = _stores.Leave(session.SessionId);
            }
            finally
            {
                operationGate.Release();
            }

            if (store != null)
            {
                await BroadcastClose(session, store, includeOwner: true);
                return;
            }

            if (removedVisitor)
                return;

            await session.SendPacketAsync(BuildCloseNotification(player.UserId));
        }

        public async Task CloseSessionAsync(EnhancedClientSession session, bool includeOwner)
        {
            if (session == null)
                return;

            var characterId = session.Player?.CharacterId ?? 0;
            ExpertJobStoreSession store = null;
            if (characterId > 0
                && _stores.TryGetOwnedStore(session.SessionId, characterId, out _))
            {
                var operationGate = _operations.GetGate(characterId);
                await operationGate.WaitAsync();
                try
                {
                    _stores.TryCloseSession(session.SessionId, out store);
                }
                finally
                {
                    operationGate.Release();
                }
            }
            else
            {
                _stores.Leave(session.SessionId);
            }

            if (store == null)
                return;

            await BroadcastClose(session, store, includeOwner);
        }

        public async Task SendAreaStoresToAsync(EnhancedClientSession session)
        {
            var player = session?.Player;
            if (player == null || player.CharacterId <= 0 || player.CurrentRun != null)
                return;

            foreach (var store in _stores.GetStoresInArea(player.CurTownId, player.CurAreaId))
            {
                if (store.OwnerCharacterId == player.CharacterId)
                    continue;

                await session.SendPacketAsync(BuildCreateNotification(store));
            }
        }

        private async Task BroadcastClose(
            EnhancedClientSession ownerSession,
            ExpertJobStoreSession store,
            bool includeOwner)
        {
            var notification = BuildCloseNotification(store.OwnerUserId);
            if (includeOwner)
                await ownerSession.SendPacketAsync(notification);
            await _sessions.BroadcastToAreaAsync(
                store.TownId,
                store.AreaId,
                store.OwnerCharacterId,
                notification,
                ownerSession.ListenerPort);
            FileLogger.Log($"[ExpertJobStore] CLOSE owner={store.OwnerCharacterId} uid={store.OwnerUserId}");
        }

        private static Task SendError(EnhancedClientSession session, ushort type, byte errorCode)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                type,
                CommonPacketBodyBuilder.BuildCmdError(errorCode)));
        }

        private static byte[] BuildCreateNotification(ExpertJobStoreSession store)
        {
            return GamePacketEnvelopeBuilder.Build(
                0x00,
                CreateExpertJobNotification,
                ExpertJobStorePacketBuilder.BuildCreateExpertJobNotification(store));
        }

        private static byte[] BuildCloseNotification(ushort ownerUserId)
        {
            return GamePacketEnvelopeBuilder.Build(
                0x00,
                CloseExpertJobNotification,
                ExpertJobStorePacketBuilder.BuildCloseNotification(ownerUserId));
        }

        private static Task SendDisjointError(EnhancedClientSession session, byte errorCode)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                DisjointCommand,
                ExpertJobStorePacketBuilder.BuildDisjointError(errorCode)));
        }

        private static Task SendRepairError(EnhancedClientSession session, byte errorCode)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                RepairCommand,
                ExpertJobStorePacketBuilder.BuildRepairError(errorCode)));
        }

        private static Task SendUpgradeError(EnhancedClientSession session, byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                UpgradeCommand,
                ExpertJobStorePacketBuilder.BuildUpgradeError(errorCode)));

        private static Task SendEnchantError(EnhancedClientSession session, byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                EnchantCommand,
                CommonPacketBodyBuilder.BuildCmdError(errorCode)));
    }
}
