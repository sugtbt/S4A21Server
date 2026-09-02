using System;
using System.Collections.Generic;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;

namespace DfoServer.Network.Builders
{
    internal static class LicensedDungeonPacketBuilder
    {
        internal static byte[] BuildCharacterLicenseInfo(
            IReadOnlyList<LicensedDungeonPermissionRecord> records)
        {
            var writer = new GamePacketWriter();
            records = records ?? Array.Empty<LicensedDungeonPermissionRecord>();
            if (records.Count > ushort.MaxValue)
                throw new InvalidOperationException(
                    "licensed dungeon permission record count exceeds u16");

            writer.WriteUInt16((ushort)records.Count);
            foreach (var record in records)
            {
                writer.WriteInt32(record.DungeonId);
                writer.WriteInt32(record.LicenseLevel);
                writer.WriteInt32(record.Field3);
            }
            return writer.ToArray();
        }

        internal static byte[] BuildDayIndex(byte dayIndex) =>
            new[] { dayIndex };

        internal static byte[] BuildShotCount(byte shotCount = 0) =>
            new[] { shotCount };

        internal static byte[] BuildRemainingEnterCount(byte remainingCount) =>
            new[] { remainingCount };

        // A21 LICENSE_DUNGEON_CLEAR_INFO is a fixed 21-byte body. The
        // directory row selects the dungeon; this packet projects the frozen
        // settlement result and the two ETC reward slots shown by the client.
        // The A21 wire order is daily reward first, then dungeon-clear reward.
        internal static byte[] BuildClearInfo(
            bool groupBossPresent,
            int clearTimeMilliseconds,
            LicensedDungeonRewardDisplayItem dungeonClearReward,
            LicensedDungeonRewardDisplayItem dailyClearReward)
        {
            if (dungeonClearReward == null)
            {
                throw new ArgumentNullException(
                    nameof(dungeonClearReward));
            }
            if (dailyClearReward == null)
                throw new ArgumentNullException(nameof(dailyClearReward));

            var writer = new GamePacketWriter();
            writer.WriteByte(groupBossPresent ? (byte)1 : (byte)0);
            writer.WriteUInt32((uint)Math.Max(0, clearTimeMilliseconds));
            writer.WriteUInt32((uint)dailyClearReward.ItemId);
            writer.WriteUInt32((uint)dailyClearReward.Count);
            writer.WriteUInt32((uint)dungeonClearReward.ItemId);
            writer.WriteUInt32((uint)dungeonClearReward.Count);
            return writer.ToArray();
        }

    }
}
