using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Shared projection for temporary dungeon buffs. Mechanisms own which buffs
    // are active; this sender owns the create/remove/full-active packet sequence.
    internal static class DungeonBuffNotificationSender
    {
        internal static Task SendAddedAndActivateAsync(
            EnhancedClientSession session,
            int addedBuffId,
            IReadOnlyList<int> activeBuffIds)
            => SendAddedAndActivateAsync(
                session,
                new[] { addedBuffId },
                activeBuffIds);

        internal static async Task SendAddedAndActivateAsync(
            EnhancedClientSession session,
            IReadOnlyList<int> addedBuffIds,
            IReadOnlyList<int> activeBuffIds,
            Func<byte[], Task<bool>> trySendPacketAsync = null)
        {
            var addedCount = addedBuffIds?.Count ?? 0;
            for (var i = 0; i < addedCount; i++)
            {
                var addBody = SpecialDungeonNotificationBuilder.BuildCharacterAddBuff(
                    addedBuffIds[i],
                    0,
                    0,
                    0);
                await SendPacketAsync(
                    session,
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketType.CHARACTER_ADD_BUFF,
                        addBody),
                    trySendPacketAsync);
            }

            var activeBody = SpecialDungeonNotificationBuilder.BuildCharacterBuffDungeon(
                activeBuffIds);
            await SendPacketAsync(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.CHARACTER_BUFF_DUNGEON,
                    activeBody),
                trySendPacketAsync);
        }

        internal static async Task ClearAsync(
            EnhancedClientSession session,
            IReadOnlyList<int> buffIds,
            Func<byte[], Task<bool>> trySendPacketAsync = null)
        {
            var removeBody = SpecialDungeonNotificationBuilder.BuildCharacterRemoveBuff(
                buffIds);
            await SendPacketAsync(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.CHARACTER_DEL_BUFF,
                    removeBody),
                trySendPacketAsync);

            var clearBody = SpecialDungeonNotificationBuilder.BuildCharacterBuffDungeon(
                System.Array.Empty<int>());
            await SendPacketAsync(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.CHARACTER_BUFF_DUNGEON,
                    clearBody),
                trySendPacketAsync);
        }

        private static async Task SendPacketAsync(
            EnhancedClientSession session,
            byte[] packet,
            Func<byte[], Task<bool>> trySendPacketAsync)
        {
            if (trySendPacketAsync != null)
            {
                await trySendPacketAsync(packet);
                return;
            }

            await session.SendPacketAsync(packet);
        }
    }
}
