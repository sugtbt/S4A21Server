using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal static class EpicBuffPotionBuffNotifier
    {
        private const string TimerPrefix = "epic-buff-potion:";

        internal static Task SendAddAsync(EnhancedClientSession session)
        {
            if (session == null)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.CHARACTER_ADD_BUFF,
                EpicBuffPotionPacketBuilder.BuildAddBuffBody()));
        }

        internal static void ScheduleRemoveForCurrentEffect(
            EnhancedClientSession session,
            int characterId)
        {
            if (session == null || characterId <= 0)
                return;

            var now = InventoryItemLifecycleService.UtcNowUnixSeconds();
            if (!TryGetCurrentEffectExpireTime(
                    session,
                    characterId,
                    now,
                    out var expireTime))
            {
                return;
            }

            var dueUtc = DateTimeOffset
                .FromUnixTimeSeconds(expireTime)
                .UtcDateTime;
            ClockService.Instance.ScheduleOneShotAsync(
                TimerPrefix + characterId,
                dueUtc,
                _ => SendRemoveIfExpiredAsync(session, characterId));
        }

        private static async Task SendRemoveIfExpiredAsync(
            EnhancedClientSession session,
            int characterId)
        {
            if (session == null || characterId <= 0)
                return;

            var now = InventoryItemLifecycleService.UtcNowUnixSeconds();
            if (TryGetCurrentEffectExpireTime(
                    session,
                    characterId,
                    now,
                    out _))
            {
                ScheduleRemoveForCurrentEffect(session, characterId);
                return;
            }

            if (session.Player?.CharacterId != characterId
                || session.TcpClient == null
                || !session.TcpClient.Connected)
            {
                return;
            }

            try
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.CHARACTER_DEL_BUFF,
                    EpicBuffPotionPacketBuilder.BuildRemoveBuffBody()));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[EpicBuffPotion] remove buff notification failed " +
                    $"cid={characterId}: {ex.Message}");
            }
        }

        private static bool TryGetCurrentEffectExpireTime(
            EnhancedClientSession session,
            int characterId,
            long nowUnixSeconds,
            out int expireTime)
        {
            expireTime = 0;
            if (session?.Player?.CharacterId != characterId
                || !InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    characterId,
                    out var lease))
            {
                return false;
            }

            lock (lease.SyncRoot)
            {
                return EpicBuffPotionDefinition.TryGetActiveEffectExpireTime(
                    lease.Inventory,
                    nowUnixSeconds,
                    out expireTime);
            }
        }
    }
}
