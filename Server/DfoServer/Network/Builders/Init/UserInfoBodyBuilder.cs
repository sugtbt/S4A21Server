using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class UserInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.USERINFO;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var c = snapshot.CharacterRecord;
            if (c == null) { body = null; return false; }

            if (occurrenceIndex == 1)
            {
                var addition = snapshot.InitializationSnapshot.UserInfoAddition;
                if (addition == null)
                {
                    DfoServer.FileLogger.Log("[UserInfoBodyBuilder] ERROR: occ1 UserInfoAddition is null — 结构化表未迁移。不兜底 blob。");
                    body = null; return false;
                }
                var w = new GamePacketWriter();
                WriteA21Subtype1Prefix(
                    w,
                    (ushort)c.CharacterId,
                    addition.ManageLevel,
                    addition.AuraSkinFlag);
                w.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(
                    addition,
                    snapshot.InitializationSnapshot.SkillInfo,
                    c.Appearance));
                body = w.ToArray(); return true;
            }

            // occ=0 创建 USERINFO0；occ=3 为 HOTKEY 前的 USERINFO0 更新。
            if (occurrenceIndex == 0 || occurrenceIndex == 3)
            {
                body = UserInfoSubtype0Builder.BuildNotificationBody(c);
                return true;
            }

            // 进号 CERA / 0x01BA 之前发 25B USERINFO subtype 6。
            if (occurrenceIndex == 2)
            {
                body = UserInfoSubtype6Builder.BuildNotificationBody(c.CharacterId);
                return true;
            }

            DfoServer.FileLogger.Log($"[UserInfoBodyBuilder] ERROR: 不支持的 occurrence {occurrenceIndex} — init 流只有 occ0/1/2/3。");
            body = null;
            return false;
        }

        internal static void WriteA21Subtype1Prefix(
            GamePacketWriter writer,
            ushort characterId,
            byte manageLevel,
            byte auraSkinFlag)
        {
            writer.WriteByte(1);
            writer.WriteUInt16(1);
            var prefix = new byte[15];
            prefix[6] = manageLevel;
            prefix[14] = auraSkinFlag != 0 ? (byte)1 : (byte)0;
            writer.WriteBytes(prefix);
            writer.WriteUInt16(characterId);
        }
    }
}
