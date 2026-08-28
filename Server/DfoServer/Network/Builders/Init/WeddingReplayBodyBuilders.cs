using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class WeddingInfoBodyBuilder : IInitPacketBuilder
    {
        private static readonly byte[] Body = Convert.FromHexString("01010101040101010101E703000005");

        public ushort NotiType => (ushort)NotiPacketTypeA21.WEDDING_INFO;

        // 常量回放包体，不依赖选角快照，可供选角序列外的补发路径直接复用。
        public static byte[] BuildBody()
        {
            return (byte[])Body.Clone();
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = BuildBody();
            return true;
        }
    }

    public sealed class WeddingCharacCmdBodyBuilder : IInitCmdPacketBuilder
    {
        private static readonly byte[] Body = new byte[31];

        public ushort CmdType => (ushort)CmdPacketTypeA21.WEDDING_CHARAC;

        public static byte[] BuildBody()
        {
            return (byte[])Body.Clone();
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, out byte[] body)
        {
            body = BuildBody();
            return true;
        }
    }

    public static class CoupleRoomBodyBuilder
    {
        private static readonly byte[] Body = Convert.FromHexString(
            "1000000000006EA19800010071A19800190072A19800140025A198000D0005A1980007001BA1980004000AA1980003005CA19800130004A1980009001DA1980010000FA19800160010A19800060062A198000A0065A19800150004A19800060021A1980003000000EE3005691E00000001");

        public static byte[] BuildBody()
        {
            return (byte[])Body.Clone();
        }
    }
}
