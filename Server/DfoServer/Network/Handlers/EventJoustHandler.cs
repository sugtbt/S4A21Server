using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Events.Joust;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Events;
using DfoServer.Network.Parsers.Events;

namespace DfoServer.Network.Handlers
{
    internal sealed class EventJoustHandler
    {
        private readonly JoustService _service;
        private readonly InventoryRefreshSender _refresh;
        private readonly ISessionDirectory _sessions;
        private readonly object _clockNotificationSync = new object();
        private string _lastClockNotificationKey;
        private bool _clockRegistered;
        private int _clockNotifyRunning;

        internal EventJoustHandler(
            JoustService service,
            InventoryRefreshSender refresh,
            ISessionDirectory sessions = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            _sessions = sessions;
        }

        internal void RegisterClock(ClockService clock)
        {
            if (clock == null || _sessions == null)
                return;

            lock (_clockNotificationSync)
            {
                if (_clockRegistered)
                    return;
                _clockRegistered = true;
            }

            clock.RegisterMinuteTick(
                "event:joust:notify",
                utcNow =>
                {
                    _ = NotifyOnMinuteTickAsync(utcNow);
                });
        }

        internal async Task NotifyOnMinuteTickAsync(DateTime utcNow)
        {
            if (_sessions == null)
                return;
            if (Interlocked.CompareExchange(ref _clockNotifyRunning, 1, 0) != 0)
                return;

            try
            {
                if (!_service.TryGetStateSnapshot(utcNow, out var state))
                {
                    ClearLastClockNotificationKey();
                    return;
                }

                var key = BuildClockNotificationKey(state);
                if (key == null)
                {
                    ClearLastClockNotificationKey();
                    return;
                }

                if (!TryMarkClockNotification(key))
                    return;

                var sessions = _sessions.GetAllGameSessions();
                if (sessions.Count == 0)
                    return;

                var tasks = new List<Task>(sessions.Count);
                foreach (var session in sessions)
                {
                    var characterId = session?.Player?.CharacterId ?? 0;
                    if (characterId <= 0)
                        continue;

                    tasks.Add(SendClockTransitionAsync(
                        session,
                        characterId,
                        utcNow));
                }

                if (tasks.Count > 0)
                    await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                FileLogger.Log("[Joust] clock notification failed: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _clockNotifyRunning, 0);
            }
        }

        internal static IReadOnlyList<byte[]> BuildClockTransitionPackets(
            JoustSnapshot snapshot)
        {
            if (snapshot == null)
                return Array.Empty<byte[]>();

            switch (snapshot.Phase)
            {
                case JoustPhase.Betting:
                case JoustPhase.StopBetting:
                    return new[]
                    {
                        BuildStatePacket(new JoustStateSnapshot
                        {
                            RoundNo = snapshot.RoundNo,
                            Phase = snapshot.Phase,
                        }),
                    };

                case JoustPhase.Racing:
                    if (snapshot.CurrentResultStageIndex < 0)
                        return Array.Empty<byte[]>();

                    return new[]
                    {
                        BuildStatePacket(new JoustStateSnapshot
                        {
                            RoundNo = snapshot.RoundNo,
                            Phase = snapshot.Phase,
                        }),
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            (ushort)NotiPacketTypeA21.JOUST_MATCH_RESULT,
                            JoustPacketBuilder.BuildMatchResult(snapshot)),
                    };

                default:
                    return Array.Empty<byte[]>();
            }
        }

        internal async Task NotifyStateOnLoginAsync(
            EnhancedClientSession session)
        {
            if (session?.Player?.CharacterId <= 0)
                return;

            if (!_service.TryGetStateSnapshot(out var state))
                return;

            await session.SendPacketAsync(BuildStatePacket(state));
        }

        internal async Task HandleInfoAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (!_service.TryGetSnapshot(characterId, out var snapshot))
            {
                await SendInfoClosedAck(session, header.type);
                return;
            }

            await SendSnapshotAsync(session, snapshot, includeState: false);
        }

        internal async Task HandleBettingAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!JoustBettingRequestParser.TryParse(body, out var command))
            {
                await SendBettingAck(session, success: false);
                return;
            }

            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendBettingAck(session, success: false);
                return;
            }

            var level = session.Player?.Level ?? 0;
            var result = _service.PlaceBet(lease, level, command);
            if (!result.Success)
            {
                FileLogger.Log(
                    $"[Joust] bet rejected cid={characterId} "
                    + $"horse={command.HorseId} amount={command.Amount} "
                    + $"status={result.Status}");
                await SendBettingAck(session, success: false);
                return;
            }

            var refreshSlots = result.Consumed
                .Select(entry => entry.SlotIndex)
                .Distinct()
                .ToArray();
            if (refreshSlots.Length > 0)
            {
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    refreshSlots);
            }

            await SendBettingAck(session, success: true);
            if (result.Snapshot != null)
                await SendSnapshotAsync(session, result.Snapshot, includeState: false);
        }

        internal async Task HandleMatchHistoryAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                JoustPacketBuilder.BuildMatchHistoryAck(
                    _service.LoadHistory(500))));
        }

        private static Task SendInfoClosedAck(
            EnhancedClientSession session,
            ushort commandType)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                commandType,
                JoustPacketBuilder.BuildJoustInfoClosedAck()));
        }

        private static Task SendBettingAck(
            EnhancedClientSession session,
            bool success)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.JOUST_BETTING,
                JoustPacketBuilder.BuildJoustBettingAck(success)));
        }

        private async Task SendClockTransitionAsync(
            EnhancedClientSession session,
            int characterId,
            DateTime utcNow)
        {
            if (!IsCurrentSession(session, characterId))
                return;
            if (!_service.TryGetSnapshotAt(characterId, utcNow, out var snapshot))
                return;

            foreach (var packet in BuildClockTransitionPackets(snapshot))
            {
                if (!IsCurrentSession(session, characterId))
                    return;

                var sent = await SessionDirectory.TrySendBestEffortAsync(
                    cancellationToken =>
                        session.SendPacketAsync(packet, cancellationToken),
                    $"joust transition characterId={characterId}");
                if (!sent)
                    return;
            }
        }

        private bool IsCurrentSession(
            EnhancedClientSession session,
            int characterId)
        {
            return session?.Player?.CharacterId == characterId
                && _sessions != null
                && _sessions.TryGet(characterId, out var current)
                && ReferenceEquals(current, session);
        }

        private static async Task SendSnapshotAsync(
            EnhancedClientSession session,
            JoustSnapshot snapshot,
            bool includeState)
        {
            if (session == null || snapshot == null)
                return;

            if (includeState)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.JOUST_STATE,
                    JoustPacketBuilder.BuildState(new JoustStateSnapshot
                    {
                        RoundNo = snapshot.RoundNo,
                        Phase = snapshot.Phase,
                    })));
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.JOUST_BETTING_INFO,
                JoustPacketBuilder.BuildBettingInfo(snapshot)));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.JOUST_INFO,
                JoustPacketBuilder.BuildInfo(snapshot)));

            if ((snapshot.Phase == JoustPhase.Racing
                    || snapshot.Phase == JoustPhase.ResultReview)
                && snapshot.CurrentResultStageIndex >= 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.JOUST_MATCH_RESULT,
                    JoustPacketBuilder.BuildMatchResult(snapshot)));
            }
        }

        private bool TryMarkClockNotification(string key)
        {
            lock (_clockNotificationSync)
            {
                if (string.Equals(
                        _lastClockNotificationKey,
                        key,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                _lastClockNotificationKey = key;
                return true;
            }
        }

        private void ClearLastClockNotificationKey()
        {
            lock (_clockNotificationSync)
                _lastClockNotificationKey = null;
        }

        private static string BuildClockNotificationKey(
            JoustStateSnapshot state)
        {
            if (state == null)
                return null;

            switch (state.Phase)
            {
                case JoustPhase.Betting:
                case JoustPhase.StopBetting:
                    return $"{state.RoundNo}:{(byte)state.Phase}";

                case JoustPhase.Racing:
                    if (state.CurrentRaceStage < 0)
                        return null;
                    return $"{state.RoundNo}:{(byte)state.Phase}:{state.CurrentRaceStage}";

                default:
                    return null;
            }
        }

        private static byte[] BuildStatePacket(JoustStateSnapshot state)
        {
            return GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.JOUST_STATE,
                JoustPacketBuilder.BuildState(state));
        }
    }
}
