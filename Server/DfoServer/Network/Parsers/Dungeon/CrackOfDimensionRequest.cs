using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal readonly struct CrackOfDimensionRequest
    {
        internal const int MinimumBodyLength = 8;

        private CrackOfDimensionRequest(
            int historicalDungeonId,
            int crackQuestId,
            int trailingLength,
            bool hasNonZeroTrailingBytes)
        {
            HistoricalDungeonId = historicalDungeonId;
            CrackQuestId = crackQuestId;
            TrailingLength = trailingLength;
            HasNonZeroTrailingBytes = hasNonZeroTrailingBytes;
        }

        internal int HistoricalDungeonId { get; }
        internal int CrackQuestId { get; }
        internal int TrailingLength { get; }
        internal bool HasNonZeroTrailingBytes { get; }

        internal static bool TryParse(
            byte[] body,
            out CrackOfDimensionRequest request)
        {
            request = default;
            if (body == null || body.Length < MinimumBodyLength)
                return false;

            var historicalDungeonId = BitConverter.ToUInt32(body, 0);
            var crackQuestId = BitConverter.ToUInt32(body, 4);
            if (historicalDungeonId > int.MaxValue
                || crackQuestId > int.MaxValue)
            {
                return false;
            }

            var hasNonZeroTrailingBytes = false;
            for (var index = MinimumBodyLength; index < body.Length; index++)
            {
                if (body[index] != 0)
                {
                    hasNonZeroTrailingBytes = true;
                    break;
                }
            }

            request = new CrackOfDimensionRequest(
                (int)historicalDungeonId,
                (int)crackQuestId,
                body.Length - MinimumBodyLength,
                hasNonZeroTrailingBytes);
            return true;
        }
    }
}
