using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    
    
    public sealed class GameOptionBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x00AD;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            // 账号无任何已保存设置时不下发 00AD，由客户端使用本地默认。
            if (init.MainGameOptionBlob == null
                && init.QuickchatBank0 == null
                && init.QuickchatBank1 == null)
            {
                body = null;
                return false;
            }

            var main = init.MainGameOptionBlob ?? Array.Empty<byte>();
            var bank0 = init.QuickchatBank0 ?? Array.Empty<byte>();
            var bank1 = init.QuickchatBank1 ?? Array.Empty<byte>();

            body = AccountSettingsPacketBuilder.BuildGameOptionBody(main, bank0, bank1);
            return true;
        }
    }
}
