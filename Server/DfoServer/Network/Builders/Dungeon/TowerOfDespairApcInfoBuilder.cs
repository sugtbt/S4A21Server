using System;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Session;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Builders
{
    internal static class TowerOfDespairApcInfoBuilder
    {
        private const int DynamicLayerInterval = 10;
        private const int LastDynamicLayer = 90;
        private const int EquipmentAndAvatarSlotCount = 22;

        internal static bool TryBuild(
            int dungeonId,
            PlayerContext player,
            out byte[] baseLayerBody,
            out byte[] currentLayerBody)
        {
            baseLayerBody = Array.Empty<byte>();
            currentLayerBody = Array.Empty<byte>();
            if (player == null
                || !DungeonData.TryGetTowerOfDespairFloor(dungeonId, out var floor)
                || floor < DynamicLayerInterval
                || floor > LastDynamicLayer
                || floor % DynamicLayerInterval != 0)
            {
                return false;
            }

            baseLayerBody = BuildBody(0, player);
            currentLayerBody = BuildBody((byte)floor, player);
            return true;
        }

        private static byte[] BuildBody(byte layer, PlayerContext player)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(layer);
            writer.WriteDstr(player.Name);
            writer.WriteByte(player.Level);
            writer.WriteByte(player.Job);
            writer.WriteByte(player.GrowType);
            writer.WriteDstr(Array.Empty<byte>()); // guild name
            writer.WriteInt32(0);                 // guild id
            for (var index = 0; index < EquipmentAndAvatarSlotCount; index++)
            {
                var appearanceSlot = index < 10
                    ? index
                    : index == 10
                        ? (int)EquipmentType.Weapon
                        : -1;
                writer.WriteInt32(ResolveDisplayItemId(player, appearanceSlot));
            }

            var tail = player.Subtype0Tail;
            writer.WriteDstr(tail?.EquippedCreatureNameBytes);
            writer.WriteUInt32(tail?.EquippedCreatureItemId ?? 0);
            return writer.ToArray();
        }

        private static int ResolveDisplayItemId(PlayerContext player, int slot)
        {
            if (slot < 0 || player.AppearanceEntries == null)
                return 0;

            foreach (var entry in player.AppearanceEntries)
            {
                if (entry != null && entry.Slot == slot)
                    return entry.DisplayItemId;
            }

            return 0;
        }
    }
}
