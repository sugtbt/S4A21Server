using System;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Settings;

namespace DfoServer.Network.Builders
{
    public sealed class CharacterOptionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0187;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var saved = snapshot?.InitializationSnapshot?.CharacterOptionBlob;
            var bits = snapshot?.CharacterRecord?.Subtype0Tail?.UserStateBits ?? (byte)3;
            body = AccountSettings.ProjectCharacterOptionBlob(saved, bits);
            return true;
        }
    }
}
