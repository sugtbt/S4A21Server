using System;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders
{
    public sealed class DimensionGateEntranceInfoBodyBuilder : IInitPacketBuilder
    {
        private readonly DungeonEntryLimitService _entryLimits;

        public DimensionGateEntranceInfoBodyBuilder()
            : this(GameDatabase.CreateDefault())
        {
        }

        internal DimensionGateEntranceInfoBodyBuilder(IGameDatabase database)
        {
            _entryLimits = new DungeonEntryLimitService(
                database ?? throw new ArgumentNullException(nameof(database)));
        }

        public ushort NotiType =>
            (ushort)NotiPacketTypeA21.DIMENSION_GATE_ENTRANCE_INFO;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex,
            out byte[] body)
        {
            var characterId = snapshot?.CharacterRecord?.CharacterId ?? 0;
            if (characterId <= 0)
            {
                body = null;
                return false;
            }

            var config = DimensionGateEntryLimitConfigProvider.Get();
            var state = _entryLimits.LoadDimensionGateLimit(
                characterId,
                config.DailyDefaultEnterCount,
                config.DailyDefaultExtraEnterCount);
            body = Build(
                state?.CurrentCount ?? config.DailyDefaultEnterCount,
                state?.ExtraCount ?? config.DailyDefaultExtraEnterCount);
            return true;
        }

        internal static byte[] Build(int remainingCount, int extraCount)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)Math.Max(0, remainingCount));
            writer.WriteUInt32((uint)Math.Max(0, extraCount));
            return writer.ToArray();
        }
    }
}
