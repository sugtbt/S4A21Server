using System;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    // 进号 USERINFO occ=2：25B subtype 6。
    // 布局：subtype, version=1, ownerCid, 0xFFFFFFFF, 0x68624229，随后两个 u32 为 0xFF。
    public static class UserInfoSubtype6Builder
    {
        public const byte Subtype = 6;
        public const int BodyLength = 25;
        public const ushort Version = 1;
        public const uint UnknownAllBits = 0xFFFFFFFFu;
        public const uint SharedOpaqueConstant = 0x68624229u;
        public const uint TownReadyFlag = 0x000000FFu;

        public static byte[] BuildNotificationBody(int characterId)
        {
            if (characterId <= 0 || characterId > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(characterId), characterId, "USERINFO subtype 6 cid 必须能写入 u16。");

            var writer = new GamePacketWriter();
            writer.WriteByte(Subtype);
            writer.WriteUInt16(Version);
            writer.WriteUInt16((ushort)characterId);
            writer.WriteUInt32(UnknownAllBits);
            writer.WriteUInt32(SharedOpaqueConstant);
            writer.WriteUInt32(TownReadyFlag);
            writer.WriteUInt32(TownReadyFlag);
            writer.WriteUInt32(0);
            var body = writer.ToArray();
            if (body.Length != BodyLength)
                throw new InvalidOperationException($"USERINFO subtype 6 must be {BodyLength}B, got {body.Length}");
            return body;
        }
    }
}
