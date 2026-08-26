using System.Collections.Generic;

namespace DfoServer.Game.Characters
{
    public interface ICharacterRepository
    {
        CharacterRecord GetById(int characterId);
        IReadOnlyList<CharacterRecord> ListByAccount(int accountId);
        int Create(CharacterRecord record);
        void UpdatePosition(int characterId, byte townId, byte areaId, short posX, short posY, byte direction, byte areaState);
        void UpdateSeedFields(int characterId, byte[] name, byte job, byte growType, byte level, byte pvpGrade, byte pvpRatingGrade, byte userState, CharacterAppearanceEntry[] appearance, System.DateTime? createdAt = null);
        void UpdateAppearance(int characterId, CharacterAppearanceEntry[] appearance);
        void SoftDelete(int characterId);
        CharacterRecord GetByName(string name);
        CharacterRecord GetByNameIncludingDeleted(string name);
        int CountByAccount(int accountId);
        void SwapSlotIndexes(int accountId, byte slotA, byte slotB);
        // 删除角色并压缩 slot: 同一事务内软删 + 被删 slot 之后所有活跃角色 slot 前移一位,
        // 保持账号内 slot 连续。两步必须原子——若只软删不前移, 会留下 delete_flag=1 占位的空洞。
        void SoftDeleteAndCompactSlots(int accountId, int characterId, byte slotIndex);
    }
}
