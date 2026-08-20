using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Quests
{
    public enum QuestFinishType : byte
    {
        Seeking = 0x00,
        ConditionUnderClear = 0x01,
        HuntMonster = 0x02,
        MeetNpc = 0x04,
        HuntEnemy = 0x06,
        CustomQuest = 0x0A,
        UseItem = 0x0B,
    }

    // 任务四个命令(接取/放弃/触发器/完成)的结构化处理结果。
    // QuestService 只产出这些对象; 序列化成应答包字节的工作全部在
    // QuestAckBuilder。ErrorCode==0 表示成功, 非零值直接进失败应答包。
    public sealed class QuestAcceptResult
    {
        public byte ErrorCode;
        public ushort QuestId;
        // ACCEPT ACK 先用 PVF/QST 初值建立客户端任务模型。
        public uint InitTrigger;
        // 事务内按角色当前状态校准并实际落库的权威值。
        public uint CommittedTrigger;
        // 两者不同时，由网络适配层在 ACCEPT ACK 后投影一次 SET_TRIGGER。
        public QuestSetTriggerResult PostAcceptTriggerProjection;
        public List<QuestEventItemGrant> EventItems = new List<QuestEventItemGrant>();

        public bool Success => ErrorCode == 0;

        public static QuestAcceptResult Fail(byte errorCode) => new QuestAcceptResult { ErrorCode = errorCode };
    }

    // 接取时发放的事件道具(应答包里逐条回显给客户端)。
    public sealed class QuestEventItemGrant
    {
        public ushort SlotIndex;
        public int ItemId;
        public int Count;
    }

    public sealed class QuestGiveupResult
    {
        public byte ErrorCode;
        public ushort QuestId;
        internal InventoryMutationSet InventoryChanges { get; } =
            new InventoryMutationSet();

        public bool Success => ErrorCode == 0;

        public static QuestGiveupResult Fail(byte errorCode) => new QuestGiveupResult { ErrorCode = errorCode };
    }

    public sealed class QuestSetTriggerResult
    {
        public byte ErrorCode;
        public ushort QuestId;
        public uint PreviousTriggerValue;
        public uint TriggerValue;

        public bool Success => ErrorCode == 0;

        public static QuestSetTriggerResult Fail(byte errorCode) => new QuestSetTriggerResult { ErrorCode = errorCode };
    }

    public sealed class QuestFinishResult
    {
        public byte ErrorCode;
        public ushort QuestId;
        // Application service projects the normalized PVF type before serialization.
        public QuestFinishType FinishType;
        // Exp 为任务规则处理后的最终奖励，HonorExp 为其中转入账号荣誉的部分。
        public uint Exp;
        public uint HonorExp;
        public ulong TotalHonorExp;
        public uint GrowthCapsuleExp;
        public uint TotalGrowthCapsuleExp;
        // A21 captures keep the 4B field after Exp at zero. Quest gold is
        // projected only through an inserted reward entry with ItemId=0.
        public uint ReservedAfterExperience;
        // 经验结算后的等级与总经验(与奖励同一事务已落库; Exp 为 0 时等于结算前取值)。
        public byte NewLevel;
        public uint NewExp;
        public int ChainType;
        // A21 ordinary reward entries share one acquisition timestamp per completion.
        public uint RewardAcquiredAtUnixTime;
        // chainType 1/2=转职号, 20=专家职类型, 30=开孔的装备栏位号。
        public int GrowNumber;
        public PetCreatureEvolutionResult PetCreatureEvolution;
        public List<ConsumedItemEntry> ConsumedEntries = new List<ConsumedItemEntry>();
        public List<InsertedItemEntry> InsertedEntries = new List<InsertedItemEntry>();
        // chainType 20 ACK 在 growNumber 后写两页压缩技能。
        public List<QuestFinishSkillPage> SkillPages = new List<QuestFinishSkillPage>();

        public bool Success => ErrorCode == 0;

        public static QuestFinishResult Fail(byte errorCode) => new QuestFinishResult { ErrorCode = errorCode };
    }

    // 客户端 CMDFUNC_FINISH_QUEST 对此字段做减法 (currentStack -= value)，必须填扣除量。
    public sealed class ConsumedItemEntry
    {
        public byte UpdateType;
        public ushort SlotIndex;
        public uint ConsumedCount;
        // All current A21 FINISH_QUEST capture samples keep this byte at zero.
        public byte ReservedTail;
    }

    public sealed class QuestFinishSkillEntry
    {
        public byte Slot;
        public ushort SkillId;
        public byte Level;
    }

    public sealed class QuestFinishSkillPage
    {
        public List<QuestFinishSkillEntry> Entries = new List<QuestFinishSkillEntry>();
    }

    public sealed class InsertedItemEntry
    {
        public ushort SlotIndex;
        public int ItemId;
        // FINISH_QUEST projects the amount granted, not an equipment instance seed.
        public uint GrantedCount;
    }
}
