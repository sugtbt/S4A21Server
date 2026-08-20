using System.Collections.Generic;
using DfoServer.Game.Quests;

namespace DfoServer.Network.Builders
{
    // 任务四个命令应答包的唯一序列化点 -- 应答字节格式只出现在这里,
    // 由 QuestAckFormatSelfTest 逐字节冻结。业务侧(QuestService/QuestManager)
    // 只与 QuestResults 里的结构化对象打交道。
    public static class QuestAckBuilder
    {
        public static byte[] BuildAccept(QuestAcceptResult r)
        {
            if (!r.Success) return BuildFail(r.ErrorCode);

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(r.QuestId);
            w.WriteUInt32(r.InitTrigger);
            w.WriteByte((byte)r.EventItems.Count);
            foreach (var item in r.EventItems)
            {
                w.WriteUInt16(item.SlotIndex);
                w.WriteUInt32((uint)item.ItemId);
                w.WriteUInt32((uint)item.Count);
            }
            return w.ToArray();
        }

        public static byte[] BuildGiveup(QuestGiveupResult r)
        {
            if (!r.Success) return BuildFail(r.ErrorCode);

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(r.QuestId);
            return w.ToArray();
        }

        public static byte[] BuildSetTrigger(QuestSetTriggerResult r)
        {
            if (!r.Success) return BuildFail(r.ErrorCode);

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(r.QuestId);
            w.WriteUInt32(r.TriggerValue);
            return w.ToArray();
        }

        public static byte[] BuildFinish(QuestFinishResult r)
        {
            if (!r.Success) return BuildFail(r.ErrorCode);

            var w = new GamePacketWriter();
            w.WriteByte(0x01);
            w.WriteUInt16(r.QuestId);
            w.WriteByte((byte)r.FinishType);
            w.WriteUInt32(r.Exp);
            w.WriteUInt32(r.ReservedAfterExperience);

            if (r.ChainType == GameWorld.QuestData.ChainTypeTitle)
            {
                // A21 title/achievement branch uses the legacy 7B consume
                // entry and terminates with chain type 5.  The reserved tail
                // used by ordinary 8B entries is absent in this branch.
                if (r.ConsumedEntries.Count > 0)
                {
                    w.WriteByte((byte)r.ConsumedEntries.Count);
                    foreach (var ce in r.ConsumedEntries)
                        WriteConsumedEntryWithoutReservedTail(w, ce);
                }
                w.WriteByte((byte)GameWorld.QuestData.ChainTypeTitle);
                return w.ToArray();
            }

            if (r.ChainType == 20)
            {
                // chain 20：7 字节 consume，随后 chain/grow 与两页压缩技能。
                // 8 字节 consume 会错位并导致客户端闪退。
                w.WriteByte((byte)r.ConsumedEntries.Count);
                foreach (var ce in r.ConsumedEntries)
                    WriteConsumedEntryWithoutReservedTail(w, ce);
                w.WriteByte((byte)r.ChainType);
                w.WriteByte((byte)r.GrowNumber);
                WriteExpertJobSkillPages(w, r.SkillPages);
                return w.ToArray();
            }

            w.WriteByte((byte)r.ConsumedEntries.Count);
            foreach (var ce in r.ConsumedEntries)
            {
                w.WriteByte(ce.UpdateType);
                w.WriteUInt16(ce.SlotIndex);
                w.WriteUInt32(ce.ConsumedCount);
                w.WriteByte(ce.ReservedTail);
            }

            if (r.ChainType == 0)
            {
                w.WriteByte((byte)r.InsertedEntries.Count);
                foreach (var ie in r.InsertedEntries)
                {
                    w.WriteUInt16(ie.SlotIndex);
                    w.WriteUInt32((uint)ie.ItemId);
                    w.WriteUInt32(ie.GrantedCount);
                    w.WriteByte(0); // upgradeLevel
                    w.WriteUInt16(0); // durability
                    w.WriteUInt32(r.RewardAcquiredAtUnixTime);
                    w.WriteUInt16(0); // A21 entry tail
                }
            }
            else if (r.ChainType == 1 || r.ChainType == 2
                || r.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion)
            {
                w.WriteByte((byte)r.ChainType);
                w.WriteByte((byte)r.GrowNumber);
                w.WriteByte(0); // npcCount layer 1
                w.WriteByte(0); // npcCount layer 2
            }
            else
            {
                w.WriteByte((byte)r.ChainType);
            }
            return w.ToArray();
        }

        private static void WriteConsumedEntryWithoutReservedTail(
            GamePacketWriter writer,
            ConsumedItemEntry entry)
        {
            writer.WriteByte(entry.UpdateType);
            writer.WriteUInt16(entry.SlotIndex);
            writer.WriteUInt32(entry.ConsumedCount);
        }

        private static void WriteExpertJobSkillPages(
            GamePacketWriter writer,
            List<QuestFinishSkillPage> pages)
        {
            for (var pageIndex = 0; pageIndex < 2; pageIndex++)
            {
                var entries = pageIndex < (pages?.Count ?? 0)
                    && pages[pageIndex]?.Entries != null
                    ? pages[pageIndex].Entries
                    : null;
                var count = entries == null
                    ? 0
                    : System.Math.Min(byte.MaxValue, entries.Count);
                writer.WriteByte((byte)count);
                for (var index = 0; index < count; index++)
                {
                    var entry = entries[index];
                    if (entry == null)
                    {
                        writer.WriteByte(0);
                        writer.WriteUInt16(0);
                        writer.WriteByte(0);
                        continue;
                    }

                    writer.WriteByte(entry.Slot);
                    writer.WriteUInt16(entry.SkillId);
                    writer.WriteByte(entry.Level);
                }
            }
        }

        private static byte[] BuildFail(byte errorCode)
        {
            return new byte[] { 0x00, errorCode };
        }
    }
}
