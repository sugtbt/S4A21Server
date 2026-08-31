using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Network;

namespace DfoServer.Game.Session
{
    public sealed class SessionDirectory : ISessionDirectory
    {
        internal static readonly TimeSpan BestEffortSendTimeout =
            TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<int, EnhancedClientSession> _byCharacterId = new ConcurrentDictionary<int, EnhancedClientSession>();
        private readonly Func<byte, byte, bool> _isShareableTownArea;

        public SessionDirectory()
            : this(IsShareableTownArea)
        {
        }

        internal SessionDirectory(Func<byte, byte, bool> isShareableTownArea)
        {
            _isShareableTownArea = isShareableTownArea
                ?? throw new ArgumentNullException(nameof(isShareableTownArea));
        }

        public event Func<int, EnhancedClientSession, Task> SessionEnding;

        public void Register(
            int characterId,
            EnhancedClientSession session)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            _byCharacterId[characterId] = session;
            FileLogger.Log(
                $"[SessionDirectory] Registered characterId={characterId} " +
                $"session={session.SessionId}");
        }

        public async Task<EnhancedClientSession> RegisterReplacingAsync(
            int characterId,
            EnhancedClientSession session)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));

            EnhancedClientSession displaced;
            while (true)
            {
                if (_byCharacterId.TryAdd(characterId, session))
                {
                    displaced = null;
                    break;
                }

                if (!_byCharacterId.TryGetValue(
                        characterId, out var current))
                {
                    continue;
                }
                if (ReferenceEquals(current, session))
                {
                    displaced = null;
                    break;
                }
                if (_byCharacterId.TryUpdate(
                        characterId, session, current))
                {
                    displaced = current;
                    break;
                }
            }

            if (displaced != null)
                await NotifySessionEndingAsync(characterId, displaced);

            FileLogger.Log(
                $"[SessionDirectory] Registered characterId={characterId} " +
                $"session={session.SessionId} " +
                $"displaced={displaced?.SessionId.ToString() ?? "none"}");
            return displaced;
        }

        public async Task<bool> UnregisterAsync(
            int characterId, EnhancedClientSession expectedSession)
        {
            if (expectedSession == null)
                return false;

            // Compare-and-remove before raising lifecycle events. An old
            // connection must never tear down a newer session that has already
            // registered the same character id, and only one concurrent caller
            // may own this teardown.
            var removed =
                ((ICollection<KeyValuePair<int, EnhancedClientSession>>)
                    _byCharacterId)
                .Remove(new KeyValuePair<int, EnhancedClientSession>(
                    characterId, expectedSession));
            if (!removed)
            {
                FileLogger.Log(
                    $"[SessionDirectory] Stale unregister skipped " +
                    $"characterId={characterId} session={expectedSession.SessionId}");
                return false;
            }

            await NotifySessionEndingAsync(characterId, expectedSession);

            FileLogger.Log(
                $"[SessionDirectory] Unregistered characterId={characterId} " +
                $"session={expectedSession.SessionId}");
            return true;
        }

        public async Task UnregisterAsync(int characterId)
        {
            if (_byCharacterId.TryGetValue(
                    characterId, out var current))
            {
                await UnregisterAsync(characterId, current);
            }
        }

        private async Task NotifySessionEndingAsync(
            int characterId,
            EnhancedClientSession endingSession)
        {
            var handler = SessionEnding;
            if (handler != null)
            {
                // Isolate subscribers so one cleanup failure cannot prevent the
                // remaining lifecycle callbacks from running.
                foreach (var d in handler.GetInvocationList())
                {
                    try
                    {
                        await ((Func<int, EnhancedClientSession, Task>)d)(
                            characterId, endingSession);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[SessionDirectory] SessionEnding subscriber failed " +
                            $"for characterId={characterId}: {ex}");
                    }
                }
            }
        }

        public bool TryGet(int characterId, out EnhancedClientSession session)
        {
            return _byCharacterId.TryGetValue(characterId, out session);
        }

        public IReadOnlyList<EnhancedClientSession> GetAllGameSessions()
        {
            return _byCharacterId.Values.ToList();
        }

        public async Task SendToAsync(int characterId, byte[] packet)
        {
            if (_byCharacterId.TryGetValue(characterId, out var session))
                await session.SendPacketAsync(packet);
        }

        public async Task BroadcastToAsync(IEnumerable<int> characterIds, byte[] packet)
        {
            var tasks = new List<Task>();
            foreach (var characterId in characterIds)
            {
                if (_byCharacterId.TryGetValue(characterId, out var session))
                {
                    tasks.Add(TrySendBestEffortAsync(
                        cancellationToken =>
                            session.SendPacketAsync(packet, cancellationToken),
                        $"characterId={characterId}"));
                }
            }
            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        // 城镇同屏的区域视图: 同一注册表上的过滤查询, 不另设第二份会话索引。
        // 副本内玩家(CurrentRun != null)一律排除 —— 其 town/area 字段是进本前的残值, 不该出现在城镇名册里。
        public IReadOnlyList<EnhancedClientSession> GetSessionsInArea(
            byte townId,
            byte areaId,
            int excludeCharacterId,
            int listenerPort = 0)
        {
            // The Cera room is private per character. Connections may share
            // the same town/area ids without occupying one shared room.
            if (!_isShareableTownArea(townId, areaId))
                return Array.Empty<EnhancedClientSession>();

            var result = new List<EnhancedClientSession>();
            foreach (var kvp in _byCharacterId)
            {
                if (kvp.Key == excludeCharacterId) continue;
                var session = kvp.Value;
                var player = session?.Player;
                if (player == null || player.CharacterId <= 0) continue;
                if (listenerPort > 0 &&
                    session.ListenerPort != listenerPort)
                {
                    continue;
                }
                if (!IsTownPresence(player)) continue;
                if (player.CurTownId != townId || player.CurAreaId != areaId) continue;
                if (session.TcpClient == null || !session.TcpClient.Connected) continue;
                result.Add(session);
            }
            return result;
        }

        public async Task BroadcastToAreaAsync(
            byte townId,
            byte areaId,
            int excludeCharacterId,
            byte[] packet,
            int listenerPort = 0)
        {
            var targets = GetSessionsInArea(
                townId,
                areaId,
                excludeCharacterId,
                listenerPort);
            if (targets.Count == 0) return;
            var tasks = new List<Task>(targets.Count);
            foreach (var session in targets)
            {
                tasks.Add(TrySendBestEffortAsync(
                    cancellationToken =>
                        session.SendPacketAsync(packet, cancellationToken),
                    $"characterId={session.Player?.CharacterId ?? 0}"));
            }
            await Task.WhenAll(tasks);
        }

        internal static bool IsTownPresence(PlayerContext player)
            => player != null &&
               player.TownPresenceReady &&
               player.CharacterId > 0 &&
               player.CurrentRun == null &&
               player.UserState == 0x00;

        internal static bool IsShareableTownArea(
            byte townId,
            byte areaId)
            => !GameWorld.Town.IsCeraRoom(townId, areaId);

        internal static async Task<bool> TrySendBestEffortAsync(
            Func<CancellationToken, Task> send,
            string target,
            TimeSpan? timeout = null)
        {
            using var timeoutSource = new CancellationTokenSource(
                timeout ?? BestEffortSendTimeout);
            try
            {
                await send(timeoutSource.Token);
                return true;
            }
            catch (OperationCanceledException ex)
                when (timeoutSource.IsCancellationRequested)
            {
                LogExpectedBroadcastFailure(target, ex);
                return false;
            }
            catch (IOException ex)
            {
                LogExpectedBroadcastFailure(target, ex);
                return false;
            }
            catch (SocketException ex)
            {
                LogExpectedBroadcastFailure(target, ex);
                return false;
            }
            catch (ObjectDisposedException ex)
            {
                LogExpectedBroadcastFailure(target, ex);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                LogExpectedBroadcastFailure(target, ex);
                return false;
            }
        }

        private static void LogExpectedBroadcastFailure(string target, Exception exception)
        {
            FileLogger.Log(
                $"[SessionDirectory] Best-effort broadcast skipped disconnected target ({target}): {exception.GetType().Name}: {exception.Message}");
        }
    }
}
