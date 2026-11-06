using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Appearance;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Pvp;
using DfoServer.Network.Parsers.Pvp;
using PvpSessionDirectory =
    DfoServer.Game.Session.ISessionDirectory;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// A21 PvP room lifecycle and match settlement. Room creation publishes
    /// USERINFO, PVP_ROOM_INFO and USER_AREA without a MAKE_PVP_ROOM ACK.
    /// Room state and publications are isolated by listener and generation.
    /// </summary>
    internal sealed partial class PvpRoomHandler : IDisposable
    {
        internal const ushort MakeRoomCommandType = (ushort)CmdPacketTypeA21.MAKE_PVP_ROOM;
        internal const ushort EnterRoomCommandType = (ushort)CmdPacketTypeA21.ENTER_PVP_ROOM;
        internal const ushort SetSeatStateCommandType = (ushort)CmdPacketTypeA21.SET_PVP_SEAT_STATE;
        internal const ushort SetReadyStateCommandType = (ushort)CmdPacketTypeA21.SET_PVP_READY_STATE;
        internal const ushort SetTeamModeCommandType = (ushort)CmdPacketTypeA21.SET_PVP_TEAM_MODE;
        internal const ushort DiePvpCharacterCommandType = (ushort)CmdPacketTypeA21.DIE_PVP_CHARACTER;
        internal const ushort PvpTimeOutCommandType = (ushort)CmdPacketTypeA21.PVP_TIME_OUT;
        internal const ushort EndPvpResultCommandType = (ushort)CmdPacketTypeA21.END_PVP_RESULT;
        internal const ushort PvpRankResponseCommandType = (ushort)CmdPacketTypeA21.RES_PVP_RANK;
        internal const ushort CompleteLoadPvpCommandType = (ushort)CmdPacketTypeA21.COMPLETE_LOAD_PVP;
        internal const ushort ConnectP2pPvpCommandType = (ushort)CmdPacketTypeA21.CONNECT_P2P_PVP;
        internal const ushort PvpRequestFightCommandType = (ushort)CmdPacketTypeA21.PVP_REQUEST_FIGHT;
        internal const ushort UserInfoNotificationType = (ushort)NotiPacketTypeA21.USERINFO;
        internal const ushort UserStateNotificationType = (ushort)NotiPacketTypeA21.USER_STATE;
        internal const ushort UserAreaNotificationType = (ushort)NotiPacketTypeA21.USER_AREA;
        internal const ushort RoomInfoNotificationType = (ushort)NotiPacketTypeA21.PVP_ROOM_INFO;
        internal const ushort RoomStateNotificationType = (ushort)NotiPacketTypeA21.PVP_ROOM_STATE;
        internal const ushort SeatStateNotificationType = (ushort)NotiPacketTypeA21.PVP_SEAT_STATE;
        internal const ushort ReadyStateNotificationType = (ushort)NotiPacketTypeA21.PVP_READY_STATE;
        internal const ushort StartPvpNotificationType = (ushort)NotiPacketTypeA21.START_PVP;
        internal const ushort DiePvpCharacterNotificationType = (ushort)NotiPacketTypeA21.DIE_PVP_CHARACTER;
        internal const ushort EndPvpNotificationType = (ushort)NotiPacketTypeA21.END_PVP;
        internal const ushort RequestPvpRankNotificationType = (ushort)NotiPacketTypeA21.REQ_PVP_RANK;
        internal const ushort PvpTurnPlayerNotificationType = (ushort)NotiPacketTypeA21.PVP_TURN_PLAYER;
        internal const ushort PvpRequestFightNotificationType = (ushort)NotiPacketTypeA21.PVP_REQUEST_FIGHT;
        internal const byte PvpUserState = 0x02;
        internal const byte PvpAreaId = 0xFE;
        private static readonly TimeSpan RequiredSendTimeout =
            TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DefaultRoomInviteLifetime =
            TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DefaultSettlementAckTimeout =
            TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DefaultRelayBattleStartDelay =
            TimeSpan.FromSeconds(7);
        private static readonly TimeSpan DefaultRelayBattleTurnDelay =
            TimeSpan.FromSeconds(3);

        private readonly PvpSessionDirectory _sessions;
        private readonly Func<EnhancedClientSession, byte[]>
            _buildFullUserInfoPacket;
        private readonly CharacterTransitionCoordinator
            _characterTransitions;
        private readonly Func<EnhancedClientSession, Task<bool>>
            _announceTownArrivalWithinTransition;
        private readonly Func<
            EnhancedClientSession,
            byte[],
            CancellationToken,
            Task> _sendTownPacket;
        private readonly PvpRequiredPublicationCoordinator
            _requiredPublication;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _roomInviteLifetime;
        private readonly TimeSpan _settlementAckTimeout;
        private readonly TimeSpan _relayBattleStartDelay;
        private readonly TimeSpan _relayBattleTurnDelay;
        private readonly Func<bool> _isFreeDuelAvailable;
        private readonly FreeDuelRoomRegistry _rooms;
        private readonly PartyUdpRelay _pvpUdpRelay;
        private readonly PvpRelayLifecycleCoordinator
            _pvpRelayLifecycle;
        private readonly IGameDatabase _database;
        private readonly Game.Friends.BlacklistRepository _blacklist;

        private bool IsBlacklistPair(EnhancedClientSession first, EnhancedClientSession second)
            => _blacklist != null && (_blacklist.IsBlocked(first.Player.CharacterId, second.Player.CharacterId)
                || _blacklist.IsBlocked(second.Player.CharacterId, first.Player.CharacterId));
        private readonly byte[] _pvpRelayIpBytes;
        private readonly PvpRoomAdmissionCoordinator
            _roomAdmission;
        private readonly ConcurrentDictionary<Guid, byte>
            _lobbyReadySessions =
                new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, byte[]>
            _basicInfoBySession =
                new ConcurrentDictionary<Guid, byte[]>();
        private readonly ConcurrentDictionary<Guid, byte>
            _pendingRoomOwnerSessions =
                new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, byte>
            _pendingLobbyReadySessions =
                new ConcurrentDictionary<Guid, byte>();
        private readonly SemaphoreSlim _roomPublicationGate =
            new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetimeCancellation =
            new CancellationTokenSource();

        // Compatibility view for read-only room-generation checks. The
        // admission coordinator owns reservation and completion lifecycle.
        private ConcurrentDictionary<int, Guid>
            _pendingRoomJoinSessions =>
                _roomAdmission.PendingRoomJoinSessions;
        private long _unreportedUdpPeerPublications;
        private volatile bool _disposed;
        private int _disposeStarted;
        private int _pendingScheduledWork;

        internal Func<Task>
            AfterJoinRegistryCommitBeforeRelaySyncForTest { get; set; }

        internal Func<Task>
            AfterMemberRegistryMutationBeforeRelaySyncForTest { get; set; }

        internal Func<string, Task> AfterRelayRoomGateAcquiredForTest
        {
            get => _pvpRelayLifecycle.AfterRoomGateAcquiredForTest;
            set => _pvpRelayLifecycle.AfterRoomGateAcquiredForTest = value;
        }

        internal long UnreportedUdpPeerPublications =>
            Interlocked.Read(
                ref _unreportedUdpPeerPublications);

        internal int PendingScheduledWorkCountForTest =>
            Volatile.Read(ref _pendingScheduledWork);

        internal int PendingRoomInviteCountForTest =>
            _roomAdmission.PendingInviteCount;

        internal bool IsLobbyReadyForTest(Guid sessionId) =>
            _lobbyReadySessions.ContainsKey(sessionId);

        internal bool CanPublishLobbySnapshotForTest(
            EnhancedClientSession session) =>
                CanPublishLobbySnapshot(session);

        internal PvpRoomHandler(
            PvpSessionDirectory sessions,
            Func<EnhancedClientSession, byte[]>
                buildFullUserInfoPacket,
            CharacterTransitionCoordinator characterTransitions,
            Func<bool> isFreeDuelAvailable = null,
            FreeDuelRoomRegistry rooms = null,
            Func<EnhancedClientSession, Task<bool>>
                announceTownArrivalWithinTransition = null,
            Func<
                EnhancedClientSession,
                byte[],
                CancellationToken,
                Task> sendQueuedPacket = null,
            TimeSpan? queuedPublicationTimeout = null,
            TimeSpan? directHandshakeTimeout = null,
            PartyUdpRelay pvpUdpRelay = null,
            Func<DateTime> utcNow = null,
            TimeSpan? roomInviteLifetime = null,
            TimeSpan? settlementAckTimeout = null,
            IGameDatabase database = null,
            Func<
                EnhancedClientSession,
                byte[],
                CancellationToken,
                Task> sendTownPacket = null,
            TimeSpan? relayBattleStartDelay = null,
            TimeSpan? relayBattleTurnDelay = null)
        {
            _sessions =
                sessions
                ?? throw new ArgumentNullException(nameof(sessions));
            _buildFullUserInfoPacket =
                buildFullUserInfoPacket
                ?? throw new ArgumentNullException(
                    nameof(buildFullUserInfoPacket));
            _characterTransitions =
                characterTransitions
                ?? throw new ArgumentNullException(
                    nameof(characterTransitions));
            _database = database;
            _blacklist = database == null ? null : new Game.Friends.BlacklistRepository(database);
            _announceTownArrivalWithinTransition =
                announceTownArrivalWithinTransition;
            _sendTownPacket =
                sendTownPacket
                ?? ((session, packet, cancellationToken) =>
                    session.SendPacketAsync(
                        packet,
                        cancellationToken));
            _requiredPublication =
                new PvpRequiredPublicationCoordinator(
                    _roomPublicationGate,
                    sendQueuedPacket
                    ?? ((session, packet, cancellationToken) =>
                        session.SendPacketAsync(
                            packet,
                            cancellationToken)),
                    queuedPublicationTimeout
                    ?? RequiredSendTimeout,
                    directHandshakeTimeout
                    ?? RequiredSendTimeout);
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _roomInviteLifetime =
                roomInviteLifetime ??
                DefaultRoomInviteLifetime;
            if (_roomInviteLifetime <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(roomInviteLifetime));
            }
            _settlementAckTimeout =
                settlementAckTimeout ??
                DefaultSettlementAckTimeout;
            if (_settlementAckTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(settlementAckTimeout));
            }
            _relayBattleStartDelay =
                relayBattleStartDelay ??
                DefaultRelayBattleStartDelay;
            if (_relayBattleStartDelay <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(relayBattleStartDelay));
            }
            _relayBattleTurnDelay =
                relayBattleTurnDelay ??
                DefaultRelayBattleTurnDelay;
            if (_relayBattleTurnDelay <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(relayBattleTurnDelay));
            }
            _isFreeDuelAvailable =
                isFreeDuelAvailable
                ?? IsFreeDuelAvailable;
            _rooms = rooms ?? new FreeDuelRoomRegistry();
            _roomAdmission = new PvpRoomAdmissionCoordinator();
            _pvpUdpRelay = pvpUdpRelay;
            if (_pvpUdpRelay != null)
            {
                if (!IPAddress.TryParse(
                        _pvpUdpRelay.PublicIp,
                        out var relayIp) ||
                    relayIp.AddressFamily !=
                    System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    throw new ArgumentException(
                        "PvP relay public IP must be numeric IPv4",
                        nameof(pvpUdpRelay));
                }
                _pvpRelayIpBytes =
                    relayIp.GetAddressBytes();
            }
            _pvpRelayLifecycle = new PvpRelayLifecycleCoordinator(
                _pvpUdpRelay,
                _rooms,
                _roomPublicationGate,
                _roomAdmission.PendingRoomJoinSessions,
                room => GetRoomMemberTargets(room));
            FileLogger.Log(
                "[GameProtocol] PvP normal-match start gate active: " +
                "ready=0x0035/0x002C start=0x002D " +
                "normal-load=0x012A/0x012B(no reply); " +
                "settlement=0x0037..0x003A/0x002E,0x002F,0x0031");
            _sessions.SessionEnding += OnSessionEndingAsync;
        }

        internal async Task<bool> HandleRoomInviteRequestAsync(
            EnhancedClientSession inviter,
            EnhancedClientSession target,
            int peerToken)
        {
            if (_disposed)
                return false;

            PendingRoomInvite invitation = null;
            var current =
                await _characterTransitions.RunIfBothCurrentAsync(
                    inviter,
                    target,
                    async () =>
                    {
                        await _roomPublicationGate.WaitAsync();
                        try
                        {
                            if (_disposed ||
                                inviter?.Player == null ||
                                target?.Player == null ||
                                IsBlacklistPair(inviter, target) ||
                                inviter.ListenerPort !=
                                    target.ListenerPort ||
                                !GameNetworkConfig.IsPvpListener(
                                    inviter.ListenerPort) ||
                                inviter.Player.UserState != PvpUserState ||
                                !CanEnterRoom(target) ||
                                !_rooms.TryGetRoomForMember(
                                    inviter.Player.CharacterId,
                                    inviter.SessionId,
                                    out var room,
                                    out _) ||
                                !IsPublishedRoomOwnerCurrentUnderGate(room) ||
                                room.RoomState !=
                                    FreeDuelRoom.WaitingRoomState ||
                                _pendingRoomJoinSessions.ContainsKey(
                                    room.RoomId))
                            {
                                return;
                            }

                            invitation = new PendingRoomInvite(
                                inviter.SessionId,
                                room.RoomId,
                                room.OwnerSessionId,
                                room.GenerationId,
                                room.ListenerPort,
                                peerToken,
                                _utcNow().Add(
                                    _roomInviteLifetime));
                            if (!_roomAdmission.TryStorePendingInvite(
                                    target.SessionId,
                                    invitation))
                            {
                                invitation = null;
                                return;
                            }
                        }
                        finally
                        {
                            _roomPublicationGate.Release();
                        }

                        if (invitation == null || _disposed)
                        {
                            if (invitation != null)
                            {
                                RemovePendingRoomInvite(
                                    target.SessionId,
                                    invitation);
                                invitation = null;
                            }
                            return;
                        }

                        // A21 118C640 ignores REQUEST_PEER when its inviter UID
                        // has no USERINFO0 record, including after an area reload.
                        var inviterIdentity = GamePacketEnvelopeBuilder.Build(
                            0x00, UserInfoNotificationType,
                            AppearanceService.BuildNoti2Body(inviter.Player, _database));
                        var writer = new GamePacketWriter();
                        writer.WriteUInt16(inviter.Player.UserId);
                        writer.WriteByte(2);
                        writer.WriteInt32(peerToken);
                        var sent =
                            await Game.Session.SessionDirectory
                                .TrySendBestEffortAsync(
                                    cancellationToken =>
                                        SendRequiredSequenceAsync(target,
                                            new[]
                                            {
                                                inviterIdentity,
                                                GamePacketEnvelopeBuilder.Build(
                                                    0x00,
                                                    (ushort)NotiPacketTypeA21.REQUEST_PEER,
                                                    writer.ToArray())
                                            }, cancellationToken),
                                    $"PvP room invite target=" +
                                    $"{target.Player.UserId}");
                        if (!sent || _disposed)
                        {
                            RemovePendingRoomInvite(
                                target.SessionId,
                                invitation);
                            invitation = null;
                        }
                    });

            if (!current || invitation == null)
            {
                FileLogger.Log(
                    "[GameProtocol] PvP room invite rejected: " +
                    $"inviter={inviter?.Player?.UserId ?? 0} " +
                    $"target={target?.Player?.UserId ?? 0}");
                return false;
            }

            FileLogger.Log(
                "[GameProtocol] PvP room invite delivered: " +
                $"inviter={inviter.Player.UserId} " +
                $"target={target.Player.UserId} " +
                $"room={invitation.RoomId}");
            return true;
        }

        internal async Task<bool> HandleRoomInviteResponseAsync(
            EnhancedClientSession inviter,
            EnhancedClientSession target,
            int echoedPeerToken,
            Func<Action, Task> checkoutParty)
        {
            if (_disposed)
                return false;

            var joined = false;
            var responseAborted = false;
            var current =
                await _characterTransitions.RunIfBothCurrentAsync(
                    inviter,
                    target,
                    async () =>
                    {
                        PendingRoomInvite invitation = null;
                        FreeDuelRoom room = null;
                        var invitationValid = false;
                        await _roomPublicationGate.WaitAsync();
                        try
                        {
                            if (_disposed || IsBlacklistPair(inviter, target))
                            {
                                responseAborted = true;
                                return;
                            }

                            if (inviter?.Player != null &&
                                target?.Player != null &&
                                _roomAdmission.TryGetPendingInvite(
                                    target.SessionId,
                                    out invitation) &&
                                invitation.PeerToken ==
                                    echoedPeerToken &&
                                invitation.InviterSessionId ==
                                    inviter.SessionId)
                            {
                                if (invitation.ExpiresAtUtc >=
                                        _utcNow() &&
                                    inviter.ListenerPort ==
                                        invitation.ListenerPort &&
                                    target.ListenerPort ==
                                        invitation.ListenerPort &&
                                    inviter.Player.UserState ==
                                        PvpUserState &&
                                    CanEnterRoom(target) &&
                                    _rooms.TryGetRoomForMember(
                                        inviter.Player.CharacterId,
                                        inviter.SessionId,
                                        out room,
                                        out _) &&
                                    room.RoomId == invitation.RoomId &&
                                    room.OwnerSessionId ==
                                        invitation.OwnerSessionId &&
                                    room.GenerationId ==
                                        invitation.RoomGenerationId &&
                                    IsPublishedRoomOwnerCurrentUnderGate(
                                        room) &&
                                    room.RoomState ==
                                        FreeDuelRoom.WaitingRoomState &&
                                    !_pendingRoomJoinSessions.ContainsKey(
                                        room.RoomId))
                                {
                                    invitationValid =
                                        RemovePendingRoomInvite(
                                            target.SessionId,
                                            invitation);
                                }
                                else
                                {
                                    // A response matching this exact invite is
                                    // one-shot even when its room generation or
                                    // lifetime is no longer current. Preserve a
                                    // newer replacement invite when the echoed
                                    // token or inviter generation differs.
                                    RemovePendingRoomInvite(
                                        target.SessionId,
                                        invitation);
                                }
                            }
                        }
                        finally
                        {
                            _roomPublicationGate.Release();
                        }
                        if (responseAborted || _disposed)
                            return;
                        if (!invitationValid)
                        {
                            await SendPvpInviteErrorAsync(
                                target,
                                19);
                            return;
                        }

                        var writer = new GamePacketWriter();
                        writer.WriteUInt16(room.RoomId);
                        writer.WriteByte(
                            room.HasPassword ? (byte)1 : (byte)0);
                        if (room.HasPassword)
                        {
                            var password = room.PasswordBytes;
                            writer.WriteInt32(password.Length);
                            writer.WriteBytes(password);
                        }

                        await HandleEnterRoomWithinTransition(
                            target,
                            writer.ToArray(),
                            invited: true,
                            invitedBy: inviter,
                            requiredOwnerSessionId:
                                invitation.OwnerSessionId,
                            requiredRoomGenerationId:
                                invitation.RoomGenerationId,
                            beforeEnter: checkoutParty);
                        joined =
                            target.Player.UserState == PvpUserState &&
                            _rooms.TryGetRoomForMember(
                                target.Player.CharacterId,
                                target.SessionId,
                                out var joinedRoom,
                                out _) &&
                            joinedRoom.RoomId == room.RoomId &&
                            joinedRoom.GenerationId ==
                                room.GenerationId;
                    });

            if (!current &&
                !_disposed &&
                _characterTransitions.IsCurrent(target))
            {
                await SendPvpInviteErrorAsync(target, 19);
            }

            FileLogger.Log(
                "[GameProtocol] PvP room invite response: " +
                $"inviter={inviter?.Player?.UserId ?? 0} " +
                $"target={target?.Player?.UserId ?? 0} " +
                $"joined={current && joined}");
            return current && joined;
        }

        internal async Task HandleMakeRoom(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandleMakeRoomWithinTransition(
                        session,
                        body));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] MAKE_PVP_ROOM ignored: " +
                    "session no longer owns the character generation");
            }
        }

        private async Task HandleMakeRoomWithinTransition(
            EnhancedClientSession session,
            byte[] body)
        {
            if (session?.Account == null ||
                session.Player == null ||
                session.Player.CharacterId <= 0 ||
                session.Player.UserId == 0 ||
                session.GameSession == null ||
                !_isFreeDuelAvailable() ||
                !GameNetworkConfig.IsPvpListener(
                    session.ListenerPort) ||
                !_lobbyReadySessions.ContainsKey(
                    session.SessionId) ||
                session.Player.UserState != 0 ||
                session.Player.CurrentRun != null)
            {
                await SendErrorAsync(session, 19);
                return;
            }

            if (!MakePvpRoomRequest.TryParse(
                    body,
                    out var request,
                    out var parseError))
            {
                FileLogger.Log(
                    "[GameProtocol] MAKE_PVP_ROOM rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"body={body?.Length ?? 0}B reason={parseError}");
                await SendErrorAsync(session, 19);
                return;
            }

            var fullUserInfoPacket =
                _buildFullUserInfoPacket(session);
            if (fullUserInfoPacket == null ||
                fullUserInfoPacket.Length == 0)
            {
                FileLogger.Log(
                    "[GameProtocol] MAKE_PVP_ROOM rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    "could not build USERINFO subtype 1");
                await SendErrorAsync(session, 19);
                return;
            }

            var previousTownId = session.Player.CurTownId;
            var pvpAreaPacket =
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    UserAreaNotificationType,
                    TownAreaNotificationBuilder.BuildUserArea(
                        new TownUserSnapshot
                        {
                            UserId = session.Player.UserId,
                            TownId = previousTownId,
                            AreaId = PvpAreaId,
                            PosX = session.Player.CurPosX,
                            PosY = session.Player.CurPosY,
                            Direction =
                                session.Player.CurDirection,
                            State =
                                session.Player.CurAreaState
                        }));
            var pvpStatePacket =
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    UserStateNotificationType,
                    EnterSelectDungeonStateBuilder.BuildUserState(
                        new[] { session.Player.UserId },
                        PvpUserState));

            FreeDuelRoom room = null;
            byte[] roomInfoPacket = null;
            Task precedingPublication = Task.CompletedTask;
            TaskCompletionSource<bool> directHandshakeBarrier = null;
            Task roomPublication = Task.CompletedTask;
            ExceptionDispatchInfo makeFailure = null;
            byte errorCode = 0;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (session?.Account != null &&
                    session.Player != null &&
                    session.Player.CharacterId > 0 &&
                    session.Player.UserId > 0 &&
                    session.GameSession != null &&
                    _isFreeDuelAvailable() &&
                    GameNetworkConfig.IsPvpListener(
                        session.ListenerPort) &&
                    _lobbyReadySessions.ContainsKey(
                        session.SessionId) &&
                    !_pendingLobbyReadySessions.ContainsKey(
                        session.SessionId) &&
                    session.Player.UserState == 0 &&
                    session.Player.CurrentRun == null &&
                    _characterTransitions.IsCurrent(session) &&
                    _rooms.TryCreate(
                        session.ListenerPort,
                        session.Player.CharacterId,
                        session.SessionId,
                        session.Player.UserId,
                        request,
                        out room,
                        out errorCode))
                {
                    try
                    {
                        roomInfoPacket =
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                RoomInfoNotificationType,
                                PvpRoomNotificationBuilder
                                    .BuildRoomInfoBody(
                                        new[] { room }));
                        _pendingRoomOwnerSessions[
                            session.SessionId] = 0;
                        ReserveDirectHandshakeUnderGate(
                            session,
                            out precedingPublication,
                            out directHandshakeBarrier);
                    }
                    catch
                    {
                        _pendingRoomOwnerSessions.TryRemove(
                            session.SessionId,
                            out _);
                        RollbackUnpublishedRoom(room);
                        throw;
                    }
                }
                else if (errorCode == 0)
                    errorCode = 19;
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            if (room == null)
            {
                await SendErrorAsync(session, errorCode);
                return;
            }

            try
            {
                using var makeHandshakeTimeout =
                    new CancellationTokenSource(
                        _requiredPublication.DirectHandshakeTimeout);

                // Legacy MakePVPRoom success order begins with creator-only
                // USERINFO. Keep later publications behind this session's
                // barrier, but perform every untrusted socket write outside
                // the global room-state gate.
                await precedingPublication.WaitAsync(
                    makeHandshakeTimeout.Token);
                await SendRequiredSequenceAsync(
                    session,
                    new[]
                    {
                        fullUserInfoPacket,
                        roomInfoPacket,
                        pvpAreaPacket
                    },
                    makeHandshakeTimeout.Token);

                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (!_pendingRoomOwnerSessions.TryRemove(
                            session.SessionId,
                            out _) ||
                        !_characterTransitions.IsCurrent(session) ||
                        !_rooms.TryGetRoomForMember(
                            session.Player.CharacterId,
                            session.SessionId,
                            out var committedRoom,
                            out var committedSeat) ||
                        committedSeat != room.ManagerSeat ||
                        committedRoom.RoomId != room.RoomId ||
                        committedRoom.OwnerSessionId !=
                            session.SessionId ||
                        committedRoom.GenerationId !=
                            room.GenerationId)
                    {
                        throw new InvalidOperationException(
                            "PvP room creation changed during handshake");
                    }

                    session.Player.TownPresenceReady = false;
                    session.Player.UserState = PvpUserState;
                    // Queue both packets as one ordered publication per peer.
                    // Await outside the global room-state gate so a slow peer
                    // applies backpressure without blocking unrelated rooms.
                    roomPublication = QueueRequiredToReadyListener(
                        room.ListenerPort,
                        session.SessionId,
                        roomInfoPacket,
                        pvpStatePacket);
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
            }
            catch (Exception ex)
            {
                await _roomPublicationGate.WaitAsync();
                try
                {
                    _pendingRoomOwnerSessions.TryRemove(
                        session.SessionId,
                        out _);
                    RollbackUnpublishedRoom(room);
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                session.Player.UserState = 0;
                session.Close();
                makeFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                CompleteDirectHandshake(
                    session,
                    directHandshakeBarrier);
            }

            if (makeFailure != null)
            {
                makeFailure.Throw();
                throw new InvalidOperationException(
                    "unreachable MAKE_PVP_ROOM failure path");
            }

            await roomPublication;

            FileLogger.Log(
                "[GameProtocol] MAKE_PVP_ROOM accepted: " +
                $"cid={session.Player.CharacterId} " +
                $"room={room.RoomId} nameType={room.RoomNameType} " +
                $"nameBytes={room.RoomNameBytes.Length} " +
                $"map={room.MapIndex} password={room.HasPassword} " +
                $"battleMode={room.BattleMode} " +
                $"roomInfoBody={FormatBody(
                    PvpRoomNotificationBuilder.BuildRoomInfoBody(
                        new[] { room }))}");
        }

        internal async Task HandleEnterRoom(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandleEnterRoomWithinTransition(
                        session,
                        body));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] ENTER_PVP_ROOM ignored: " +
                    "session no longer owns the character generation");
            }
        }

        private async Task HandleEnterRoomWithinTransition(
            EnhancedClientSession session,
            byte[] body,
            bool invited = false,
            EnhancedClientSession invitedBy = null,
            Guid? requiredOwnerSessionId = null,
            Guid? requiredRoomGenerationId = null,
            Func<Action, Task> beforeEnter = null)
        {
            if (!CanEnterRoom(session))
            {
                await SendEnterRoomErrorAsync(
                    session,
                    19,
                    invited);
                return;
            }
            if (!EnterPvpRoomRequest.TryParse(
                    body,
                    out var request,
                    out var parseError))
            {
                FileLogger.Log(
                    "[GameProtocol] ENTER_PVP_ROOM rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"body={body?.Length ?? 0}B reason={parseError}");
                await SendEnterRoomErrorAsync(
                    session,
                    19,
                    invited);
                return;
            }

            var newcomerFullInfo =
                _buildFullUserInfoPacket(session);
            if (newcomerFullInfo == null ||
                newcomerFullInfo.Length == 0)
            {
                await SendEnterRoomErrorAsync(
                    session,
                    19,
                    invited);
                return;
            }

            var newcomerBasicInfo = GamePacketEnvelopeBuilder.Build(
                0x00, UserInfoNotificationType,
                AppearanceService.BuildNoti2Body(session.Player, _database));

            FreeDuelRoom preparedRoom = null;
            FreeDuelRoom room = null;
            byte preparedSeat = byte.MaxValue;
            long preparedBaseRevision = -1;
            var preparedOwnerSessionId = Guid.Empty;
            byte errorCode = 0;
            IReadOnlyList<EnhancedClientSession> preparedExistingMembers =
                Array.Empty<EnhancedClientSession>();
            Task precedingPublication = Task.CompletedTask;
            TaskCompletionSource<bool> directHandshakeBarrier = null;
            Task roomPublication = Task.CompletedTask;
            Task rollbackPublication = Task.CompletedTask;
            ExceptionDispatchInfo joinFailure = null;

            // Reserve only a publication-order barrier under the global gate.
            // The registry remains unchanged until the direct handshake has
            // completed, so a slow client cannot freeze another room.
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (IsRoomPendingPublication(
                        session.ListenerPort,
                        request.RoomId))
                {
                    errorCode = 22;
                }
                else if (_pendingRoomJoinSessions.ContainsKey(
                             request.RoomId))
                {
                    errorCode = 22;
                }
                else if (CanEnterRoom(session) &&
                    (!invited ||
                     invitedBy?.Player != null &&
                     _rooms.TryGetRoomForMember(
                         invitedBy.Player.CharacterId,
                         invitedBy.SessionId,
                         out var inviterRoom,
                         out _) &&
                     inviterRoom.RoomId == request.RoomId &&
                     requiredOwnerSessionId.HasValue &&
                     inviterRoom.OwnerSessionId ==
                         requiredOwnerSessionId.Value &&
                     requiredRoomGenerationId.HasValue &&
                     inviterRoom.GenerationId ==
                         requiredRoomGenerationId.Value) &&
                    _rooms.TryPrepareJoin(
                        session.ListenerPort,
                        session.Player.CharacterId,
                        session.SessionId,
                        session.Player.UserId,
                        request,
                        out preparedRoom,
                        out preparedSeat,
                        out preparedBaseRevision,
                        out preparedOwnerSessionId,
                        out errorCode))
                {
                    if (requiredOwnerSessionId.HasValue &&
                        preparedOwnerSessionId !=
                            requiredOwnerSessionId.Value ||
                        requiredRoomGenerationId.HasValue &&
                        preparedRoom.GenerationId !=
                            requiredRoomGenerationId.Value)
                    {
                        preparedRoom = null;
                        errorCode = 22;
                    }
                    else
                    {
                        if (!IsPublishedRoomOwnerCurrentUnderGate(
                                preparedRoom))
                        {
                            preparedRoom = null;
                            errorCode = 22;
                        }
                        else
                        {
                            try
                            {
                                preparedExistingMembers =
                                    GetRoomMemberTargets(preparedRoom)
                                        .Where(
                                            target =>
                                                target.SessionId !=
                                                session.SessionId)
                                        .ToArray();
                            }
                            catch (InvalidOperationException ex)
                            {
                                FileLogger.Log(
                                    "[GameProtocol] ENTER_PVP_ROOM " +
                                    "retryable reject: " +
                                    $"cid={session.Player.CharacterId} " +
                                    $"room={preparedRoom.RoomId} " +
                                    "reason=stale-member-generation " +
                                    $"error={ex.Message}");
                                preparedRoom = null;
                                errorCode = 22;
                            }

                            if (preparedRoom != null &&
                                !_roomAdmission.TryReservePendingJoin(
                                    preparedRoom.RoomId,
                                    session.SessionId))
                            {
                                preparedRoom = null;
                                errorCode = 22;
                            }
                            else if (preparedRoom != null)
                            {
                                ReserveDirectHandshakeUnderGate(
                                    session,
                                    out precedingPublication,
                                    out directHandshakeBarrier);
                            }
                        }
                    }
                }
                else if (errorCode == 0)
                {
                    errorCode = 19;
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            if (preparedRoom == null)
            {
                await SendEnterRoomErrorAsync(
                    session,
                    errorCode,
                    invited);
                return;
            }

            var predictedMembers =
                preparedExistingMembers
                    .Concat(new[] { session })
                    .OrderBy(
                        member => member.Player.UserId)
                    .ToArray();
            LogUnreportedUdpEndpoints(
                preparedRoom,
                predictedMembers,
                "join-prepare");
            var preparedRelay =
                await TrySyncPvpRelayGenerationAsync(
                    preparedRoom,
                    preparedExistingMembers,
                    predictedMembers,
                    "join-prepare",
                    expectedRegistryRevision:
                        preparedBaseRevision,
                    pendingJoinSessionId: session.SessionId,
                    closeOnFailure: false);
            if (!preparedRelay.Success ||
                !preparedRelay.GenerationCurrent)
            {
                FileLogger.Log(
                    "[GameProtocol] ENTER_PVP_ROOM retryable reject: " +
                    $"cid={session.Player.CharacterId} " +
                    $"room={preparedRoom.RoomId} " +
                    "reason=pvp-relay-allocation");
                try
                {
                    using var failureTimeout =
                        new CancellationTokenSource(
                        _requiredPublication.DirectHandshakeTimeout);
                    await SendRequiredSequenceAsync(
                        session,
                        new[]
                        {
                            BuildEnterRoomErrorPacket(
                                19,
                                invited)
                        },
                        failureTimeout.Token);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        "[GameProtocol] ENTER_PVP_ROOM retryable error " +
                        $"publication failed: cid=" +
                        $"{session.Player.CharacterId} " +
                        $"error={ex.GetType().Name}: {ex.Message}");
                    session.Close();
                }
                finally
                {
                    CompletePendingRoomJoin(
                        preparedRoom.RoomId,
                        session.SessionId);
                    CompleteDirectHandshake(
                        session,
                        directHandshakeBarrier);
                }
                return;
            }

            IReadOnlyList<EnhancedClientSession> committedMembers =
                Array.Empty<EnhancedClientSession>();
            var joinCommitted = false;
            var invitedCheckoutCommitted = false;
            try
            {
                using var joinHandshakeTimeout =
                    new CancellationTokenSource(
                        _requiredPublication.DirectHandshakeTimeout);

                // Drain packets that were already queued to this client, then
                // keep later publications chained behind our barrier until the
                // direct legacy handshake and atomic commit both finish.
                await precedingPublication.WaitAsync(
                    joinHandshakeTimeout.Token);

                var existingPackets =
                    new List<byte[]>(
                        preparedExistingMembers.Count + 2);
                // USERINFO1 (13E6370) and USER_UDP_IP_PORT (11896E0)
                // both require the UID records established by USERINFO0.
                existingPackets.Add(GamePacketEnvelopeBuilder.Build(
                    0x00, UserInfoNotificationType,
                    BuildBasicInfoRosterBody(predictedMembers.Select(
                        member => AppearanceService.BuildNoti2Body(member.Player, _database)))));
                foreach (var existing in preparedExistingMembers)
                {
                    var existingFullInfo =
                        _buildFullUserInfoPacket(existing);
                    if (existingFullInfo == null ||
                        existingFullInfo.Length == 0)
                    {
                        throw new InvalidOperationException(
                            "could not build existing PvP member USERINFO");
                    }
                    existingPackets.Add(existingFullInfo);
                }
                existingPackets.Add(
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000B,
                        BuildPvpPeerInfoBody(
                            preparedRoom,
                            preparedExistingMembers,
                            session,
                            preparedRelay.Snapshot)));
                await SendRequiredSequenceAsync(
                    session,
                    existingPackets,
                    joinHandshakeTimeout.Token);

                var newcomerPeerPacket =
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000B,
                        BuildPvpPeerInfoBody(
                            preparedRoom,
                            new[] { session },
                            session,
                            preparedRelay.Snapshot));
                await SendRequiredSequenceAsync(
                    session,
                    new[]
                    {
                        newcomerFullInfo,
                        newcomerPeerPacket
                    },
                    joinHandshakeTimeout.Token);
                if (!invited)
                {
                    await SendRequiredSequenceAsync(
                        session,
                        new[]
                        {
                            GamePacketEnvelopeBuilder.Build(
                                0x01,
                                EnterRoomCommandType,
                                PvpRoomNotificationBuilder
                                    .BuildEnterSuccessBody(
                                        preparedRoom))
                        },
                        joinHandshakeTimeout.Token);
                }

                var seatBody =
                    PvpRoomNotificationBuilder
                        .BuildSeatStateBody(
                            preparedRoom,
                            preparedSeat);
                var seatPacket =
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        SeatStateNotificationType,
                        seatBody);
                var pvpAreaPacket =
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        UserAreaNotificationType,
                        TownAreaNotificationBuilder
                            .BuildUserArea(
                                new TownUserSnapshot
                                {
                                    UserId =
                                        session.Player.UserId,
                                    TownId =
                                        session.Player.CurTownId,
                                    AreaId = PvpAreaId,
                                    PosX =
                                        session.Player.CurPosX,
                                    PosY =
                                        session.Player.CurPosY,
                                    Direction =
                                        session.Player.CurDirection,
                                    State =
                                        session.Player.CurAreaState
                                }));
                if (!invited)
                {
                    await SendRequiredSequenceAsync(
                        session,
                        new[] { seatPacket, pvpAreaPacket },
                        joinHandshakeTimeout.Token);
                }

                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (!CanEnterRoom(session) ||
                        !_rooms.TryCommitPreparedJoin(
                            session.ListenerPort,
                            session.Player.CharacterId,
                            session.SessionId,
                            session.Player.UserId,
                            request,
                            preparedBaseRevision,
                            preparedOwnerSessionId,
                            preparedSeat,
                            out room,
                            out errorCode))
                    {
                        throw new InvalidOperationException(
                            "PvP room changed during enter handshake " +
                            $"(error={errorCode})");
                    }

                    try
                    {
                        committedMembers =
                            GetRoomMemberTargets(room);

                        session.Player.TownPresenceReady = false;
                        session.Player.UserState = PvpUserState;
                        joinCommitted = true;
                    }
                    catch
                    {
                        if (_rooms.TryRollbackJoinedMember(
                                session.Player.CharacterId,
                                session.SessionId,
                                out var rolledBackRoom,
                                out var rolledBackSeat))
                        {
                            joinCommitted = false;
                            rollbackPublication =
                                QueueRequiredToReadyListener(
                                    rolledBackRoom.ListenerPort,
                                    session.SessionId,
                                    GamePacketEnvelopeBuilder.Build(
                                        0x00,
                                        SeatStateNotificationType,
                                        PvpRoomNotificationBuilder
                                            .BuildSeatStateBody(
                                                rolledBackRoom,
                                                rolledBackSeat)));
                        }
                        session.Player.UserState = 0;
                        throw;
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }

                if (AfterJoinRegistryCommitBeforeRelaySyncForTest != null)
                {
                    await AfterJoinRegistryCommitBeforeRelaySyncForTest();
                }
                var committedRelay =
                    // The prepare matrix is already bound to this exact
                    // pending TCP session generation. Preserve a UDP tuple
                    // learned after the newcomer's first 0x000B; reconnects
                    // rotate SessionId and TrySyncRoom clears stale tuples.
                    await TrySyncPvpRelayGenerationAsync(
                        room,
                        committedMembers,
                        committedMembers,
                        "join-commit",
                        pendingJoinSessionId:
                            session.SessionId);
                if (!committedRelay.Success ||
                    !committedRelay.GenerationCurrent)
                {
                    throw new InvalidOperationException(
                        "PvP relay could not publish committed room matrix");
                }
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (!joinCommitted ||
                        !IsExactRegistrySnapshotCurrentUnderGate(
                            session,
                            room,
                            committedMembers))
                    {
                        throw new InvalidOperationException(
                            "PvP room changed during committed relay sync");
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                if (invited)
                {
                    if (beforeEnter != null)
                    {
                        await beforeEnter(
                            () =>
                                invitedCheckoutCommitted = true);
                        if (!invitedCheckoutCommitted)
                        {
                            throw new InvalidOperationException(
                                "PvP invite party checkout did not commit");
                        }
                    }
                    else
                    {
                        invitedCheckoutCommitted = true;
                    }

                    Task invitedPreGotoPublication;
                    await _roomPublicationGate.WaitAsync();
                    try
                    {
                        var invitedExistingMembers =
                            committedMembers
                                .Where(
                                    target =>
                                        target.SessionId !=
                                        session.SessionId)
                                .ToArray();
                        var invitedIdentityPublication =
                            QueueRequired(
                                 invitedExistingMembers,
                                 room.ListenerPort,
                                 newcomerBasicInfo,
                                 newcomerFullInfo);
                        var invitedPeerPublication =
                            PublishPvpNewcomerPeerRecords(
                                room,
                                invitedExistingMembers,
                                session,
                                committedRelay.Snapshot);
                        var invitedSeatBroadcast =
                            QueueRequiredToReadyListener(
                                room.ListenerPort,
                                session.SessionId,
                                seatPacket);
                        invitedPreGotoPublication =
                            Task.WhenAll(
                                invitedIdentityPublication,
                                invitedPeerPublication,
                                invitedSeatBroadcast);
                    }
                    finally
                    {
                        _roomPublicationGate.Release();
                    }
                    await invitedPreGotoPublication;

                    using var invitedEntryTimeout =
                        new CancellationTokenSource(
                        _requiredPublication.DirectHandshakeTimeout);
                    await SendRequiredSequenceAsync(
                        session,
                        new[] { seatPacket, pvpAreaPacket },
                        invitedEntryTimeout.Token);
                }
                await _roomPublicationGate.WaitAsync();
                try
                {
                    var committedExistingMembers =
                        committedMembers
                            .Where(
                                target =>
                                    target.SessionId !=
                                    session.SessionId)
                            .ToArray();
                    var committedPublications =
                        new List<Task>();
                    if (!invited)
                    {
                        committedPublications.Add(
                            QueueRequired(
                                 committedExistingMembers,
                                 room.ListenerPort,
                                 newcomerBasicInfo,
                                 newcomerFullInfo));
                        committedPublications.Add(
                            PublishPvpNewcomerPeerRecords(
                                room,
                                committedMembers,
                                session,
                                committedRelay.Snapshot));
                        committedPublications.Add(
                            QueueRequiredToReadyListener(
                                room.ListenerPort,
                                session.SessionId,
                                seatPacket));
                    }
                    committedPublications.Add(
                        QueueRequiredToReadyListener(
                            room.ListenerPort,
                            session.SessionId,
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                UserStateNotificationType,
                                EnterSelectDungeonStateBuilder
                                    .BuildUserState(
                                        new[]
                                        {
                                            session.Player.UserId
                                        },
                                        PvpUserState))));
                    roomPublication =
                        Task.WhenAll(committedPublications);
                }
                finally
                {
                    _roomPublicationGate.Release();
                }

                FileLogger.Log(
                    "[GameProtocol] ENTER_PVP_ROOM accepted: " +
                    $"cid={session.Player.CharacterId} " +
                    $"room={room.RoomId} seat={preparedSeat} " +
                    $"password={request.HasPassword} " +
                    $"seatBody={FormatBody(seatBody)} " +
                    $"revision={room.Revision}");
            }
            catch (Exception ex)
            {
                var committedBeforeFailure =
                    joinCommitted;
                var rollbackSucceeded =
                    !committedBeforeFailure;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (joinCommitted &&
                        !invitedCheckoutCommitted &&
                        _rooms.TryRollbackJoinedMember(
                            session.Player.CharacterId,
                            session.SessionId,
                            out var rolledBackRoom,
                            out var rolledBackSeat))
                    {
                        rollbackSucceeded = true;
                        room = rolledBackRoom;
                        rollbackPublication =
                            QueueRequiredToReadyListener(
                                rolledBackRoom.ListenerPort,
                                session.SessionId,
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    SeatStateNotificationType,
                                    PvpRoomNotificationBuilder
                                        .BuildSeatStateBody(
                                            rolledBackRoom,
                                            rolledBackSeat)));
                    }

                    if (!invitedCheckoutCommitted &&
                        rollbackSucceeded)
                    {
                        joinCommitted = false;
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                if (!invitedCheckoutCommitted &&
                    rollbackSucceeded)
                {
                    session.Player.UserState = 0;
                }
                await ReconcilePvpRelayGenerationAsync(
                    preparedRoom,
                    "join-rollback");
                var recoverableInvitedFailure =
                    invited &&
                    !invitedCheckoutCommitted &&
                    rollbackSucceeded;
                if (recoverableInvitedFailure)
                {
                    try
                    {
                        await rollbackPublication;
                        using var failureTimeout =
                            new CancellationTokenSource(
                        _requiredPublication.DirectHandshakeTimeout);
                        await SendRequiredSequenceAsync(
                            session,
                            new[]
                            {
                                BuildEnterRoomErrorPacket(
                                    19,
                                    invited: true)
                            },
                            failureTimeout.Token);
                        FileLogger.Log(
                            "[GameProtocol] ENTER_PVP_ROOM invited " +
                            $"rollback reported: cid=" +
                            $"{session.Player?.CharacterId ?? 0} " +
                            $"reason={ex.GetType().Name}");
                    }
                    catch (Exception sendEx)
                    {
                        FileLogger.Log(
                            "[GameProtocol] ENTER_PVP_ROOM invited " +
                            $"rollback error publication failed: cid=" +
                            $"{session.Player?.CharacterId ?? 0} " +
                            $"error={sendEx.GetType().Name}: " +
                            $"{sendEx.Message}");
                        session.Close();
                    }
                }
                else
                {
                    session.Close();
                    joinFailure =
                        ExceptionDispatchInfo.Capture(ex);
                }
            }
            finally
            {
                CompletePendingRoomJoin(
                    preparedRoom.RoomId,
                    session.SessionId);
                CompleteDirectHandshake(
                    session,
                    directHandshakeBarrier);
            }

            if (joinFailure != null)
            {
                try
                {
                    await rollbackPublication;
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        "[GameProtocol] ENTER_PVP_ROOM rollback " +
                        $"publication failed: cid=" +
                        $"{session.Player?.CharacterId ?? 0} " +
                        $"error={ex.GetType().Name}: {ex.Message}");
                }
                joinFailure.Throw();
                throw new InvalidOperationException(
                    "unreachable ENTER_PVP_ROOM failure path");
            }

            await roomPublication;
        }

        internal async Task HandleReportedUdpEndpointChanged(
            EnhancedClientSession session)
        {
            if (session?.Player == null ||
                session.Player.CharacterId <= 0 ||
                session.Player.UserId == 0)
            {
                return;
            }

            FreeDuelRoom room = null;
            IReadOnlyList<EnhancedClientSession> members =
                Array.Empty<EnhancedClientSession>();
            while (true)
            {
                Task joinCompletion = null;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (!_characterTransitions.IsCurrent(session))
                    {
                        return;
                    }

                    if (!_rooms.TryGetRoomForMember(
                            session.Player.CharacterId,
                            session.SessionId,
                            out room,
                            out _))
                    {
                        if (!_roomAdmission.TryFindPendingJoinForSession(
                                session.SessionId,
                                out _,
                                out joinCompletion))
                        {
                            return;
                        }
                    }
                    else
                    {
                        if (!_pendingRoomJoinSessions.ContainsKey(
                                room.RoomId) ||
                            !_roomAdmission.TryGetPendingJoinCompletion(
                                room.RoomId,
                                out joinCompletion))
                        {
                            try
                            {
                                members =
                                    GetRoomMemberTargets(room);
                            }
                            catch (InvalidOperationException ex)
                            {
                                FileLogger.Log(
                                    "[GameProtocol] PvP UDP endpoint refresh " +
                                    "deferred: " +
                                    $"cid={session.Player.CharacterId} " +
                                    $"room={room.RoomId} " +
                                    "reason=stale-member-generation " +
                                    $"error={ex.Message}");
                                return;
                            }
                        }
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }

                if (joinCompletion == null)
                    break;

                FileLogger.Log(
                    "[GameProtocol] PvP UDP endpoint refresh waiting: " +
                    $"cid={session.Player.CharacterId} " +
                    $"room={room?.RoomId ?? -1} reason=join-in-flight");
                // Every join success, failure, disconnect and Dispose path
                // completes this barrier. It is state coordination rather
                // than wire/relay I/O, so do not silently drop a refresh at
                // an arbitrary relay timeout.
                await joinCompletion;
            }

            Task rosterPublication = Task.CompletedTask;
            var published = false;
            if (_pvpUdpRelay == null)
            {
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (!IsExactRoomSnapshotCurrentUnderGate(
                            session,
                            room,
                            members))
                    {
                        return;
                    }
                    rosterPublication =
                        PublishPvpPeerRosters(
                            room,
                            members,
                            relaySnapshot: null);
                    published = true;
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
            }
            else
            {
                var relay =
                    await TrySyncPvpRelayGenerationAsync(
                        room,
                        members,
                        members,
                        "endpoint-refresh",
                        resetOwnerUserId:
                            session.Player.UserId,
                        requireExactRevision: false);
                if (!relay.GenerationCurrent)
                {
                    return;
                }
                if (!relay.Success)
                {
                    session.Close();
                    return;
                }

                var staleMembership = false;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (IsExactRoomSnapshotCurrentUnderGate(
                            session,
                            room,
                            members))
                    {
                        rosterPublication =
                            PublishPvpPeerRosters(
                                room,
                                members,
                                relay.Snapshot);
                        published = true;
                    }
                    else if (
                        TryGetSameMembershipCurrentRoomUnderGate(
                            session,
                            room,
                            members,
                            out var currentRoom,
                            out var currentMembers))
                    {
                        room = currentRoom;
                        members = currentMembers;
                        rosterPublication =
                            PublishPvpPeerRosters(
                                room,
                                members,
                                relay.Snapshot);
                        published = true;
                    }
                    else
                    {
                        staleMembership = true;
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }

                if (staleMembership)
                {
                    await ReconcilePvpRelayGenerationAsync(
                        room,
                        "endpoint-refresh-stale");
                }
            }

            await rosterPublication;
            if (published)
            {
                FileLogger.Log(
                    "[GameProtocol] PvP UDP endpoint refresh published: " +
                    $"cid={session.Player.CharacterId} " +
                    $"room={room.RoomId} members={members.Count}");
            }
        }

        // Caller holds _roomPublicationGate.
        private bool IsExactRoomSnapshotCurrentUnderGate(
            EnhancedClientSession source,
            FreeDuelRoom expectedRoom,
            IReadOnlyList<EnhancedClientSession> expectedMembers)
        {
            return TryGetSameMembershipCurrentRoomUnderGate(
                       source,
                       expectedRoom,
                       expectedMembers,
                       out var currentRoom,
                       out _)
                   && currentRoom.Revision ==
                   expectedRoom.Revision;
        }

        // Caller holds _roomPublicationGate. A session-ending callback can
        // already have removed an old member from SessionDirectory while it is
        // deliberately waiting for this exact pending join to finish. Validate
        // the committed room generation against the registry's seat identities
        // instead of requiring every pre-existing TCP session to remain
        // discoverable during that narrow hand-off window.
        private bool IsExactRegistrySnapshotCurrentUnderGate(
            EnhancedClientSession source,
            FreeDuelRoom expectedRoom,
            IReadOnlyList<EnhancedClientSession> expectedMembers)
        {
            if (!_characterTransitions.IsCurrent(source) ||
                !_rooms.TryGetRoomForMember(
                    source.Player.CharacterId,
                    source.SessionId,
                    out var currentRoom,
                    out _) ||
                currentRoom.RoomId != expectedRoom.RoomId ||
                currentRoom.OwnerSessionId !=
                    expectedRoom.OwnerSessionId ||
                currentRoom.GenerationId !=
                    expectedRoom.GenerationId ||
                currentRoom.Revision != expectedRoom.Revision)
            {
                return false;
            }

            return PvpRelayLifecycleCoordinator
                .SameRegistryMemberGeneration(
                currentRoom,
                expectedMembers);
        }

        // Caller holds _roomPublicationGate.
        private bool
            TryGetSamePublishedMembershipCurrentRoomUnderGate(
                FreeDuelRoom expectedRoom,
                IReadOnlyList<EnhancedClientSession> expectedMembers,
                out FreeDuelRoom currentRoom,
                out IReadOnlyList<EnhancedClientSession> currentMembers)
        {
            currentRoom = null;
            currentMembers =
                Array.Empty<EnhancedClientSession>();
            if (expectedRoom == null ||
                _pendingRoomJoinSessions.ContainsKey(
                    expectedRoom.RoomId))
            {
                return false;
            }

            currentRoom =
                _rooms.SnapshotForListener(
                        expectedRoom.ListenerPort)
                    .FirstOrDefault(
                        candidate =>
                            candidate.RoomId ==
                            expectedRoom.RoomId);
            if (currentRoom == null ||
                currentRoom.OwnerSessionId !=
                    expectedRoom.OwnerSessionId ||
                currentRoom.GenerationId !=
                    expectedRoom.GenerationId)
            {
                return false;
            }

            try
            {
                currentMembers =
                    GetRoomMemberTargets(currentRoom);
            }
            catch
            {
                return false;
            }
            return currentMembers
                .Select(member => member.SessionId)
                .SequenceEqual(
                    expectedMembers.Select(
                        member => member.SessionId));
        }

        // Caller holds _roomPublicationGate.
        private bool TryGetSameMembershipCurrentRoomUnderGate(
            EnhancedClientSession source,
            FreeDuelRoom expectedRoom,
            IReadOnlyList<EnhancedClientSession> expectedMembers,
            out FreeDuelRoom currentRoom,
            out IReadOnlyList<EnhancedClientSession> currentMembers)
        {
            currentRoom = null;
            currentMembers =
                Array.Empty<EnhancedClientSession>();
            if (!_characterTransitions.IsCurrent(source) ||
                !_rooms.TryGetRoomForMember(
                    source.Player.CharacterId,
                    source.SessionId,
                    out currentRoom,
                    out _) ||
                currentRoom.RoomId != expectedRoom.RoomId ||
                currentRoom.OwnerSessionId !=
                    expectedRoom.OwnerSessionId ||
                currentRoom.GenerationId !=
                    expectedRoom.GenerationId)
            {
                return false;
            }

            try
            {
                currentMembers =
                    GetRoomMemberTargets(currentRoom);
            }
            catch
            {
                return false;
            }
            return currentMembers
                .Select(member => member.SessionId)
                .SequenceEqual(
                    expectedMembers.Select(
                        member => member.SessionId));
        }

        private byte[] BuildPvpPeerInfoBody(
            FreeDuelRoom room,
            IReadOnlyList<EnhancedClientSession> members,
            EnhancedClientSession recipient,
            PartyUdpRelay.RoomSnapshot relaySnapshot)
        {
            if (_pvpUdpRelay == null ||
                members == null)
            {
                return PvpPeerInfoBuilder.Build(members);
            }
            if (relaySnapshot == null)
            {
                if (members.Count <= 1 &&
                    (members.Count == 0 ||
                     members[0]?.SessionId ==
                     recipient?.SessionId))
                {
                    return PvpPeerInfoBuilder.Build(members);
                }
                throw new InvalidOperationException(
                    "PvP relay snapshot is required for a multi-member room");
            }
            if (!PvpRelayLifecycleCoordinator.IsSecureSnapshotForRoom(
                    room,
                    relaySnapshot))
            {
                throw new InvalidOperationException(
                    "PvP relay snapshot does not match the secure room " +
                    "generation");
            }

            return PvpPeerInfoBuilder.BuildForRelay(
                members,
                recipient,
                _pvpRelayIpBytes,
                peerUserId =>
                {
                    return relaySnapshot.TryGetPort(
                            recipient.Player.UserId,
                            peerUserId,
                            out var port)
                        ? port
                        : 0;
                });
        }

        // Caller owns _roomPublicationGate, as required by QueueRequired.
        private Task PublishPvpPeerRosters(
            FreeDuelRoom room,
            IReadOnlyList<EnhancedClientSession> members,
            PartyUdpRelay.RoomSnapshot relaySnapshot)
        {
            if (room == null ||
                members == null ||
                members.Count == 0)
            {
                return Task.CompletedTask;
            }

            LogUnreportedUdpEndpoints(
                room,
                members,
                "peer-publication");
            var publications =
                new List<Task>(members.Count);
            foreach (var recipient in members)
            {
                var packet =
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000B,
                        BuildPvpPeerInfoBody(
                            room,
                            members,
                            recipient,
                            relaySnapshot));
                publications.Add(
                    QueueRequired(
                        new[] { recipient },
                        room.ListenerPort,
                        packet));
            }
            return Task.WhenAll(publications);
        }

        // Caller owns _roomPublicationGate, as required by QueueRequired.
        private Task PublishPvpNewcomerPeerRecords(
            FreeDuelRoom room,
            IReadOnlyList<EnhancedClientSession> recipients,
            EnhancedClientSession newcomer,
            PartyUdpRelay.RoomSnapshot relaySnapshot)
        {
            if (room == null ||
                newcomer?.Player == null ||
                recipients == null ||
                recipients.Count == 0)
            {
                return Task.CompletedTask;
            }

            var publications =
                new List<Task>(recipients.Count);
            foreach (var recipient in recipients)
            {
                var packet =
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x000B,
                        BuildPvpPeerInfoBody(
                            room,
                            new[] { newcomer },
                            recipient,
                            relaySnapshot));
                publications.Add(
                    QueueRequired(
                        new[] { recipient },
                        room.ListenerPort,
                        packet));
            }
            return Task.WhenAll(publications);
        }

        private void LogUnreportedUdpEndpoints(
            FreeDuelRoom room,
            IReadOnlyList<EnhancedClientSession> members,
            string phase)
        {
            var missing =
                (members ?? Array.Empty<EnhancedClientSession>())
                    .Where(
                        member =>
                            member?.Player != null &&
                            !member.Player.HasReportedUdpEndpoint)
                    .Select(
                        member => member.Player.UserId)
                    .Distinct()
                    .OrderBy(userId => userId)
                    .ToArray();
            if (missing.Length == 0)
                return;

            Interlocked.Add(
                ref _unreportedUdpPeerPublications,
                missing.Length);
            FileLogger.Log(
                "[GameProtocol] PvP peer endpoint unreported: " +
                $"room={room?.RoomId ?? -1} phase={phase} " +
                $"uids=[{string.Join(",", missing)}] " +
                "wire=zero-ip-port-mtu no-fallback");
        }

        private Task<PvpRelaySyncResult>
            TrySyncPvpRelayGenerationAsync(
                FreeDuelRoom expectedRoom,
                IReadOnlyList<EnhancedClientSession>
                    expectedRegistryMembers,
                IReadOnlyList<EnhancedClientSession>
                    desiredRelayMembers,
                string phase,
                ushort resetOwnerUserId = 0,
                bool requireExactRevision = true,
                long? expectedRegistryRevision = null,
                Guid pendingJoinSessionId = default,
                bool closeOnFailure = true)
        {
            return _pvpRelayLifecycle.TrySyncGenerationAsync(
                expectedRoom,
                expectedRegistryMembers,
                desiredRelayMembers,
                phase,
                resetOwnerUserId,
                requireExactRevision,
                expectedRegistryRevision,
                pendingJoinSessionId,
                closeOnFailure);
        }

        private Task ReconcilePvpRelayGenerationAsync(
            FreeDuelRoom expectedRoom,
            string phase)
        {
            return _pvpRelayLifecycle.ReconcileGenerationAsync(
                expectedRoom,
                phase);
        }

        private Task<bool> ClosePvpRelayRoomAsync(
            FreeDuelRoom expectedRoom,
            string phase)
        {
            return _pvpRelayLifecycle.CloseRoomAsync(
                expectedRoom,
                phase);
        }
        internal async Task HandleSetSeatState(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            Func<Task> deferredCompletion = null;
            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    async () =>
                    {
                        deferredCompletion =
                            await HandleSetSeatStateWithinTransition(
                                session,
                                body);
                    });
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] SET_PVP_SEAT_STATE ignored: " +
                    "session no longer owns the character generation");
                return;
            }

            // An owner exit can fan out to other character generations. Run
            // that second phase only after this owner's transition lease has
            // been released, so no owner->member lock order can deadlock with
            // pair operations that acquire character gates by CID.
            if (deferredCompletion != null)
                await deferredCompletion();
        }

        private async Task<Func<Task>>
            HandleSetSeatStateWithinTransition(
            EnhancedClientSession session,
            byte[] body)
        {
            if (!CanMutateRoomMember(session))
            {
                await SendErrorAsync(
                    session,
                    SetSeatStateCommandType,
                    19);
                return null;
            }
            if (!SetPvpSeatStateRequest.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    "[GameProtocol] SET_PVP_SEAT_STATE rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"body={body?.Length ?? 0}B");
                await SendErrorAsync(
                    session,
                    SetSeatStateCommandType,
                    8);
                return null;
            }

            FreeDuelRoom room = null;
            FreeDuelRoom removedRoom = null;
            byte[] vacatedSeatPacket = null;
            byte[] destroyedRoomPacket = null;
            Task roomPublication = Task.CompletedTask;
            Task ownerPromotionPublication = Task.CompletedTask;
            byte[][] departingLifecyclePackets =
                Array.Empty<byte[]>();
            EnhancedClientSession kickedMember = null;
            Task kickPeerLifecyclePublication = Task.CompletedTask;
            byte errorCode = 0;
            var exitedRoom = false;
            var exitedOwner = false;
            var promotedOwner = false;
            IReadOnlyList<EnhancedClientSession> displacedMembers =
                Array.Empty<EnhancedClientSession>();
            IReadOnlyList<EnhancedClientSession> remainingMembers =
                Array.Empty<EnhancedClientSession>();
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (!CanMutateRoomMember(session) ||
                    !_rooms.TryGetRoomForMember(
                        session.Player.CharacterId,
                        session.SessionId,
                        out var memberRoom,
                        out var memberSeat))
                {
                    errorCode = 19;
                }
                else if (_pendingRoomJoinSessions.ContainsKey(
                             memberRoom.RoomId))
                {
                    errorCode = 22;
                }
                else if (memberRoom.RoomState !=
                         FreeDuelRoom.WaitingRoomState)
                {
                    errorCode = 19;
                }
                else if (request.SeatState ==
                             FreeDuelRoom.ClosedSeatState &&
                         memberRoom.OwnerSessionId ==
                             session.SessionId &&
                         request.Seat != memberSeat &&
                         memberRoom.IsOccupiedSeat(request.Seat))
                {
                    IReadOnlyList<EnhancedClientSession>
                        liveTargets;
                    try
                    {
                        liveTargets =
                            GetRoomMemberTargets(memberRoom);
                    }
                    catch
                    {
                        liveTargets =
                            Array.Empty<EnhancedClientSession>();
                        errorCode = 8;
                    }
                    kickedMember =
                        liveTargets
                            .FirstOrDefault(
                                target =>
                                    target.SessionId ==
                                    memberRoom.GetSeatSessionId(
                                        request.Seat));
                    if (kickedMember == null ||
                        !_rooms.TryRemoveNonOwnerMember(
                            memberRoom.GetSeatCharacterId(request.Seat),
                            memberRoom.GetSeatSessionId(request.Seat),
                            out room,
                            out var kickedSeat,
                            out errorCode) ||
                        kickedSeat != request.Seat)
                    {
                        kickedMember = null;
                        room = null;
                    }
                    else
                    {
                        remainingMembers =
                            liveTargets
                                .Where(
                                    target =>
                                        target.SessionId !=
                                        kickedMember.SessionId)
                                .ToArray();
                        vacatedSeatPacket =
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                SeatStateNotificationType,
                                PvpRoomNotificationBuilder
                                    .BuildSeatStateBody(
                                        room,
                                        request.Seat));
                        kickPeerLifecyclePublication =
                            QueueRequiredToReadyListener(
                                room.ListenerPort,
                                kickedMember.SessionId,
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    UserStateNotificationType,
                                    EnterSelectDungeonStateBuilder
                                        .BuildUserState(
                                            new[]
                                            {
                                                kickedMember.Player.UserId
                                            },
                                            0)),
                                vacatedSeatPacket);
                    }
                }
                else if (request.Seat == memberSeat &&
                          request.SeatState ==
                          FreeDuelRoom.ClosedSeatState)
                {
                    if (memberRoom.OwnerSessionId ==
                        session.SessionId)
                    {
                        IReadOnlyList<EnhancedClientSession>
                            liveTargets;
                        try
                        {
                            liveTargets =
                                GetRoomMemberTargets(memberRoom);
                        }
                        catch
                        {
                            liveTargets =
                                Array.Empty<EnhancedClientSession>();
                            errorCode = 8;
                        }
                        var liveSuccessors =
                            liveTargets
                                .Where(
                                    target =>
                                        target.SessionId !=
                                        session.SessionId)
                                .ToArray();
                        if (errorCode == 0 &&
                            liveSuccessors.Length > 0)
                        {
                            if (_rooms.TryRemoveOwnerAndPromote(
                                    session.Player.CharacterId,
                                    session.SessionId,
                                    out room,
                                    out var promotedVacatedSeat,
                                    out _) &&
                                promotedVacatedSeat == request.Seat)
                            {
                                promotedOwner = true;
                                exitedOwner = true;
                                remainingMembers = liveSuccessors;
                            }
                            else
                            {
                                room = null;
                                errorCode = 8;
                            }
                        }
                        else if (errorCode == 0)
                        {
                            if (!_rooms.TryTakeOwnedRoomForRemoval(
                                    session.Player.CharacterId,
                                    session.SessionId,
                                    out removedRoom))
                            {
                                errorCode = 8;
                            }
                            else
                            {
                                room =
                                    removedRoom.CreateResetSnapshot();
                                exitedOwner = true;
                            }
                        }
                    }
                    else
                    {
                        IReadOnlyList<EnhancedClientSession>
                            liveTargets;
                        try
                        {
                            liveTargets =
                                GetRoomMemberTargets(memberRoom);
                        }
                        catch
                        {
                            liveTargets =
                                Array.Empty<EnhancedClientSession>();
                            errorCode = 8;
                        }
                        if (errorCode != 0 ||
                            !_rooms.TryRemoveNonOwnerMember(
                                session.Player.CharacterId,
                                session.SessionId,
                                out room,
                                out var vacatedSeat,
                                out errorCode) ||
                            vacatedSeat != request.Seat)
                        {
                            room = null;
                        }
                        else
                        {
                            remainingMembers =
                                liveTargets
                                    .Where(
                                        target =>
                                            target.SessionId !=
                                            session.SessionId)
                                    .ToArray();
                        }
                    }

                    if (room != null)
                    {
                        session.Player.UserState = 0;
                        vacatedSeatPacket =
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                SeatStateNotificationType,
                                PvpRoomNotificationBuilder
                                    .BuildSeatStateBody(
                                        room,
                                        request.Seat));
                        destroyedRoomPacket =
                            exitedOwner && !promotedOwner
                                ? GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    RoomStateNotificationType,
                                    PvpRoomNotificationBuilder
                                        .BuildDestroyedRoomStateBody(
                                        room.RoomId))
                                : null;
                        if (promotedOwner)
                        {
                            var returnedStatePacket =
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    UserStateNotificationType,
                                    EnterSelectDungeonStateBuilder
                                        .BuildUserState(
                                            new[]
                                            {
                                                session.Player.UserId
                                            },
                                            0));
                            departingLifecyclePackets =
                                new[]
                                {
                                    vacatedSeatPacket,
                                    GamePacketEnvelopeBuilder.Build(
                                        0x00,
                                        RoomStateNotificationType,
                                        PvpRoomNotificationBuilder
                                            .BuildRoomStateBody(room))
                                };
                            ownerPromotionPublication =
                                QueueRequiredToReadyListener(
                                    room.ListenerPort,
                                    session.SessionId,
                                    returnedStatePacket,
                                    departingLifecyclePackets[0],
                                    departingLifecyclePackets[1]);
                        }
                        else if (!exitedOwner)
                        {
                            var returnedStatePacket =
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    UserStateNotificationType,
                                    EnterSelectDungeonStateBuilder
                                        .BuildUserState(
                                            new[]
                                            {
                                                session.Player.UserId
                                            },
                                            0));
                            departingLifecyclePackets =
                                new[] { vacatedSeatPacket };
                            ownerPromotionPublication =
                                QueueRequiredToReadyListener(
                                    room.ListenerPort,
                                    session.SessionId,
                                    returnedStatePacket,
                                    vacatedSeatPacket);
                        }

                        exitedRoom = true;
                    }
                }
                else if (!_rooms.TrySetSeatState(
                             session.Player.CharacterId,
                             session.SessionId,
                             request.Seat,
                             request.SeatState,
                             out room,
                             out errorCode))
                {
                    room = null;
                }
                else
                {
                    if (room.Revision != memberRoom.Revision)
                    {
                        roomPublication = QueueRequiredToReadyListener(
                            room.ListenerPort,
                            excludeSessionId: null,
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                SeatStateNotificationType,
                                PvpRoomNotificationBuilder
                                    .BuildSeatStateBody(
                                        room,
                                        request.Seat)));
                    }
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            if (kickedMember != null && room != null)
            {
                await kickPeerLifecyclePublication;
                var relay =
                    await TrySyncPvpRelayGenerationAsync(
                        room,
                        remainingMembers,
                        remainingMembers,
                        "member-kick",
                        requireExactRevision: false);
                var kickRelayReady =
                    relay.Success &&
                    relay.GenerationCurrent;
                if (!relay.Success &&
                    relay.GenerationCurrent)
                {
                    FileLogger.Log(
                        "[GameProtocol] PvP member-kick relay " +
                        $"reconcile failed closed: room={room.RoomId}");
                    foreach (var remaining in remainingMembers)
                        remaining.Close();
                }
                else if (!relay.GenerationCurrent)
                {
                    FileLogger.Log(
                        "[GameProtocol] PvP member-kick relay " +
                        $"superseded: room={room.RoomId}");
                }
                Task peerPublication = Task.CompletedTask;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (kickRelayReady &&
                        TryGetSamePublishedMembershipCurrentRoomUnderGate(
                            room,
                            remainingMembers,
                            out var currentRoom,
                            out var currentMembers))
                    {
                        peerPublication =
                            PublishPvpPeerRosters(
                                currentRoom,
                                currentMembers,
                                relay.Snapshot);
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                await peerPublication;
                return () => CompleteKickedMemberExitAsync(
                    kickedMember,
                    room,
                    vacatedSeatPacket);
            }

            if (exitedRoom)
            {
                await ownerPromotionPublication;
                PartyUdpRelay.RoomSnapshot exitRelaySnapshot = null;
                var exitRelayReady = true;
                var ownerRelayClosed = true;
                if (exitedOwner && !promotedOwner)
                {
                    ownerRelayClosed =
                        await ClosePvpRelayRoomAsync(
                        room,
                        "owner-exit");
                }
                else
                {
                    if (AfterMemberRegistryMutationBeforeRelaySyncForTest !=
                        null)
                    {
                        await
                            AfterMemberRegistryMutationBeforeRelaySyncForTest();
                    }
                    var relay =
                        await TrySyncPvpRelayGenerationAsync(
                            room,
                            remainingMembers,
                            remainingMembers,
                            promotedOwner
                                ? "owner-promote-exit"
                                : "member-exit",
                            requireExactRevision: false);
                    if (relay.Success &&
                        relay.GenerationCurrent)
                    {
                        exitRelaySnapshot =
                            relay.Snapshot;
                    }
                    else if (relay.GenerationCurrent)
                    {
                        exitRelayReady = false;
                        FileLogger.Log(
                            "[GameProtocol] PvP member-exit relay " +
                            $"reconcile failed closed: room={room.RoomId}");
                        foreach (var remaining in remainingMembers)
                            remaining.Close();
                    }
                    else
                    {
                        exitRelayReady = false;
                        FileLogger.Log(
                            "[GameProtocol] PvP member-exit relay " +
                            $"superseded: room={room.RoomId}");
                    }
                }

                // Legacy out_from_pvp inserts the player into their saved town
                // area (0x0017/0x0018) before publishing state 0, the vacated
                // seat, and the reset room. This method still owns only the
                // requesting character's transition gate.
                var sessionReturned =
                    await TryReturnPvpSessionToTownAsync(
                        session,
                        room.ListenerPort,
                        departingLifecyclePackets.Length == 0,
                        room.RoomId,
                        "member-exit");

                if (sessionReturned &&
                    departingLifecyclePackets.Length > 0)
                {
                    // Town navigation has already been published to this
                    // recipient; it no longer consumes the old battle seats.
                    await _requiredPublication.QueueRequired(
                        new[] { session },
                        room.ListenerPort,
                        departingLifecyclePackets);
                }

                if (exitedOwner && !promotedOwner)
                {
                    return () => CompleteOwnerExitAsync(
                        session,
                        room,
                        removedRoom,
                        displacedMembers,
                        vacatedSeatPacket,
                        destroyedRoomPacket,
                        request.Seat,
                        body,
                        sessionReturned,
                        ownerRelayClosed);
                }

                // A non-owner can immediately enter another room after its
                // character gate is released. Queue the vacancy first so that
                // no stale vacancy packet can follow that later entry.
                Task teardownPublication = Task.CompletedTask;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (TryGetSamePublishedMembershipCurrentRoomUnderGate(
                            room,
                            remainingMembers,
                            out var currentRoom,
                            out var currentMembers))
                    {
                        var peerPublication =
                            exitRelayReady
                                ? PublishPvpPeerRosters(
                                    currentRoom,
                                    currentMembers,
                                    exitRelaySnapshot)
                                : Task.CompletedTask;
                        teardownPublication =
                            Task.WhenAll(
                                peerPublication);
                    }
                    else
                    {
                        FileLogger.Log(
                            "[GameProtocol] PvP member-exit stale " +
                            $"publication skipped: room={room.RoomId}");
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                await teardownPublication;

                FileLogger.Log(
                    "[GameProtocol] SET_PVP_SEAT_STATE exit accepted: " +
                    $"cid={session.Player.CharacterId} " +
                    $"room={room.RoomId} seat={request.Seat} " +
                    $"owner={exitedOwner} promoted={promotedOwner} " +
                    $"requestBody={FormatBody(body)} " +
                    $"seatBody={FormatBody(
                        PvpRoomNotificationBuilder.BuildSeatStateBody(
                        room,
                        request.Seat))} " +
                    $"revision={room.Revision}");
                return null;
            }

            if (room == null)
            {
                await SendErrorAsync(
                    session,
                    SetSeatStateCommandType,
                    errorCode);
                return null;
            }

            await roomPublication;

            FileLogger.Log(
                "[GameProtocol] SET_PVP_SEAT_STATE accepted: " +
                $"cid={session.Player.CharacterId} " +
                $"room={room.RoomId} seat={request.Seat} " +
                $"state=0x{request.SeatState:X2} " +
                $"requestBody={FormatBody(body)} " +
                $"seatBody={FormatBody(
                    PvpRoomNotificationBuilder.BuildSeatStateBody(
                        room,
                        request.Seat))} " +
                $"revision={room.Revision}");
            return null;
        }

        private async Task CompleteKickedMemberExitAsync(
            EnhancedClientSession kickedMember,
            FreeDuelRoom room,
            byte[] vacatedSeatPacket)
        {
            await Task.Yield();
            try
            {
                await _characterTransitions.RunIfCurrentAsync(
                    kickedMember,
                    async () =>
                    {
                        if (!await TryReturnPvpSessionToTownAsync(
                                kickedMember,
                                room.ListenerPort,
                                publishToPeers: false,
                                room.RoomId,
                                "member-kick"))
                        {
                            return;
                        }
                        await _requiredPublication.QueueRequired(
                            new[] { kickedMember },
                            room.ListenerPort,
                            vacatedSeatPacket);
                    });
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GameProtocol] PvP member kick return failed: " +
                    $"cid={kickedMember?.Player?.CharacterId ?? 0} " +
                    $"room={room.RoomId} " +
                    $"error={ex.GetType().Name}: {ex.Message}");
                kickedMember?.Close();
            }
        }

        private async Task CompleteOwnerExitAsync(
            EnhancedClientSession owner,
            FreeDuelRoom room,
            FreeDuelRoom removedRoom,
            IReadOnlyList<EnhancedClientSession> displacedMembers,
            byte[] vacatedSeatPacket,
            byte[] destroyedRoomPacket,
            byte requestSeat,
            byte[] requestBody,
            bool ownerReturned,
            bool relayClosed)
        {
            var successfulReturns =
                ownerReturned
                    ? 1
                    : 0;
            try
            {
                foreach (var displaced in displacedMembers)
                {
                    try
                    {
                        var returned = false;
                        var current =
                            await _characterTransitions.RunIfCurrentAsync(
                                displaced,
                                async () =>
                                {
                                    returned =
                                        await TryReturnPvpSessionToTownAsync(
                                            displaced,
                                            room.ListenerPort,
                                            publishToPeers: true,
                                            room.RoomId,
                                            "owner-exit");
                                });
                        if (current && returned)
                            successfulReturns++;
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            "[GameProtocol] PvP owner-exit town return " +
                            $"failed: cid=" +
                            $"{displaced.Player?.CharacterId ?? 0} " +
                            $"room={room.RoomId} " +
                            $"error={ex.GetType().Name}: {ex.Message}");
                        displaced.Close();
                    }
                }

                Task teardownPublication = Task.CompletedTask;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    // The room ID has no wire generation. Keep it retired until
                    // every ready client has either received the teardown or
                    // has been isolated by closing its failed connection.
                    teardownPublication =
                        QueueRequiredToReadyListener(
                            room.ListenerPort,
                            excludeSessionId: null,
                            vacatedSeatPacket,
                            destroyedRoomPacket);
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                await teardownPublication;

                FileLogger.Log(
                    "[GameProtocol] SET_PVP_SEAT_STATE exit accepted: " +
                    $"cid={owner.Player?.CharacterId ?? 0} " +
                    $"room={room.RoomId} seat={requestSeat} owner=True " +
                    $"returned={successfulReturns}/" +
                    $"{displacedMembers.Count + 1} " +
                    $"requestBody={FormatBody(requestBody)} " +
                    $"seatBody={FormatBody(
                        PvpRoomNotificationBuilder.BuildSeatStateBody(
                            room,
                            requestSeat))} " +
                    $"revision={room.Revision}");
            }
            finally
            {
                if (relayClosed)
                {
                    _rooms.ReleaseRemovedRoomId(removedRoom);
                }
                else
                {
                    FileLogger.Log(
                        "[GameProtocol] PvP room id retained after " +
                        $"owner-exit relay close failure: room={room.RoomId}");
                }
            }
        }

        private async Task<bool> TryReturnPvpSessionToTownAsync(
            EnhancedClientSession session,
            int listenerPort,
            bool publishToPeers,
            ushort roomId,
            string phase)
        {
            if (session?.Player == null)
                return false;

            try
            {
                if (!_characterTransitions.IsCurrent(session)
                    || session.Player.CurrentRun != null
                    || _rooms.TryGetRoomForMember(
                        session.Player.CharacterId, session.SessionId, out _, out _))
                {
                    return false;
                }
                session.Player.UserState = 0;
                session.Player.TownPresenceReady = true;
                await PublishTownReturnAsync(session);
                if (!await PublishReturnedPvpStateAsync(
                        session,
                        listenerPort,
                        publishToPeers))
                {
                    throw new InvalidOperationException(
                        "town return state publication was superseded");
                }
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GameProtocol] PvP town return failed: " +
                    $"cid={session.Player.CharacterId} " +
                    $"room={roomId} phase={phase} " +
                    $"error={ex.GetType().Name}: {ex.Message}");
                session.Close();
                return false;
            }
        }

        private async Task PublishTownReturnAsync(
            EnhancedClientSession session)
        {
            var snapshot =
                TownAreaNotificationBuilder.CreateCurrentSnapshot(
                    session.Player);
            using var sendTimeout =
                new CancellationTokenSource(
                    RequiredSendTimeout);

            // TownHandler's shared-arrival delegate publishes the destination
            // roster and announces this player to existing occupants. Normal
            // SET_USER_AREA sends the returning player's own 0x0017 first, so
            // preserve the legacy out_from_pvp order here as well.
            await _sendTownPacket(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    UserAreaNotificationType,
                    TownAreaNotificationBuilder.BuildUserArea(
                        snapshot)),
                sendTimeout.Token);

            if (_announceTownArrivalWithinTransition != null)
            {
                if (!await _announceTownArrivalWithinTransition(
                        session))
                {
                    throw new InvalidOperationException(
                        "town arrival delegate declined publication");
                }

                return;
            }

            if (!_characterTransitions.IsCurrent(session)
                || !TownHandler.IsTownArrivalStateEligible(
                    session.Player))
            {
                throw new InvalidOperationException(
                    "town return generation is no longer current");
            }

            await _sendTownPacket(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0018,
                    TownAreaNotificationBuilder.BuildAreaUsers(
                        snapshot)),
                sendTimeout.Token);
            if (!_characterTransitions.IsCurrent(session)
                || !TownHandler.IsTownArrivalStateEligible(
                    session.Player))
            {
                throw new InvalidOperationException(
                    "town return generation changed during fallback");
            }
            session.Player.TownPresenceReady = true;
        }

        internal async Task HandleSetReadyState(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandleSetReadyStateWithinTransition(
                        session,
                        body));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] SET_PVP_READY_STATE ignored: " +
                    "session no longer owns the character generation");
            }
        }

        private async Task HandleSetReadyStateWithinTransition(
            EnhancedClientSession session,
            byte[] body)
        {
            if (!CanMutateRoomMember(session))
            {
                await SendErrorAsync(
                    session,
                    SetReadyStateCommandType,
                    19);
                return;
            }
            if (!SetPvpReadyStateRequest.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    "[GameProtocol] SET_PVP_READY_STATE rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"body={body?.Length ?? 0}B");
                await SendErrorAsync(
                    session,
                    SetReadyStateCommandType,
                    8);
                return;
            }

            // Readiness is also a native result-scene exit signal when the
            // server ACK watchdog has already returned the room to waiting.
            await FlushDeferredPvpDeparturesAsync(session);
            FreeDuelRoom room = null;
            byte seat = byte.MaxValue;
            var started = false;
            var silentlyConsumed = false;
            byte errorCode = 0;
            Task publication = Task.CompletedTask;
            IReadOnlyList<EnhancedClientSession> members =
                Array.Empty<EnhancedClientSession>();
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (!CanMutateRoomMember(session) ||
                    !_rooms.TryGetRoomForMember(
                        session.Player.CharacterId,
                        session.SessionId,
                        out var currentRoom,
                        out _) ||
                    _pendingRoomJoinSessions.ContainsKey(
                        currentRoom.RoomId))
                {
                    errorCode = 22;
                }
                else if (currentRoom.BattleMode == 6)
                {
                    // The legacy practice-room path consumes CMD 0x0035
                    // before set_ready_state and emits neither an error nor
                    // a readiness publication.
                    silentlyConsumed = true;
                }
                else
                {
                    var ownerFalse =
                        currentRoom.OwnerSessionId ==
                            session.SessionId &&
                        !request.IsReady;
                    if (!ownerFalse)
                    {
                        try
                        {
                            // SessionDirectory removes a disconnecting
                            // generation before SessionEnding can acquire the
                            // room gate. Resolve every exact target before the
                            // registry commit so a disappearing peer cannot
                            // leave state=2 committed without queued start
                            // publications.
                            members =
                                GetRoomMemberTargets(
                                    currentRoom);
                        }
                        catch
                        {
                            errorCode = 22;
                        }
                    }

                    if (errorCode == 0 &&
                        !_rooms.TrySetReadyState(
                            session.Player.CharacterId,
                            session.SessionId,
                            request.IsReady,
                            out room,
                            out seat,
                            out started,
                            out errorCode))
                    {
                        room = null;
                    }
                    else if (room != null && !ownerFalse)
                    {
                        var readyPacket =
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                ReadyStateNotificationType,
                                PvpRoomNotificationBuilder
                                    .BuildReadyStateBody(
                                        seat,
                                        request.IsReady));
                        if (!started)
                        {
                            if (seat == room.ManagerSeat &&
                                request.IsReady)
                            {
                                var memberSessionIds =
                                    new HashSet<Guid>(
                                        members.Select(
                                            member =>
                                                member.SessionId));
                                var roomStatePacket =
                                    GamePacketEnvelopeBuilder.Build(
                                        0x00,
                                        RoomStateNotificationType,
                                        PvpRoomNotificationBuilder
                                            .BuildRoomStateBody(room));
                                publication =
                                    Task.WhenAll(
                                        QueueRequired(
                                            members,
                                            room.ListenerPort,
                                            readyPacket,
                                            roomStatePacket),
                                        QueueRequired(
                                            GetPublicationListenerTargets(
                                                    room.ListenerPort)
                                                .Where(
                                                    target =>
                                                        !memberSessionIds
                                                            .Contains(
                                                                target
                                                                    .SessionId))
                                                .ToArray(),
                                            room.ListenerPort,
                                            roomStatePacket));
                            }
                            else
                            {
                                publication =
                                    QueueRequired(
                                        members,
                                        room.ListenerPort,
                                        readyPacket);
                            }
                        }
                        else
                        {
                            var memberSessionIds =
                                new HashSet<Guid>(
                                    members.Select(
                                        member =>
                                            member.SessionId));
                            var roomStatePacket =
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    RoomStateNotificationType,
                                    PvpRoomNotificationBuilder
                                        .BuildRoomStateBody(room));
                            var memberPublication =
                                QueueRequired(
                                    members,
                                    room.ListenerPort,
                                    readyPacket,
                                    GamePacketEnvelopeBuilder.Build(
                                        0x00,
                                        StartPvpNotificationType,
                                        PvpRoomNotificationBuilder
                                            .BuildStartPvpBody(room)),
                                    roomStatePacket);
                            var observerPublication =
                                QueueRequired(
                                    GetPublicationListenerTargets(
                                        room.ListenerPort)
                                        .Where(
                                            target =>
                                                !memberSessionIds.Contains(
                                                    target.SessionId))
                                        .ToArray(),
                                    room.ListenerPort,
                                    roomStatePacket);
                            publication =
                                Task.WhenAll(
                                    memberPublication,
                                    observerPublication);
                        }
                    }
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            if (silentlyConsumed)
            {
                FileLogger.Log(
                    "[GameProtocol] SET_PVP_READY_STATE consumed: " +
                    $"cid={session.Player.CharacterId} " +
                    "practice-room mode has no normal-match start");
                return;
            }

            if (room == null)
            {
                await SendErrorAsync(
                    session,
                    SetReadyStateCommandType,
                    errorCode == 0
                        ? (byte)19
                        : errorCode);
                return;
            }

            await publication;
            if (started && room.BattleMode == 3)
            {
                ScheduleRelayBattleTurn(
                    room,
                    _relayBattleStartDelay);
            }
            FileLogger.Log(
                "[GameProtocol] SET_PVP_READY_STATE accepted: " +
                $"cid={session.Player.CharacterId} " +
                $"room={room.RoomId} seat={seat} " +
                $"ready={request.IsReady} started={started} " +
                $"map={room.SelectedMapIndex} " +
                $"revision={room.Revision}");
        }

        internal async Task HandleCompleteLoadPvp(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (body != null && body.Length != 0)
            {
                FileLogger.Log(
                    "[GameProtocol] COMPLETE_LOAD_PVP ignored: " +
                    $"expected empty body, received {body.Length}B");
                return;
            }

            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => ConsumeNormalMatchSignalAsync(
                        session,
                        "COMPLETE_LOAD_PVP",
                        detail: null));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] COMPLETE_LOAD_PVP ignored: " +
                    "session no longer owns the character generation");
            }
        }

        internal async Task HandleConnectP2pPvp(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!ConnectP2pPvpRequest.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    "[GameProtocol] CONNECT_P2P_PVP ignored: " +
                    $"invalid body={body?.Length ?? 0}B");
                return;
            }

            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => ConsumeNormalMatchSignalAsync(
                        session,
                        "CONNECT_P2P_PVP",
                        $"peers={request.Count}"));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] CONNECT_P2P_PVP ignored: " +
                    "session no longer owns the character generation");
            }
        }

        internal async Task HandleDiePvpCharacter(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!DiePvpCharacterRequest.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    "[GameProtocol] DIE_PVP_CHARACTER ignored: " +
                    $"invalid body={body?.Length ?? 0}B");
                return;
            }

            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandleDiePvpCharacterWithinTransition(
                        session,
                        request,
                        body));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] DIE_PVP_CHARACTER ignored: " +
                    "session no longer owns the character generation");
            }
        }

        internal async Task HandlePvpRequestFight(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!PvpRequestFightRequest.TryParse(
                    body,
                    out _))
            {
                FileLogger.Log(
                    "[GameProtocol] PVP_REQUEST_FIGHT ignored: " +
                    $"invalid body={body?.Length ?? 0}B");
                return;
            }

            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandlePvpRequestFightWithinTransition(
                        session));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] PVP_REQUEST_FIGHT ignored: " +
                    "session no longer owns the character generation");
            }
        }

        private async Task HandlePvpRequestFightWithinTransition(
            EnhancedClientSession session)
        {
            Task publication = Task.CompletedTask;
            FreeDuelRoom room = null;
            byte seat = byte.MaxValue;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (session?.Player == null ||
                    !_rooms.TryGetRoomForMember(
                        session.Player.CharacterId,
                        session.SessionId,
                        out room,
                        out seat) ||
                    room.RoomState !=
                        FreeDuelRoom.StartedRoomState ||
                    room.BattleMode != 3 ||
                    room.IsObserverSeat(seat))
                {
                    FileLogger.Log(
                        "[GameProtocol] PVP_REQUEST_FIGHT ignored: " +
                        $"cid={session?.Player?.CharacterId ?? 0}");
                    return;
                }

                // Native CRelayBattleMgr::OnRequestFight toggles its internal
                // request bit, then broadcasts NOTI 0x0071 with the seat.
                // The client performs the visible toggle from that notice.
                publication =
                    QueueRequired(
                        GetRoomMemberTargets(
                            room,
                            skipMissing: true),
                        room.ListenerPort,
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            PvpRequestFightNotificationType,
                            PvpRoomNotificationBuilder
                                .BuildRelayRequestFightBody(
                                    seat)));
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            await publication;
            FileLogger.Log(
                "[GameProtocol] PVP_REQUEST_FIGHT accepted: " +
                $"cid={session.Player.CharacterId} " +
                $"room={room.RoomId} seat={seat}");
        }

        private async Task HandleDiePvpCharacterWithinTransition(
            EnhancedClientSession session,
            DiePvpCharacterRequest request,
            byte[] body)
        {
            Task publication = Task.CompletedTask;
            FreeDuelRoom terminalRoom = null;
            FreeDuelRoom relayTurnRoom = null;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (session?.Player == null ||
                    session.Player.UserState != PvpUserState ||
                    !_rooms.TryReportDeath(
                        session.Player.CharacterId,
                        session.SessionId,
                        request.ReportedDeadUserId,
                        out var room,
                        out var deadSeat,
                        out var killerSeat,
                        out var terminal,
                        request.ReportedKillerUserId))
                {
                    FileLogger.Log(
                        "[GameProtocol] DIE_PVP_CHARACTER ignored: " +
                        $"cid={session?.Player?.CharacterId ?? 0} " +
                        $"reportedDeadUid={request.ReportedDeadUserId} " +
                        $"body={FormatBody(body)}");
                    return;
                }

                var packets = new List<byte[]>
                {
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        DiePvpCharacterNotificationType,
                        PvpRoomNotificationBuilder.BuildDeathBody(
                            deadSeat,
                            killerSeat == byte.MaxValue
                                ? -1
                                : killerSeat))
                };
                if (terminal)
                {
                    packets.Add(
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            RequestPvpRankNotificationType,
                            PvpRoomNotificationBuilder
                                .BuildRankRequestBody()));
                    terminalRoom = room;
                }
                else if (room.BattleMode == 3)
                {
                    relayTurnRoom = room;
                }

                publication =
                    QueueRequired(
                        GetRoomMemberTargets(
                            room,
                            skipMissing: true),
                        room.ListenerPort,
                        packets.ToArray());
                FileLogger.Log(
                    "[GameProtocol] DIE_PVP_CHARACTER accepted: " +
                    $"cid={session.Player.CharacterId} " +
                    $"room={room.RoomId} deadSeat={deadSeat} " +
                    $"killerSeat={killerSeat} terminal={terminal} " +
                    $"reportedDeadUid={request.ReportedDeadUserId} " +
                    $"body={FormatBody(body)}");
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            await publication;
            if (terminalRoom != null)
                ScheduleRankSettlementTimeout(terminalRoom);
            else if (relayTurnRoom != null)
                ScheduleRelayBattleTurn(
                    relayTurnRoom,
                    _relayBattleTurnDelay);
        }

        internal async Task HandlePvpRankResponse(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!PvpRankResponseRequest.TryParse(
                    body,
                    out _))
            {
                FileLogger.Log(
                    "[GameProtocol] RES_PVP_RANK ignored: " +
                    $"invalid body={body?.Length ?? 0}B");
                return;
            }

            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandlePvpRankResponseWithinTransition(
                        session));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] RES_PVP_RANK ignored: " +
                    "session no longer owns the character generation");
            }
        }

        private async Task HandlePvpRankResponseWithinTransition(
            EnhancedClientSession session)
        {
            Task publication = Task.CompletedTask;
            FreeDuelRoom awaitingEndRoom = null;
            PvpMatchSettlement settlement = null;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (session?.Player == null ||
                    !_rooms.TryAcknowledgeRank(
                        session.Player.CharacterId,
                        session.SessionId,
                        out var room,
                        out var completed,
                        completedRoom => settlement = SettleMatch(completedRoom)))
                {
                    FileLogger.Log(
                        "[GameProtocol] RES_PVP_RANK ignored: " +
                        $"cid={session?.Player?.CharacterId ?? 0}");
                    return;
                }

                if (completed)
                {
                    awaitingEndRoom = room;
                    publication = QueueEndPvpResult(
                        room, settlement);
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            await publication;
            if (awaitingEndRoom != null)
                ScheduleEndSettlementTimeout(awaitingEndRoom);
        }

        internal async Task HandleEndPvpResult(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!EndPvpResultRequest.TryParse(
                    body,
                    out _))
            {
                FileLogger.Log(
                    "[GameProtocol] END_PVP_RESULT ignored: " +
                    $"invalid body={body?.Length ?? 0}B");
                return;
            }

            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandleEndPvpResultWithinTransition(
                        session));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] END_PVP_RESULT ignored: " +
                    "session no longer owns the character generation");
            }
        }

        private async Task HandleEndPvpResultWithinTransition(
            EnhancedClientSession session)
        {
            await FlushDeferredPvpDeparturesAsync(session);
            Task publication = Task.CompletedTask;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (session?.Player == null ||
                    !_rooms.TryAcknowledgeEnd(
                        session.Player.CharacterId,
                        session.SessionId,
                        out var room,
                        out var completed))
                {
                    FileLogger.Log(
                        "[GameProtocol] END_PVP_RESULT ignored: " +
                        $"cid={session?.Player?.CharacterId ?? 0}");
                    return;
                }

                if (completed)
                {
                    publication =
                        QueueRequired(
                            GetRoomMemberTargets(
                                room,
                                skipMissing: true),
                            room.ListenerPort,
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                RoomStateNotificationType,
                                PvpRoomNotificationBuilder
                                    .BuildRoomStateBody(room)));
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            await publication;
        }

        internal Task HandlePvpTimeOut(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!PvpTimeOutRequest.TryParse(
                    body,
                    out _))
            {
                FileLogger.Log(
                    "[GameProtocol] PVP_TIME_OUT ignored: " +
                    $"unknown body={body?.Length ?? 0}B");
                return Task.CompletedTask;
            }

            // The native client report contains eight untrusted i32 values.
            // Do not let those client-owned values end a match unilaterally.
            FileLogger.Log(
                "[GameProtocol] PVP_TIME_OUT ignored fail-closed: " +
                $"cid={session?.Player?.CharacterId ?? 0}");
            return Task.CompletedTask;
        }

        private void ScheduleRelayBattleTurn(
            FreeDuelRoom room,
            TimeSpan delay)
        {
            ScheduleBackgroundWork(
                () => PublishRelayBattleTurnAfterDelayAsync(
                    room.RoomId,
                    room.GenerationId,
                    room.MatchGeneration,
                    room.ListenerPort,
                    delay),
                "relay-turn " +
                $"room={room.RoomId} " +
                $"matchGeneration={room.MatchGeneration}");
        }

        private async Task PublishRelayBattleTurnAfterDelayAsync(
            ushort roomId,
            Guid generationId,
            long matchGeneration,
            int listenerPort,
            TimeSpan delay)
        {
            await Task.Delay(
                delay,
                _lifetimeCancellation.Token);
            if (_disposed)
                return;

            Task publication = Task.CompletedTask;
            FreeDuelRoom room = null;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (_disposed)
                    return;

                room =
                    _rooms.SnapshotForListener(listenerPort)
                        .FirstOrDefault(
                            candidate =>
                                candidate.RoomId == roomId &&
                                candidate.GenerationId == generationId &&
                                candidate.MatchGeneration ==
                                    matchGeneration &&
                                candidate.RoomState ==
                                    FreeDuelRoom.StartedRoomState &&
                                candidate.SettlementPhase == FreeDuelRoom.CombatSettlementPhase &&
                                candidate.BattleMode == 3);
                if (room == null)
                    return;

                publication =
                    QueueRequired(
                        GetRoomMemberTargets(
                            room,
                            skipMissing: true),
                        room.ListenerPort,
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            PvpTurnPlayerNotificationType,
                            PvpRoomNotificationBuilder
                                .BuildRelayTurnBody(room)));
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            await publication;
            FileLogger.Log(
                "[GameProtocol] PVP_TURN_PLAYER sent: " +
                $"room={room.RoomId} " +
                $"matchGeneration={room.MatchGeneration}");
        }

        private void ScheduleRankSettlementTimeout(
            FreeDuelRoom room)
        {
            ScheduleBackgroundWork(
                () => CompleteRankSettlementAfterTimeoutAsync(
                    room.RoomId,
                    room.GenerationId,
                    room.MatchGeneration),
                "rank-settlement " +
                $"room={room.RoomId} " +
                $"matchGeneration={room.MatchGeneration}");
        }

        private async Task CompleteRankSettlementAfterTimeoutAsync(
            ushort roomId,
            Guid generationId,
            long matchGeneration)
        {
            await Task.Delay(
                _settlementAckTimeout,
                _lifetimeCancellation.Token);
            if (_disposed)
                return;

            Task publication = Task.CompletedTask;
            FreeDuelRoom awaitingEndRoom = null;
            PvpMatchSettlement settlement = null;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (_disposed)
                    return;

                if (_rooms.TryForceRankSettlement(
                        roomId,
                        generationId,
                        matchGeneration,
                        out var room,
                        completedRoom => settlement = SettleMatch(completedRoom)))
                {
                    awaitingEndRoom = room;
                    publication = QueueEndPvpResult(
                        room, settlement);
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            await publication;
            if (awaitingEndRoom != null)
                ScheduleEndSettlementTimeout(awaitingEndRoom);
        }

        private void ScheduleEndSettlementTimeout(
            FreeDuelRoom room)
        {
            ScheduleBackgroundWork(
                () => CompleteEndSettlementAfterTimeoutAsync(
                    room.RoomId,
                    room.GenerationId,
                    room.MatchGeneration),
                "end-settlement " +
                $"room={room.RoomId} " +
                $"matchGeneration={room.MatchGeneration}");
        }

        private async Task CompleteEndSettlementAfterTimeoutAsync(
            ushort roomId,
            Guid generationId,
            long matchGeneration)
        {
            await Task.Delay(
                _settlementAckTimeout,
                _lifetimeCancellation.Token);
            if (_disposed)
                return;

            Task publication = Task.CompletedTask;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (_disposed)
                    return;

                if (_rooms.TryForceEndSettlement(
                        roomId,
                        generationId,
                        matchGeneration,
                        out var room))
                {
                    publication =
                        QueueRequired(
                            GetRoomMemberTargets(
                                room,
                                skipMissing: true),
                            room.ListenerPort,
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                RoomStateNotificationType,
                                PvpRoomNotificationBuilder
                                    .BuildRoomStateBody(room)));
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            await publication;
        }

        private void ScheduleBackgroundWork(
            Func<Task> work,
            string operation)
        {
            if (_disposed)
                return;

            Interlocked.Increment(ref _pendingScheduledWork);
            _ = ObserveBackgroundWorkAsync(work, operation);
        }

        private async Task ObserveBackgroundWorkAsync(
            Func<Task> work,
            string operation)
        {
            try
            {
                await work();
            }
            catch (OperationCanceledException)
                when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GameProtocol] PvP scheduled work failed: " +
                    $"operation={operation} " +
                    $"error={ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingScheduledWork);
            }
        }

        private async Task ConsumeNormalMatchSignalAsync(
            EnhancedClientSession session,
            string signal,
            string detail)
        {
            FreeDuelRoom room = null;
            byte seat = byte.MaxValue;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (!CanMutateRoomMember(session) ||
                    !_rooms.TryGetRoomForMember(
                        session.Player.CharacterId,
                        session.SessionId,
                        out room,
                        out seat) ||
                    room.RoomState !=
                        FreeDuelRoom.StartedRoomState ||
                    room.MatchingType != 0 ||
                    _pendingRoomJoinSessions.ContainsKey(
                        room.RoomId))
                {
                    room = null;
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            if (room == null)
            {
                FileLogger.Log(
                    $"[GameProtocol] {signal} ignored: " +
                    "session is not in a started normal-match room");
                return;
            }

            // CNormalMatch inherits the legacy IMatch no-op callbacks for
            // these commands. Consume them without fabricating NOTI 0x0119
            // or 0x011A; those replies belong to fair/league matchmaking.
            FileLogger.Log(
                $"[GameProtocol] {signal} consumed: " +
                $"cid={session.Player.CharacterId} " +
                $"room={room.RoomId} seat={seat}" +
                (string.IsNullOrEmpty(detail)
                    ? string.Empty
                    : $" {detail}"));
        }

        internal async Task HandleSetTeamMode(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var ran =
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandleSetTeamModeWithinTransition(
                        session,
                        body));
            if (!ran)
            {
                FileLogger.Log(
                    "[GameProtocol] SET_PVP_TEAM_MODE ignored: " +
                    "session no longer owns the character generation");
            }
        }

        private async Task HandleSetTeamModeWithinTransition(
            EnhancedClientSession session,
            byte[] body)
        {
            if (!CanMutateOwnedRoom(session))
            {
                await SendErrorAsync(
                    session,
                    SetTeamModeCommandType,
                    19);
                return;
            }
            if (!SetPvpTeamModeRequest.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    "[GameProtocol] SET_PVP_TEAM_MODE rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"body={body?.Length ?? 0}B");
                await SendErrorAsync(
                    session,
                    SetTeamModeCommandType,
                    8);
                return;
            }

            FreeDuelRoom room = null;
            Task roomPublication = Task.CompletedTask;
            byte errorCode = 0;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (!CanMutateOwnedRoom(session) ||
                    !_rooms.TryGetRoomForMember(
                        session.Player.CharacterId,
                        session.SessionId,
                        out var currentRoom,
                        out _) ||
                    _pendingRoomJoinSessions.ContainsKey(
                        currentRoom.RoomId))
                {
                    errorCode = 22;
                    room = null;
                }
                else if (
                    !_rooms.TrySetBattleMode(
                        session.Player.CharacterId,
                        session.SessionId,
                        request.BattleMode,
                        out room,
                        out errorCode))
                {
                    room = null;
                    if (errorCode == 0)
                        errorCode = 19;
                }
                else
                {
                    // Publish the mode transition as ordered single-seat
                    // PVP_SEAT_STATE deltas, followed by the room snapshot.
                    var modePackets = new List<byte[]>();
                    for (var seat = 0;
                         seat < FreeDuelRoom.SeatCount;
                         seat++)
                    {
                        if (!room.IsOccupiedSeat(seat))
                            continue;

                        modePackets.Add(
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                SeatStateNotificationType,
                                PvpRoomNotificationBuilder
                                    .BuildSeatStateBody(room, seat)));
                    }
                    roomPublication = QueueRequiredToReadyListener(
                        room.ListenerPort,
                        excludeSessionId: null,
                        modePackets.ToArray());
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            if (room == null)
            {
                await SendErrorAsync(
                    session,
                    SetTeamModeCommandType,
                    errorCode);
                return;
            }

            await roomPublication;

            FileLogger.Log(
                "[GameProtocol] SET_PVP_TEAM_MODE accepted: " +
                $"cid={session.Player.CharacterId} " +
                $"room={room.RoomId} mode={request.BattleMode} " +
                $"revision={room.Revision}");
        }

        internal async Task HandleLobbyReadyAsync(
            EnhancedClientSession session)
        {
            if (!CanPublishLobbySnapshot(session))
                return;

            byte[] newcomerBasicInfo = null;
            byte[] identityRosterPacket = null;
            byte[] roomSnapshotBody = null;
            byte[] roomSnapshotPacket = null;
            int roomCount = 0;
            Task precedingPublication = Task.CompletedTask;
            TaskCompletionSource<bool> directHandshakeBarrier = null;
            Task newcomerPublication = Task.CompletedTask;
            ExceptionDispatchInfo lobbyFailure = null;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (!CanPublishLobbySnapshot(session))
                    return;

                var existingTargets =
                    GetReadyListenerTargets(
                        session.ListenerPort,
                        session.SessionId);
                var rooms =
                    SnapshotPublishedRoomsForListener(
                        session.ListenerPort);
                newcomerBasicInfo =
                    AppearanceService.BuildNoti2Body(
                        session.Player,
                        _database);
                var basicInfoBodies =
                    new List<byte[]>(
                        existingTargets.Count
                        + rooms.Count
                        + 1);
                foreach (var existing in existingTargets)
                {
                    var existingBasicInfo =
                        AppearanceService.BuildNoti2Body(
                            existing.Player,
                            _database);
                    _basicInfoBySession[
                        existing.SessionId] =
                            existingBasicInfo;
                    basicInfoBodies.Add(existingBasicInfo);
                }
                foreach (var room in rooms)
                {
                    if (_basicInfoBySession.TryGetValue(
                            room.OwnerSessionId,
                            out var ownerBasicInfo))
                    {
                        basicInfoBodies.Add(ownerBasicInfo);
                    }
                }
                basicInfoBodies.Add(newcomerBasicInfo);

                roomSnapshotBody =
                    PvpRoomNotificationBuilder
                        .BuildRoomInfoBody(rooms);
                roomSnapshotPacket =
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        RoomInfoNotificationType,
                        roomSnapshotBody);
                identityRosterPacket =
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        UserInfoNotificationType,
                        BuildBasicInfoRosterBody(
                            basicInfoBodies));
                roomCount = rooms.Count;

                _pendingLobbyReadySessions[
                    session.SessionId] = 0;
                ReserveDirectHandshakeUnderGate(
                    session,
                    out precedingPublication,
                    out directHandshakeBarrier);
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            try
            {
                using var lobbyHandshakeTimeout =
                    new CancellationTokenSource(
                        _requiredPublication.DirectHandshakeTimeout);

                await precedingPublication.WaitAsync(
                    lobbyHandshakeTimeout.Token);

                await SendRequiredSequenceAsync(
                    session,
                    new[]
                    {
                        identityRosterPacket,
                        roomSnapshotPacket
                    },
                    lobbyHandshakeTimeout.Token);

                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (!_pendingLobbyReadySessions.TryRemove(
                            session.SessionId,
                            out _) ||
                        !CanCompleteLobbySnapshot(session))
                    {
                        throw new InvalidOperationException(
                            "PvP lobby generation changed during snapshot");
                    }

                    // Publish the newcomer only after its own snapshot has
                    // completed. Pending handshakes remain publication targets
                    // so they receive this identity behind their own barriers.
                    newcomerPublication = QueueRequired(
                        GetPublicationListenerTargets(
                            session.ListenerPort,
                            session.SessionId),
                        session.ListenerPort,
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            UserInfoNotificationType,
                            newcomerBasicInfo));

                    _basicInfoBySession[
                        session.SessionId] =
                            newcomerBasicInfo;
                    _lobbyReadySessions[session.SessionId] = 0;
                }
                finally
                {
                    _roomPublicationGate.Release();
                }

                FileLogger.Log(
                    "[GameProtocol] PVP_ROOM_INFO snapshot sent: " +
                    $"targetCid={session.Player.CharacterId} " +
                    $"targetSession={session.SessionId} " +
                    $"rooms={roomCount} " +
                    $"body={FormatBody(roomSnapshotBody)}");
            }
            catch (Exception ex)
            {
                await _roomPublicationGate.WaitAsync();
                try
                {
                    _pendingLobbyReadySessions.TryRemove(
                        session.SessionId,
                        out _);
                    _basicInfoBySession.TryRemove(
                        session.SessionId,
                        out _);
                    _lobbyReadySessions.TryRemove(
                        session.SessionId,
                        out _);
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                session.Close();
                lobbyFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                CompleteDirectHandshake(
                    session,
                    directHandshakeBarrier);
            }

            if (lobbyFailure != null)
            {
                lobbyFailure.Throw();
                throw new InvalidOperationException(
                    "unreachable PvP lobby snapshot failure path");
            }

            await newcomerPublication;
        }

        private async Task OnSessionEndingAsync(
            int characterId,
            EnhancedClientSession endingSession)
        {
            // An invited/direct join can already be committed in the room
            // registry while its relay/publication phase is still pending.
            // Let that exact generation finish before manager succession so
            // the join rollback path can never strand the promoted newcomer.
            while (true)
            {
                Task pendingOwnerRoomJoin = null;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    if (_rooms.TryGetRoomForMember(
                            characterId,
                            endingSession.SessionId,
                            out var endingRoom,
                            out _) &&
                        _pendingRoomJoinSessions.ContainsKey(
                            endingRoom.RoomId))
                    {
                        _roomAdmission.TryGetPendingJoinCompletion(
                            endingRoom.RoomId,
                            out pendingOwnerRoomJoin);
                    }
                }
                finally
                {
                    _roomPublicationGate.Release();
                }

                if (pendingOwnerRoomJoin == null)
                    break;
                await pendingOwnerRoomJoin;
            }

            _roomAdmission.RemovePendingInvitesForSession(
                endingSession.SessionId);

            FreeDuelRoom room = null;
            var removedRoom = false;
            var removedUnpublishedRoom = false;
            var removedMember = false;
            var promotedManager = false;
            var combatSettled = false;
            var deferCombatDeparture = false;
            var disconnectedCombatant = false;
            var wasLobbyReady = false;
            byte vacatedSeat = byte.MaxValue;
            FreeDuelRoom unpublishedRoom = null;
            FreeDuelRoom disconnectTerminalRoom = null;
            IReadOnlyList<EnhancedClientSession> displacedMembers =
                Array.Empty<EnhancedClientSession>();
            IReadOnlyList<EnhancedClientSession> remainingMembers =
                Array.Empty<EnhancedClientSession>();
            byte[][] teardownPackets = Array.Empty<byte[]>();
            Task lifecyclePublication = Task.CompletedTask;
            await _roomPublicationGate.WaitAsync();
            try
            {
                foreach (var pendingJoin in
                         _roomAdmission.SnapshotPendingJoins())
                {
                    if (pendingJoin.Value ==
                        endingSession.SessionId)
                    {
                        CompletePendingRoomJoin(
                            pendingJoin.Key,
                            endingSession.SessionId);
                    }
                }
                _pendingLobbyReadySessions.TryRemove(
                    endingSession.SessionId,
                    out _);
                var roomWasPending =
                    _pendingRoomOwnerSessions.TryRemove(
                        endingSession.SessionId,
                        out _);
                wasLobbyReady =
                    _lobbyReadySessions.TryRemove(
                        endingSession.SessionId,
                        out _);
                _deferredPvpDepartures.Remove(endingSession.SessionId);
                if (_rooms.TryRemoveEndingSession(
                        characterId,
                        endingSession.SessionId,
                        out var departure))
                {
                    room = departure.Room;
                    deferCombatDeparture = room.RoomState == FreeDuelRoom.StartedRoomState;
                    vacatedSeat = departure.VacatedSeat;
                    disconnectedCombatant =
                        departure.WasActiveCombatant;
                    promotedManager =
                        departure.Kind ==
                        FreeDuelRoomDepartureKind.OwnerPromoted;
                    removedRoom =
                        departure.Kind ==
                        FreeDuelRoomDepartureKind.RoomRemoved;
                    removedMember =
                        departure.Kind ==
                            FreeDuelRoomDepartureKind.MemberRemoved ||
                        promotedManager;
                }
                if (removedRoom && roomWasPending)
                {
                    // No peer has observed this room yet. Reclaim the reserved
                    // ID without publishing a synthetic destroy notification.
                    unpublishedRoom = room;
                    removedRoom = false;
                    removedUnpublishedRoom = true;
                    room = null;
                }
                if (removedMember &&
                    disconnectedCombatant &&
                    room != null &&
                    room.RoomState ==
                    FreeDuelRoom.StartedRoomState)
                {
                    combatSettled =
                        _rooms.TrySettleCombatAfterDisconnect(
                            room.RoomId,
                            room.GenerationId,
                            room.MatchGeneration,
                            out var settledRoom);
                    if (combatSettled)
                    {
                        room = settledRoom;
                        disconnectTerminalRoom = settledRoom;
                    }
                }
                if (removedRoom)
                {
                    // The owner is gone and the room ID is already retired.
                    // Capture exact member generations under the room gate,
                    // then return from the lifecycle callback before acquiring
                    // any member character gates.
                    displacedMembers =
                        GetRoomMemberTargets(
                            room,
                            endingSession.SessionId,
                            skipMissing: true);
                }
                else if (removedMember)
                {
                    remainingMembers =
                        GetRoomMemberTargets(
                            room,
                            skipMissing: true);
                }
                if (!wasLobbyReady &&
                    !removedRoom &&
                    !removedMember &&
                    !removedUnpublishedRoom)
                {
                    return;
                }

                if (!removedRoom)
                {
                    var packets = new List<byte[]>(5);
                    if (removedMember)
                    {
                        FileLogger.Log(
                            (promotedManager
                                ? "[GameProtocol] PVP room owner promoted " +
                                  "on session end: "
                                : "[GameProtocol] PVP room member removed " +
                                  "on session end: ") +
                            $"cid={characterId} room={room.RoomId} " +
                            $"seat={vacatedSeat}");
                        if (disconnectedCombatant && deferCombatDeparture)
                        {
                            var combatPackets = new List<byte[]>
                            {
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    DiePvpCharacterNotificationType,
                                    PvpRoomNotificationBuilder
                                        .BuildDeathBody(
                                            vacatedSeat,
                                            -1))
                            };
                            if (combatSettled)
                            {
                                combatPackets.Add(
                                    GamePacketEnvelopeBuilder.Build(
                                        0x00,
                                        RequestPvpRankNotificationType,
                                        PvpRoomNotificationBuilder.BuildRankRequestBody()));
                            }
                            lifecyclePublication = QueueRequired(
                                remainingMembers,
                                room.ListenerPort,
                                combatPackets.ToArray());
                        }
                        packets.Add(
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                SeatStateNotificationType,
                                PvpRoomNotificationBuilder
                                    .BuildSeatStateBody(
                                        room,
                                        vacatedSeat)));
                        if (promotedManager && !combatSettled)
                        {
                            packets.Add(
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    RoomStateNotificationType,
                                    PvpRoomNotificationBuilder
                                        .BuildRoomStateBody(room)));
                        }
                    }
                    if (wasLobbyReady &&
                        endingSession.Player?.UserId > 0)
                    {
                        packets.Add(
                            TownHandler.BuildUserLeavePacket(
                                endingSession.Player.UserId));
                    }

                    teardownPackets = packets.ToArray();
                    var deferredTargets = deferCombatDeparture && removedMember
                        ? remainingMembers
                        : Array.Empty<EnhancedClientSession>();
                    foreach (var target in deferredTargets)
                    {
                        DeferPvpDepartureUnderGate(target, room, vacatedSeat,
                            endingSession.Player.UserId, endingSession.SessionId);
                    }
                    var deferredIds = deferredTargets.Select(target => target.SessionId).ToHashSet();
                    var immediateTargets = GetReadyListenerTargets(
                            endingSession.ListenerPort, endingSession.SessionId)
                        .Where(target => !deferredIds.Contains(target.SessionId))
                        .ToArray();
                    lifecyclePublication = Task.WhenAll(lifecyclePublication,
                        QueueRequired(immediateTargets, endingSession.ListenerPort, teardownPackets));
                }
            }
            finally
            {
                _basicInfoBySession.TryRemove(
                    endingSession.SessionId,
                    out _);
                _roomPublicationGate.Release();
            }

            if (unpublishedRoom != null)
            {
                var unpublishedRelayClosed =
                    await ClosePvpRelayRoomAsync(
                    unpublishedRoom,
                    "unpublished-owner-disconnect");
                if (unpublishedRelayClosed)
                {
                    _rooms.ReleaseRemovedRoomId(
                        unpublishedRoom);
                }
            }

            if (removedRoom)
            {
                var ownerRelayClosed =
                    await ClosePvpRelayRoomAsync(
                    room,
                    "owner-disconnect");
                FileLogger.Log(
                    "[GameProtocol] PVP room removed on owner session end: " +
                    $"cid={characterId} room={room.RoomId} " +
                    $"members={displacedMembers.Count}");

                // This callback can itself be awaited while the owner
                // character gate is held. The yielded background phase starts
                // only after the callback returns and that gate is released.
                _ = CompleteOwnerDisconnectTeardownAsync(
                    endingSession,
                    room,
                    displacedMembers,
                    wasLobbyReady,
                    ownerRelayClosed);
                return;
            }

            PartyUdpRelay.RoomSnapshot disconnectRelaySnapshot = null;
            var disconnectRelayReady = true;
            if (removedMember)
            {
                if (AfterMemberRegistryMutationBeforeRelaySyncForTest !=
                    null)
                {
                    await
                        AfterMemberRegistryMutationBeforeRelaySyncForTest();
                }
                var relay =
                        await TrySyncPvpRelayGenerationAsync(
                            room,
                            remainingMembers,
                            remainingMembers,
                            promotedManager
                                ? "owner-promote-disconnect"
                                : "member-disconnect",
                        requireExactRevision: false);
                if (relay.Success &&
                    relay.GenerationCurrent)
                {
                    disconnectRelaySnapshot =
                        relay.Snapshot;
                }
                else if (relay.GenerationCurrent)
                {
                    disconnectRelayReady = false;
                    FileLogger.Log(
                        "[GameProtocol] PvP member-disconnect relay " +
                        $"reconcile failed closed: room={room.RoomId}");
                    foreach (var remaining in remainingMembers)
                        remaining.Close();
                }
                else
                {
                    disconnectRelayReady = false;
                    FileLogger.Log(
                        "[GameProtocol] PvP member-disconnect relay " +
                        $"superseded: room={room.RoomId}");
                }
            }

            Task peerTeardownPublication = Task.CompletedTask;
            await _roomPublicationGate.WaitAsync();
            try
            {
                var currentRoom = room;
                var currentMembers =
                    remainingMembers;
                var sameRemainingRoom =
                    !removedMember ||
                    TryGetSamePublishedMembershipCurrentRoomUnderGate(
                        room,
                        remainingMembers,
                        out currentRoom,
                        out currentMembers);
                peerTeardownPublication =
                    removedMember &&
                    disconnectRelayReady &&
                    sameRemainingRoom
                        ? PublishPvpPeerRosters(
                            currentRoom,
                            currentMembers,
                            disconnectRelaySnapshot)
                        : Task.CompletedTask;
            }
            finally
            {
                _roomPublicationGate.Release();
            }
            await Task.WhenAll(
                lifecyclePublication,
                peerTeardownPublication);
            if (disconnectTerminalRoom != null)
                ScheduleRankSettlementTimeout(
                    disconnectTerminalRoom);
            else if (disconnectedCombatant && room?.BattleMode == 3 &&
                     room.SettlementPhase == FreeDuelRoom.CombatSettlementPhase)
                ScheduleRelayBattleTurn(room, _relayBattleTurnDelay);
        }

        private async Task CompleteOwnerDisconnectTeardownAsync(
            EnhancedClientSession endingSession,
            FreeDuelRoom room,
            IReadOnlyList<EnhancedClientSession> displacedMembers,
            bool wasLobbyReady,
            bool relayClosed)
        {
            await Task.Yield();
            try
            {
                var successfulReturns = 0;
                foreach (var displaced in displacedMembers)
                {
                    try
                    {
                        var returned = false;
                        var current =
                            await _characterTransitions.RunIfCurrentAsync(
                                displaced,
                                async () =>
                                {
                                    returned =
                                        await TryReturnPvpSessionToTownAsync(
                                            displaced,
                                            room.ListenerPort,
                                            publishToPeers: true,
                                            room.RoomId,
                                            "owner-disconnect");
                                });
                        if (current && returned)
                            successfulReturns++;
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            "[GameProtocol] PvP owner-disconnect town " +
                            $"return failed: cid=" +
                            $"{displaced.Player?.CharacterId ?? 0} " +
                            $"room={room.RoomId} " +
                            $"error={ex.GetType().Name}: {ex.Message}");
                        displaced.Close();
                    }
                }
                FileLogger.Log(
                    "[GameProtocol] PVP owner-disconnect town returns: " +
                    $"room={room.RoomId} " +
                    $"returned={successfulReturns}/" +
                    $"{displacedMembers.Count}");

                Task teardownPublication = Task.CompletedTask;
                await _roomPublicationGate.WaitAsync();
                try
                {
                    var resetRoom = room.CreateResetSnapshot();
                    var packets = new List<byte[]>(3)
                    {
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            SeatStateNotificationType,
                            PvpRoomNotificationBuilder
                                .BuildSeatStateBody(
                                    resetRoom,
                                    room.ManagerSeat)),
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            RoomStateNotificationType,
                            PvpRoomNotificationBuilder
                                .BuildDestroyedRoomStateBody(
                                    room.RoomId))
                    };
                    if (wasLobbyReady &&
                        endingSession.Player?.UserId > 0)
                    {
                        packets.Add(
                            TownHandler.BuildUserLeavePacket(
                                endingSession.Player.UserId));
                    }

                    teardownPublication =
                        QueueRequiredToReadyListener(
                            endingSession.ListenerPort,
                            endingSession.SessionId,
                            packets.ToArray());
                }
                finally
                {
                    _roomPublicationGate.Release();
                }
                await teardownPublication;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[GameProtocol] PvP owner-disconnect teardown failed: " +
                    $"cid={endingSession.Player?.CharacterId ?? 0} " +
                    $"room={room.RoomId} " +
                    $"error={ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (relayClosed)
                {
                    _rooms.ReleaseRemovedRoomId(room);
                }
                else
                {
                    FileLogger.Log(
                        "[GameProtocol] PvP room id retained after owner " +
                        $"disconnect relay close failure: room={room.RoomId}");
                }
            }
        }

        private async Task<bool> PublishReturnedPvpStateAsync(
            EnhancedClientSession returning,
            int listenerPort,
            bool publishToPeers = true)
        {
            if (returning?.Player == null)
                return false;

            Task statePublication = Task.CompletedTask;
            await _roomPublicationGate.WaitAsync();
            try
            {
                // The caller still owns this character's transition gate.
                // Queue state zero before releasing it, so a later room entry
                // cannot be followed by a stale state-zero publication.
                if (!_characterTransitions.IsCurrent(returning)
                    || !_lobbyReadySessions.ContainsKey(
                        returning.SessionId)
                    || returning.Player.UserState != 0
                    || returning.Player.CurrentRun != null
                    || _rooms.TryGetRoomForMember(
                        returning.Player.CharacterId,
                        returning.SessionId,
                        out _,
                        out _))
                {
                    return false;
                }

                if (publishToPeers)
                {
                    statePublication =
                        QueueRequiredToReadyListener(
                            listenerPort,
                            returning.SessionId,
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                UserStateNotificationType,
                                EnterSelectDungeonStateBuilder
                                    .BuildUserState(
                                        new[] { returning.Player.UserId },
                                        0)));
                }
                // USER_AREA/AREA_USERS were sent before this state commit.
                // A forced kick can exit without an END_PVP_RESULT command.
                if (_deferredPvpDepartures.Remove(returning.SessionId, out var pending))
                {
                    statePublication = Task.WhenAll(statePublication,
                        _requiredPublication.QueueRequired(new[] { returning }, listenerPort,
                            BuildDeferredUserLeavesUnderGate(pending).ToArray()));
                }
            }
            finally
            {
                _roomPublicationGate.Release();
            }

            await statePublication;
            return true;
        }

        private Task QueueRequiredToReadyListener(
            int listenerPort,
            Guid? excludeSessionId,
            params byte[][] packets)
        {
            return QueueRequired(
                GetPublicationListenerTargets(
                    listenerPort,
                    excludeSessionId),
                listenerPort,
                packets);
        }

        // The registry calls this before accepting the final rank transition.
        // A failed transaction keeps that match in its retryable rank phase.
        private PvpMatchSettlement SettleMatch(FreeDuelRoom room)
        {
            // A21's traditional channel records progress. Free practice (13)
            // explicitly excludes wins/losses and EXP (client text 70408).
            var enabled = GameNetworkConfig.TryResolveGameChannel(room.ListenerPort, out var channel)
                && channel.ChannelType == 24;
            return new SqlitePvpRecordRepository(_database).Settle(room, enabled, DateTime.UtcNow);
        }

        // Caller holds _roomPublicationGate; all recipients share one commit.
        private Task QueueEndPvpResult(FreeDuelRoom room, PvpMatchSettlement settlement)
        {
            var publications = new List<Task>();
            foreach (var target in GetRoomMemberTargets(
                         room,
                         skipMissing: true))
            {
                if (!room.TryGetSeatForSession(
                        target.SessionId,
                        out var seat))
                {
                    throw new InvalidOperationException(
                        "PvP result target has no room seat");
                }

                var progress = settlement.Players[target.Player.CharacterId];
                target.Player.PvpGrade = progress.Record.Grade;
                publications.Add(
                    QueueRequired(
                        new[] { target },
                        room.ListenerPort,
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            EndPvpNotificationType,
                            PvpRoomNotificationBuilder
                                .BuildEndPvpBody(room, seat, settlement.Players)),
                        GamePacketEnvelopeBuilder.Build(0x00,
                            (ushort)NotiPacketTypeA21.PVP_RECORD,
                            PvpRecordBodyBuilder.BuildBody(progress.Record))));
            }

            return publications.Count == 0
                ? Task.CompletedTask
                : Task.WhenAll(publications);
        }

        private Task QueueRequired(
            IReadOnlyList<EnhancedClientSession> targets,
            int listenerPort,
            params byte[][] packets)
        {
            // Caller owns _roomPublicationGate. Result-exit drains and ordered
            // post-town projections use the underlying publisher directly.
            var publications = new List<Task>(targets.Count);
            foreach (var target in targets)
            {
                var deliver = FilterDeferredDeparturePacketsUnderGate(target, packets);
                if (deliver.Length != 0)
                    publications.Add(_requiredPublication.QueueRequired(
                        new[] { target }, listenerPort, deliver));
            }
            return Task.WhenAll(publications);
        }

        private IReadOnlyList<EnhancedClientSession>
            GetReadyListenerTargets(
                int listenerPort,
                Guid? excludeSessionId = null)
        {
            return _sessions.GetAllGameSessions()
                .Where(
                    candidate =>
                        candidate?.Player != null &&
                        candidate.Player.CharacterId > 0 &&
                        candidate.Player.UserId > 0 &&
                        candidate.GameSession != null &&
                        candidate.ListenerPort == listenerPort &&
                        _lobbyReadySessions.ContainsKey(
                            candidate.SessionId) &&
                        (!excludeSessionId.HasValue ||
                         candidate.SessionId !=
                             excludeSessionId.Value))
                .OrderBy(
                    candidate =>
                        candidate.Player.UserId)
                .ToArray();
        }

        private IReadOnlyList<EnhancedClientSession>
            GetPublicationListenerTargets(
                int listenerPort,
                Guid? excludeSessionId = null)
        {
            return _sessions.GetAllGameSessions()
                .Where(
                    candidate =>
                        candidate?.Player != null &&
                        candidate.Player.CharacterId > 0 &&
                        candidate.Player.UserId > 0 &&
                        candidate.GameSession != null &&
                        candidate.ListenerPort == listenerPort &&
                        (_lobbyReadySessions.ContainsKey(
                             candidate.SessionId) ||
                         _pendingLobbyReadySessions.ContainsKey(
                             candidate.SessionId)) &&
                        (!excludeSessionId.HasValue ||
                         candidate.SessionId !=
                             excludeSessionId.Value))
                .OrderBy(
                    candidate =>
                        candidate.Player.UserId)
                .ToArray();
        }

        private static byte[] BuildBasicInfoRosterBody(
            IEnumerable<byte[]> singleUserBodies)
        {
            var records =
                new SortedDictionary<ushort, byte[]>();
            foreach (var body in
                     singleUserBodies
                     ?? Enumerable.Empty<byte[]>())
            {
                if (body == null ||
                    body.Length < UserInfoSubtype0Builder.SingleRecordUserIdOffset + sizeof(ushort) ||
                    body[0] != 0 ||
                    BitConverter.ToUInt16(body, 1) != 1)
                {
                    throw new InvalidOperationException(
                        "invalid subtype-0 USERINFO body");
                }

                // A21 13E7340 reads a 38-byte header before each UID.
                var userId = BitConverter.ToUInt16(
                    body, UserInfoSubtype0Builder.SingleRecordUserIdOffset);
                if (userId == 0 ||
                    records.ContainsKey(userId))
                {
                    continue;
                }

                var record = new byte[body.Length - 3];
                Buffer.BlockCopy(
                    body,
                    3,
                    record,
                    0,
                    record.Length);
                records.Add(userId, record);
            }

            if (records.Count > ushort.MaxValue)
                throw new InvalidOperationException(
                    "too many PvP USERINFO records");

            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteUInt16((ushort)records.Count);
            foreach (var record in records.Values)
                writer.WriteBytes(record);
            return writer.ToArray();
        }

        private IReadOnlyList<EnhancedClientSession>
            GetRoomMemberTargets(
                FreeDuelRoom room,
                Guid? excludedSessionId = null,
                bool skipMissing = false)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));

            var bySession =
                _sessions.GetAllGameSessions()
                    .Where(candidate => candidate != null)
                    .ToDictionary(
                        candidate => candidate.SessionId);
            var result =
                new List<EnhancedClientSession>(
                    FreeDuelRoom.SeatCount);
            for (var seat = 0;
                 seat < FreeDuelRoom.SeatCount;
                 seat++)
            {
                if (!room.IsOccupiedSeat(seat))
                    continue;

                var sessionId =
                    room.GetSeatSessionId(seat);
                if (excludedSessionId.HasValue &&
                    sessionId == excludedSessionId.Value)
                {
                    continue;
                }
                if (!bySession.TryGetValue(
                        sessionId,
                        out var member) ||
                    member.Player == null ||
                    member.Player.CharacterId !=
                    room.GetSeatCharacterId(seat) ||
                    member.Player.UserId !=
                    room.GetSeatUserId(seat) ||
                    member.ListenerPort != room.ListenerPort)
                {
                    if (skipMissing)
                    {
                        FileLogger.Log(
                            "[GameProtocol] PvP publication skipped stale " +
                            $"member room={room.RoomId} seat={seat} " +
                            $"session={sessionId}");
                        continue;
                    }
                    throw new InvalidOperationException(
                        $"PvP room {room.RoomId} seat {seat} " +
                        "has no matching live session");
                }
                result.Add(member);
            }

            return result;
        }

        private Task SendRequiredSequenceAsync(
            EnhancedClientSession session,
            IEnumerable<byte[]> packets,
            CancellationToken cancellationToken)
        {
            return _requiredPublication.SendRequiredSequenceAsync(
                session,
                packets,
                cancellationToken);
        }

        // Caller must hold _roomPublicationGate. Replacing the current tail
        // with an incomplete barrier gives a direct handshake exclusive wire
        // order without forcing unrelated mutations to await that client.
        private void ReserveDirectHandshakeUnderGate(
            EnhancedClientSession session,
            out Task precedingPublication,
            out TaskCompletionSource<bool> barrier)
        {
            _requiredPublication.ReserveDirectHandshakeUnderGate(
                session,
                out precedingPublication,
                out barrier);
        }

        private void CompleteDirectHandshake(
            EnhancedClientSession session,
            TaskCompletionSource<bool> barrier)
        {
            _requiredPublication.CompleteDirectHandshake(
                session,
                barrier);
        }

        private void CompletePendingRoomJoin(
            int roomId,
            Guid sessionId)
        {
            _roomAdmission.CompletePendingJoin(
                roomId,
                sessionId);
        }

        private IReadOnlyList<FreeDuelRoom>
            SnapshotPublishedRoomsForListener(
            int listenerPort)
        {
            return _rooms.SnapshotForListener(listenerPort)
                .Where(IsPublishedRoomOwnerCurrentUnderGate)
                .ToArray();
        }

        // Caller holds _roomPublicationGate. SessionDirectory intentionally
        // removes an ending generation before invoking this Handler, so room
        // snapshots and new joins must reject that short stale-owner window.
        private bool IsPublishedRoomOwnerCurrentUnderGate(
            FreeDuelRoom room)
        {
            return room != null &&
                   !_pendingRoomOwnerSessions.ContainsKey(
                       room.OwnerSessionId) &&
                   _lobbyReadySessions.ContainsKey(
                       room.OwnerSessionId) &&
                   _sessions.TryGet(
                       room.OwnerCharacterId,
                       out var owner) &&
                   owner?.Player != null &&
                   owner.SessionId == room.OwnerSessionId &&
                   owner.Player.CharacterId == room.OwnerCharacterId &&
                   owner.Player.UserId == room.OwnerUserId &&
                   owner.ListenerPort == room.ListenerPort;
        }

        private bool IsRoomPendingPublication(
            int listenerPort,
            ushort roomId)
        {
            return _rooms.SnapshotForListener(listenerPort)
                .Any(
                    room =>
                        room.RoomId == roomId &&
                        _pendingRoomOwnerSessions.ContainsKey(
                            room.OwnerSessionId));
        }

        private bool CanEnterRoom(
            EnhancedClientSession session)
        {
            return session?.Account != null
                   && session.Player != null
                   && session.Player.CharacterId > 0
                   && session.Player.UserId > 0
                   && session.GameSession != null
                   && _isFreeDuelAvailable()
                   && GameNetworkConfig.IsPvpListener(
                       session.ListenerPort)
                   && _lobbyReadySessions.ContainsKey(
                       session.SessionId)
                   && !_pendingLobbyReadySessions.ContainsKey(
                       session.SessionId)
                   && session.Player.UserState == 0
                   && session.Player.CurrentRun == null
                   && _sessions.TryGet(
                       session.Player.CharacterId,
                       out var current)
                   && ReferenceEquals(current, session);
        }

        private static string FormatBody(byte[] body)
        {
            return body == null
                ? "null"
                : BitConverter.ToString(body);
        }

        private bool RemovePendingRoomInvite(
            Guid targetSessionId,
            PendingRoomInvite invitation)
        {
            return _roomAdmission.TryRemovePendingInvite(
                targetSessionId,
                invitation);
        }

        private bool CanPublishLobbySnapshot(
            EnhancedClientSession session)
        {
            if (session?.Account == null ||
                session.Player == null ||
                session.Player.CharacterId <= 0 ||
                session.Player.UserId == 0 ||
                session.GameSession == null ||
                !_isFreeDuelAvailable() ||
                !GameNetworkConfig.IsPvpListener(
                    session.ListenerPort) ||
                _lobbyReadySessions.ContainsKey(
                    session.SessionId) ||
                _pendingLobbyReadySessions.ContainsKey(
                    session.SessionId) ||
                session.Player.UserState != 0 ||
                session.Player.CurrentRun != null)
            {
                return false;
            }

            return _sessions.TryGet(
                       session.Player.CharacterId,
                       out var current) &&
                   ReferenceEquals(current, session);
        }

        private bool CanCompleteLobbySnapshot(
            EnhancedClientSession session)
        {
            if (session?.Account == null ||
                session.Player == null ||
                session.Player.CharacterId <= 0 ||
                session.Player.UserId == 0 ||
                session.GameSession == null ||
                !_isFreeDuelAvailable() ||
                !GameNetworkConfig.IsPvpListener(
                    session.ListenerPort) ||
                _lobbyReadySessions.ContainsKey(
                    session.SessionId) ||
                session.Player.UserState != 0 ||
                session.Player.CurrentRun != null)
            {
                return false;
            }

            return _sessions.TryGet(
                       session.Player.CharacterId,
                       out var current) &&
                   ReferenceEquals(current, session);
        }

        private bool CanMutateOwnedRoom(
            EnhancedClientSession session)
        {
            return session?.Account != null
                   && session.Player != null
                   && session.Player.CharacterId > 0
                   && session.Player.UserId > 0
                   && session.GameSession != null
                   && _isFreeDuelAvailable()
                   && GameNetworkConfig.IsPvpListener(
                       session.ListenerPort)
                   && _lobbyReadySessions.ContainsKey(
                       session.SessionId)
                   && session.Player.UserState == PvpUserState
                   && session.Player.CurrentRun == null;
        }

        private bool CanMutateRoomMember(
            EnhancedClientSession session)
        {
            return session?.Account != null
                   && session.Player != null
                   && session.Player.CharacterId > 0
                   && session.Player.UserId > 0
                   && session.GameSession != null
                   && _isFreeDuelAvailable()
                   && GameNetworkConfig.IsPvpListener(
                       session.ListenerPort)
                   && _lobbyReadySessions.ContainsKey(
                       session.SessionId)
                   && session.Player.UserState == PvpUserState
                   && session.Player.CurrentRun == null
                   && _rooms.TryGetRoomForMember(
                       session.Player.CharacterId,
                       session.SessionId,
                       out _,
                       out _);
        }

        private void RollbackUnpublishedRoom(FreeDuelRoom room)
        {
            if (room == null ||
                !_rooms.TryTakeOwnedRoomForRemoval(
                    room.OwnerCharacterId,
                    room.OwnerSessionId,
                    out var unpublished))
            {
                return;
            }

            _rooms.ReleaseRemovedRoomId(unpublished);
        }

        private static Task SendErrorAsync(
            EnhancedClientSession session,
            byte errorCode)
        {
            return SendErrorAsync(
                session,
                MakeRoomCommandType,
                errorCode);
        }

        private static Task SendEnterRoomErrorAsync(
            EnhancedClientSession session,
            byte errorCode,
            bool invited)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                BuildEnterRoomErrorPacket(
                    errorCode,
                    invited));
        }

        private static byte[] BuildEnterRoomErrorPacket(
            byte errorCode,
            bool invited)
        {
            return invited
                ? GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x000B,
                    new byte[] { 0, errorCode, 2 })
                : GamePacketEnvelopeBuilder.Build(
                    0x01,
                    EnterRoomCommandType,
                    new byte[] { 0, errorCode });
        }

        private static Task SendPvpInviteErrorAsync(
            EnhancedClientSession session,
            byte errorCode)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x000B,
                    new byte[] { 0, errorCode, 2 }));
        }

        private static Task SendErrorAsync(
            EnhancedClientSession session,
            ushort commandType,
            byte errorCode)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    commandType,
                    new byte[] { 0, errorCode }));
        }

        private static bool IsFreeDuelAvailable()
        {
            return GameNetworkConfig.FreeDuelListenerEnabled;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposeStarted,
                    1) != 0)
            {
                return;
            }

            _disposed = true;
            _lifetimeCancellation.Cancel();
            _sessions.SessionEnding -= OnSessionEndingAsync;
            _roomAdmission.Dispose();
            _requiredPublication.Dispose();
            _pvpRelayLifecycle.Dispose();
        }
    }
}
