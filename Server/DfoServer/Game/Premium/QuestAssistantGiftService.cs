using DfoServer.Game.DailyReset;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Premium
{
    internal sealed class QuestAssistantGiftService
    {
        internal const string DailyCounterKey =
            "devil_contract_quest_assistant_gift_claimed";

        // stackable/stackable.lst -> cash/chn_contract_devil/chn_quest_helper_*.stk
        private static readonly int[] GiftItemTemplateIds =
        {
            2681925, // 阿甘左
            2681923, // 死亡之纳特亚
            2681922, // 希苏拉
            2681924, // 娜塔莉娅·休勒
        };

        private readonly IGameDatabase _database;
        private readonly DailyResetService _dailyReset;
        private readonly MailboxService _mailbox;

        internal QuestAssistantGiftService(
            IGameDatabase database,
            DailyResetService dailyReset,
            MailboxService mailbox)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _dailyReset = dailyReset ?? throw new ArgumentNullException(nameof(dailyReset));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        }

        internal QuestAssistantGiftDeliveryResult TryDeliver(
            int characterId,
            int accountId)
        {
            if (characterId <= 0 || accountId <= 0)
                return QuestAssistantGiftDeliveryResult.Failed(
                    "invalid character or account");

            try
            {
                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction(
                           deferred: false))
                {
                    if (!PremiumService.HasActiveDevilContract(
                            connection,
                            transaction,
                            accountId,
                            DevilContractUsagePolicy.QuestAssistantSlot))
                    {
                        transaction.Commit();
                        return QuestAssistantGiftDeliveryResult.Inactive;
                    }

                    if (_dailyReset.GetCounter(
                            connection,
                            transaction,
                            characterId,
                            DailyCounterKey) > 0)
                    {
                        transaction.Commit();
                        return QuestAssistantGiftDeliveryResult.AlreadyDelivered;
                    }

                    var nextResetUtc = ResolveNextResetUtc(DateTime.UtcNow);
                    var expireUnix = (int)Math.Min(
                        int.MaxValue,
                        new DateTimeOffset(nextResetUtc).ToUnixTimeSeconds());
                    var attachments = new List<MailboxSendAttachmentRequest>(
                        GiftItemTemplateIds.Length);
                    foreach (var itemTemplateId in GiftItemTemplateIds)
                    {
                        attachments.Add(new MailboxSendAttachmentRequest
                        {
                            ItemType = 0,
                            ItemId = itemTemplateId,
                            ItemCount = 1,
                            ExpireTime = expireUnix,
                        });
                    }

                    var dayId = DailyResetService.TodayId();
                    var send = _mailbox.SendSystemMails(
                        connection,
                        transaction,
                        new[]
                        {
                            new MailboxSendRequest
                            {
                                SenderCharacterId = characterId,
                                SenderAccountId = accountId,
                                SenderName = "DNF管理员",
                                ReceiverCharacterId = characterId,
                                ReceiverAccountId = accountId,
                                Title = "魔王之契约：任务助手",
                                Text = "今日的4种APC助手已送达，请在次日凌晨6点前领取。",
                                MailType = 1,
                                SourceProtocol = 0,
                                Unlimited = false,
                                ExpireAtUtc = new DateTimeOffset(nextResetUtc),
                                IdempotencyKey =
                                    $"devil-contract-quest-assistant:{characterId}:{dayId}",
                                AuditActor = "devil-contract",
                                AuditReason = "daily quest assistant APC gifts",
                                Attachments = attachments,
                            },
                        });
                    if (!send.Success)
                    {
                        return QuestAssistantGiftDeliveryResult.Failed(
                            "mail send failed: " + send.Error);
                    }

                    if (!_dailyReset.TryClaimFlag(
                            connection,
                            transaction,
                            characterId,
                            DailyCounterKey))
                    {
                        return QuestAssistantGiftDeliveryResult.Failed(
                            "daily gift claim raced");
                    }

                    transaction.Commit();
                    return QuestAssistantGiftDeliveryResult.Delivered(
                        send.MessageId);
                }
            }
            catch (Exception ex)
            {
                return QuestAssistantGiftDeliveryResult.Failed(ex.Message);
            }
        }

        private static DateTime ResolveNextResetUtc(DateTime utcNow)
        {
            var boundary = DailyResetService.GetDailyResetBoundaryUtc(utcNow);
            return utcNow < boundary ? boundary : boundary.AddDays(1);
        }
    }

    internal sealed class QuestAssistantGiftDeliveryResult
    {
        internal static QuestAssistantGiftDeliveryResult Inactive { get; } =
            new QuestAssistantGiftDeliveryResult();
        internal static QuestAssistantGiftDeliveryResult AlreadyDelivered { get; } =
            new QuestAssistantGiftDeliveryResult { SkippedAsAlreadyDelivered = true };

        internal bool Success { get; private set; }
        internal bool SkippedAsAlreadyDelivered { get; private set; }
        internal long MessageId { get; private set; }
        internal string Error { get; private set; }

        internal static QuestAssistantGiftDeliveryResult Delivered(long messageId)
            => new QuestAssistantGiftDeliveryResult
            {
                Success = true,
                MessageId = messageId,
            };

        internal static QuestAssistantGiftDeliveryResult Failed(string error)
            => new QuestAssistantGiftDeliveryResult
            {
                Error = error ?? "unknown error",
            };
    }
}
