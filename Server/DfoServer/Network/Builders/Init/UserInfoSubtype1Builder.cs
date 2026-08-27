using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class UserInfoSubtype1Builder
    {
        public static byte[] BuildFromSnapshot(
            UserInfoAdditionSnapshot addition,
            SkillInfoSnapshot skills)
            => BuildFromSnapshot(addition, skills, appearance: null);

        public static byte[] BuildFromSnapshot(
            UserInfoAdditionSnapshot addition,
            SkillInfoSnapshot skills,
            CharacterAppearanceEntry[] appearance)
        {
            if (addition == null)
                throw new ArgumentNullException(nameof(addition));

            var equipped = MergeA21FashionEntries(
                addition.EquippedEntries,
                appearance);
            var writer = new GamePacketWriter();

            writer.WriteUInt32(addition.CharacExp);
            writer.WriteInt32(CombatStatBlobWriter.BlobLength);
            CombatStatBlobWriter.Write(writer, addition);
            writer.WriteByte(addition.ExEquipSlotStat);

            writer.WriteByte((byte)Math.Min(byte.MaxValue, equipped.Count));
            foreach (var entry in equipped)
            {
                var core = entry?.Core;
                if (core == null)
                    throw new InvalidDataException(
                        $"[UserInfoSubtype1Builder] slot {entry?.Slot}: ItemCore 未初始化，不能写入 subtype1 装备 entry。");

                ItemListProtocolWriter.WriteNoti2EquippedEntry(
                    writer,
                    entry.Slot,
                    core,
                    addition.GetAvatarDetail(core),
                    addition.GetCreatureDetail(core));
            }

            writer.WriteUInt32(addition.CloneTitleItemId);
            writer.WriteUInt32(addition.NameTagItemId);
            writer.WriteUInt32(addition.NameTagExpireTime);
            writer.WriteByte(DfoServer.Game.Skills.SkillTreeExpansionState.LockedWireValue);
            WriteSkillPage(writer, skills, 0);
            WriteSkillPage(writer, skills, 1);
            writer.WriteByte(addition.EquippedCreatureLevel);
            WriteA21DimensionTail(writer, addition);
            return writer.ToArray();
        }

        internal static IList<EquippedEntrySnapshot> MergeA21FashionEntries(
            IList<EquippedEntrySnapshot> equipped,
            CharacterAppearanceEntry[] appearance)
        {
            var result = new List<EquippedEntrySnapshot>();
            var slots = new HashSet<short>();
            if (equipped != null)
            {
                foreach (var entry in equipped)
                {
                    if (entry == null)
                        continue;
                    result.Add(entry);
                    slots.Add(entry.Slot);
                }
            }

            if (appearance != null)
            {
                foreach (var entry in appearance)
                {
                    if (entry == null || entry.DisplayItemId <= 0 || entry.Slot > 7)
                        continue;
                    if (!slots.Add(entry.Slot))
                        continue;

                    result.Add(new EquippedEntrySnapshot
                    {
                        Slot = entry.Slot,
                        Core = ItemCore.Create(ItemCore.KindAvatar, entry.DisplayItemId),
                    });
                }
            }

            return result;
        }

        private static readonly byte[] A21AfterDimensionPrefix =
        {
            0x02, 0x00, 0x05, 0x00, 0x6F,
        };

        private static readonly byte[] A21AfterDimensionRest =
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        private static void WriteA21DimensionTail(
            GamePacketWriter writer,
            UserInfoAdditionSnapshot addition)
        {
            var stored = new Dictionary<uint, DimensionEntrySnapshot>();
            if (addition.Dimensions != null)
            {
                foreach (var entry in addition.Dimensions)
                    stored[entry.Key] = entry;
            }

            writer.WriteByte((byte)SpecialDungeonEntryLimitDefaults.Entries.Length);
            foreach (var defaultEntry in SpecialDungeonEntryLimitDefaults.Entries)
            {
                var key = (uint)defaultEntry.DungeonId;
                byte value1 = 0;
                var value2 = defaultEntry.CurrentCount;
                if (stored.TryGetValue(key, out var saved))
                {
                    value2 = saved.Val2;
                }

                writer.WriteUInt32(key);
                writer.WriteByte(value1);
                writer.WriteByte(value2);
            }

            // A21 consumes the values after 0x6F as completed quest ids and
            // resolves each QST [special reward status] locally. The repository
            // already filters this snapshot to completed special-reward quests.
            writer.WriteBytes(A21AfterDimensionPrefix);
            writer.WriteUInt32((uint)addition.SpecialRewardQuestIds.Count);
            foreach (var questId in addition.SpecialRewardQuestIds)
                writer.WriteUInt32(questId);
            writer.WriteByte(addition.ManageLevel);
            writer.WriteUInt32(unchecked((uint)addition.ManagePoint));
            writer.WriteBytes(A21AfterDimensionRest);
        }

        private static void WriteSkillPage(
            GamePacketWriter writer,
            SkillInfoSnapshot skills,
            int pageIndex)
        {
            if (A21ShouldOmitCopiedPage1(skills) && pageIndex == 1)
            {
                writer.WriteByte(0);
                return;
            }

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

            writer.WriteByte((byte)Math.Min(byte.MaxValue, count));
            foreach (var entry in page.Entries)
            {
                if (entry == null || entry.Level <= 0)
                    continue;
                writer.WriteUInt16(entry.SkillId);
                writer.WriteByte(entry.Level);
            }
        }

        internal static bool A21ShouldOmitCopiedPage1(SkillInfoSnapshot skills)
        {
            if (skills == null || skills.Pages.Count < 2)
                return false;

            var page0 = GetLeveledSkills(skills.Pages[0]);
            var page1 = GetLeveledSkills(skills.Pages[1]);
            if (page0.Count == 0 || page0.Count != page1.Count)
                return false;

            for (var i = 0; i < page0.Count; i++)
            {
                if (page0[i].SkillId != page1[i].SkillId
                    || page0[i].Level != page1[i].Level)
                    return false;
            }

            return true;
        }

        private static List<SkillInfoEntrySnapshot> GetLeveledSkills(
            SkillInfoPageSnapshot page)
        {
            var result = new List<SkillInfoEntrySnapshot>();
            if (page?.Entries == null)
                return result;

            foreach (var entry in page.Entries)
            {
                if (entry != null && entry.Level > 0)
                    result.Add(entry);
            }

            return result;
        }
    }
}
