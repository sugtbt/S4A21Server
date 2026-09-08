using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Party;
using DfoServer.Network.Parsers.Town;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class TownHandler
    {
        private static readonly TimeSpan PositionPersistThrottle = TimeSpan.FromSeconds(5);

        private readonly struct TownProjectionGuard
        {
            private TownProjectionGuard(
                DungeonRunIdentity endedRun,
                DungeonSelectionContext selection)
            {
                EndedRun = endedRun;
                Selection = selection;
            }

            internal DungeonRunIdentity EndedRun { get; }
            internal DungeonSelectionContext Selection { get; }

            internal static TownProjectionGuard ForEndedRun(
                DungeonRunIdentity identity) =>
                new TownProjectionGuard(identity, null);

            internal static TownProjectionGuard ForSelection(
                DungeonSelectionContext selection) =>
                new TownProjectionGuard(default(DungeonRunIdentity), selection);
        }

        private readonly ICharacterRepository _characterRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly GrowthCapsuleSyncService _growthCapsule;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly Game.SelectCharacter.SqliteSelectCharacterDataSource _selectDataSource;
        private readonly Game.Party.PartyManager _partyManager;   // 可空: 副本退出/回城时把队员一起拉回城(跟随退出)
        // 可空: 会话目录(charId→session)。同屏区域查询与队员定位共用这一份注册表, 不另设区域广播器。
        private readonly Game.Session.ISessionDirectory _sessions;
        private readonly DungeonInstanceRegistry _dungeonInstances;
        private readonly Game.Raid.RaidManager _raidManager;
        private readonly IGameDatabase _database;
        private Func<EnhancedClientSession, ushort, Guid, int, Task>
            _dungeonGiveupPartyDeparture;
        private Func<Task> _publishTownPartyLists;

        private readonly InventoryRefreshSender _refresh;

        public string ProtocolName => "GameProtocol";

        public TownHandler(
            ICharacterRepository characterRepository,
            Game.SelectCharacter.SqliteSelectCharacterDataSource selectDataSource = null,
            Game.Party.PartyManager partyManager = null,
            Game.Session.ISessionDirectory sessions = null,
            InventoryRefreshSender refresh = null)
            : this(
                characterRepository,
                selectDataSource,
                partyManager,
                sessions,
                refresh,
                dungeonInstances: null,
                raidManager: null,
                database: null)
        {
        }

        internal TownHandler(
            ICharacterRepository characterRepository,
            Game.SelectCharacter.SqliteSelectCharacterDataSource selectDataSource,
            Game.Party.PartyManager partyManager,
            Game.Session.ISessionDirectory sessions,
            InventoryRefreshSender refresh,
            DungeonInstanceRegistry dungeonInstances,
            Game.Raid.RaidManager raidManager,
            IGameDatabase database = null)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _database = database ?? GameDatabase.CreateDefault();
            _honorLevel = new HonorLevelSyncService(
                _characterRepository,
                _database);
            _growthCapsule = new GrowthCapsuleSyncService(
                _characterRepository,
                _database);
            _subtype0Repository = new SqliteSubtype0FieldsRepository(_database);
            _refresh = refresh;
            _selectDataSource = selectDataSource;  // 可空: PVP 房间仍需构建完整 USERINFO subtype1。
            _partyManager = partyManager;          // 可空: 组队副本收尾 fan-out(跟随退出); 与副本共享同一 PartyManager
            _sessions = sessions;                  // 可空: 未注入时退化为单人(不广播)
            _dungeonInstances = dungeonInstances;
            _raidManager = raidManager;
        }

        internal void ConfigureDungeonGiveupPartyDeparture(
            Func<EnhancedClientSession, ushort, Guid, int, Task> handler)
        {
            _dungeonGiveupPartyDeparture = handler;
        }

        internal void ConfigureTownPartyListPublisher(Func<Task> publisher)
        {
            _publishTownPartyLists = publisher;
        }

        // 构建某在线会话玩家的【完整 USERINFO subtype1】(0x0002 occ1, ~1458B: 属性/装备/技能)。
        // 同屏时仅推 subtype0(精简外观)客户端能渲染但判定"对方不在城镇/不可邀请"; self 进游戏收的是 subtype0+subtype1
        // 两份, 故给同屏他人补 subtype1。id 头(bytes 3-4)由 CharacterId 改写为 UserId 以对齐城镇名册。
        internal byte[] BuildFullUserInfoPacket(EnhancedClientSession s)
        {
            if (_selectDataSource == null || s?.Player == null || s.Player.CharacterId <= 0)
                return null;
            try
            {
                var snap = _selectDataSource.Load(s.Player.CharacterId, s.Account?.AccountId ?? 1);
                if (snap?.CharacterRecord == null || snap.InitializationSnapshot?.UserInfoAddition == null)
                    return null;
                if (!new Network.Builders.UserInfoBodyBuilder().TryBuild(snap, 1, out var fullBody) || fullBody == null || fullBody.Length < 5)
                    return null;
                BitConverter.GetBytes(s.Player.UserId).CopyTo(fullBody, 3);
                return GamePacketEnvelopeBuilder.Build(0x00, 0x0002, fullBody);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] BuildFullUserInfoPacket cid={s.Player.CharacterId} 失败: {ex.Message}");
                return null;
            }
        }

        public void PersistPosition(EnhancedClientSession session, bool forceImmediate, string source)
        {
            try
            {
                if (session?.Player == null || session.Player.CharacterId <= 0)
                    return;
                if (!GameChannelSpawnPolicy.ShouldPersistPosition(
                        session.ListenerPort))
                    return;

                var now = DateTime.UtcNow;
                if (!forceImmediate)
                {
                    if (now - session.Player.LastPositionPersistAt < PositionPersistThrottle)
                        return;
                }

                var gate = GameWorld.Town.GetCeraRoomInfo(session.Player.CurTownId);
                if (gate.Town <= 0)
                    return;

                _characterRepository.UpdatePosition(
                    session.Player.CharacterId,
                    session.Player.CurTownId,
                    session.Player.CurAreaId,
                    session.Player.CurPosX,
                    session.Player.CurPosY,
                    session.Player.CurDirection,
                    session.Player.CurAreaState);
                session.Player.LastPositionPersistAt = now;
                FileLogger.Log($"[{ProtocolName}] Persisted position ({source}) character_id={session.Player.CharacterId} town={session.Player.CurTownId} area={session.Player.CurAreaId} pos=({session.Player.CurPosX},{session.Player.CurPosY})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Persist position ({source}) failed: {ex.Message}");
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SET_USER_POSITION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 7) return;
            var movementSequence = session.Player.NextTownMovementSequence();
            var gotoPosX = BitConverter.ToInt16(body, 0);
            var gotoPosY = BitConverter.ToInt16(body, 2);
            var direction = body[4];
            var motionState = BitConverter.ToUInt16(body, 5);
            session.Player.CurPosX = gotoPosX;
            session.Player.CurPosY = gotoPosY;
            session.Player.CurDirection = direction;
            PersistPosition(session, forceImmediate: false, source: "set_user_position");

            var snap = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);
            var positionPacket = GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0016,
                TownAreaNotificationBuilder.BuildUserPosition(snap, motionState));

            // A21 回包会回到发起 SET_USER_POSITION 的客户端本身；单机时
            // _sessions 没有其它目标，不能只做“给别人广播”。
            await session.SendPacketAsync(positionPacket);

            // A21 fresh-login flow reports SET_USER_POSITION without first
            // reporting SET_USER_AREA. Treat the first position of each
            // hydrated character as the one-time town-arrival projection so
            // both clients receive appearance + area + a self-first roster.
            // Later movement remains the lightweight USER_POSITION broadcast.
            if (ShouldProjectInitialAreaRoster(
                    session.Player,
                    movementSequence))
            {
                await BroadcastAreaRosterAsync(session, snap);
            }
            else if (_sessions != null && session.Player.CharacterId > 0)
            {
                await _sessions.BroadcastToAreaAsync(
                    session.Player.CurTownId, session.Player.CurAreaId, session.Player.CharacterId,
                    positionPacket,
                    session.ListenerPort);
            }
        }

        public async Task Handle_ENUM_CMDPACKET_GET_PCROOM_TIME_POINT_ITEM(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            // A21 capture: request body is 15B and the server returns the same
            // CMD opcode with a fixed 6B zero body during town-return recovery.
            if (body == null || body.Length < 15)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GET_PCROOM_TIME_POINT_ITEM rejected " +
                    $"bodyLength={body?.Length ?? 0} (expected >=15B)");
                return;
            }

            await session.SendPacketAsync(
                BuildGetPcRoomTimePointItemResponsePacket());
        }

        internal static byte[] BuildGetPcRoomTimePointItemResponsePacket() =>
            GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.GET_PCROOM_TIME_POINT_ITEM,
                new byte[6]);

        public Task Handle_ENUM_CMDPACKET_SET_USER_AREA(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => SetUserAreaCoreAsync(
                session,
                body,
                default(TownProjectionGuard));

        private async Task SetUserAreaCoreAsync(
            EnhancedClientSession session,
            byte[] body,
            TownProjectionGuard projectionGuard)
        {
            if (body == null || body.Length < 6) return;
            if (!CanContinueTownProjection(session, projectionGuard))
                return;
            var gotoTownId = body[0];
            var gotoAreaId = body[1];
            var gotoPosX = BitConverter.ToInt16(body, 2);
            var gotoPosY = BitConverter.ToInt16(body, 4);

            if (!CanChangeRaidArea(session, gotoTownId, gotoAreaId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SET_USER_AREA rejected for non-raid member: " +
                    $"cid={session.Player.CharacterId} " +
                    $"current={session.Player.CurTownId}:{session.Player.CurAreaId} " +
                    $"target={gotoTownId}:{gotoAreaId}");
                await ChannelTownRestrictionSender.SendCurrentAreaAsync(session);
                return;
            }

            if (!GameChannelSpawnPolicy.CanEnterTown(
                    session.ListenerPort,
                    gotoTownId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] SET_USER_AREA rejected by channel policy: " +
                    $"cid={session.Player.CharacterId} listener={session.ListenerPort} " +
                    $"current={session.Player.CurTownId}:{session.Player.CurAreaId} " +
                    $"target={gotoTownId}:{gotoAreaId}");
                await ChannelTownRestrictionSender.SendAsync(session);
                return;
            }

            var previousTownId = session.Player.CurTownId;
            var previousAreaId = session.Player.CurAreaId;

            session.Player.CurTownId = gotoTownId;
            session.Player.CurAreaId = gotoAreaId;
            session.Player.CurPosX = gotoPosX;
            session.Player.CurPosY = gotoPosY;
            session.Player.CurDirection = 0x05;
            // A21 USER_AREA/AREA_USERS samples use state=0 for town arrival.
            // The client-provided body has additional fields, but its legacy
            // state byte is not authoritative for the server projection.
            session.Player.CurAreaState = 0x00;

            var selfSnapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            // A21 USER_AREA(0x0017) has a distinct remote-user branch when
            // the packet town/area differs from the recipient's current area:
            // it removes that user's scene projection without touching party
            // slots. 0x0006 cannot be used here because its consumer also
            // clears the matching party member slot; 0x0018 only updates
            // objects already named in its roster and does not remove ghosts.
            if (_sessions != null
                && session.Player.CharacterId > 0
                && (previousTownId != gotoTownId
                    || previousAreaId != gotoAreaId))
            {
                await _sessions.BroadcastToAreaAsync(
                    previousTownId,
                    previousAreaId,
                    session.Player.CharacterId,
                    BuildAreaTransitionDeparturePacket(selfSnapshot),
                    session.ListenerPort);
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;

                FileLogger.Log(
                    $"[{ProtocolName}] AREA departure projection: " +
                    $"uid={session.Player.UserId} " +
                    $"from={previousTownId}:{previousAreaId} " +
                    $"to={gotoTownId}:{gotoAreaId} " +
                    $"listener={session.ListenerPort}");
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017, TownAreaNotificationBuilder.BuildUserArea(selfSnapshot)));
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            // 联机同屏: 名册含同区域其它玩家, 并让已在场玩家看到新来的自己。
            await BroadcastAreaRosterAsync(
                session,
                selfSnapshot,
                projectionGuard);
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            // 离开旧区域: 向旧区域广播不含离开者的权威名册, 让残留白影按名册消失。
            // 逻辑在共享 TownAreaRosterDepartureNotifier(参照 86JP 已知协议验证, 不用 USER_LEAVE,
            // 见设计文档 §4.5), 与进本路径复用同一机制, 不复制。
            if (previousTownId != gotoTownId
                || previousAreaId != gotoAreaId)
                await TownAreaRosterDepartureNotifier.NotifyOldAreaDepartureAsync(
                    _sessions,
                    session,
                    previousTownId,
                    previousAreaId);
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            PersistPosition(session, forceImmediate: true, source: "set_user_area");
        }

        // 城镇残留白影修复已抽到共享 TownAreaRosterDepartureNotifier,
        // 与进本路径(DungeonEntryHandler)复用同一机制, 见该类注释。

        // 同屏"插入他人"包的构造。脱壳客户端逆向确认(2026-07-06夜):右键组队邀请要求
        // 目标客户端对象 vtable[+40] 返回的 type==4(sub_118C100=sub_118C080==4),否则报字符串311
        // "对方不在城镇内"、连 REQUEST_PEER(0x000A) 都不发。df insert_user: 城镇分支(area[+0x68]==1)
        // 发 0x0018、野外/副本分支发 0x0017 —— 0x17/0x18 编码"对象在野外 vs 城镇"。当前用 0x0017(野外)
        // 插同屏他人 → 疑客户端建成野外对象(type≠4)→ 不可邀请。
        // 真机抓包确认：0x0018 是接收端的完整区域名册，首项必须是接收者自己。
        // 把单个“他人”作为 count=1 名册会替换本地角色，导致自己消失且无法移动。
        private bool CanChangeRaidArea(
            EnhancedClientSession session,
            byte targetTownId,
            byte targetAreaId)
        {
            if (!GameNetworkConfig.IsRaidListener(session.ListenerPort)
                || session.Player.CurTownId != GameChannelSpawnPolicy.RaidTownId
                || targetTownId != GameChannelSpawnPolicy.RaidTownId
                || targetAreaId == session.Player.CurAreaId)
            {
                return true;
            }

            if (session.Player.CurAreaId == 1 && targetAreaId == 2)
            {
                return _raidManager != null
                    && _raidManager.TryGetByUser(session.Player.UserId, out _);
            }

            return true;
        }

        private static byte[] BuildCoPresenceInsert(TownUserSnapshot snap) =>
            GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snap));

        internal static byte[] BuildAreaTransitionDeparturePacket(
            TownUserSnapshot snapshot)
            => BuildCoPresenceInsert(snapshot);

        /// <summary>
        /// 城镇同屏核心: 收集同区域全部会话, 给每个人下发含全体的 AREA_USERS(0x0018)。
        /// _sessions 为空(单人/未注入)时退化为只发自己 —— 与既有单机行为等价。
        /// </summary>
        private async Task BroadcastAreaRosterAsync(
            EnhancedClientSession session,
            TownUserSnapshot selfSnapshot,
            TownProjectionGuard projectionGuard = default(TownProjectionGuard))
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return;
            var townId = session.Player.CurTownId;
            var areaId = session.Player.CurAreaId;

            IReadOnlyList<EnhancedClientSession> others = _sessions?.GetSessionsInArea(
                    townId,
                    areaId,
                    session.Player.CharacterId,
                    session.ListenerPort)
                ?? System.Array.Empty<EnhancedClientSession>();

            FileLogger.Log(
                $"[{ProtocolName}] AREA co-presence: uid={session.Player.UserId} " +
                $"town={townId} area={areaId} listener={session.ListenerPort} " +
                $"others={others.Count}");

            // 全体名册(自己 + 其它人)。
            var roster = new List<TownUserSnapshot>(others.Count + 1) { selfSnapshot };
            foreach (var o in others)
                roster.Add(TownAreaNotificationBuilder.CreateCurrentSnapshot(o.Player));

            // 真机实测(逆向+抓包结论): 只发 0x17/0x18 客户端既不生成他人角色对象、也不主动拉外观。
            // self 能渲染是因为进游戏时收了【完整外观】(USERINFO 0x0002 含形象)。故照"自身入场先有外观后有位置"
            // 主动 PUSH: 给新人为【每个已在场玩家】先推一份 USERINFO(0x0002 外观)、再发 0x0017(定位/生成), 最后补 0x0018 名册。
            // 给新人: 每个已在场玩家只推 subtype0(精简外观)+城镇定位。
            // subtype1 是本地选角/PVP 初始化数据，推给城镇中的“他人”会让 A21
            // 客户端把后到角色替换成本地角色，表现为自己消失且无法移动。
            foreach (var o in others)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002,
                    Game.Appearance.AppearanceService.BuildNoti2Body(
                        o.Player,
                        _database)));
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;
                var oSnap = TownAreaNotificationBuilder.CreateCurrentSnapshot(o.Player);
                await session.SendPacketAsync(BuildCoPresenceInsert(oSnap));
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(townId, areaId, roster)));
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            // 给每个已在场玩家推【新人】的 subtype0 + 城镇定位。
            var selfAppearance = GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0002,
                Game.Appearance.AppearanceService.BuildNoti2Body(
                    session.Player,
                    _database));
            var selfArea = BuildCoPresenceInsert(selfSnapshot);
            var peerProjections = new List<Task>(others.Count);
            foreach (var o in others)
            {
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;

                // AREA_USERS 的首项必须是当前接收者自己。
                var recipientRoster = new List<TownUserSnapshot>(others.Count + 1)
                {
                    TownAreaNotificationBuilder.CreateCurrentSnapshot(o.Player),
                };
                foreach (var peer in others)
                {
                    if (peer.Player.CharacterId != o.Player.CharacterId)
                    {
                        recipientRoster.Add(
                            TownAreaNotificationBuilder.CreateCurrentSnapshot(
                                peer.Player));
                    }
                }
                recipientRoster.Add(selfSnapshot);
                var recipientRosterPacket = GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0018,
                    TownAreaNotificationBuilder.BuildAreaUsers(
                        townId,
                        areaId,
                        recipientRoster));
                var recipient = o;
                peerProjections.Add(SessionDirectory.TrySendBestEffortAsync(
                    async cancellationToken =>
                    {
                        if (!CanContinueTownProjection(
                                session,
                                projectionGuard))
                        {
                            return;
                        }
                        await recipient.SendPacketAsync(
                            selfAppearance,
                            cancellationToken);
                        if (!CanContinueTownProjection(
                                session,
                                projectionGuard))
                        {
                            return;
                        }
                        await recipient.SendPacketAsync(
                            selfArea,
                            cancellationToken);
                        if (!CanContinueTownProjection(
                                session,
                                projectionGuard))
                        {
                            return;
                        }
                        await recipient.SendPacketAsync(
                            recipientRosterPacket,
                            cancellationToken);
                    },
                    $"town-presence characterId=" +
                    $"{recipient.Player?.CharacterId ?? 0}"));
            }
            if (peerProjections.Count > 0)
                await Task.WhenAll(peerProjections);
            if (_publishTownPartyLists != null)
                await _publishTownPartyLists();
        }

        /// <summary>
        /// 联机同屏: 会话真正离开时通知同区域其它玩家移除该分身。
        /// A21 客户端会把 USER_LEAVE(0x0006) 同时应用到队伍槽，普通切图不得发送。
        /// </summary>
        public async Task NotifyLeaveAsync(EnhancedClientSession session)
        {
            if (_sessions == null || session?.Player == null || session.Player.CharacterId <= 0)
                return;
            await _sessions.BroadcastToAreaAsync(
                session.Player.CurTownId,
                session.Player.CurAreaId,
                session.Player.CharacterId,
                BuildUserLeavePacket(session.Player.UserId),
                session.ListenerPort);
        }

        public async Task Handle_ENUM_CMDPACKET_FINISH_LOADING(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            // A21 dungeon loading has no CMD 37 response; the client consumes
            // NOTI 30 as the completion notification. Keep the town response
            // for callers that are not attached to a live DungeonRun.
            if (session?.Player?.CurrentRun == null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0025, CommonPacketBodyBuilder.BuildSuccessAck()));
            }
            await SendFinishLoadingCompletionAsync(session);
        }

        internal async Task SendFinishLoadingCompletionAsync(
            EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, FinishLoadingBuilder.BuildNotification()));
            await _growthCapsule.SendExpProgressAsync(session, "finish-loading");
        }

        public async Task Handle_ENUM_CMDPACKET_TELEPORT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!ItemTeleportRequest.TryParse(body, out var request))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected invalid body: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"length={body?.Length ?? 0} " +
                    $"raw={(body == null ? "null" : BitConverter.ToString(body))}");
                return;
            }

            var (cid, _) = InventoryHandler.ResolveOwner(session);
            if (!InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    cid,
                    out var lease))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected missing owned inventory: " +
                    $"cid={cid} item=0x{request.ItemTemplateId:X8}");
                return;
            }

            lock (lease.SyncRoot)
            {
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        session.SessionId,
                        cid)
                    || lease.Inventory.CountMainItem(
                        request.ItemTemplateId) < 1)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] TELEPORT rejected item not owned: " +
                        $"cid={cid} item=0x{request.ItemTemplateId:X8}");
                    return;
                }
            }

            if (!TeleportConsumableDefinitionProvider.TryResolve(
                    request.ItemTemplateId,
                    out var definition)
                || !definition.IsValid
                || definition.Kind
                    != TeleportConsumableKind.TownSelection)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected invalid item definition: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"item=0x{request.ItemTemplateId:X8}");
                return;
            }

            if (request.TargetTownId > byte.MaxValue)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected invalid town id: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"targetTown={request.TargetTownId} " +
                    $"item=0x{request.ItemTemplateId:X8}");
                return;
            }

            var targetTownId = (int)request.TargetTownId;
            if (!GameChannelSpawnPolicy.CanEnterTown(
                    session.ListenerPort,
                    targetTownId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected by channel policy: " +
                    $"cid={session.Player.CharacterId} listener={session.ListenerPort} " +
                    $"current={session.Player.CurTownId}:{session.Player.CurAreaId} " +
                    $"targetTown={request.TargetTownId} " +
                    $"item=0x{request.ItemTemplateId:X8}");
                await ChannelTownRestrictionSender.SendAsync(session);
                return;
            }

            CeraRoomInfo ceraRoomInfo;
            try
            {
                ceraRoomInfo = Town.GetCeraRoomInfo(targetTownId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected invalid target: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"targetTown={request.TargetTownId} error={ex.Message}");
                return;
            }
            if (ceraRoomInfo.Town != request.TargetTownId)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected target without gate: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"targetTown={request.TargetTownId}");
                return;
            }

            var persistPosition = GameChannelSpawnPolicy.ShouldPersistPosition(
                session.ListenerPort);
            if (!TeleportConsumableCommitService.TryCommit(
                    lease,
                    request.ItemTemplateId,
                    ceraRoomInfo.Town,
                    ceraRoomInfo.Area,
                    ceraRoomInfo.X,
                    ceraRoomInfo.Y,
                    direction: 0,
                    areaState: 3,
                    persistPosition,
                    out var consumeResult))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT commit failed: " +
                    $"cid={cid} item=0x{request.ItemTemplateId:X8} " +
                    $"target={ceraRoomInfo.Town}:{ceraRoomInfo.Area}");
                return;
            }

            session.Player.CurTownId = ceraRoomInfo.Town;
            session.Player.CurAreaId = ceraRoomInfo.Area;
            session.Player.CurPosX = ceraRoomInfo.X;
            session.Player.CurPosY = ceraRoomInfo.Y;
            session.Player.CurDirection = 0;
            session.Player.CurAreaState = 3;
            if (persistPosition)
                session.Player.LastPositionPersistAt = DateTime.UtcNow;

            FileLogger.Log(
                $"[{ProtocolName}] TELEPORT: consumed item=" +
                $"0x{request.ItemTemplateId:X8} slot={consumeResult.SlotIndex} " +
                $"remaining={consumeResult.RemainingCount}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.LOAD_COOLTIME_ITEM_INFO,
                TeleportPacketBuilder.BuildTeleportNotification(
                    request.ItemTemplateId)));
            if (_refresh != null)
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    consumeResult.SlotIndex);
            var selfSnapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(
                session.Player);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0017,
                TownAreaNotificationBuilder.BuildUserArea(selfSnapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(selfSnapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x00CA,
                new byte[] { 0x00 }));

        }

        public async Task Handle_ENUM_CMDPACKET_PARTY_TELEPORT(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!PartyTeleportRequest.TryParse(body, out var request))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT rejected invalid body: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"length={body?.Length ?? 0}");
                return;
            }

            if (!GameChannelTeleportPolicy.CanUsePartyTeleport(
                    session.ListenerPort)
                || !GameChannelSpawnPolicy.CanEnterTown(
                    session.ListenerPort,
                    request.TownId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT rejected by channel policy: " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"listener={session?.ListenerPort ?? 0} " +
                    $"target={request.TownId}:{request.AreaId}");
                await ChannelTownRestrictionSender.SendAsync(session);
                return;
            }

            if (session?.Player == null
                || session.Player.CurrentRun != null
                || _partyManager == null
                || _sessions == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT rejected unavailable state: " +
                    $"cid={session?.Player?.CharacterId ?? 0}");
                return;
            }

            var party = _partyManager.GetPartyByUser(
                session.Player.UserId);
            var snapshot = party == null
                ? null
                : _partyManager.GetPartySnapshot(party.PartyId);
            if (snapshot == null
                || !snapshot.IsLeader(session.Player.UserId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] PARTY_TELEPORT rejected non-leader: " +
                    $"cid={session.Player.CharacterId} " +
                    $"uid={session.Player.UserId}");
                return;
            }

            var areaBody = new byte[6];
            Buffer.BlockCopy(body, 0, areaBody, 0, areaBody.Length);
            var moved = 0;
            foreach (var member in snapshot.MembersBySlot())
            {
                EnhancedClientSession memberSession;
                if (member.UserId == session.Player.UserId)
                {
                    memberSession = session;
                }
                else if (!_sessions.TryGet(
                             member.CharacterId,
                             out memberSession))
                {
                    continue;
                }

                if (memberSession?.Player == null
                    || memberSession.SessionId != member.SessionId
                    || memberSession.ListenerPort != session.ListenerPort
                    || memberSession.Player.CurrentRun != null
                    || !GameChannelTeleportPolicy.CanUsePartyTeleport(
                        memberSession.ListenerPort)
                    || !GameChannelSpawnPolicy.CanEnterTown(
                        memberSession.ListenerPort,
                        request.TownId))
                {
                    continue;
                }

                await SetUserAreaCoreAsync(
                    memberSession,
                    areaBody,
                    default(TownProjectionGuard));
                moved++;
            }

            FileLogger.Log(
                $"[{ProtocolName}] PARTY_TELEPORT: " +
                $"leaderCid={session.Player.CharacterId} " +
                $"party={snapshot.PartyId} " +
                $"target={request.TownId}:{request.AreaId} " +
                $"pos=({request.X},{request.Y}) direction={request.Direction} " +
                $"moved={moved}/{snapshot.Count}");
        }

        public async Task Handle_ENUM_CMDPACKET_GIVEUP_GAME(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var sourceRun = session?.Player?.CurrentRun;
            if (sourceRun == null)
            {
                var selection = session?.Player?.CurrentDungeonSelection;
                var transitionGate = selection?.PartyCohort?.TransitionGate;
                var rerouteToRun = false;
                if (transitionGate != null)
                    await transitionGate.WaitAsync();
                try
                {
                    // sourceRun was sampled before taking the cohort gate. If
                    // SELECT won while this command waited, restart against
                    // the now-current run instead of returning a stale
                    // selection context.
                    if (session?.Player?.CurrentRun != null)
                    {
                        rerouteToRun = true;
                    }
                    else if (selection == null || !selection.TryBeginReturn())
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] RETURN_TO_TOWN rejected without run: " +
                            $"type=0x{header.type:X4} cid={session?.Player?.CharacterId ?? 0} " +
                            $"selection={(selection?.SelectionId ?? 0)}");
                        return;
                    }
                    else
                    {
                        var selectionGuard = TownProjectionGuard.ForSelection(selection);
                        try
                        {
                            if (!await ReturnSelectionToTownAsync(
                                    session,
                                    selection,
                                    selectionGuard,
                                    header.type,
                                    sendCommandResponse: true))
                            {
                                selection.CancelReturn();
                                return;
                            }
                            if (ShouldFanOutPartySelectionReturn(header.type))
                            {
                                await TryFanOutPartySelectionReturnAsync(
                                    session,
                                    selection,
                                    header.type);
                            }
                            if (!CanContinueTownProjection(session, selectionGuard))
                                return;
                            await SendTownAccountStateAsync(
                                session,
                                "leave-dungeon-selection",
                                selectionGuard);
                            if (!CanContinueTownProjection(session, selectionGuard))
                                return;
                            if (CanContinueTownProjection(session, selectionGuard))
                                session.Player.CompleteDungeonSelection(selection);
                            FileLogger.Log(
                                $"[{ProtocolName}] RETURN_TO_TOWN from selection: " +
                                $"type=0x{header.type:X4} cid={session.Player.CharacterId} " +
                                $"selection={selection.SelectionId}");
                        }
                        catch
                        {
                            if (session?.Player?.IsCurrentDungeonSelection(selection) == true)
                                selection.CancelReturn();
                            throw;
                        }
                    }
                }
                finally
                {
                    transitionGate?.Release();
                }
                if (rerouteToRun)
                {
                    await Handle_ENUM_CMDPACKET_GIVEUP_GAME(session, header, body);
                    return;
                }
                return;
            }

            var runTransitionGate =
                sourceRun.EntryPartySelectionCohort?.TransitionGate;
            var rerouteAfterRunGate = false;
            if (runTransitionGate != null)
                await runTransitionGate.WaitAsync();
            try
            {
                var currentRun = session?.Player?.CurrentRun;
                if (currentRun == null
                    || !currentRun.Matches(sourceRun.CaptureIdentity()))
                {
                    rerouteAfterRunGate = true;
                }
                else
                {
                    await HandleGiveupFromRunAsync(
                        session,
                        header,
                        currentRun);
                }
            }
            finally
            {
                runTransitionGate?.Release();
            }
            if (rerouteAfterRunGate)
                await Handle_ENUM_CMDPACKET_GIVEUP_GAME(session, header, body);
        }

        private async Task HandleGiveupFromRunAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            DungeonRun sourceRun)
        {
            var sourceRunIdentity = sourceRun.CaptureIdentity();
            var deferTutorialVillageObjectList = sourceRun.IsA21TutorialEntry;
            var expectedUserId = session?.Player?.UserId ?? (ushort)0;
            var expectedSessionId = session?.SessionId ?? Guid.Empty;
            var expectedPartyId = 0;
            if (header.type == 0x002A && expectedUserId != 0)
            {
                var liveParty = _partyManager?.GetPartyByUser(
                    expectedUserId);
                var currentParty = liveParty == null
                    ? null
                    : _partyManager.GetPartySnapshot(liveParty.PartyId);
                var currentMember = currentParty?.GetMember(
                    expectedUserId);
                if (currentMember?.SessionId == expectedSessionId)
                    expectedPartyId = currentParty.PartyId;
            }
            Func<Task> afterRunEnded = null;
            if (header.type == 0x002A &&
                _dungeonGiveupPartyDeparture != null)
            {
                afterRunEnded = () => _dungeonGiveupPartyDeparture(
                    session,
                    expectedUserId,
                    expectedSessionId,
                    expectedPartyId);
            }
            if (!await ReturnSelfToTownAsync(
                    session,
                    header,
                    sourceRunIdentity,
                    sourceRun.TownReturnAnchor,
                    sendCommandResponse: true,
                    afterRunEnded: afterRunEnded))
            {
                return;
            }

            if (deferTutorialVillageObjectList)
            {
                session.A21TutorialReturnNeedsVillageObjectList = true;
                FileLogger.Log(
                    $"[{ProtocolName}] A21 tutorial return defers " +
                    $"VILLAGE_OBJECT_LIST until town STORY_PAUSE cid={session.Player.CharacterId}");
            }
            else
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x00CA,
                    new byte[] { 0x00 }));
            }

            // 跟随退出只在通关回城 BACK_2_VILLAGE 0x84 触发。
            // GIVEUP_GAME 0x2A 只让本人回城，不拉仍在副本的队员；
            // 回城成功后在当前 cohort gate 内提交本人离队/末人保队。
            if (header.type == 0x0084)
                await TryFanOutLeaderReturnToTownAsync(
                    session,
                    header,
                    sourceRunIdentity);
            else
                FileLogger.Log(
                    $"[{ProtocolName}] GIVEUP_GAME(type=0x{header.type:X2}): " +
                    $"cid={session.Player?.CharacterId} 独自回城并等待离队提交, " +
                    $"不拉仍在副本的队员");

            // A21 CMD 成功响应已在 USER_STATE 之前发送；回城尾部不再追加
            // 第二个 ACK 或 subtype0。客户端随后继续发送教程
            // SYNC_ITEM_SPACE、STORY_PAUSE、GET_PCROOM_TIME_POINT_ITEM
            // 和 SET_USER_POSITION。
        }

        // 把【单个会话】自己拉回城镇(EndRun + 城镇区域同步)。队长/队员复用同一序列。
        private async Task<bool> ReturnSelfToTownAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            DfoServer.Game.Dungeon.DungeonRunIdentity runIdentity,
            DungeonTownReturnAnchor returnAnchor,
            bool sendCommandResponse,
            Func<Task> afterRunEnded = null)
        {
            if (!await Dungeon.DungeonRunLifecycle.EndRunAsync(
                    session,
                    DfoServer.Game.Dungeon.DungeonRunEndReason.ReturnToTown,
                    runIdentity,
                    _dungeonInstances))
            {
                return false;
            }
            try
            {
                var projectionGuard = TownProjectionGuard.ForEndedRun(runIdentity);
                if (!CanContinueTownProjection(session, projectionGuard))
                {
                    return false;
                }
                Dungeon.DungeonRunLifecycle.ApplyTownReturnAnchor(
                    session.Player,
                    returnAnchor,
                    session.ListenerPort);
                session.Player.UserState = 0x00;

                // A21 客户端抓包中，GIVEUP_GAME/BACK_2_VILLAGE 的 CMD 成功响应
                // body=[01] 位于 USER_STATE 之前。客户端先用 CMD 响应结束副本
                // 请求状态，再开始消费城镇 USER_STATE/USER_AREA/AREA_USERS。
                if (sendCommandResponse)
                {
                    await session.SendPacketAsync(
                        BuildReturnToTownSuccessPacket(header.type));
                    if (!CanContinueTownProjection(session, projectionGuard))
                    {
                        return false;
                    }
                }

                // A21 回城顺序的第一个城镇投影包是 USER_STATE(0x0003)。
                // 该包不能只更新 PlayerContext 后省略；客户端会以它确认
                // 角色已离开副本，再消费 USER_AREA/AREA_USERS 的城镇坐标。
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.USER_STATE,
                    EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
                if (!CanContinueTownProjection(session, projectionGuard))
                {
                    return false;
                }

                await SetUserAreaCoreAsync(
                    session,
                    BuildTownAreaProjectionBody(session.Player),
                    projectionGuard);
                if (!CanContinueTownProjection(session, projectionGuard))
                {
                    return false;
                }
                if (ShouldRefreshPartyProjectionAfterTownReturn(header.type))
                {
                    await RefreshPartyProjectionAfterTownReturnAsync(
                        session,
                        projectionGuard);
                }
                // 回城过图后客户端重置结婚属性 UI：城镇 USER_STATE/USER_AREA
                // 投影之后补发婚礼回放三包。只覆盖进/出本触发点，
                // 不挂城镇内每次过图。
                await InventoryRefreshSender.SendWeddingReplayRefresh(session);
                return CanContinueTownProjection(
                    session,
                    projectionGuard);
            }
            finally
            {
                if (afterRunEnded != null)
                {
                    try
                    {
                        await afterRunEnded();
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] post-EndRun party policy failed: " +
                            $"cid={session?.Player?.CharacterId ?? 0} " +
                            $"error={ex.Message}");
                    }
                }
            }
        }

        internal static bool ShouldRefreshPartyProjectionAfterTownReturn(
            ushort responsePacketType)
        {
            // GIVEUP_GAME is an explicit abandoned-run exit. The party
            // handler commits the departure immediately after town return,
            // so restoring the old roster here would briefly re-form the
            // returning player with members who are still in the dungeon.
            return responsePacketType != 0x002A;
        }

        private async Task<bool> ReturnSelectionToTownAsync(
            EnhancedClientSession session,
            DungeonSelectionContext selection,
            TownProjectionGuard projectionGuard,
            ushort responsePacketType,
            bool sendCommandResponse)
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return false;

            Dungeon.DungeonRunLifecycle.ApplyTownReturnAnchor(
                session.Player,
                selection.ReturnAnchor,
                session.ListenerPort);
            session.Player.UserState = 0x00;
            // 回城 → 状态回空闲：同频道在线好友推 USERINFO(0x0002) 更新场景实体状态。
            // （与 DungeonTownReturnCoordinator.ReturnAsync 一致，补齐 GIVEUP_GAME/BACK_2_VILLAGE 路径。）
            if (_sessions != null)
                await UnitedFriendSystem.NotifyUserStateChanged(
                    session, _sessions);

            if (sendCommandResponse)
            {
                await session.SendPacketAsync(
                    BuildReturnToTownSuccessPacket(responsePacketType));
                if (!CanContinueTownProjection(session, projectionGuard))
                {
                    return false;
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.USER_STATE,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            if (!CanContinueTownProjection(session, projectionGuard))
            {
                return false;
            }

            await SetUserAreaCoreAsync(
                session,
                BuildTownAreaProjectionBody(session.Player),
                projectionGuard);
            if (!CanContinueTownProjection(session, projectionGuard))
            {
                return false;
            }
            await RefreshPartyProjectionAfterTownReturnAsync(
                session,
                projectionGuard);
            return CanContinueTownProjection(session, projectionGuard);
        }

        internal async Task ReturnRejectedPartySelectionToTownAsync(
            EnhancedClientSession leader,
            DungeonSelectionContext leaderSelection)
        {
            if (leader?.Player == null
                || leaderSelection == null
                || !leader.Player.IsCurrentDungeonSelection(leaderSelection))
            {
                return;
            }

            await TryFanOutPartySelectionReturnAsync(
                leader,
                leaderSelection,
                responsePacketType: 0);
            if (!leader.Player.IsCurrentDungeonSelection(leaderSelection)
                || !leaderSelection.TryBeginReturn())
            {
                return;
            }

            var guard = TownProjectionGuard.ForSelection(leaderSelection);
            if (!await ReturnSelectionToTownAsync(
                    leader,
                    leaderSelection,
                    guard,
                    responsePacketType: 0,
                    sendCommandResponse: false))
            {
                leaderSelection.CancelReturn();
                return;
            }
            await SendTownAccountStateAsync(
                leader,
                "party-entry-rejected",
                guard);
            if (CanContinueTownProjection(leader, guard))
                leader.Player.CompleteDungeonSelection(leaderSelection);
        }

        internal static bool ShouldFanOutPartySelectionReturn(
            ushort responsePacketType)
            => responsePacketType == 0x0084;

        private async Task TryFanOutPartySelectionReturnAsync(
            EnhancedClientSession leader,
            DungeonSelectionContext leaderSelection,
            ushort responsePacketType)
        {
            if (Environment.GetEnvironmentVariable(
                    "DFO_PARTY_DUNGEON_COOP") == "0"
                || leader?.Player == null
                || leaderSelection == null
                || _sessions == null)
            {
                return;
            }

            var cohort = leaderSelection.PartyCohort;
            if (cohort == null
                || cohort.LeaderUserId != leader.Player.UserId
                || !leader.Player.IsCurrentDungeonSelection(leaderSelection))
            {
                return;
            }

            var returned = 0;
            foreach (var participant in cohort.Participants)
            {
                if (participant.UserId == cohort.LeaderUserId)
                    continue;
                if (!_sessions.TryGet(
                        participant.CharacterId, out var follower)
                    || follower?.Player == null
                    || follower.SessionId != participant.SessionId
                    || follower.Player.UserId != participant.UserId
                    || follower.ListenerPort != leader.ListenerPort
                    || follower.TcpClient == null
                    || !follower.TcpClient.Connected
                    || follower.Player.CurrentRun != null)
                {
                    continue;
                }

                var followerSelection =
                    follower.Player.CurrentDungeonSelection;
                if (!follower.Player.IsCurrentDungeonSelection(
                        followerSelection)
                    || !ReferenceEquals(
                        followerSelection.PartyCohort,
                        cohort)
                    || !followerSelection.TryBeginReturn())
                {
                    continue;
                }

                var followerGuard =
                    TownProjectionGuard.ForSelection(followerSelection);
                try
                {
                    if (!await ReturnSelectionToTownAsync(
                            follower,
                            followerSelection,
                            followerGuard,
                            responsePacketType,
                            sendCommandResponse: false))
                    {
                        followerSelection.CancelReturn();
                        continue;
                    }
                    await SendTownAccountStateAsync(
                        follower,
                        "party-leave-dungeon-selection",
                        followerGuard);
                    if (!CanContinueTownProjection(
                            follower,
                            followerGuard))
                    {
                        continue;
                    }
                    follower.Player.CompleteDungeonSelection(
                        followerSelection);
                    returned++;
                }
                catch (Exception ex)
                {
                    if (follower.Player.IsCurrentDungeonSelection(
                            followerSelection))
                    {
                        followerSelection.CancelReturn();
                    }
                    FileLogger.Log(
                        $"[{ProtocolName}] PARTY_SELECTION_RETURN failed: " +
                        $"uid={participant.UserId} " +
                        $"party={cohort.PartyId} " +
                        $"projection={cohort.ProjectionId} " +
                        $"error={ex.Message}");
                }
            }

            FileLogger.Log(
                $"[{ProtocolName}] PARTY_SELECTION_RETURN: " +
                $"leader={leader.Player.CharacterId} " +
                $"party={cohort.PartyId} projection={cohort.ProjectionId} " +
                $"returned={returned}/{cohort.Participants.Count - 1}");
        }

        private async Task RefreshPartyProjectionAfterTownReturnAsync(
            EnhancedClientSession session,
            TownProjectionGuard projectionGuard)
        {
            var player = session?.Player;
            var party = player == null
                ? null
                : _partyManager?.GetPartyByUser(player.UserId);
            var snapshot = party == null
                ? null
                : _partyManager.GetPartySnapshot(party.PartyId);
            var packets = BuildTownReturnPartyProjectionPackets(snapshot);
            foreach (var packet in packets)
            {
                if (!CanContinueTownProjection(session, projectionGuard))
                    return;
                await session.SendPacketAsync(packet);
            }

            if (packets.Length > 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] RETURN_TO_TOWN party projection: " +
                    $"cid={player.CharacterId} party={snapshot.PartyId} " +
                    $"members={snapshot.Count}");
            }
        }

        internal async Task ProjectDungeonTownPresenceAsync(
            EnhancedClientSession session,
            DfoServer.Game.Dungeon.DungeonRunIdentity runIdentity)
        {
            var projectionGuard = TownProjectionGuard.ForEndedRun(runIdentity);
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            await SetUserAreaCoreAsync(
                session,
                BuildTownAreaProjectionBody(session.Player),
                projectionGuard);
        }

        internal static bool ShouldProjectInitialAreaRoster(
            PlayerContext player,
            long movementSequence)
            => movementSequence == 1
               && IsTownArrivalStateEligible(player)
               && !player.DungeonSelectionPending;

        internal static byte[][] BuildTownReturnPartyProjectionPackets(
            Game.Party.Party party)
        {
            if (party == null || party.Count <= 1)
                return Array.Empty<byte[]>();

            return new[]
            {
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.PARTY_INFO,
                    PartyInfoNotiBuilder.Build(party, 0)),
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0099,
                    PartyRealtimeInfoBuilder.Build(party)),
            };
        }

        private static byte[] BuildTownAreaProjectionBody(PlayerContext player)
        {
            var list = new List<byte>();
            list.Add(player.CurTownId);
            list.Add(player.CurAreaId);
            list.AddRange(BitConverter.GetBytes(player.CurPosX));
            list.AddRange(BitConverter.GetBytes(player.CurPosY));
            list.Add(player.CurDirection);
            list.Add(player.CurTownId);
            list.Add(player.CurAreaState);
            list.Add(player.CurAreaId);
            return list.ToArray();
        }

        internal static byte[] BuildReturnToTownSuccessPacket(ushort packetType) =>
            GamePacketEnvelopeBuilder.Build(
                0x01,
                packetType,
                CommonPacketBodyBuilder.BuildSuccessAck());

        // ★组队副本收尾 fan-out(⚠️协议/渲染, 待真机)。仅当【队长】+开 DFO_PARTY_DUNGEON_COOP + 队伍>1:
        //   把每个仍在副本内(CurrentRun!=null)的在线队员也拉回其城镇 → 客户端呈现"跟着队长退出"。
        //   非队长放弃(item16 个人退出)不 fan-out, 只回自己, 其余人继续留本。
        private async Task TryFanOutLeaderReturnToTownAsync(
            EnhancedClientSession leader,
            GamePacketHeader header,
            DfoServer.Game.Dungeon.DungeonRunIdentity leaderRunIdentity)
        {
            var leaderGuard = TownProjectionGuard.ForEndedRun(leaderRunIdentity);
            if (!CanContinueTownProjection(leader, leaderGuard)) return;
            if (Environment.GetEnvironmentVariable("DFO_PARTY_DUNGEON_COOP") == "0") return;
            if (_partyManager == null || _sessions == null || leader?.Player == null) return;

            var leaderUid = (ushort)leader.Player.CharacterId;
            var party = _partyManager.GetPartyByUser(leaderUid);
            if (party == null || party.Count <= 1 || !party.IsLeader(leaderUid)) return;

            FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: leader={leader.Player.CharacterId} party={party.PartyId} members={party.Count} → fan-out 跟随退出");
            foreach (var m in party.MembersBySlot())
            {
                if (!CanContinueTownProjection(leader, leaderGuard)) return;
                if (m.UserId == leaderUid) continue;
                _sessions.TryGet(m.CharacterId, out var bs);
                if (bs?.Player == null || bs.TcpClient == null || !bs.TcpClient.Connected) continue;
                var memberRun = bs.Player.CurrentRun;
                if (memberRun == null
                    || memberRun.PartyDungeonInstanceId
                        != leaderRunIdentity.PartyDungeonInstanceId)
                {
                    continue;
                }
                var memberRunIdentity = memberRun.CaptureIdentity();
                try
                {
                    if (!await ReturnSelfToTownAsync(
                            bs,
                            header,
                            memberRunIdentity,
                            memberRun.TownReturnAnchor,
                            sendCommandResponse: false))
                    {
                        continue;
                    }
                    await SendTownAccountStateAsync(
                        bs,
                        "party-return-village",
                        TownProjectionGuard.ForEndedRun(memberRunIdentity));
                    FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: member cid={bs.Player.CharacterId} 跟随退出→城镇");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: member uid={m.UserId} 跟随异常: {ex.Message}");
                }
            }
        }

        private async Task SendTownAccountStateAsync(
            EnhancedClientSession session,
            string reason,
            TownProjectionGuard projectionGuard)
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return;
            var accountId = session?.Account?.AccountId ?? 0;
            var characterId = session?.Player?.CharacterId ?? 0;
            if (accountId <= 0 || characterId <= 0)
                return;

            var summary = _honorLevel.LoadSummary(accountId);
            await UserInfoBroadcastService.SendSubtype0Async(
                session,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                $"{reason} subtype0",
                summary);
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            await _honorLevel.SendInfoAsync(session, ProtocolName, reason, summary);
        }

        private static bool CanContinueTownProjection(
            EnhancedClientSession session,
            TownProjectionGuard projectionGuard)
        {
            if (projectionGuard.Selection != null)
            {
                return projectionGuard.Selection.IsReturning
                    && session?.Player?.IsCurrentDungeonSelection(
                        projectionGuard.Selection) == true;
            }

            return !projectionGuard.EndedRun.IsValid
                || Dungeon.DungeonRunLifecycle.CanProjectTownState(
                    session,
                    projectionGuard.EndedRun);
        }

        internal static byte[] BuildUserLeavePacket(ushort userId)
            => GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0006,
                TownAreaNotificationBuilder.BuildUserLeave(userId));

        internal static bool IsTownArrivalStateEligible(
            PlayerContext player)
            => player != null
               && player.TownPresenceReady
               && player.CharacterId > 0
               && player.CurrentRun == null
               && player.UserState == 0x00;
    }
}
