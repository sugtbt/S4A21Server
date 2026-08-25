using DfoServer.Game.Party;
using DfoServer.Game.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// 8.6 SEND_MESSAGE：mode:u8 + targetUid:u16 + targetCharacterId:u32 + message:dstr；
    /// 私聊还可跟 targetName:dstr。
    public sealed class ChatHandler : IDisposable
    {
        private const int MaximumMessageBytes = 256;
        private const byte DirectMessageMode = 1;
        private const byte PartyMessageMode = 2;
        private const byte AreaMessageMode = 3;
        private const byte AlternateDirectMessageMode = 7;
        private const byte OneToOneConversationMode = 45;

        private readonly ISessionDirectory _sessions;
        private readonly PartyManager _parties;
        private readonly object _conversationLock = new object();
        private readonly Dictionary<ulong, uint> _activeConversations =
            new Dictionary<ulong, uint>();
        private uint _nextConversationId = 1;

        public ChatHandler(
            ISessionDirectory sessions,
            PartyManager parties)
        {
            _sessions = sessions
                ?? throw new ArgumentNullException(nameof(sessions));
            _parties = parties
                ?? throw new ArgumentNullException(nameof(parties));
            _sessions.SessionEnding += OnSessionEndingAsync;
        }

        public void Dispose()
        {
            _sessions.SessionEnding -= OnSessionEndingAsync;
        }

        public async Task Handle_SEND_MESSAGE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || !TryParseRequest(body, out var request))
            {
                FileLogger.Log(
                    $"[GameProtocol] SEND_MESSAGE invalid " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"body({body?.Length ?? 0}B): " +
                    $"{(body == null ? "null" : BitConverter.ToString(body))}");
                return;
            }

            var recipients = ResolveRecipients(session, request);
            var sendTasks = new List<Task>(recipients.Count);
            foreach (var recipient in recipients)
            {
                if (request.Mode == OneToOneConversationMode
                    && recipient.SessionId == session.SessionId)
                {
                    // The 86JP conversation window performs local echo. A
                    // second server projection would duplicate the sender's
                    // own line.
                    continue;
                }
                var notificationType = request.Mode == OneToOneConversationMode
                    ? NotiPacketType.MESSAGE_GROUP_CHAT
                    : NotiPacketType.MESSAGE;
                var packet = GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)notificationType,
                    request.Mode == OneToOneConversationMode
                        ? BuildGroupChatNotificationBody(
                            request.ConversationId,
                            session.Player.Name,
                            request.MessageBytes)
                        : BuildNotificationBody(
                            request.Mode,
                            session.Player.UserId,
                            serverGroup: 0,
                            request.MessageBytes));
                sendTasks.Add(recipient.SendPacketAsync(packet));
            }

            if (sendTasks.Count > 0)
                await Task.WhenAll(sendTasks);

            FileLogger.Log(
                $"[GameProtocol] SEND_MESSAGE cid={session.Player.CharacterId} " +
                $"uid={session.Player.UserId} mode={request.Mode} " +
                $"targetUid={request.TargetUniqueId} " +
                $"targetCid={request.TargetCharacterId} " +
                $"messageBytes={request.MessageBytes.Length} " +
                $"recipients={sendTasks.Count}" +
                (request.Mode == OneToOneConversationMode
                    ? $" raw={BitConverter.ToString(body)}"
                    : string.Empty));
        }

        internal IReadOnlyList<EnhancedClientSession> ResolveRecipients(
            EnhancedClientSession sender,
            ChatMessageRequest request)
        {
            var result = new Dictionary<Guid, EnhancedClientSession>();
            AddIfCurrentChannel(result, sender, sender);

            if (IsDirectMessageMode(request.Mode))
            {
                if (request.Mode == OneToOneConversationMode)
                {
                    foreach (var target in FindConversationPeers(
                                 sender,
                                 request.ConversationId))
                        AddIfOnline(result, target);
                }
                else
                {
                    AddIfOnline(result, FindDirectTarget(request));
                }
                return result.Values.ToList();
            }

            if (request.Mode == PartyMessageMode
                || sender.Player.CurrentRun != null)
            {
                var party = _parties.GetPartyByUser(sender.Player.UserId);
                if (party != null)
                {
                    foreach (var member in party.MembersBySlot())
                    {
                        if (_sessions.TryGet(
                                member.CharacterId,
                                out var memberSession))
                        {
                            AddIfCurrentChannel(
                                result,
                                sender,
                                memberSession);
                        }
                    }
                }
                return result.Values.ToList();
            }

            if (request.Mode == AreaMessageMode)
            {
                foreach (var areaSession in _sessions.GetSessionsInArea(
                             sender.Player.CurTownId,
                             sender.Player.CurAreaId,
                             sender.Player.CharacterId,
                             sender.ListenerPort))
                {
                    AddIfCurrentChannel(result, sender, areaSession);
                }
            }

            // Unknown modes deliberately remain sender-only.  Several values
            // are backed by guild/megaphone services and must not become a
            // free cross-channel broadcast merely because their wire shape is
            // shared with ordinary chat.
            return result.Values.ToList();
        }

        private EnhancedClientSession FindDirectTarget(
            ChatMessageRequest request)
        {
            if (request.TargetCharacterId > 0
                && request.TargetCharacterId <= int.MaxValue
                && _sessions.TryGet(
                    (int)request.TargetCharacterId,
                    out var byCharacterId))
            {
                return byCharacterId;
            }

            foreach (var candidate in _sessions.GetAllGameSessions())
            {
                if (candidate?.Player == null)
                    continue;
                if (request.TargetUniqueId != 0
                    && candidate.Player.UserId == request.TargetUniqueId)
                {
                    return candidate;
                }
                if (request.TargetNameBytes.Length > 0
                    && candidate.Player.Name != null
                    && candidate.Player.Name.SequenceEqual(
                        request.TargetNameBytes))
                {
                    return candidate;
                }
            }
            return null;
        }

        public async Task Handle_CREATE_GROUP(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || !TryParseNameArgument(body, out var targetName))
            {
                FileLogger.Log(
                    $"[GameProtocol] CREATE_GROUP invalid " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"body({body?.Length ?? 0}B): " +
                    $"{(body == null ? "null" : BitConverter.ToString(body))}");
                return;
            }

            var target = FindSessionByName(targetName);
            if (!IsOnline(target) || target.SessionId == session.SessionId)
            {
                FileLogger.Log(
                    $"[GameProtocol] CREATE_GROUP target offline " +
                    $"cid={session.Player.CharacterId} " +
                    $"targetBytes={targetName.Length}");
                return;
            }

            var conversationKey = MakeConversationKey(
                session.Player.CharacterId,
                target.Player.CharacterId);
            uint conversationId;
            lock (_conversationLock)
            {
                if (_activeConversations.ContainsKey(conversationKey))
                {
                    FileLogger.Log(
                        $"[GameProtocol] CREATE_GROUP deduplicated " +
                        $"from={session.Player.CharacterId} " +
                        $"to={target.Player.CharacterId}");
                    return;
                }

                conversationId = AllocateConversationIdLocked();
                _activeConversations.Add(conversationKey, conversationId);
            }

            // Current 86JP client reads CREATE_GROUP as:
            // result:u8 + conversationId:u32 + memberCount:u8
            // + memberName:dstr[]. The first entry selects the receiving
            // player's chat UI; the complete set renders the peer as title.
            await Task.WhenAll(
                SendCreateGroupNotificationAsync(
                    session,
                    conversationId,
                    session.Player.Name,
                    target.Player.Name),
                SendCreateGroupNotificationAsync(
                    target,
                    conversationId,
                    target.Player.Name,
                    session.Player.Name));

            FileLogger.Log(
                $"[GameProtocol] CREATE_GROUP relayed " +
                $"from={session.Player.CharacterId} " +
                $"to={target.Player.CharacterId} " +
                $"groupId={conversationId} " +
                $"senderNameBytes={session.Player.Name?.Length ?? 0}");
        }

        public Task Handle_ONE_TO_ONE_CHAT_STATE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log(
                $"[GameProtocol] ONE_TO_ONE_CHAT_STATE " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"body({body?.Length ?? 0}B): " +
                $"{(body == null ? "null" : BitConverter.ToString(body))}");
            return Task.CompletedTask;
        }

        private static Task SendCreateGroupNotificationAsync(
            EnhancedClientSession recipient,
            uint conversationId,
            byte[] recipientName,
            byte[] peerName)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0); // success
            writer.WriteUInt32(conversationId);
            writer.WriteByte(2);
            writer.WriteDstr(recipientName);
            writer.WriteDstr(peerName);
            return recipient.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.CREATE_GROUP,
                    writer.ToArray()));
        }

        private IReadOnlyList<EnhancedClientSession> FindConversationPeers(
            EnhancedClientSession sender,
            uint conversationId)
        {
            var peers = new List<EnhancedClientSession>();
            var senderId = sender?.Player?.CharacterId ?? 0;
            if (senderId <= 0)
                return peers;

            foreach (var candidate in _sessions.GetAllGameSessions())
            {
                var candidateId = candidate?.Player?.CharacterId ?? 0;
                if (candidateId <= 0 || candidateId == senderId)
                    continue;
                lock (_conversationLock)
                {
                    if (_activeConversations.TryGetValue(
                            MakeConversationKey(senderId, candidateId),
                            out var activeConversationId)
                        && activeConversationId == conversationId)
                    {
                        peers.Add(candidate);
                    }
                }
            }
            return peers;
        }

        private Task OnSessionEndingAsync(
            int characterId,
            EnhancedClientSession session)
        {
            lock (_conversationLock)
            {
                foreach (var key in _activeConversations.Keys
                             .Where(key => ConversationKeyContains(key, characterId))
                             .ToArray())
                {
                    _activeConversations.Remove(key);
                }
            }
            return Task.CompletedTask;
        }

        private uint AllocateConversationIdLocked()
        {
            while (_nextConversationId == 0
                || _activeConversations.ContainsValue(_nextConversationId))
            {
                _nextConversationId++;
            }

            return _nextConversationId++;
        }

        private static ulong MakeConversationKey(int first, int second)
        {
            var low = (uint)Math.Min(first, second);
            var high = (uint)Math.Max(first, second);
            return ((ulong)low << 32) | high;
        }

        private static bool ConversationKeyContains(ulong key, int id)
            => (uint)(key >> 32) == (uint)id
                || (uint)key == (uint)id;

        private EnhancedClientSession FindSessionByName(byte[] nameBytes)
        {
            if (nameBytes == null || nameBytes.Length == 0)
                return null;

            foreach (var candidate in _sessions.GetAllGameSessions())
            {
                if (candidate?.Player?.Name != null
                    && candidate.Player.Name.SequenceEqual(nameBytes))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static bool TryParseNameArgument(
            byte[] body,
            out byte[] nameBytes)
        {
            nameBytes = Array.Empty<byte>();
            if (body == null || body.Length < 5)
                return false;

            var length = BitConverter.ToInt32(body, 0);
            if (length <= 0 || length > 30 || body.Length != 4 + length)
                return false;

            nameBytes = new byte[length];
            Buffer.BlockCopy(body, 4, nameBytes, 0, length);
            return Array.IndexOf(nameBytes, (byte)0) < 0;
        }

        private static bool IsDirectMessageMode(byte mode)
            => mode == DirectMessageMode
                || mode == AlternateDirectMessageMode
                || mode == OneToOneConversationMode;

        private static void AddIfCurrentChannel(
            IDictionary<Guid, EnhancedClientSession> recipients,
            EnhancedClientSession sender,
            EnhancedClientSession candidate)
        {
            if (candidate?.Player == null
                || candidate.Player.CharacterId <= 0
                || candidate.TcpClient == null
                || !candidate.TcpClient.Connected)
            {
                return;
            }
            if (sender.ListenerPort > 0
                && candidate.ListenerPort != sender.ListenerPort)
            {
                return;
            }
            recipients[candidate.SessionId] = candidate;
        }

        private static void AddIfOnline(
            IDictionary<Guid, EnhancedClientSession> recipients,
            EnhancedClientSession candidate)
        {
            if (IsOnline(candidate))
                recipients[candidate.SessionId] = candidate;
        }

        private static bool IsOnline(EnhancedClientSession candidate)
            => candidate?.Player != null
                && candidate.Player.CharacterId > 0
                && candidate.TcpClient != null
                && candidate.TcpClient.Connected;

        internal static bool TryParseRequest(
            byte[] body,
            out ChatMessageRequest request)
        {
            request = null;
            if (body == null || body.Length < 11)
                return false;

            var mode = body[0];
            var targetUniqueId = BitConverter.ToUInt16(body, 1);
            var targetCharacterId = BitConverter.ToUInt32(body, 3);
            var messageLength = BitConverter.ToInt32(body, 7);
            if (messageLength <= 0
                || messageLength > MaximumMessageBytes
                || body.Length < 11 + messageLength)
            {
                return false;
            }

            var messageBytes = new byte[messageLength];
            Buffer.BlockCopy(body, 11, messageBytes, 0, messageLength);
            if (Array.IndexOf(messageBytes, (byte)0) >= 0)
                return false;

            var offset = 11 + messageLength;
            var targetNameBytes = Array.Empty<byte>();
            if (IsDirectMessageMode(mode))
            {
                if (body.Length > offset)
                {
                    if (body.Length < offset + 4)
                        return false;
                    var nameLength = BitConverter.ToInt32(body, offset);
                    if (nameLength < 0
                        || nameLength > 30
                        // 86JP appends a one-byte direct-conversation flag
                        // after targetName. Older clients omit it.
                        || (body.Length != offset + 4 + nameLength
                            && body.Length != offset + 5 + nameLength))
                    {
                        return false;
                    }
                    targetNameBytes = new byte[nameLength];
                    if (nameLength > 0)
                    {
                        Buffer.BlockCopy(
                            body,
                            offset + 4,
                            targetNameBytes,
                            0,
                            nameLength);
                    }
                }
            }
            else if (body.Length != offset)
            {
                return false;
            }

            request = new ChatMessageRequest(
                mode,
                targetUniqueId,
                targetCharacterId,
                mode == OneToOneConversationMode
                    // Mode 45 uses the target-character field as its
                    // server-assigned conversation id.
                    ? targetCharacterId
                    : 0,
                messageBytes,
                targetNameBytes);
            return true;
        }

        internal static byte[] BuildNotificationBody(
            byte mode,
            ushort senderUniqueId,
            byte serverGroup,
            byte[] messageBytes)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteUInt16(senderUniqueId);
            writer.WriteByte(serverGroup);
            writer.WriteDstr(messageBytes ?? Array.Empty<byte>());
            return writer.ToArray();
        }

        internal static byte[] BuildGroupChatNotificationBody(
            uint conversationId,
            byte[] senderNameBytes,
            byte[] messageBytes)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(conversationId);
            writer.WriteDstr(senderNameBytes ?? Array.Empty<byte>());
            writer.WriteDstr(messageBytes ?? Array.Empty<byte>());
            return writer.ToArray();
        }
    }

    internal sealed class ChatMessageRequest
    {
        internal ChatMessageRequest(
            byte mode,
            ushort targetUniqueId,
            uint targetCharacterId,
            uint conversationId,
            byte[] messageBytes,
            byte[] targetNameBytes)
        {
            Mode = mode;
            TargetUniqueId = targetUniqueId;
            TargetCharacterId = targetCharacterId;
            ConversationId = conversationId;
            MessageBytes = messageBytes ?? Array.Empty<byte>();
            TargetNameBytes = targetNameBytes ?? Array.Empty<byte>();
        }

        internal byte Mode { get; }
        internal ushort TargetUniqueId { get; }
        internal uint TargetCharacterId { get; }
        internal uint ConversationId { get; }
        internal byte[] MessageBytes { get; }
        internal byte[] TargetNameBytes { get; }
    }
}
