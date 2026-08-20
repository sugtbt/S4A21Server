using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Mailbox
{
    public enum MailboxSendError
    {
        None,
        InvalidRequest,
        EmptyContent,
        ReceiverNotFound,
        ReceiverDeleted,
        InsufficientGold,
        ReceiverGoldLimitExceeded,
        InvalidAttachment,
        TooManyAttachments,
        NotTradable,
        AccountBound,
        LimitedPeriodItem,
        ExpiredItem,
        ItemLocked,
        DailyGoldLimitExceeded,
        Blacklisted,
        IllegalText,
        ReceiverTradeRestricted,
        SenderLevelOrSendLimit,
        MailNotFound,
        InventoryFull,
        ItemCarryLimitExceeded,
        GoldCarryLimitExceeded,
        TradeRestricted,
        PersonalShopOpen,
        Trading,
        SelfSendNotAllowed,
        MailboxStorageFull,
        ServerBusy
    }

    public sealed class MailboxSendRequest
    {
        public int SenderCharacterId { get; set; }
        public int SenderAccountId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public int ReceiverCharacterId { get; set; }
        public int ReceiverAccountId { get; set; }
        public string ReceiverName { get; set; } = string.Empty;
        public int SenderLevel { get; set; }
        public int ReceiverLevel { get; set; }
        public int Gold { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public int MailType { get; set; }
        public ushort SourceProtocol { get; set; }
        // MailType 控制系统信展示。过期策略独立：玩家寄信固定 15 天；系统寄信默认不过期，也可指定截止时间。
        public bool? Unlimited { get; set; }
        public DateTimeOffset? ExpireAtUtc { get; set; }
        // Administrative attribution is persisted only for system-mail audit.
        // It deliberately does not participate in the business idempotency hash.
        public string AuditActor { get; set; } = string.Empty;
        public string AuditReason { get; set; } = string.Empty;
        // Stable for one logical send operation. Player requests derive it from the
        // protocol sequence/checksum; GM/campaign callers supply a durable campaign key.
        public string IdempotencyKey { get; set; } = string.Empty;
        public IReadOnlyList<MailboxSendAttachmentRequest> Attachments { get; set; } = Array.Empty<MailboxSendAttachmentRequest>();
    }

    public sealed class MailboxExpirationRecipient
    {
        public int CharacterId { get; set; }
        public IReadOnlyList<long> MessageIds { get; set; } = Array.Empty<long>();
    }

    public sealed class MailboxExpirationBatchResult
    {
        public int ExpiredRecipientCount { get; set; }
        public int PurgedMessageCount { get; set; }
        public IReadOnlyList<MailboxExpirationRecipient> Recipients { get; set; } = Array.Empty<MailboxExpirationRecipient>();
    }

    public sealed class MailboxSendAttachmentRequest
    {
        public byte ItemType { get; set; }
        public ushort ItemSlot { get; set; }
        public int ItemId { get; set; }
        public int ItemCount { get; set; }
        public int InstanceValue { get; set; }
        public int Durability { get; set; }
        public int SealFlag { get; set; }
        public int OptionValue { get; set; }
        public int ExpireTime { get; set; }
        public int Marker16 { get; set; }
        public int PetSerialOrHandle { get; set; }
        public string ExtraJson { get; set; } = "{}";
        public byte[] ItemCoreData { get; set; } = Array.Empty<byte>();
        public string DetailJson { get; set; } = string.Empty;
    }

    public sealed class MailboxSendResult
    {
        public bool Success { get; set; }
        public MailboxSendError Error { get; set; }
        public long MessageId { get; set; }
        public int FeeGold { get; set; }
        public int UpdatedGold { get; set; }

        public static MailboxSendResult Fail(MailboxSendError error)
        {
            return new MailboxSendResult { Success = false, Error = error };
        }
    }

    public sealed class MailboxCampaignBatchResult
    {
        public bool Success { get; set; }
        public MailboxSendError Error { get; set; }
        public string CampaignId { get; set; } = string.Empty;
        public int DeliveredCount { get; set; }
        public int LastCharacterId { get; set; }
        public bool Completed { get; set; }

        public static MailboxCampaignBatchResult Fail(string campaignId, MailboxSendError error)
        {
            return new MailboxCampaignBatchResult
            {
                Success = false,
                Error = error,
                CampaignId = campaignId ?? string.Empty
            };
        }
    }

    public sealed class MailboxClaimResult
    {
        public bool Success { get; set; }
        public MailboxSendError Error { get; set; }
        public long MessageId { get; set; }
        public int ClaimedGold { get; set; }
        public int ClaimedAttachmentCount { get; set; }
        public bool RemovedFromInbox { get; set; }
        public IReadOnlyList<short> UpdatedMainSlots { get; set; } = Array.Empty<short>();
        public IReadOnlyList<short> UpdatedAvatarSlots { get; set; } = Array.Empty<short>();
        public IReadOnlyList<short> UpdatedPetSlots { get; set; } = Array.Empty<short>();
        public IReadOnlyList<InventoryMutationResult> InventoryMutations { get; set; } =
            Array.Empty<InventoryMutationResult>();

        public static MailboxClaimResult Fail(MailboxSendError error)
        {
            return new MailboxClaimResult { Success = false, Error = error };
        }
    }

    public sealed class MailboxDeleteResult
    {
        public bool Success { get; set; }
        public MailboxSendError Error { get; set; }
        public long MessageId { get; set; }

        public static MailboxDeleteResult Fail(MailboxSendError error)
        {
            return new MailboxDeleteResult { Success = false, Error = error };
        }
    }

    public sealed class MailboxListEntry
    {
        public long MessageId { get; set; }
        public int SenderCharacterId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public int MailType { get; set; }
        public int SourceProtocol { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public int Gold { get; set; }
        public int AttachmentCount { get; set; }
        public IReadOnlyList<MailboxAttachmentEntry> Attachments { get; set; } = Array.Empty<MailboxAttachmentEntry>();
        public int FirstAttachmentItemId { get; set; }
        public int FirstAttachmentItemCount { get; set; }
        public string FirstAttachmentItemKind { get; set; } = string.Empty;
        public int FirstAttachmentInstanceValue { get; set; }
        public int FirstAttachmentDurability { get; set; }
        public int FirstAttachmentSealFlag { get; set; }
        public int FirstAttachmentOptionValue { get; set; }
        public int FirstAttachmentExpireTime { get; set; }
        public int FirstAttachmentMarker16 { get; set; }
        public int FirstAttachmentPetSerialOrHandle { get; set; }
        public int RemainSeconds { get; set; }
        public int CreatedAtUnixSeconds { get; set; }
        public int LetterStat { get; set; }
    }

    public sealed class MailboxInboxPage
    {
        public IReadOnlyList<MailboxListEntry> Entries { get; set; } = Array.Empty<MailboxListEntry>();
        public int TotalCount { get; set; }
        public int LoadedInboxCount { get; set; }

        public int NotLoadedCount => Math.Max(0, TotalCount - LoadedInboxCount);
    }

    public sealed class MailboxAttachmentEntry
    {
        public long AttachmentId { get; set; }
        public int Ordinal { get; set; }
        public byte ItemType { get; set; }
        public int SourceListType { get; set; }
        public int SourceSlotIndex { get; set; }
        public long SourceItemUid { get; set; }
        public int ItemTemplateId { get; set; }
        public string ItemKind { get; set; } = string.Empty;
        public int ItemCount { get; set; }
        public int InstanceValue { get; set; }
        public int Durability { get; set; }
        public int SealFlag { get; set; }
        public int OptionValue { get; set; }
        public int ExpireTime { get; set; }
        public int Marker16 { get; set; }
        public int PetSerialOrHandle { get; set; }
        public string ExtraJson { get; set; } = "{}";
        public byte[] ItemCoreData { get; set; } = Array.Empty<byte>();
        public string DetailJson { get; set; } = string.Empty;
    }

    internal sealed class MailboxAttachmentSnapshot
    {
        public int Ordinal { get; set; }
        public byte ItemType { get; set; }
        public int SourceListType { get; set; }
        public int SourceSlotIndex { get; set; }
        public long SourceItemUid { get; set; }
        public int ItemTemplateId { get; set; }
        public string ItemKind { get; set; } = "unknown";
        public int ItemCount { get; set; }
        public int InstanceValue { get; set; }
        public int Durability { get; set; }
        public int SealFlag { get; set; }
        public int OptionValue { get; set; }
        public int EquipmentLockId { get; set; }
        public int ExpireTime { get; set; }
        public int Marker16 { get; set; }
        public int PetSerialOrHandle { get; set; }
        public string ExtraJson { get; set; } = "{}";
        public byte[] ItemCoreData { get; set; } = Array.Empty<byte>();
        public string DetailJson { get; set; } = string.Empty;
    }
}
