using DfoServer.Game.Accounts;
using DfoServer.Game.Appearance;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Characters;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.KnightShield;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Names;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class CharacterSelectHandler
    {
        internal const byte A21MaxCreateJob = 13;

        internal static bool IsSupportedA21CreateJob(byte job)
            => job <= A21MaxCreateJob;

        private readonly ISelectCharacterDataSource _selectCharacterDataSource;
        private readonly ICharacterRepository _characterRepository;
        private readonly GetUserInfoTemplate _getUserInfoTemplate;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly Game.Session.ISessionDirectory _sessions;   // 他人外观 PULL: 按 uid 找目标在线会话; 可空(上游注册表)
        private readonly GrowthCapsuleSyncService _growthCapsule;
        private readonly IMercenaryRestrictionService _mercenaryRestrictions;
        private readonly DailyResetService _dailyResetService;
        private readonly Game.Dungeon.DungeonPersistentEffectApplicationService
            _dungeonPersistentEffects;
        private readonly Game.Dungeon.DungeonInstanceRegistry _dungeonInstances;
        private readonly IGameDatabase _database;
        private readonly Game.CharacterData.SqliteSubtype0FieldsRepository
            _subtype0Repository;
        private readonly Game.CharacterData.SqliteSubtype1Repository
            _subtype1Repository;
        private readonly Game.Skills.SqlitePvpSkillRepository
            _pvpSkillRepository;
        private readonly Network.Builders.InitPacketBuilderRegistry
            _initPacketBuilders;

        public string ProtocolName => "GameProtocol";

        public CharacterSelectHandler(
            ISelectCharacterDataSource selectCharacterDataSource,
            ICharacterRepository characterRepository,
            GetUserInfoTemplate getUserInfoTemplate,
            Game.Session.ISessionDirectory sessions = null,
            IMercenaryRestrictionService mercenaryRestrictions = null,
            IGameDatabase database = null,
            DailyResetService dailyResetService = null)
            : this(
                null,
                selectCharacterDataSource,
                characterRepository,
                getUserInfoTemplate,
                sessions,
                null,
                mercenaryRestrictions,
                database,
                dailyResetService)
        {
        }

        internal CharacterSelectHandler(
            Game.Dungeon.DungeonPersistentEffectApplicationService
                dungeonPersistentEffects,
            ISelectCharacterDataSource selectCharacterDataSource,
            ICharacterRepository characterRepository,
            GetUserInfoTemplate getUserInfoTemplate,
            Game.Session.ISessionDirectory sessions = null,
            Game.Dungeon.DungeonInstanceRegistry dungeonInstances = null,
            IMercenaryRestrictionService mercenaryRestrictions = null,
            IGameDatabase database = null,
            DailyResetService dailyResetService = null)
        {
            _database = database ?? GameDatabase.CreateDefault();
            _selectCharacterDataSource = selectCharacterDataSource ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _getUserInfoTemplate = getUserInfoTemplate;
            _honorLevel = new HonorLevelSyncService(_characterRepository, _database);
            _sessions = sessions;
            _growthCapsule = new GrowthCapsuleSyncService(_characterRepository, _database);
            _mercenaryRestrictions = mercenaryRestrictions;
            _dailyResetService = dailyResetService ?? new DailyResetService(_database);
            _dungeonPersistentEffects = dungeonPersistentEffects;
            _dungeonInstances = dungeonInstances;
            _subtype0Repository = new Game.CharacterData.SqliteSubtype0FieldsRepository(
                _database);
            _subtype1Repository = new Game.CharacterData.SqliteSubtype1Repository(
                _database);
            _pvpSkillRepository = new Game.Skills.SqlitePvpSkillRepository(
                _database);
            _initPacketBuilders = new Network.Builders.InitPacketBuilderRegistry(
                _database, _sessions);
        }

        // 按 UserId 找同一游戏频道的在线会话。城镇同屏也按 listener 隔离。
        internal static EnhancedClientSession FindInspectableOnlineByUserId(
            Game.Session.ISessionDirectory sessions,
            EnhancedClientSession requester,
            ushort uid)
        {
            if (sessions == null || requester == null)
                return null;

            EnhancedClientSession match = null;
            foreach (var s in sessions.GetAllGameSessions())
            {
                if (s?.Player != null
                    && s.Player.CharacterId > 0
                    && s.Player.UserId == uid
                    && PartyHandler.IsSameGameChannel(requester, s))
                {
                    // UserId is only 16 bits on the wire. Do not disclose an
                    // arbitrary player when two full character ids collide.
                    if (match != null && !ReferenceEquals(match, s))
                        return null;
                    match = s;
                }
            }

            return match;
        }

        private bool IsAuthorizedInspectRequester(
            EnhancedClientSession requester)
        {
            var player = requester?.Player;
            return _sessions != null
                && requester.Account?.AccountId > 0
                && player != null
                && player.CharacterId > 0
                && player.UserId != 0
                && player.UserId == unchecked((ushort)player.CharacterId)
                && _sessions.TryGet(player.CharacterId, out var current)
                && ReferenceEquals(current, requester);
        }

        private bool IsCurrentInspectableTarget(
            EnhancedClientSession requester,
            EnhancedClientSession target,
            ushort requestedUserId)
        {
            var player = target?.Player;
            return _sessions != null
                && target.Account?.AccountId > 0
                && player != null
                && player.CharacterId > 0
                && player.UserId == requestedUserId
                && player.UserId
                    == unchecked((ushort)player.CharacterId)
                && _sessions.TryGet(
                    player.CharacterId,
                    out var current)
                && ReferenceEquals(current, target)
                && PartyHandler.IsSameGameChannel(requester, target);
        }

        private static int ResolveAccountId(EnhancedClientSession session, CharacterRecord record)
        {
            if (session?.Account?.AccountId > 0)
                return session.Account.AccountId;

            return record?.AccountId ?? 0;
        }

        private InventoryService TryLoadInventoryForLease(int characterId, int accountId)
        {
            if (characterId <= 0 || accountId <= 0)
                return null;

            try
            {
                using (var connection = _database.OpenConnection())
                {
                    return InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        _database);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] inventory lease load failed cid={characterId} aid={accountId}: {ex}");
                return null;
            }
        }

        private InventoryLease TryRegisterInventoryLease(
            EnhancedClientSession session,
            CharacterRecord record,
            InventoryService inventory)
        {
            if (session == null || record == null || inventory == null)
                return null;

            try
            {
                return InventoryContext.Register(session.SessionId, record.CharacterId, inventory);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] inventory lease register failed cid={record.CharacterId} aid={inventory.AccountId}: {ex}");
                return null;
            }
        }

        private void ApplyLoginItemExpirationMaintenance(InventoryLease lease)
        {
            if (lease?.Inventory == null)
                return;

            int removedItems;
            lock (lease.SyncRoot)
            {
                removedItems = InventoryItemLifecycleService.RemoveExpiredItems(
                    lease.Inventory,
                    InventoryItemLifecycleService.UtcNowUnixSeconds(),
                    null);
            }

            if (removedItems <= 0)
                return;

            if (!InventoryPersistenceService.SaveDirty(lease))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] expired item cleanup persistence failed " +
                    $"cid={lease.CharacterId} removed={removedItems}");
                return;
            }

            FileLogger.Log(
                $"[{ProtocolName}] expired item cleanup applied " +
                $"cid={lease.CharacterId} removed={removedItems}");
        }

        private bool TryApplyAccountDailyReset(int accountId)
        {
            if (accountId <= 0)
                return true;

            try
            {
                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var applied = false;
                    var ok = _dailyResetService.TryRunAccountFirstLoginReset(
                        connection,
                        transaction,
                        accountId,
                        (conn, tx) =>
                        {
                            if (!_dailyResetService.ResetUsableCountLimitsForAccount(
                                    conn,
                                    tx,
                                    accountId))
                            {
                                return false;
                            }

                            return ItemPurchaseLimitService.ResetPurchasesForAccount(
                                conn,
                                tx,
                                accountId);
                        },
                        out applied);
                    if (!ok)
                    {
                        transaction.Rollback();
                        return false;
                    }

                    transaction.Commit();
                    if (applied)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] account daily reset applied account_id={accountId}");
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] account daily reset failed account_id={accountId}: {ex}");
                return false;
            }
        }

        internal async Task HandleResolvedSelectCharacterAsync(
            EnhancedClientSession session,
            CharacterRecord record,
            int slot)
        {
            GameChannelSpawn selectedSpawn = null;
            try
            {
                // 换角色前丢弃上一个角色的副本局: PlayerContext 实例跨角色复用, 不丢会把
                // 上个角色的副本状态带给下个角色。
                Dungeon.DungeonRunLifecycle.EndRunOnTeardown(
                    session,
                    "select_character",
                    _dungeonInstances);

                if (record != null)
                {
                    if (_dungeonPersistentEffects != null)
                    {
                        var recovery = _dungeonPersistentEffects
                            .RecoverCharacter(record.CharacterId);
                        if (recovery.CommittedCount > 0)
                        {
                            record = _characterRepository.GetById(
                                record.CharacterId) ?? record;
                        }
                        if (recovery.CommittedCount > 0
                            || recovery.DeadLetterCount > 0
                            || recovery.FailedCount > 0
                            || recovery.HasRemaining)
                        {
                            FileLogger.Log(
                                $"[{ProtocolName}] dungeon effect recovery: " +
                                $"cid={record.CharacterId} " +
                                $"committed={recovery.CommittedCount} " +
                                $"dead={recovery.DeadLetterCount} " +
                                $"failed={recovery.FailedCount} " +
                                $"pages={recovery.PagesScanned} " +
                                $"scanned={recovery.RecordsScanned} " +
                                $"remaining={recovery.RemainingCount} " +
                                $"pageLimit={recovery.ReachedPageLimit} " +
                                $"timeLimit={recovery.ReachedTimeLimit}");
                        }
                    }

                    var inventory = TryLoadInventoryForLease(
                        record.CharacterId,
                        ResolveAccountId(session, record));
                    selectedSpawn = GameChannelSpawnPolicy.Resolve(
                        session.ListenerPort,
                        record.TownId);
                    session.Player.HydrateFrom(
                        record,
                        selectedSpawn);
                    var registeredLease = TryRegisterInventoryLease(session, record, inventory);
                    ApplyLoginItemExpirationMaintenance(registeredLease);

                    try
                    {
                        var tail = _subtype0Repository.Load(record.CharacterId);
                        var skillTreeIndex = _subtype1Repository.LoadSkillTreeIndex(
                            record.CharacterId);
                        if (skillTreeIndex.HasValue)
                        {
                            tail = tail ?? new UserInfoMinimumTailSnapshot();
                            tail.SkillTreeIndex = skillTreeIndex.Value;
                        }
                        if (tail == null && session.Account != null)
                            tail = new UserInfoMinimumTailSnapshot();
                        if (tail != null && session.Account != null)
                        {
                            _honorLevel.ApplyToSubtype0Tail(tail, session.Account.AccountId, null);
                        }
                        if (GameNetworkConfig.IsRaidListener(session.ListenerPort))
                        {
                            tail = tail ?? new UserInfoMinimumTailSnapshot();
                            tail.ChannelDisplayMode = 5;
                            tail.ChannelType = GameNetworkConfig.ResolveLoginEnvironment(session.ListenerPort);
                            tail.ChannelId = (ushort)GameNetworkConfig
                                .ResolveGameChannel(session.ListenerPort).ChannelId;
                        }
                        if (tail != null)
                        {
                            record.Subtype0Tail = tail;
                            session.Player.Subtype0Tail = tail;
                        }

                        // 城镇模型使用会话内的 AppearanceEntries；不要使用可能过期/空的 characters.appearance_blob，
                        // 每次选角都从当前穿戴栏重建，避免角色选人/副本正确但城镇武器外观错误。
                        record.Appearance = AppearanceService.LoadOnlineAppearanceFromInventory(
                            record.CharacterId,
                            record.Job,
                            record.GrowType,
                            database: _database);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[{ProtocolName}] Select character subtype0 load failed: {ex.Message}");
                    }

                    session.Player.AppearanceEntries = record.Appearance ?? Array.Empty<CharacterAppearanceEntry>();
                    if (GameChannelSpawnPolicy.ShouldPersistPosition(
                            session.ListenerPort))
                    {
                        _characterRepository.UpdatePosition(
                            session.Player.CharacterId,
                            session.Player.CurTownId,
                            session.Player.CurAreaId,
                            session.Player.CurPosX,
                            session.Player.CurPosY,
                            session.Player.CurDirection,
                            session.Player.CurAreaState);
                    }
                    FileLogger.Log($"[{ProtocolName}] Select character hydrated session {session.SessionId} slot={slot} <- character_id={record.CharacterId} name={record.DisplayName} town={session.Player.CurTownId} area={session.Player.CurAreaId} pos=({session.Player.CurPosX},{session.Player.CurPosY})");
                }
                else
                {
                    FileLogger.Log($"[{ProtocolName}] Select character: no record resolved, keeping in-memory defaults");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Select character DB load failed: {ex.Message}");
            }

            var ownerCharId = session.Player.CharacterId > 0 ? session.Player.CharacterId : _selectCharacterDataSource.GetSeedCharacterId();
            var ownerAcctId = ResolveAccountId(session, record);
            if (ownerAcctId <= 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] Select character rejected: missing account id " +
                    $"session={session.SessionId} cid={ownerCharId}");
                session.Close();
                return;
            }
            if (!TryApplyAccountDailyReset(ownerAcctId))
            {
                session.Close();
                return;
            }
            var characterList = BuildCharacterList(ownerAcctId);
            var routingByte = _getUserInfoTemplate != null ? _getUserInfoTemplate.Pkt0RoutingByte7 : (byte)0;

            // Character selection rebuilds the current character's complete skill state.
            // Keep reconciliation explicit because shared snapshot loads also serve non-skill refreshes.
            if (_selectCharacterDataSource is SqliteSelectCharacterDataSource sqliteDataSource)
            {
                sqliteDataSource.PrepareForSkillSynchronization(
                    ownerCharId,
                    ownerAcctId);
            }

            SkillInfoSnapshot pvpSkillOverride = null;
            if (GameNetworkConfig.IsFreeDuelListener(session.ListenerPort))
            {
                var skillOwner = _characterRepository.GetById(ownerCharId);
                if (skillOwner != null)
                {
                    pvpSkillOverride = _pvpSkillRepository.LoadOrInitialize(
                        ownerCharId,
                        skillOwner.Job,
                        skillOwner.Level,
                        skillOwner.GrowType);
                    FileLogger.Log(
                        $"[{ProtocolName}] Loaded independent PvP skills " +
                        $"character_id={ownerCharId} " +
                        $"entries={pvpSkillOverride.Pages[0].Entries.Count}+" +
                        $"{pvpSkillOverride.Pages[1].Entries.Count}");
                }
            }

            foreach (var packet in SelectCharacterPacketBuilder.BuildPacketStream(
                         _selectCharacterDataSource,
                         ownerCharId,
                         ownerAcctId,
                         pvpSkillOverride,
                         selectedSpawn,
                         _initPacketBuilders))
                await session.SendPacketAsync(packet);

            if (InventoryContext.TryGetLease(ownerCharId, out var inventoryLease)
                && inventoryLease.IsOwnedBy(session.SessionId))
            {
                if (DailyRefillItemService.TryApply(
                        inventoryLease,
                        _database,
                        out var dailyRefillGrants))
                {
                    foreach (var group in dailyRefillGrants
                        .Where(item => item.SlotIndex >= 0)
                        .GroupBy(item => item.ListType))
                    {
                        await InventoryRefreshSender.SendOnlineUpdateItemList(
                            session,
                            group.Key,
                            group.Select(item => item.SlotIndex));
                    }

                    if (dailyRefillGrants.Count > 0)
                        FileLogger.Log(
                            $"[{ProtocolName}] DAILY_REFILL item updates cid={ownerCharId} count={dailyRefillGrants.Count}");
                }
                else
                {
                    // The database transaction rolled back. Discard any earlier in-memory
                    // grants from the same batch before the lease can be persisted later.
                    inventoryLease.Inventory.ClearDirtyState();
                    var restoredInventory = TryLoadInventoryForLease(ownerCharId, ownerAcctId);
                    if (restoredInventory != null)
                        TryRegisterInventoryLease(
                            session,
                            _characterRepository.GetById(ownerCharId),
                            restoredInventory);
                    FileLogger.Log(
                        $"[{ProtocolName}] daily refill rolled back and inventory reloaded cid={ownerCharId}");
                }
            }

            var visibilityBits = session.Player.Subtype0Tail?.UserStateBits ?? (byte)3;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.CHARAC_INVISIBLE_FALGS,
                CharacterVisibilityBodyBuilder.Build(session.Player.UserId, visibilityBits)));

            var cloneTitle = AppearanceService.LoadCloneTitleItemId(
                ownerCharId,
                _database);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0239,
                AppearanceService.BuildCloneTitleAckBody(cloneTitle, suppressMessage: 1)));
            FileLogger.Log($"[{ProtocolName}] SELECT_CHARACTER clone title restore: char={ownerCharId} cloneTitle=0x{cloneTitle:X8}");

            // 切角色可能跳过 GET_USERINFO，主选角流后补发账号 subtype2。
            await session.SendPacketAsync(BuildPacketWithRouting(0x00, 0x0002, characterList.Body, routingByte));
            await SendHonorLevelInfoAsync(session, "select-character-ready", characterList.Honor);
            await _growthCapsule.SendExpProgressAsync(
                session, "select-character-ready", honor: characterList.Honor);
        }

        public async Task Handle_ENUM_CMDPACKET_GET_USERINFO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            try
            {
                // body = {u16 targetUserId, u8 mode}，允许 padding。
                // 城镇查看基本信息用 mode=3，只回一个 USERINFO subtype 3。
                // mode 0/1 仍按目标查；mode 2 回请求者名册。
                // 无效、过期、歧义或跨频道目标直接失败。
                if (_sessions == null || body == null || body.Length < 3)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GET_USERINFO rejected malformed " +
                        $"request bodyLen={body?.Length ?? 0}");
                    return;
                }

                ushort reqUid = BitConverter.ToUInt16(body, 0);
                byte mode = body[2];
                FileLogger.Log(
                    $"[{ProtocolName}] GET_USERINFO uid={reqUid} mode={mode} " +
                    $"selfUid={session.Player?.UserId} " +
                    $"selfCid={session.Player?.CharacterId}");
                if (mode != 0x00
                    && mode != 0x01
                    && mode != 0x02
                    && mode != 0x03)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GET_USERINFO rejected " +
                        $"unknown mode={mode}");
                    return;
                }
                if (mode != 0x02)
                {
                    if (reqUid == 0xFFFF)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] GET_USERINFO inspect " +
                            "rejected invalid uid=0xFFFF");
                        return;
                    }
                    if (!IsAuthorizedInspectRequester(session))
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] GET_USERINFO inspect " +
                            $"rejected unauthenticated/stale requester");
                        return;
                    }
                    var target = FindInspectableOnlineByUserId(
                        _sessions,
                        session,
                        reqUid);
                    if (target != null)
                    {
                        var otherRoutingByte =
                            _getUserInfoTemplate?.Pkt0RoutingByte7
                            ?? (byte)0x01;
                        var packets = OtherUserInfoResponseBuilder.Build(
                            _selectCharacterDataSource,
                            _characterRepository,
                            target,
                            mode,
                            otherRoutingByte,
                            _database,
                            out var detailError);
                        if (!IsAuthorizedInspectRequester(session)
                            || !IsCurrentInspectableTarget(
                                session,
                                target,
                                reqUid))
                        {
                            FileLogger.Log(
                                $"[{ProtocolName}] GET_USERINFO " +
                                $"generation changed before response " +
                                $"uid={reqUid}");
                            return;
                        }
                        foreach (var packet in packets)
                        {
                            if (!IsAuthorizedInspectRequester(session)
                                || !IsCurrentInspectableTarget(
                                    session,
                                    target,
                                    reqUid))
                            {
                                FileLogger.Log(
                                    $"[{ProtocolName}] GET_USERINFO " +
                                    $"response aborted after generation " +
                                    $"change uid={reqUid}");
                                return;
                            }
                            await session.SendPacketAsync(packet);
                        }
                        FileLogger.Log(
                            $"[{ProtocolName}] GET_USERINFO other MATCH " +
                            $"reqUid={reqUid} mode={mode} " +
                            $"packets={packets.Count} " +
                            $"detailError={detailError ?? "none"} " +
                            $"targetCid={target.Player.CharacterId}");
                        return;
                    }

                    FileLogger.Log(
                        $"[{ProtocolName}] GET_USERINFO other reqUid={reqUid} " +
                        $"mode={mode} no same-channel target " +
                        $"selfPort={session.ListenerPort}");
                    return;
                }

                var accountId = session.Account?.AccountId ?? 0;
                if (accountId <= 0)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] GET_USERINFO roster rejected " +
                        "unauthenticated requester");
                    return;
                }
                var characterList = BuildCharacterList(accountId);
                byte routingByte = _getUserInfoTemplate != null ? _getUserInfoTemplate.Pkt0RoutingByte7 : (byte)0;
                await session.SendPacketAsync(BuildPacketWithRouting(0x00, 0x0002, characterList.Body, routingByte));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0286, new byte[] { 0x00, 0x04 }));
                await SendHonorLevelInfoAsync(session, "get-userinfo-ready", characterList.Honor);
                await _growthCapsule.SendExpProgressAsync(
                    session, "get-userinfo-ready", honor: characterList.Honor);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] GET_USERINFO EXCEPTION: {ex}");
            }
        }

        public async Task Handle_ENUM_CMDPACKET_OTHER_USER_TITLE_BOOK_LIST(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!IsAuthorizedInspectRequester(session))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] OTHER_USER_TITLE_BOOK_LIST " +
                    $"rejected unauthenticated/stale requester");
                return;
            }

            if (_sessions == null || body == null || body.Length < 2)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] OTHER_USER_TITLE_BOOK_LIST rejected: " +
                    $"bodyLen={body?.Length ?? 0} sessions={_sessions != null}");
                return;
            }

            var requestedUserId = BitConverter.ToUInt16(body, 0);
            if (requestedUserId == 0xFFFF)
            {
                return;
            }

            var target = FindInspectableOnlineByUserId(
                _sessions,
                session,
                requestedUserId);
            if (target == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] OTHER_USER_TITLE_BOOK_LIST " +
                    $"uid={requestedUserId} no same-channel target");
                return;
            }

            var packets = OtherUserInfoResponseBuilder.BuildTitleBookList(
                _selectCharacterDataSource,
                _characterRepository,
                target,
                infoType: 1,
                out var error);
            if (!IsAuthorizedInspectRequester(session)
                || !IsCurrentInspectableTarget(
                    session,
                    target,
                    requestedUserId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] OTHER_USER_TITLE_BOOK_LIST " +
                    $"generation changed before response " +
                    $"uid={requestedUserId}");
                return;
            }
            foreach (var packet in packets)
            {
                if (!IsAuthorizedInspectRequester(session)
                    || !IsCurrentInspectableTarget(
                        session,
                        target,
                        requestedUserId))
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] OTHER_USER_TITLE_BOOK_LIST " +
                        $"response aborted after generation change " +
                        $"uid={requestedUserId}");
                    return;
                }
                await session.SendPacketAsync(packet);
            }

            FileLogger.Log(
                $"[{ProtocolName}] OTHER_USER_TITLE_BOOK_LIST " +
                $"uid={requestedUserId} packets={packets.Count} " +
                $"error={error ?? "none"}");
        }

        private static bool NameBytesEqual(byte[] a, byte[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] BuildPacketWithRouting(byte command, ushort type, byte[] body, byte routingByte7)
        {
            int totalLen = 15 + (body != null ? body.Length : 0);
            var packet = new byte[totalLen];
            packet[0] = command;
            Buffer.BlockCopy(BitConverter.GetBytes(type), 0, packet, 1, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(totalLen), 0, packet, 3, 4);
            packet[7] = routingByte7;
            if (body != null && body.Length > 0)
                Buffer.BlockCopy(body, 0, packet, 15, body.Length);
            return packet;
        }

        public async Task Handle_ENUM_CMDPACKET_CHECK_DOUBLE_CHARACTER_NAME(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 5)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, new byte[] { 0x02 }));
                return;
            }

            var nameLen = BitConverter.ToInt32(body, 0);
            if (nameLen <= 0 || nameLen > 30 || 4 + nameLen > body.Length)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, new byte[] { 0x14 }));
                return;
            }

            var nameRaw = new byte[nameLen];
            Buffer.BlockCopy(body, 4, nameRaw, 0, nameLen);
            if (!NameInputValidator.TryValidateRawName(nameRaw, minBytes: 2, maxBytes: 30, out var name, out var failure))
            {
                FileLogger.Log($"[{ProtocolName}] CHECK_NAME: invalid name reason={failure}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x02B5,
                    CommonPacketBodyBuilder.BuildCmdError(NameInputValidator.InvalidNameErrorCode)));
                return;
            }

            var existing = _characterRepository.GetByName(name);
            if (existing != null)
            {
                // 20/24 公告 已存在的角色名
                // 159 公告 包含无法使用的文字
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x02B5,
                    CommonPacketBodyBuilder.BuildCmdError(24)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x02B5, CommonPacketBodyBuilder.BuildSuccessAck()));
            FileLogger.Log($"[{ProtocolName}] CHECK_NAME: '{name}' is available");
        }

        public async Task Handle_ENUM_CMDPACKET_CREATE_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
                return;
            }

            var job = body[0];
            if (!IsSupportedA21CreateJob(job))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
                return;
            }

            var nameLen = BitConverter.ToInt32(body, 1);
            if (nameLen < 2 || nameLen > 18 || 5 + nameLen + 1 > body.Length)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x12 }));
                return;
            }

            var nameRaw = new byte[nameLen];
            Buffer.BlockCopy(body, 5, nameRaw, 0, nameLen);
            if (!NameInputValidator.TryValidateRawName(nameRaw, minBytes: 2, maxBytes: 18, out var nameStr, out var nameFailure))
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: invalid name reason={nameFailure}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0005,
                    CommonPacketBodyBuilder.BuildCmdError(NameInputValidator.InvalidNameErrorCode)));
                return;
            }

            var accountId = session.Account?.AccountId ?? 1;

            var count = _characterRepository.CountByAccount(accountId);
            var slotLimit = CharacterSlotPolicy.ResolveSlotLimit(_getUserInfoTemplate?.GateOrCount1, _getUserInfoTemplate?.GateOrCount2);
            if (!CharacterSlotPolicy.HasAvailableSlot(count, _getUserInfoTemplate?.GateOrCount1, _getUserInfoTemplate?.GateOrCount2))
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: account_id={accountId} has no free character slot (count={count}, limit={slotLimit})");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
                return;
            }

            if (_characterRepository.GetByName(nameStr) != null)
            {
                // 与 CHECK_DOUBLE_CHARACTER_NAME 一致：24 = 已存在的角色名
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0005,
                    CommonPacketBodyBuilder.BuildCmdError(24)));
                return;
            }

            var newCharId = 0;
            try
            {
                var record = new CharacterRecord
                {
                    AccountId = accountId,
                    Name = nameRaw,
                    Job = job,
                    GrowType = 0,
                    Level = 1,
                    TownId = 1,
                    AreaId = 0,
                    PosX = 474,
                    PosY = 234,
                    Direction = 5,
                    AreaState = 3,
                };

                newCharId = _characterRepository.Create(record);
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: created character_id={newCharId} name='{nameStr}' job={job} for account_id={accountId}");

                _selectCharacterDataSource.InitializeNewCharacter(newCharId, accountId, job);

                // 1. CMD ACK success
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, CommonPacketBodyBuilder.BuildSuccessAck()));

                // 2. NOTI 2 subtype 2 — character list refresh
                var characterList = BuildCharacterList(accountId);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00, 0x0002, characterList.Body));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER failed: {ex}");
                if (newCharId > 0)
                {
                    try
                    {
                        _characterRepository.SoftDelete(newCharId);
                        FileLogger.Log($"[{ProtocolName}] CREATE_CHARACTER: rolled back character_id={newCharId}");
                    }
                    catch (Exception rollbackEx)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] CREATE_CHARACTER rollback failed "
                            + $"character_id={newCharId}: {rollbackEx}");
                    }
                }
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0005, new byte[] { 0x04 }));
            }
        }

        public async Task Handle_ENUM_CMDPACKET_DELETE_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x02 }));
                return;
            }

            // wire 格式实测：DELETE_CHARACTER 请求 = [slot:u16 LE][nameLen:u32 LE][name]，
            // 如 01 00 03 00 00 00 34 34 34 = slot1, len3, "444"。
            // 旧解析 [slot:u8][nameLen@1] 会把 nameLen 错读为 768 → 恒触发 nameLen>30 静默拦截。
            var slotIndex = BitConverter.ToUInt16(body, 0);
            var nameLen = BitConverter.ToInt32(body, 2);
            if (nameLen <= 0 || nameLen > 30 || 6 + nameLen > body.Length)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x02 }));
                return;
            }

            var nameRaw = new byte[nameLen];
            Buffer.BlockCopy(body, 6, nameRaw, 0, nameLen);
            var name = ClientTextEncoding.GetString(nameRaw);
            var accountId = session.Account?.AccountId ?? 1;

            var list = _characterRepository.ListByAccount(accountId);
            if (slotIndex >= list.Count)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x02 }));
                return;
            }

            var target = list[slotIndex];
            if (!NameBytesEqual(target.Name, nameRaw))
            {
                FileLogger.Log($"[{ProtocolName}] DELETE_CHARACTER: name mismatch slot={slotIndex} expected='{target.DisplayName}' got='{name}'");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x15 }));
                return;
            }

            if (_mercenaryRestrictions != null && !_mercenaryRestrictions.CanDelete(target.CharacterId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] DELETE_CHARACTER blocked: character_id={target.CharacterId} is on mercenary expedition");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0006,
                    new byte[] { 0x28 }));
                return;
            }

            try
            {
                // 软删 + 前移 slot 在同一事务内原子完成: 被删 slot 之后所有活跃角色 slot 前移一位,
                // 保持账号内 slot 连续。客户端会话内列表刷新(创建/删除后补发列表)要求 slot 连续,
                // 空洞 slot 会被当数组索引访问越界崩溃。用 DB 实际 slot(target.SlotIndex)而非客户端
                // 索引, 保证即使列表带空洞压缩也正确。两步非事务时, 软删成功但前移失败会留下空洞。
                _characterRepository.SoftDeleteAndCompactSlots(accountId, target.CharacterId, target.SlotIndex);
                // 删除角色 → 清理好友图/表两方向关系，并向显示它的在线会话推 subcmd=2 删节点。
                if (_sessions != null)
                    await UnitedFriendSystem.HandleCharacterDeletedAsync(
                        name, _sessions);
                FileLogger.Log($"[{ProtocolName}] DELETE_CHARACTER: soft-deleted character_id={target.CharacterId} name='{name}' slot={target.SlotIndex} compacted");

                // 删除 ACK = [0x01][flag:1][slotIndex:u16]（逆向定论）：
                //   body[0]=0x01 Ok（必须非 0；0x00 → 客户端判 Error，ErrCode=body[1] 崩溃）；
                //   body[1]=flag：0 = 不设删除残留态（保守）；
                //   body[2..3]=客户端本地列表 0-based slotIndex（回显请求值，勿用 charId）。
                // 旧 [00][charId] ACK → 客户端判 Error 且把 charId 低字节当 ErrCode → 闪退。
                var ackBody = new byte[]
                {
                    0x01,
                    0x00,
                    (byte)(slotIndex & 0xFF),
                    (byte)((slotIndex >> 8) & 0xFF),
                };
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, 0x0006, ackBody));

                // 删除后【不补发角色列表】：客户端 ACK Ok 分支已从本地角色向量擦除该角色并后移
                // 压缩，本地自管理刷新。服务端重发列表反而与客户端本地状态对账冲突。
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] DELETE_CHARACTER failed: {ex.Message}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0006, new byte[] { 0x28 }));
            }
        }

        public async Task Handle_ENUM_CMDPACKET_RETURN_SELECT_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var shieldReset = BuildKnightShieldReturnSelectReset(session?.Player);
            if (shieldReset != null)
            {
                // 423 窗口会跨角色保留 catalog。离开角色时先把它的五槽归一为空，
                // 避免下一守护者的真实物品 ID 被旧 growType catalog 反向清零。
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    KnightShieldDeckBodyBuilder.DeckNotificationType,
                    shieldReset));
                FileLogger.Log(
                    $"[{ProtocolName}] RETURN_SELECT_CHARACTER: cleared client knight-shield deck " +
                    $"for character_id={session.Player.CharacterId}");
            }

            Dungeon.DungeonRunLifecycle.EndRunOnTeardown(
                session,
                "return_select_character",
                _dungeonInstances);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0007, CommonPacketBodyBuilder.BuildSuccessAck()));
            FileLogger.Log($"[{ProtocolName}] RETURN_SELECT_CHARACTER: sent ACK for session {session.SessionId}");
            await SendCharacterListAsync(session);
        }

        internal static byte[] BuildKnightShieldReturnSelectReset(Game.Session.PlayerContext player)
        {
            if (player == null
                || player.CharacterId <= 0
                || !KnightShieldDataProvider.IsEligibleCharacter(player.Job))
            {
                return null;
            }

            return KnightShieldDeckBodyBuilder.BuildDeck(new KnightShieldDeckSnapshot());
        }

        public async Task Handle_CHANGE_CHARAC_SLOT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 8)
            {
                FileLogger.Log($"[{ProtocolName}] CHANGE_CHARAC_SLOT body too short ({body?.Length ?? 0}B)");
                return;
            }

            var slotA = BitConverter.ToUInt32(body, 0);
            var slotB = BitConverter.ToUInt32(body, 4);
            var accountId = session.Account?.AccountId ?? 1;

            _characterRepository.SwapSlotIndexes(accountId, (byte)slotA, (byte)slotB);
            FileLogger.Log($"[{ProtocolName}] CHANGE_CHARAC_SLOT swapped slot {slotA} <-> {slotB} for account_id={accountId}");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0127, CommonPacketBodyBuilder.BuildSuccessAck()));
            await SendCharacterListAsync(session);
        }

        public async Task SendCharacterListAsync(EnhancedClientSession session)
        {
            var accountId = session.Account?.AccountId ?? 1;
            var characterList = BuildCharacterList(accountId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, characterList.Body));
            await SendHonorLevelInfoAsync(session, "character-list-ready", characterList.Honor);
            FileLogger.Log($"[{ProtocolName}] Sent character list for account_id={accountId}");
        }

        private Task SendHonorLevelInfoAsync(
            EnhancedClientSession session,
            string reason,
            HonorLevelSummary summary)
        {
            return _honorLevel.SendInfoAsync(session, ProtocolName, reason, summary);
        }

        private (byte[] Body, HonorLevelSummary Honor) BuildCharacterList(int accountId)
        {
            var characters = _characterRepository.ListByAccount(accountId);
            var honorLevel = _honorLevel.LoadSummary(accountId, characters);
            var body = AccountCharacterListBodyBuilder.Build(
                characters,
                _getUserInfoTemplate,
                out _,
                honorLevel,
                accountId,
                _database);
            return (body, honorLevel);
        }
    }
}
