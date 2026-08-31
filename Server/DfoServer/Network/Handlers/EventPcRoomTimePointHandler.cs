using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Events.PcRoomTimePoint;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Events;
using DfoServer.Network.Parsers.Events;

namespace DfoServer.Network.Handlers
{
    internal sealed class EventPcRoomTimePointHandler
    {
        private const string TimerPrefix = "event:pcroom-timepoint:";

        private readonly PcRoomTimePointService _service;
        private readonly ISessionDirectory _sessions;
        private readonly object _notificationSync = new object();
        private readonly Dictionary<Guid, string> _lastNotificationKeys =
            new Dictionary<Guid, string>();
        private ClockService _clock;
        private bool _clockRegistered;
        private int _tickRunning;

        internal EventPcRoomTimePointHandler(
            PcRoomTimePointService service,
            ISessionDirectory sessions = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _sessions = sessions;
        }

        internal void RegisterClock(ClockService clock)
        {
            if (clock == null || _sessions == null)
                return;

            lock (_notificationSync)
            {
                if (_clockRegistered)
                    return;

                _clock = clock;
                _clockRegistered = true;
            }

            clock.RegisterMinuteTick(
                "event:pcroom-timepoint:flush",
                utcNow =>
                {
                    _ = NotifyOnMinuteTickAsync(utcNow);
                });
        }

        internal async Task NotifyStateOnLoginAsync(EnhancedClientSession session)
        {
            if (!TryGetIdentity(
                    session,
                    out var accountId,
                    out var characterId))
            {
                return;
            }

            _service.BeginSession(session.SessionId, accountId, characterId);
            if (!_service.TryGetSnapshotForSession(
                    session.SessionId,
                    accountId,
                    characterId,
                    out var snapshot))
            {
                return;
            }

            await SendStateIfCurrentAsync(
                session,
                snapshot,
                force: true,
                reason: "login");
        }

        internal Task NotifySessionEndingAsync(
            EnhancedClientSession session,
            string reason)
        {
            if (session == null)
                return Task.CompletedTask;

            CancelTimers(session.SessionId);
            ClearNotificationKey(session.SessionId);
            _service.EndSession(session.SessionId);
            FileLogger.Log(
                "[PcRoomTimePoint] session ending "
                + $"reason={reason ?? "unknown"} "
                + $"account_id={session.Account?.AccountId ?? 0} "
                + $"cid={session.Player?.CharacterId ?? 0}");
            return Task.CompletedTask;
        }

        internal async Task HandleAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!PcRoomTimePointRequestParser.TryParse(body, out var command))
            {
                await SendAckAsync(session);
                FileLogger.Log(
                    "[PcRoomTimePoint] rejected request "
                    + $"bodyLength={body?.Length ?? 0} "
                    + $"body={(body == null ? "null" : BitConverter.ToString(body))}");
                return;
            }

            if (!TryGetIdentity(
                    session,
                    out var accountId,
                    out var characterId))
            {
                await SendAckAsync(session);
                FileLogger.Log(
                    "[PcRoomTimePoint] rejected request without active character");
                return;
            }

            if (command.Kind == PcRoomTimePointRequestKind.Query)
            {
                _service.BeginSession(session.SessionId, accountId, characterId);
                await SendAckAsync(session);
                if (_service.TryGetSnapshotForSession(
                        session.SessionId,
                        accountId,
                        characterId,
                        out var snapshot))
                {
                    await SendStateIfCurrentAsync(
                        session,
                        snapshot,
                        force: true,
                        reason: "query");
                }

                return;
            }

            var result = _service.Claim(
                session.SessionId,
                accountId,
                characterId,
                DecodeCharacterName(session),
                session.Player?.Level ?? 0,
                command);

            await SendAckAsync(session);
            if (result.MailDelivered)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.MAILBOX_ALARM,
                    MailboxHandler.BuildMailboxAlarmNotification(1)));
            }

            if (result.Snapshot != null)
            {
                await SendStateIfCurrentAsync(
                    session,
                    result.Snapshot,
                    force: true,
                    reason: "claim");
            }

            if (!result.Success)
            {
                FileLogger.Log(
                    "[PcRoomTimePoint] claim rejected "
                    + $"account_id={accountId} cid={characterId} "
                    + $"kind={command.Kind} stage={command.StageIndex} "
                    + $"selector=0x{command.Selector:X2} index=0x{command.IndexOrFF:X2} "
                    + $"status={result.Status}");
            }
        }

        private async Task NotifyOnMinuteTickAsync(DateTime utcNow)
        {
            if (_sessions == null)
                return;
            if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0)
                return;

            try
            {
                var sessions = _sessions.GetAllGameSessions();
                if (sessions.Count == 0)
                    return;

                var tasks = new List<Task>(sessions.Count);
                foreach (var session in sessions)
                {
                    if (!TryGetIdentity(
                            session,
                            out var accountId,
                            out var characterId))
                    {
                        continue;
                    }

                    if (!_service.TryGetSnapshotForSessionAt(
                            session.SessionId,
                            accountId,
                            characterId,
                            utcNow,
                            out var snapshot))
                    {
                        continue;
                    }

                    tasks.Add(SendStateIfCurrentAsync(
                        session,
                        snapshot,
                        force: false,
                        reason: "minute"));
                }

                if (tasks.Count > 0)
                    await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                FileLogger.Log("[PcRoomTimePoint] minute tick failed: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        private async Task SendStateIfCurrentAsync(
            EnhancedClientSession session,
            PcRoomTimePointSnapshot snapshot,
            bool force,
            string reason)
        {
            if (session == null || snapshot == null)
                return;
            if (!IsCurrentSession(session, snapshot.CharacterId))
                return;

            var key = BuildNotificationKey(snapshot);
            if (!TryMarkNotification(session.SessionId, key, force))
            {
                ScheduleNextDailyThreshold(session, snapshot);
                return;
            }

            var packet = PcRoomTimePointPacketBuilder.BuildStatePacket(snapshot);
            var sent = await SessionDirectory.TrySendBestEffortAsync(
                cancellationToken => session.SendPacketAsync(packet, cancellationToken),
                $"pcroomtimepoint state {reason} cid={snapshot.CharacterId}");
            if (sent)
                ScheduleNextDailyThreshold(session, snapshot);
        }

        private void ScheduleNextDailyThreshold(
            EnhancedClientSession session,
            PcRoomTimePointSnapshot snapshot)
        {
            if (_clock == null
                || _sessions == null
                || session == null
                || snapshot == null)
            {
                return;
            }

            var prefix = TimerNamePrefix(session.SessionId);
            if (snapshot.NextDailyStageIndex <= 0
                || snapshot.NextDailyStageRemainingMillis <= 0)
            {
                _clock.CancelOneShotsByPrefix(prefix);
                return;
            }

            var accountId = snapshot.AccountId;
            var characterId = snapshot.CharacterId;
            var dueUtc = DateTime.UtcNow.AddMilliseconds(
                Math.Min(snapshot.NextDailyStageRemainingMillis, int.MaxValue));
            _clock.ScheduleOneShotAsync(
                prefix + "daily",
                dueUtc,
                async utcNow =>
                {
                    if (!IsCurrentSession(session, characterId))
                        return;
                    if (!_service.TryGetSnapshotForSessionAt(
                            session.SessionId,
                            accountId,
                            characterId,
                            utcNow,
                            out var latest))
                    {
                        return;
                    }

                    await SendStateIfCurrentAsync(
                        session,
                        latest,
                        force: false,
                        reason: "timer");
                });
        }

        private static Task SendAckAsync(EnhancedClientSession session)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                PcRoomTimePointPacketBuilder.BuildAckPacket());
        }

        private bool IsCurrentSession(
            EnhancedClientSession session,
            int characterId)
        {
            if (session?.Player?.CharacterId != characterId || characterId <= 0)
                return false;
            if (_sessions == null)
                return true;

            return _sessions.TryGet(characterId, out var current)
                && ReferenceEquals(current, session);
        }

        private static bool TryGetIdentity(
            EnhancedClientSession session,
            out int accountId,
            out int characterId)
        {
            accountId = session?.Account?.AccountId ?? 0;
            characterId = session?.Player?.CharacterId ?? 0;
            return accountId > 0 && characterId > 0;
        }

        private bool TryMarkNotification(
            Guid sessionId,
            string key,
            bool force)
        {
            lock (_notificationSync)
            {
                if (!force
                    && _lastNotificationKeys.TryGetValue(sessionId, out var last)
                    && string.Equals(last, key, StringComparison.Ordinal))
                {
                    return false;
                }

                _lastNotificationKeys[sessionId] = key;
                return true;
            }
        }

        private void ClearNotificationKey(Guid sessionId)
        {
            lock (_notificationSync)
                _lastNotificationKeys.Remove(sessionId);
        }

        private void CancelTimers(Guid sessionId)
        {
            _clock?.CancelOneShotsByPrefix(TimerNamePrefix(sessionId));
        }

        private static string TimerNamePrefix(Guid sessionId)
            => TimerPrefix + sessionId.ToString("N") + ":";

        private static string BuildNotificationKey(PcRoomTimePointSnapshot snapshot)
        {
            return $"{snapshot.DayId}:{snapshot.PeriodCompletedCount}:"
                + $"{snapshot.DailyAvailableMask}:{snapshot.PeriodAvailableMask}:"
                + $"{snapshot.DailyClaimMask}:{snapshot.PeriodClaimMask}";
        }

        private static string DecodeCharacterName(EnhancedClientSession session)
        {
            try
            {
                return ClientTextEncoding.GetString(
                    session?.Player?.Name ?? Array.Empty<byte>());
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
