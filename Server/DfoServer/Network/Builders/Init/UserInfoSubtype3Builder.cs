using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using System.IO;

namespace DfoServer.Network.Builders
{
    /// <summary>
    /// USERINFO subtype 3：城镇查看他人（GET_USERINFO mode=3）。
    /// 头为 subtype/ver/manageLevel/5B 零/uid；战斗块 88B 后接
    /// ExEquipSlotStat。公会 dstr 前 27B 目前写 0。
    /// </summary>
    public static class UserInfoSubtype3Builder
    {
        public const byte Subtype = 3;
        public const ushort Version = 1;
        public const int PrefixPaddingLength = 5;
        public const int InspectContextLength = 27;
        public const byte InspectGuildMarker = 0x6F;

        public static byte[] BuildNotificationBody(
            ushort targetUserId,
            UserInfoAdditionSnapshot addition,
            SkillInfoSnapshot skills,
            CharacterRecord characterRecord)
        {
            if (addition == null)
                throw new InvalidDataException(
                    "USERINFO subtype 3 requires UserInfoAddition");

            var writer = new GamePacketWriter();
            writer.WriteByte(Subtype);
            writer.WriteUInt16(Version);
            writer.WriteUInt16(addition.ManageLevel);
            writer.WriteZeroBytes(PrefixPaddingLength);
            writer.WriteUInt16(targetUserId);

            writer.WriteUInt32(addition.CharacExp);
            writer.WriteInt32(CombatStatBlobWriter.BlobLength);
            CombatStatBlobWriter.Write(writer, addition);
            writer.WriteByte(addition.ExEquipSlotStat);
            WriteEquipment(writer, addition, characterRecord?.Appearance);

            writer.WriteUInt32(addition.CloneTitleItemId);
            writer.WriteUInt32(addition.NameTagItemId);
            writer.WriteUInt32(addition.NameTagExpireTime);

            writer.WriteByte(addition.SkillTreeIndex);
            WriteSkillPage(writer, skills, 0);
            WriteSkillPage(writer, skills, 1);
            writer.WriteByte(addition.EquippedCreatureLevel);

            WriteInspectContext(writer);
            WritePostContextFields(
                writer,
                addition,
                characterRecord?.Subtype0Tail);
            return writer.ToArray();
        }

        private static void WriteInspectContext(GamePacketWriter writer)
        {
            // 公会 dstr 前 6×u32 + 3B，字段所有者未定时写 0。
            for (var index = 0; index < 6; index++)
                writer.WriteUInt32(0);
            writer.WriteZeroBytes(3);
        }

        private static void WritePostContextFields(
            GamePacketWriter writer,
            UserInfoAdditionSnapshot addition,
            UserInfoMinimumTailSnapshot minimumTail)
        {
            writer.WriteDstr(minimumTail?.GuildNameBytes);
            writer.WriteByte(minimumTail?.GuildLevel ?? 0);
            writer.WriteByte(0);
            writer.WriteByte(InspectGuildMarker);
            writer.WriteUInt32(
                (uint)addition.SpecialRewardQuestIds.Count);
            foreach (var questId in addition.SpecialRewardQuestIds)
                writer.WriteUInt32(questId);
            writer.WriteByte(0);
        }

        private static void WriteEquipment(
            GamePacketWriter writer,
            UserInfoAdditionSnapshot addition,
            CharacterAppearanceEntry[] appearance)
        {
            var equipped = UserInfoSubtype1Builder.MergeA21FashionEntries(
                addition.EquippedEntries,
                appearance);
            if (equipped.Count > byte.MaxValue)
            {
                throw new InvalidDataException(
                    "USERINFO subtype 3 equipped count exceeds 255");
            }

            writer.WriteByte((byte)equipped.Count);
            foreach (var entry in equipped)
            {
                if (entry?.Core == null)
                {
                    throw new InvalidDataException(
                        $"USERINFO subtype 3 slot {entry?.Slot}: " +
                        "ItemCore is unavailable");
                }

                ItemListProtocolWriter.WriteNoti2EquippedEntry(
                    writer,
                    entry.Slot,
                    entry.Core,
                    addition.GetAvatarDetail(entry.Core),
                    addition.GetCreatureDetail(entry.Core));
            }
        }

        private static void WriteSkillPage(
            GamePacketWriter writer,
            SkillInfoSnapshot skills,
            int pageIndex)
        {
            if (skills == null || pageIndex >= skills.Pages.Count)
            {
                writer.WriteByte(0);
                return;
            }

            var page = skills.Pages[pageIndex];
            var count = 0;
            foreach (var entry in page.Entries)
            {
                if (entry != null && entry.Level > 0)
                    count++;
            }

            if (count > byte.MaxValue)
            {
                throw new InvalidDataException(
                    "USERINFO subtype 3 skill page exceeds 255 entries");
            }

            writer.WriteByte((byte)count);
            foreach (var entry in page.Entries)
            {
                if (entry == null || entry.Level <= 0)
                    continue;
                writer.WriteUInt16(entry.SkillId);
                writer.WriteByte(entry.Level);
            }
        }
    }
}
