using System;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class HotkeyConfigBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x01C7;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            if (init.HotkeyConfigSlots.Count == 0)
            {
                body = null;
                return false;
            }
            body = AccountSettingsPacketBuilder.BuildHotkeyOptionBody(init.HotkeyKeyType, init.HotkeyConfigSlots);
            return true;
        }
    }
}
