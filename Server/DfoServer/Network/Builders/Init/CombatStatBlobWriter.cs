using DfoServer.Game.SelectCharacter;
using System;

namespace DfoServer.Network.Builders
{
    // USERINFO subtype1 与 TAG_CHARACTER_INFO 0x019F 共用的 88B 战斗属性块。
    internal static class CombatStatBlobWriter
    {
        public const int BlobLength = 88;
        public const ushort MiddleMarker = 5500;
        public const uint TrailingConstant = 100;
        public const int MiddleZeroUInt16Count = 16;

        public static byte[] Build(UserInfoAdditionSnapshot addition)
        {
            var writer = new GamePacketWriter();
            Write(writer, addition);
            var blob = writer.ToArray();
            if (blob.Length != BlobLength)
            {
                throw new InvalidOperationException(
                    $"combat stat blob length {blob.Length} != {BlobLength}");
            }

            return blob;
        }

        public static void Write(GamePacketWriter writer, UserInfoAdditionSnapshot addition)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (addition == null)
                throw new ArgumentNullException(nameof(addition));

            writer.WriteUInt32(addition.StatHpMax);
            writer.WriteUInt32(addition.StatMpMax);
            writer.WriteInt16(addition.StatPhysicalAttack);
            writer.WriteInt16(addition.StatPhysicalDefense);
            writer.WriteInt16(addition.StatMagicalAttack);
            writer.WriteInt16(addition.StatMagicalDefense);
            writer.WriteInt16(addition.StatFireResistance);
            writer.WriteInt16(addition.StatWaterResistance);
            writer.WriteInt16(addition.StatDarkResistance);
            writer.WriteInt16(addition.StatLightResistance);
            for (var i = 0; i < MiddleZeroUInt16Count; i++)
                writer.WriteUInt16(0);
            writer.WriteUInt16(MiddleMarker);
            writer.WriteUInt32(addition.StatInventoryLimit);
            writer.WriteUInt16(addition.StatHpRegenSpeed);
            writer.WriteUInt16(addition.StatMpRegenSpeed);
            writer.WriteUInt32(addition.StatMoveSpeed);
            writer.WriteUInt16(addition.StatAttackSpeed);
            writer.WriteUInt16(addition.StatCastSpeed);
            writer.WriteUInt16(addition.StatHitRecovery);
            writer.WriteUInt16(addition.StatJumpPower);
            writer.WriteUInt32(addition.StatWeight);
            writer.WriteUInt32(TrailingConstant);
            writer.WriteByte(0);
            writer.WriteByte(0);
        }
    }
}
