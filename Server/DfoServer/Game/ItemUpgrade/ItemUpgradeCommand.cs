using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.ItemUpgrade
{
    public sealed class ItemUpgradeCommand
    {
        public ItemUpgradeMethod Method { get; set; }
        public ItemUpgradeMode Mode { get; set; }
        public InventoryListType TargetListType { get; set; } = InventoryListType.Main;
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public short MaterialSlotIndex { get; set; }
        public short OptionalTicketSlotIndex { get; set; } = -1;
        public string TargetItemName { get; set; }
    }

    public sealed class ItemUpgradeResult
    {
        public const byte ErrorInvalidTarget = 4;
        public const byte ErrorInventoryFull = 21;
        public const byte ErrorInsufficientGold = 10;
        public const byte ErrorUnsupported = 13;
        public const byte ErrorRestriction = 19;
        public const byte ErrorUnsupportedOptionalTicket = 21;
        public const byte ErrorInvalidMaterial = 22;
        public const byte ErrorWrongUpgradeMode = 23;
        public const byte ErrorDurability = 7;
        public const byte ErrorMaxLevel = 95;
        public const byte ErrorAmplifyNotIdentified = 174;
        public const byte ErrorLocked = 213;

        public ItemUpgradeCommand Command { get; set; }
        public bool Success { get; set; }
        public byte ErrorCode { get; set; }
        public ItemUpgradeMethod Method { get; set; }
        public ItemUpgradeMode Mode { get; set; }
        public ItemUpgradeScene Scene { get; set; }
        public InventoryListType TargetListType { get; set; } = InventoryListType.Main;
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public short MaterialSlotIndex { get; set; }
        public int MaterialItemTemplateId { get; set; }
        public short OptionalTicketSlotIndex { get; set; } = -1;
        public byte OldLevel { get; set; }
        public byte NewLevel { get; set; }
        public byte ResultCode { get; set; }
        public bool UpgradeSucceeded { get; set; }
        public int FinalSuccessWeight { get; set; }
        public int MaterialRemainingStackCount { get; set; }
        public List<ItemUpgradeRewardItem> DestroyRewardItems { get; } = new List<ItemUpgradeRewardItem>();
        public List<short> MainRefreshSlots { get; } = new List<short>();
        public int GoldCost { get; set; }
        public int UpdatedGold { get; set; }
        public bool NoticeRequired { get; set; }
        internal ItemCore TargetItemSnapshot { get; set; }

        public static ItemUpgradeResult Error(ItemUpgradeCommand command, byte errorCode)
        {
            return new ItemUpgradeResult
            {
                Command = command,
                Method = command != null ? command.Method : ItemUpgradeMethod.Reinforce,
                Mode = command != null ? command.Mode : ItemUpgradeMode.Reinforce,
                TargetListType = command != null ? command.TargetListType : InventoryListType.Main,
                TargetSlotIndex = command != null ? command.TargetSlotIndex : (short)-1,
                TargetItemTemplateId = command != null ? command.TargetItemTemplateId : 0,
                MaterialSlotIndex = command != null ? command.MaterialSlotIndex : (short)-1,
                OptionalTicketSlotIndex = command != null ? command.OptionalTicketSlotIndex : (short)-1,
                ErrorCode = errorCode,
            };
        }
    }

    public sealed class ItemUpgradeRewardItem
    {
        public short SlotIndex { get; set; }
        public int ItemTemplateId { get; set; }
        public int Count { get; set; }
    }

    internal sealed class ItemUpgradeSlotCount
    {
        public short SlotIndex { get; set; }
        public int ItemTemplateId { get; set; }
        public int Count { get; set; }
    }
}
