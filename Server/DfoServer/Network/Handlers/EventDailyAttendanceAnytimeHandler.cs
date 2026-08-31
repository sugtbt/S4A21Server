using System;
using System.Threading.Tasks;
using DfoServer.Game.Events.DailyAttendanceAnytime;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Events;

namespace DfoServer.Network.Handlers
{
    internal sealed class EventDailyAttendanceAnytimeHandler
    {
        private readonly DailyAttendanceAnytimeService _service;

        internal EventDailyAttendanceAnytimeHandler(
            DailyAttendanceAnytimeService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        internal async Task NotifyStateOnLoginAsync(
            EnhancedClientSession session)
        {
            if (!TryGetIdentity(
                    session,
                    out var accountId,
                    out var characterId))
            {
                return;
            }

            if (!_service.TryGetSnapshot(
                    accountId,
                    characterId,
                    out var snapshot))
            {
                return;
            }

            await SendStateAsync(session, snapshot, "login");
        }

        internal async Task HandleClaimAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!TryGetIdentity(
                    session,
                    out var accountId,
                    out var characterId))
            {
                await SendAckAsync(session, header.type);
                FileLogger.Log(
                    "[DailyAttendanceAnytime] rejected claim without active character");
                return;
            }

            var result = _service.ClaimAccumulateReward(
                accountId,
                characterId,
                DecodeCharacterName(session),
                session.Player?.Level ?? 0);

            await SendAckAsync(session, header.type);
            if (result.MailDelivered)
                await SendMailboxAlarmAsync(session);
            if (result.Snapshot != null)
                await SendStateAsync(session, result.Snapshot, "claim");

            if (result.Status != DailyAttendanceAnytimeClaimStatus.Claimed)
            {
                FileLogger.Log(
                    "[DailyAttendanceAnytime] claim skipped "
                    + $"account_id={accountId} cid={characterId} "
                    + $"status={result.Status} "
                    + $"bodyLength={body?.Length ?? 0}");
            }
        }

        internal static Task<bool> SendStateAsync(
            EnhancedClientSession session,
            DailyAttendanceAnytimeSnapshot snapshot,
            string reason)
        {
            if (session == null
                || snapshot == null
                || snapshot.CharacterId <= 0
                || session.Player?.CharacterId != snapshot.CharacterId)
            {
                return Task.FromResult(false);
            }

            var packet = DailyAttendanceAnytimePacketBuilder
                .BuildStatePacket(snapshot);
            return SessionDirectory.TrySendBestEffortAsync(
                cancellationToken =>
                    session.SendPacketAsync(packet, cancellationToken),
                "dailyattendanceanytime state "
                + $"{reason ?? "unknown"} cid={snapshot.CharacterId}");
        }

        internal static Task<bool> SendMailboxAlarmAsync(
            EnhancedClientSession session)
        {
            if (session?.Player?.CharacterId <= 0)
                return Task.FromResult(false);

            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.MAILBOX_ALARM,
                MailboxHandler.BuildMailboxAlarmNotification(1));
            return SessionDirectory.TrySendBestEffortAsync(
                cancellationToken => session.SendPacketAsync(
                    packet,
                    cancellationToken),
                "dailyattendanceanytime mailbox alarm "
                + $"cid={session.Player.CharacterId}");
        }

        private static Task SendAckAsync(
            EnhancedClientSession session,
            ushort type)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    type,
                    Array.Empty<byte>()));
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
