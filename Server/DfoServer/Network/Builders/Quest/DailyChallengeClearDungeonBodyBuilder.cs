using System;

namespace DfoServer.Network.Builders
{
    internal static class DailyChallengeClearDungeonBodyBuilder
    {
        // The verified A21 client handler consumes exactly one UInt32 and does
        // not interpret it further. Keep a stable per-settlement token so the
        // packet also remains compatible with clients that deduplicate it.
        internal static byte[] Build(uint completionToken)
            => BitConverter.GetBytes(completionToken);

        internal static uint ResolveCompletionToken(Guid sourceEventId)
        {
            if (sourceEventId == Guid.Empty)
                throw new ArgumentException(
                    "A stable dungeon-clear event id is required.",
                    nameof(sourceEventId));

            unchecked
            {
                var hash = 2166136261u;
                foreach (var value in sourceEventId.ToByteArray())
                    hash = (hash ^ value) * 16777619u;
                return hash == 0 ? 1u : hash;
            }
        }
    }
}
