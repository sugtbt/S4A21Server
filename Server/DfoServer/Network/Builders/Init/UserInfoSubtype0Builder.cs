using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class UserInfoSubtype0Builder
    {
        public static byte[] BuildNotificationBody(CharacterRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteUInt16(1);
            // A21 USERINFO0 在 subtype/版本字段后保留 38B 固定头。
            writer.WriteZeroBytes(38);
            writer.WriteUInt16((ushort)record.CharacterId);
            writer.WriteDstr(record.Name);
            writer.WriteBytes(BuildRemainingBytes(record));
            return writer.ToArray();
        }

        public static byte[] BuildRemainingBytes(CharacterRecord record)
        {
            var writer = new GamePacketWriter();

            
            writer.WriteByte(record.Job);           
            writer.WriteByte(record.GrowType);      
            writer.WriteByte(record.Level);              
            writer.WriteByte(record.PvpGrade);           
            writer.WriteByte(record.PvpRatingGrade);     
            writer.WriteByte(record.UserState);          

            
            var appearances = GetAppearanceEntries(record);
            writer.WriteByte((byte)appearances.Count);   
            foreach (var e in appearances)
                WriteAppearanceEntry(writer, e);

            
            WriteTail(writer, record);

            return writer.ToArray();
        }

        private static void WriteTail(GamePacketWriter writer, CharacterRecord record)
        {
            var t = record.Subtype0Tail ?? new UserInfoMinimumTailSnapshot();
            ApplyOnlineInventoryTailFields(record.CharacterId, t);

            writer.WriteUInt32(t.CloneTitleItemId);
            writer.WriteByte(t.Forging); // 锻造
            writer.WriteByte(t.CreatureField2);             
            writer.WriteByte(t.CreatureField3);             
            writer.WriteByte(t.CreatureField4);             
            writer.WriteUInt32(t.NameTagItemId); // 名称装饰卡ID
            writer.WriteUInt32(t.NameTagExpireTime); // 名称装饰卡到期时间戳
            writer.WriteByte(t.Stamina);                    
            writer.WriteUInt32(t.FatiguePenalty);           
            writer.WriteByte(t.IsEventCharacter);           
            if (t.EquippedCreatureItemId == 0)
            {
                // A21 无宠物固定为 -1 +「没有宠物」+ alive=0xFF。
                writer.WriteUInt32(0xFFFFFFFFu);
                writer.WriteDstr(ClientTextEncoding.GetBytes("没有宠物"));
                writer.WriteByte(0xFF);
            }
            else
            {
                writer.WriteUInt32(t.EquippedCreatureItemId); // 宠物ID
                writer.WriteDstr(t.EquippedCreatureNameBytes); // 宠物名称
                writer.WriteByte(t.EquippedCreatureAliveState); // 宠物存活状态
            }

            // A21 无工会路径使用固定 64B 尾，避免客户端把旧 ProgressB 当作 dstr 长度。
            writer.WriteBytes(BuildA21AfterAliveNoGuild(t));
        }

        private static void ApplyOnlineInventoryTailFields(int characterId, UserInfoMinimumTailSnapshot tail)
        {
            if (characterId <= 0 || tail == null || !InventoryContext.TryGetLease(characterId, out var lease))
                return;

            lock (lease.SyncRoot)
                new Noti2InventoryProjectionBuilder().ApplySubtype0TailDynamicFields(lease.Inventory, tail);
        }

        // A21 无工会 64B 尾：ExpertJobType/Exp 在 +23/+24；ChannelId 在 +59。
        internal const int A21AfterAliveLength = 64;
        internal const int A21AfterAliveExpertJobTypeOffset = 23;
        internal const int A21AfterAliveExpertJobExpOffset = 24;
        internal const int A21AfterAliveChannelIdOffset = 59;

        private static readonly byte[] A21AfterAliveNoGuild =
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0xCE, 0xDE, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00,
        };

        private static byte[] BuildA21AfterAliveNoGuild(UserInfoMinimumTailSnapshot tail)
        {
            var body = (byte[])A21AfterAliveNoGuild.Clone();
            if (tail == null)
                return body;

            body[A21AfterAliveExpertJobTypeOffset] = tail.ExpertJobType;
            var experience = BitConverter.GetBytes(ProjectA21ExpertJobExp(tail));
            Buffer.BlockCopy(
                experience,
                0,
                body,
                A21AfterAliveExpertJobExpOffset,
                sizeof(uint));
            var channelId = BitConverter.GetBytes(
                tail.ChannelId == 0
                    ? (ushort)2
                    : tail.ChannelId);
            Buffer.BlockCopy(
                channelId,
                0,
                body,
                A21AfterAliveChannelIdOffset,
                sizeof(ushort));
            return body;
        }

        // A21 无副职业时经验为 0，不发历史 -1 / uint.MaxValue。
        internal static uint ProjectA21ExpertJobExp(UserInfoMinimumTailSnapshot tail)
        {
            if (tail == null
                || tail.ExpertJobType == 0
                || tail.ExpertJobExp == uint.MaxValue)
                return 0;
            return tail.ExpertJobExp;
        }

        private static List<CharacterAppearanceEntry> GetAppearanceEntries(CharacterRecord record)
        {
            var result = new List<CharacterAppearanceEntry>();
            if (record?.Appearance != null)
            {
                foreach (var e in record.Appearance)
                    AddA21AppearanceEntry(result, e);
            }

            if (result.Count == 0 && record != null && record.CharacterId > 0)
            {
                var fromDb = AppearanceService.LoadCharacterAppearanceFromDb(record);
                if (fromDb != null)
                {
                    foreach (var e in fromDb)
                        AddA21AppearanceEntry(result, e);
                }
            }

            return result;
        }

        private static void AddA21AppearanceEntry(
            List<CharacterAppearanceEntry> result,
            CharacterAppearanceEntry entry)
        {
            if (entry == null || entry.DisplayItemId <= 0)
                return;

            var slot = EquipmentTypeInfo.ToA21AppearanceSlot(entry.Slot);
            if (!EquipmentTypeInfo.IsA21RosterAppearanceSlot(slot))
                return;

            result.Add(new CharacterAppearanceEntry(
                (byte)slot,
                entry.DisplayItemId,
                entry.ExpansionLen,
                entry.ExpansionData,
                entry.State,
                entry.LinkItemId,
                entry.EnchantValue,
                entry.Flag20));
        }

        internal static void WriteAppearanceEntry(GamePacketWriter writer, CharacterAppearanceEntry e)
        {
            writer.WriteByte(e.Slot);
            writer.WriteInt32(e.DisplayItemId);
            writer.WriteInt32(e.ExpansionLen);
            writer.WriteBytes(e.ExpansionData != null && e.ExpansionData.Length == 4
                ? e.ExpansionData : new byte[4]);
            writer.WriteByte(e.State);
            writer.WriteInt32(e.LinkItemId);
            writer.WriteUInt32(e.EnchantValue);
            writer.WriteByte(e.Flag20);
        }
    }
}
