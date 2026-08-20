namespace DfoServer.Game.Mercenary
{
    // 单个角色的支援兵槽位选择状态。
    public sealed class MercenarySupportState
    {
        // 支援兵状态的服务端单例主键，不是客户端 wire slot。
        public const byte SingletonStateKey = 0;

        public int OwnerCharacterId { get; set; }
        public byte Slot { get; set; }
        public int SupportCharacterId { get; set; }
        public ushort SkillId { get; set; }
        // 保存 PVF [striker skill] 第四字段；校验只认 skillId。
        public ushort StrikerSkillId { get; set; }
    }
}
