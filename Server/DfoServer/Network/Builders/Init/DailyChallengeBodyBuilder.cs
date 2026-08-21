using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class DailyChallengeBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.DAILY_CHALLENGE;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot?.InitializationSnapshot ?? new SelectCharacterInitializationSnapshot();
            var characterLevel = snapshot?.CharacterRecord?.Level ?? init.DailyChallengeCharacterLevel;
            body = Build(init, characterLevel);
            return true;
        }

        public static byte[] Build(SelectCharacterInitializationSnapshot init)
        {
            var characterLevel = init?.DailyChallengeCharacterLevel ?? 1;
            return Build(init, characterLevel);
        }

        private static byte[] Build(
            SelectCharacterInitializationSnapshot init,
            uint characterLevel)
        {
            init ??= new SelectCharacterInitializationSnapshot();
            var groups = init.RacingDungeonGroups;
            var groupFlags = NormalizeClaimFlags(init.DailyChallengeRewardClaimFlags);

            var groupCount = groups?.Count ?? 0;
            var totalEntries = 0;
            for (var i = 0; i < groupCount; i++)
                totalEntries += groups[i].Entries.Count;

            // A21 DNF.exe SHA256 1F8244... handler 0x01170F00 reads the
            // claim-flag vector and returns immediately.  Its state updater
            // 0x00884FB0 rejects any vector whose length is not exactly five.
            // The migrated A12 sixth special flag and tail list are not A21 wire.
            var size = 4 + 4 + groupCount * 8 + totalEntries * 12 + 4 + groupFlags.Length;
            var body = new byte[size];

            var offset = 0;
            Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(1u, characterLevel)), 0, body, offset, 4);
            offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes((uint)groupCount), 0, body, offset, 4);
            offset += 4;

            for (var i = 0; i < groupCount; i++)
            {
                var group = groups[i];
                Buffer.BlockCopy(BitConverter.GetBytes(group.GroupId), 0, body, offset, 4);
                offset += 4;
                Buffer.BlockCopy(BitConverter.GetBytes((uint)group.Entries.Count), 0, body, offset, 4);
                offset += 4;
                for (var j = 0; j < group.Entries.Count; j++)
                {
                    var entry = group.Entries[j];
                    Buffer.BlockCopy(BitConverter.GetBytes(entry.TrackLikeId), 0, body, offset, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(entry.RemainingValue), 0, body, offset + 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(entry.TargetValue), 0, body, offset + 8, 4);
                    offset += 12;
                }
            }

            Buffer.BlockCopy(BitConverter.GetBytes((uint)groupFlags.Length), 0, body, offset, 4);
            offset += 4;
            Buffer.BlockCopy(groupFlags, 0, body, offset, groupFlags.Length);

            return body;
        }

        private static byte[] NormalizeClaimFlags(byte[] source)
        {
            var flags = new byte[
                SelectCharacterInitializationSnapshot.DailyChallengeClaimFlagCount];
            if (source == null)
                return flags;

            Buffer.BlockCopy(source, 0, flags, 0, Math.Min(source.Length, flags.Length));
            for (var index = 0; index < flags.Length; index++)
                flags[index] = flags[index] == 0 ? (byte)0 : (byte)1;
            return flags;
        }
    }
}
