using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class StrikerSupportTagCharacterBodyBuilder : IInitPacketBuilder
    {
        private readonly IGameDatabase _database;

        public StrikerSupportTagCharacterBodyBuilder()
            : this(GameDatabase.CreateDefault())
        {
        }

        public StrikerSupportTagCharacterBodyBuilder(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public ushort NotiType => (ushort)NotiPacketTypeA21.TAG_CHARACTER_INFO;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            if (occurrenceIndex == 0)
            {
                var characterId = snapshot.CharacterRecord?.CharacterId ?? 0;
                if (StrikerSupportTagCharacterPacketBuilder.TryBuildOwnerSupportBody(
                        characterId,
                        _database,
                        out body))
                    return true;

                body = BuildEmptyBody();
                return true;
            }

            body = null;
            return false;
        }

        internal static byte[] BuildEmptyBody()
        {
            return new byte[] { 0x00, 0x00 };
        }
    }
}
