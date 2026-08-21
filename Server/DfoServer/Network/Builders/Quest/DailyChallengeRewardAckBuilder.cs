using DfoServer.Game.Quests;

namespace DfoServer.Network.Builders
{
    internal static class DailyChallengeRewardAckBuilder
    {
        internal static byte[] Build(DailyChallengeRewardClaimResult result)
        {
            if (result == null || !result.ClientSuccess)
                return new byte[] { 0, 0 };

            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(result.GroupIndex);
            // A21 exposes only group indices 0-4. Keep the migrated reserved
            // field zero until the current client establishes another meaning.
            writer.WriteInt32(0);
            return writer.ToArray();
        }
    }
}
