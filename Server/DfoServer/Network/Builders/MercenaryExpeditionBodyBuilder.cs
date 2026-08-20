using DfoServer.Game.Mercenary;
using System;

namespace DfoServer.Network.Builders
{
    public static class MercenaryExpeditionBodyBuilder
    {
        // 无奖励归队时成功 ACK 第二字节固定为 2，不是请求 purpose 回显。
        public const byte ReturnSuccessStatus = 2;

        public static byte[] BuildInfoSuccess(MercenaryInfoSnapshot snapshot)
        {
            snapshot = snapshot ?? new MercenaryInfoSnapshot();
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte(snapshot.ManageLevel);
            writer.WriteInt32(snapshot.ManagePoint);

            var recordCount = Math.Min(byte.MaxValue, snapshot.Records.Count);
            writer.WriteByte((byte)recordCount);
            for (var i = 0; i < recordCount; i++)
            {
                var record = snapshot.Records[i];
                writer.WriteInt32(record.CharacterId);
                writer.WriteDstr(record.Name);
                writer.WriteByte((byte)record.State);
                writer.WriteInt32(record.RemainingSeconds);
                writer.WriteByte(record.AreaIndex);
                writer.WriteByte(record.PeriodIndex);
                writer.WriteByte(record.AvatarBonusTier);
            }
            return writer.ToArray();
        }

        public static byte[] BuildReturnSuccess(
            int characterId,
            int itemTemplateId,
            int itemCount,
            bool hasReward)
        {
            var hasAreaLoot = itemTemplateId > 0 && itemCount > 0;
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteByte(ReturnSuccessStatus);
            writer.WriteInt32(characterId);
            writer.WriteInt32(hasAreaLoot ? itemTemplateId : 0);
            writer.WriteInt32(hasAreaLoot ? itemCount : 0);
            writer.WriteByte(hasReward ? (byte)1 : (byte)0);
            return writer.ToArray();
        }

        public static byte[] BuildCompetitionSuccess(int characterId, byte areaIndex, byte periodIndex)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(characterId);
            writer.WriteByte(areaIndex);
            writer.WriteByte(periodIndex);
            return writer.ToArray();
        }

        public static byte[] BuildError(byte errorCode)
            => new byte[] { 0, errorCode };
    }
}
