using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Shared protocol sender for notifications used by more than one dungeon mechanism.
    // Business coordinators decide when a condition is complete; this class only projects
    // the verified client protocol and records the common envelope diagnostics.
    internal static class DungeonMechanismNotificationSender
    {
        internal static async Task SendNpcItemDropAsync(
            EnhancedClientSession session,
            DropInfo drop,
            int x,
            int y,
            int questId,
            int objectCode,
            string actionPath)
        {
            if (session?.Player == null)
                return;

            var packetX = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, x));
            var packetY = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, y));
            var body = DropItemBuilder.BuildDrop(
                session.Player.UserId,
                packetX,
                packetY,
                drop,
                0);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.DROP_ITEM,
                body));

            FileLogger.Log(
                $"[DungeonMechanism] NPC item DROP_ITEM sent: " +
                $"cid={session.Player.CharacterId} " +
                $"dungeon={session.Player.CurrentRun?.DungeonId ?? 0} " +
                $"quest={questId} object={objectCode} " +
                $"item={drop.TemplateId} sceneSlot={drop.SceneSlot} " +
                $"pos=({packetX},{packetY}) action={actionPath ?? string.Empty}");
        }

        internal static async Task SendCompleteConditionPassGateAsync(
            EnhancedClientSession session,
            string mechanism,
            string reason)
        {
            if (session?.Player == null)
                return;

            var body = SpecialDungeonNotificationBuilder
                .BuildCompleteConditionPassGateTrigger();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.COMPLETE_CONDITION_PASS_GATE,
                body));

            FileLogger.Log(
                $"[DungeonMechanism] COMPLETE_CONDITION_PASS_GATE sent: " +
                $"mechanism={mechanism ?? string.Empty} " +
                $"reason={reason ?? string.Empty} " +
                $"cid={session.Player.CharacterId} " +
                $"dungeon={session.Player.CurrentRun?.DungeonId ?? 0} " +
                $"body={BitConverter.ToString(body)}");
        }
    }
}
