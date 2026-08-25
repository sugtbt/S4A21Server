using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Characters
{
    public sealed class CharacterRecord
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public byte[] Name { get; set; }
        public byte Job { get; set; }
        public byte GrowType { get; set; }
        public int GrowupChangeCount { get; set; }
        public byte Level { get; set; }
        public byte PvpGrade { get; set; }
        public byte PvpRatingGrade { get; set; }
        public byte UserState { get; set; }
        public byte TownId { get; set; }
        public byte AreaId { get; set; }
        public short PosX { get; set; }
        public short PosY { get; set; }
        public byte Direction { get; set; } = 5;
        public byte AreaState { get; set; } = 3;
        public CharacterAppearanceEntry[] Appearance { get; set; }
        public SelectCharacter.UserInfoMinimumTailSnapshot Subtype0Tail { get; set; }
        public uint Exp { get; set; }
        public byte ExEquipSlotStat { get; set; }
        public byte AuraSkinFlag { get; set; }
        public int BonusSp { get; set; }
        public int BonusTp { get; set; }
        public byte SlotIndex { get; set; }
        public bool Deleted { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public string DisplayName
        {
            get
            {
                if (Name == null || Name.Length == 0)
                    return string.Empty;
                return ClientTextEncoding.GetString(Name);
            }
        }
    }
}
