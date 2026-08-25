using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class MailboxHandler
    {
        private const int MailboxListNotificationType = (int)NotiPacketTypeA21.MAILBOX_MAIL_LIST;
        private const int MailboxRemoveNotificationType = (int)NotiPacketTypeA21.MAILBOX_REMOVE_MAIL;
        // Client sub_D317E0 consumes one WORD and raises the online mailbox notice.
        private const int MailboxAlarmNotificationType = (int)NotiPacketTypeA21.MAILBOX_ALARM;
        private const ushort MailboxChangeLetterStatType = (ushort)CmdPacketTypeA21.CHANGE_LETTER_STAT;
        private const int MailboxPageSize = 20;
        private const int MailboxStorageLimit = 10;
        private const int MailboxSenderNameSize = 30;
        private const int MailboxLetterTextSize = 512;
        private const int MinExpirationUnixTime = 1000000000;
        private const int OfficialMailSenderCharacterId = 0;
        private const string OfficialMailSenderName = "DNFadmin";
        private const string DefaultMailboxSafetyText = "DNF\u8FD0\u8425\u8005\u4E0D\u4F1A\u4EE5\u4EFB\u4F55\u5F62\u5F0F\u7D22\u8981\u6216\u8BE2\u95EE\u60A8\u7684\u8D26\u53F7\u5BC6\u7801,\u8BF7\u60A8\u4E0D\u8981\u90AE\u5BC4\u5199\u6709DNF\u8D26\u53F7\u5BC6\u7801\u7B49\u91CD\u8981\u4FE1\u606F\u7684\u4FE1\u4EF6";
        private const bool MailboxSummaryAttachmentPreviewEnabled = true;
        private const int QueryCharacterInfoNameSize = 20;
        private const byte QueryCharacterInfoSelfSendErrorCode = 7; // 0x0144: 7=self-send, 21=receiver/role not found, 2=try again later.

        private readonly ICharacterRepository _characterRepository;
        private readonly MailboxService _mailboxService;
        private readonly ISessionDirectory _sessionDirectory;
        private readonly InventoryRefreshSender _inventoryRefreshSender;
        private readonly ConcurrentDictionary<Guid, MailboxPageRemovalRefreshState> _pageRemovalRefreshes = new ConcurrentDictionary<Guid, MailboxPageRemovalRefreshState>();
        private readonly ConditionalWeakTable<EnhancedClientSession, MailboxAlarmState> _mailboxAlarmStates = new ConditionalWeakTable<EnhancedClientSession, MailboxAlarmState>();
        private int _mailboxMaintenanceRunning;

        public MailboxHandler(
            ICharacterRepository characterRepository,
            MailboxService mailboxService,
            ISessionDirectory sessionDirectory,
            InventoryRefreshSender inventoryRefreshSender)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _mailboxService = mailboxService ?? throw new ArgumentNullException(nameof(mailboxService));
            _sessionDirectory = sessionDirectory ?? throw new ArgumentNullException(nameof(sessionDirectory));
            _inventoryRefreshSender = inventoryRefreshSender ?? throw new ArgumentNullException(nameof(inventoryRefreshSender));
            ClockService.Instance.RegisterMinuteTick("mailbox-expiration", RunMailboxMaintenance);
        }

        private void RunMailboxMaintenance(DateTime utcNow)
        {
            if (Interlocked.CompareExchange(ref _mailboxMaintenanceRunning, 1, 0) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = _mailboxService.MaintainExpiredMail();
                    foreach (var recipient in result.Recipients)
                    {
                        if (!_sessionDirectory.TryGet(recipient.CharacterId, out var session))
                            continue;

                        var clientMessageIds = new List<int>();
                        var refreshPage = false;
                        foreach (var messageId in recipient.MessageIds)
                        {
                            if (messageId <= 0 || messageId > int.MaxValue)
                                continue;
                            var clientMessageId = (int)messageId;
                            clientMessageIds.Add(clientMessageId);
                            refreshPage |= RegisterMailboxPageRemoval(session, clientMessageId);
                        }

                        if (clientMessageIds.Count > 0)
                        {
                            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                                0x00,
                                MailboxRemoveNotificationType,
                                BuildRemoveMailNotification(clientMessageIds))).ConfigureAwait(false);
                        }

                        if (refreshPage)
                            await SendMailboxListRefresh(session, recipient.CharacterId, "expiration-page-complete").ConfigureAwait(false);
                    }

                    if (result.ExpiredRecipientCount > 0 || result.PurgedMessageCount > 0)
                    {
                        FileLogger.Log($"[Mailbox] MAINTENANCE utc={utcNow:O} expired={result.ExpiredRecipientCount} purged={result.PurgedMessageCount} notified={result.Recipients.Count}");
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Mailbox] MAINTENANCE failed: {ex}");
                }
                finally
                {
                    Volatile.Write(ref _mailboxMaintenanceRunning, 0);
                }
            });
        }

        public async Task HandleOpenMailbox(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var characterId = session.Player?.CharacterId ?? 0;
            // 打开邮箱只清会话级新邮件提示；单封已读仍由 CHANGE_LETTER_STAT action=2 控制。
            _mailboxAlarmStates.Remove(session);
            var page = _mailboxService.LoadInboxPage(characterId, MailboxPageSize);
            var entries = page.Entries;
            ClearMailboxPageRemovalRefresh(session);
            FileLogger.Log($"[Mailbox] OPEN cid={characterId} entries={entries.Count} total={page.TotalCount} notLoaded={page.NotLoadedCount} requestType=0x{header.type:X4}");

            var notLoaded = ClampUInt16(page.NotLoadedCount);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, BuildOpenMailboxAck(notLoaded)));
            // 打开 ACK 之后发完整 0x0061，有邮件时与进城 init 同一份。
            await SendMailboxListNotifications(session, entries, notLoaded).ConfigureAwait(false);
        }

        public Task HandleQueryCharacterInfoMailbox(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var receiverName = ReadMailboxName(body);
            FileLogger.Log($"[Mailbox] QUERY_CHARAC_INFO_MAILBOX request type=0x{header.type:X4} bodyLen={body?.Length ?? 0} name='{receiverName}'");

            if (string.IsNullOrWhiteSpace(receiverName))
                return SendQueryCharacterInfoError(session, header.type, 21);

            var senderCharacterId = session.Player?.CharacterId ?? 0;
            if (senderCharacterId <= 0)
            {
                FileLogger.Log($"[Mailbox] QUERY_CHARAC_INFO_MAILBOX rejected: sender is not selected name='{receiverName}'");
                return SendQueryCharacterInfoError(session, header.type, 21);
            }

            var character = _characterRepository.GetByName(receiverName);
            if (character == null || character.Deleted)
            {
                FileLogger.Log($"[Mailbox] QUERY_CHARAC_INFO_MAILBOX not found name='{receiverName}'");
                return SendQueryCharacterInfoError(session, header.type, 21);
            }

            if (character.CharacterId == senderCharacterId)
            {
                FileLogger.Log($"[Mailbox] QUERY_CHARAC_INFO_MAILBOX rejected: self-send cid={senderCharacterId} name='{receiverName}'");
                return SendQueryCharacterInfoError(session, header.type, QueryCharacterInfoSelfSendErrorCode);
            }

            var responseBody = BuildQueryCharacterInfoAck(character, receiverName);
            FileLogger.Log($"[Mailbox] QUERY_CHARAC_INFO_MAILBOX ok cid={character.CharacterId} level={character.Level} job={character.Job} grow={character.GrowType}");

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                responseBody));
        }

        public Task HandleSendMailbox(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryParseSendMailboxRequest(body, MailboxSendFormat.SingleAttachment, out var request, out var error))
                return HandleSendMailboxParseFailure(session, header, body, error, MailboxSendError.InvalidRequest);

            return HandleParsedSendMailbox(session, header, request);
        }

        public Task HandleSendMultiMailbox(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryParseSendMailboxRequest(body, MailboxSendFormat.MultiAttachment, out var request, out var error))
                return HandleSendMailboxParseFailure(session, header, body, error, MailboxSendError.InvalidRequest);

            return HandleParsedSendMailbox(session, header, request);
        }

        private Task HandleSendMailboxParseFailure(EnhancedClientSession session, GamePacketHeader header, byte[] body, string error, MailboxSendError sendError)
        {
            FileLogger.Log($"[Mailbox] SEND parse failed type=0x{header.type:X4} bodyLen={body?.Length ?? 0}: {error}");
            return SendMailboxSendError(session, header.type, sendError);
        }

        private Task HandleParsedSendMailbox(EnhancedClientSession session, GamePacketHeader header, SendMailboxRequest request)
        {
            var text = NormalizeMailboxText(request.Text);

            var receiver = _characterRepository.GetByName(request.ReceiverName);
            if (receiver == null)
            {
                var deletedReceiver = _characterRepository.GetByNameIncludingDeleted(request.ReceiverName);
                var receiverError = deletedReceiver != null && deletedReceiver.Deleted
                    ? MailboxSendError.ReceiverDeleted
                    : MailboxSendError.ReceiverNotFound;
                FileLogger.Log($"[Mailbox] SEND rejected: receiver lookup failed name='{request.ReceiverName}' reason={receiverError}");
                return SendMailboxSendError(session, header.type, receiverError);
            }

            if (request.Gold < 0)
            {
                FileLogger.Log($"[Mailbox] SEND rejected: invalid gold={request.Gold}");
                return SendMailboxSendError(session, header.type, MailboxSendError.InvalidRequest);
            }

            if (request.AttachmentCount > 10 || request.Attachments.Length > 10)
            {
                FileLogger.Log($"[Mailbox] SEND rejected: attachment count={request.AttachmentCount}");
                return SendMailboxSendError(session, header.type, MailboxSendError.TooManyAttachments);
            }

            var senderCharacterId = session.Player?.CharacterId ?? 0;
            if (senderCharacterId <= 0)
            {
                FileLogger.Log("[Mailbox] SEND rejected: sender is not selected");
                return SendMailboxSendError(session, header.type, MailboxSendError.InvalidRequest);
            }

            if (receiver.CharacterId == senderCharacterId)
            {
                FileLogger.Log($"[Mailbox] SEND rejected: self-send cid={senderCharacterId} receiver='{request.ReceiverName}'");
                return SendMailboxSendError(session, header.type, MailboxSendError.SelfSendNotAllowed);
            }

            var sender = _characterRepository.GetById(senderCharacterId);
            if (sender == null || sender.Deleted)
            {
                FileLogger.Log($"[Mailbox] SEND rejected: sender not found cid={senderCharacterId}");
                return SendMailboxSendError(session, header.type, MailboxSendError.InvalidRequest);
            }

            var serviceRequest = new MailboxSendRequest
            {
                SenderCharacterId = sender.CharacterId,
                SenderAccountId = sender.AccountId,
                SenderName = sender.DisplayName,
                SenderLevel = sender.Level,
                ReceiverCharacterId = receiver.CharacterId,
                ReceiverAccountId = receiver.AccountId,
                ReceiverName = receiver.DisplayName,
                ReceiverLevel = receiver.Level,
                Gold = request.Gold,
                Text = text,
                SourceProtocol = header.type,
                // A client retry within the same connection retains seq/checksum and
                // therefore resolves to the original committed message. SessionId keeps
                // a later connection (including packet-sequence wraparound) independent.
                IdempotencyKey = $"player:{sender.CharacterId}:{session.SessionId:N}:{header.type:X4}:{header.seq:X4}:{header.checksum:X8}",
                Attachments = ConvertAttachments(request.Attachments)
            };

            if (!InventoryContext.TryGetLease(senderCharacterId, out var senderInventory)
                || !senderInventory.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[Mailbox] SEND rejected: online inventory lease unavailable cid={senderCharacterId}");
                return SendMailboxSendError(session, header.type, MailboxSendError.ServerBusy);
            }

            var result = _mailboxService.SendMail(serviceRequest, senderInventory);
            if (!result.Success)
            {
                FileLogger.Log($"[Mailbox] SEND rejected by service: reason={result.Error}");
                return SendMailboxSendErrorAndRestoreAttachments(
                    session,
                    header.type,
                    result.Error,
                    request.Attachments);
            }

            FileLogger.Log($"[Mailbox] SEND committed messageId={result.MessageId} receiverCid={receiver.CharacterId} senderCid={sender.CharacterId} fee={result.FeeGold} updatedGold={result.UpdatedGold}");
            return SendSuccessAndNotifyReceiver(session, header.type, receiver.CharacterId);
        }

        private async Task SendMailboxSendErrorAndRestoreAttachments(
            EnhancedClientSession session,
            ushort ackType,
            MailboxSendError error,
            SendMailboxAttachment[] attachments)
        {
            await SendMailboxSendError(session, ackType, error).ConfigureAwait(false);
            if (attachments == null || attachments.Length == 0)
                return;

            var slotsByList = new Dictionary<InventoryListType, List<short>>();
            foreach (var attachment in attachments)
            {
                if (attachment == null || attachment.ItemId <= 0)
                    continue;

                var listType = MapMailboxItemTypeToInventoryList(attachment.ItemType);
                AddClaimRefreshSlot(slotsByList, listType, (short)attachment.ItemSlot);
            }

            foreach (var pair in slotsByList)
            {
                await _inventoryRefreshSender.SendUpdateItemList(session, pair.Key, pair.Value).ConfigureAwait(false);
            }
            await _inventoryRefreshSender.SendAllSortItemLockRefresh(session).ConfigureAwait(false);
            await _inventoryRefreshSender.SendAllEquipmentItemLockListRefresh(session).ConfigureAwait(false);
            FileLogger.Log(
                $"[Mailbox] SEND failure restored attachment slots={FormatClaimRefreshSlots(slotsByList)} " +
                $"reason={error}");
        }

        private static InventoryListType MapMailboxItemTypeToInventoryList(byte itemType)
        {
            switch (itemType)
            {
                case 1:
                    return InventoryListType.Avatar;
                case 3:
                case 7:
                    return InventoryListType.Pet;
                case 0:
                case 2:
                default:
                    return InventoryListType.Main;
            }
        }

        public async Task HandleClaimMailbox(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4)
            {
                FileLogger.Log($"[Mailbox] CLAIM rejected: body too short type=0x{header.type:X4} bodyLen={body?.Length ?? 0}");
                await SendMailboxSendError(session, header.type, MailboxSendError.InvalidRequest);
                return;
            }

            var claimObjectId = BitConverter.ToInt32(body, 0);
            var characterId = session.Player?.CharacterId ?? 0;

            if (!InventoryContext.TryGetLease(characterId, out var receiverInventory)
                || !receiverInventory.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[Mailbox] CLAIM rejected: online inventory lease unavailable cid={characterId}");
                await SendMailboxSendError(session, header.type, MailboxSendError.ServerBusy);
                return;
            }

            var result = _mailboxService.ClaimMail(characterId, claimObjectId, receiverInventory);
            if (!result.Success)
            {
                FileLogger.Log($"[Mailbox] CLAIM rejected claimObjectId={claimObjectId} reason={result.Error}");
                var partialSlots = BuildClaimRefreshSlots(result);
                if (partialSlots.Count > 0)
                {
                    await SendClaimInventoryRefresh(session, partialSlots);
                    FileLogger.Log($"[Mailbox] CLAIM partial refresh claimObjectId={claimObjectId} slots={FormatClaimRefreshSlots(partialSlots)}");
                }

                await SendMailboxSendError(session, header.type, result.Error);
                return;
            }

            var updatedSlots = BuildClaimRefreshSlots(result);
            if (result.ClaimedGold > 0)
                AddClaimRefreshSlot(updatedSlots, InventoryListType.Main, 0);

            await SendClaimMailboxResult(session, header.type, claimObjectId);
            await SendClaimInventoryRefresh(session, updatedSlots);

            if (result.RemovedFromInbox)
            {
                await SendChangeLetterStatResult(session, MailboxChangeLetterStatType, (int)result.MessageId, 0);
                if (RegisterMailboxPageRemoval(session, (int)result.MessageId))
                    await SendMailboxListRefresh(session, characterId, "claim-page-complete");
            }

            var questManager = session.GameSession?.QuestManager;
            if (questManager != null)
            {
                await questManager.SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(
                    receiverInventory,
                    result.InventoryMutations);
            }

            FileLogger.Log($"[Mailbox] CLAIM ok claimObjectId={claimObjectId} messageId={result.MessageId} gold={result.ClaimedGold} attachments={result.ClaimedAttachmentCount} removed={result.RemovedFromInbox} refreshSlots={FormatClaimRefreshSlots(updatedSlots)}");
        }

        private async Task SendClaimInventoryRefresh(EnhancedClientSession session, IDictionary<InventoryListType, List<short>> updatedSlots)
        {
            if (updatedSlots == null || updatedSlots.Count == 0)
                return;

            foreach (var pair in updatedSlots)
                await _inventoryRefreshSender.SendUpdateItemList(session, pair.Key, pair.Value).ConfigureAwait(false);
        }

        private static Dictionary<InventoryListType, List<short>> BuildClaimRefreshSlots(MailboxClaimResult result)
        {
            var slots = new Dictionary<InventoryListType, List<short>>();
            AddClaimRefreshSlots(slots, InventoryListType.Main, result?.UpdatedMainSlots);
            AddClaimRefreshSlots(slots, InventoryListType.Avatar, result?.UpdatedAvatarSlots);
            AddClaimRefreshSlots(slots, InventoryListType.Pet, result?.UpdatedPetSlots);
            return slots;
        }

        private static void AddClaimRefreshSlots(
            Dictionary<InventoryListType, List<short>> target,
            InventoryListType listType,
            IReadOnlyList<short> slots)
        {
            if (slots == null)
                return;

            foreach (var slot in slots)
                AddClaimRefreshSlot(target, listType, slot);
        }

        private static void AddClaimRefreshSlot(
            Dictionary<InventoryListType, List<short>> target,
            InventoryListType listType,
            short slot)
        {
            if (!target.TryGetValue(listType, out var list))
            {
                list = new List<short>();
                target[listType] = list;
            }

            if (!list.Contains(slot))
                list.Add(slot);
        }

        private static string FormatClaimRefreshSlots(IDictionary<InventoryListType, List<short>> slotsByList)
        {
            if (slotsByList == null || slotsByList.Count == 0)
                return "none";

            var builder = new StringBuilder();
            foreach (var pair in slotsByList)
            {
                if (builder.Length > 0)
                    builder.Append("; ");
                builder.Append(pair.Key).Append(':').Append(string.Join(",", pair.Value));
            }
            return builder.ToString();
        }

        public async Task HandleChangeLetterStatMailbox(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 6)
            {
                FileLogger.Log($"[Mailbox] CHANGE_STAT rejected: body too short type=0x{header.type:X4} bodyLen={body?.Length ?? 0}");
                await SendMailboxSendError(session, header.type, MailboxSendError.InvalidRequest);
                return;
            }

            var messageId = BitConverter.ToInt32(body, 0);
            var action = body.Length >= 6 ? BitConverter.ToUInt16(body, 4) : (ushort)0;
            var characterId = session.Player?.CharacterId ?? 0;

            if (action == 2)
            {
                var markResult = _mailboxService.MarkMailRead(characterId, messageId);
                if (!markResult.Success)
                {
                    if (markResult.Error == MailboxSendError.MailNotFound)
                    {
                        await SendChangeLetterStatResult(session, header.type, messageId, action);
                        FileLogger.Log($"[Mailbox] CHANGE_STAT mark-read already gone messageId={messageId}");
                        return;
                    }

                    FileLogger.Log($"[Mailbox] CHANGE_STAT mark-read rejected messageId={messageId} reason={markResult.Error}");
                    await SendMailboxSendError(session, header.type, markResult.Error);
                    return;
                }

                await SendChangeLetterStatResult(session, header.type, messageId, action);
                FileLogger.Log($"[Mailbox] CHANGE_STAT mark-read ok messageId={messageId}");
                return;
            }

            if (action == 3)
            {
                var saveResult = _mailboxService.SaveMail(characterId, messageId);
                if (!saveResult.Success)
                {
                    FileLogger.Log($"[Mailbox] CHANGE_STAT save rejected messageId={messageId} reason={saveResult.Error}");
                    await SendMailboxSendError(session, header.type, saveResult.Error);
                    return;
                }

                await SendChangeLetterStatResult(session, header.type, messageId, action);
                FileLogger.Log($"[Mailbox] CHANGE_STAT save ok messageId={messageId}");
                return;
            }

            if (action != 0)
            {
                FileLogger.Log($"[Mailbox] CHANGE_STAT unsupported action={action} messageId={messageId}");
                await SendMailboxSendError(session, header.type, MailboxSendError.InvalidRequest);
                return;
            }

            var result = _mailboxService.DeleteMail(characterId, messageId);
            if (!result.Success)
            {
                if (result.Error == MailboxSendError.MailNotFound)
                {
                    await SendChangeLetterStatResult(session, header.type, messageId, action);
                    if (RegisterMailboxPageRemoval(session, messageId))
                        await SendMailboxListRefresh(session, characterId, "delete-page-complete");
                    FileLogger.Log($"[Mailbox] CHANGE_STAT delete already gone messageId={messageId}");
                    return;
                }

                FileLogger.Log($"[Mailbox] CHANGE_STAT delete rejected messageId={messageId} reason={result.Error}");
                await SendMailboxSendError(session, header.type, result.Error);
                return;
            }

            await SendChangeLetterStatResult(session, header.type, messageId, action);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, MailboxRemoveNotificationType, BuildRemoveMailNotification(messageId)));
            if (RegisterMailboxPageRemoval(session, messageId))
                await SendMailboxListRefresh(session, characterId, "delete-page-complete");
            FileLogger.Log($"[Mailbox] CHANGE_STAT delete ok messageId={messageId}");
        }

        public Task HandleMailboxCommand(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
        }

        private static Task SendSimpleResult(EnhancedClientSession session, ushort ackType, bool ok, byte errorCode)
        {
            var body = ok
                ? new[] { (byte)0x01 }
                : new[] { (byte)0x00, errorCode };

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, ackType, body));
        }

        private static Task SendClaimMailboxResult(EnhancedClientSession session, ushort ackType, long claimObjectId)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                ackType,
                BuildClaimMailboxSuccessBody(claimObjectId)));
        }

        internal static byte[] BuildClaimMailboxSuccessBody(long claimObjectId)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(checked((int)claimObjectId));
            return writer.ToArray();
        }

        private static Task SendChangeLetterStatResult(EnhancedClientSession session, ushort ackType, int messageId, ushort action)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(messageId);
            writer.WriteUInt16(action);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, ackType, writer.ToArray()));
        }

        private async Task SendSuccessAndNotifyReceiver(EnhancedClientSession senderSession, ushort ackType, int receiverCharacterId)
        {
            await SendSimpleResult(senderSession, ackType, true, 0);

            if (!_sessionDirectory.TryGet(receiverCharacterId, out var receiverSession))
            {
                return;
            }

            await SendMailboxAlarmIfNeeded(receiverCharacterId, receiverSession);
        }

        private async Task SendMailboxAlarmIfNeeded(int receiverCharacterId, EnhancedClientSession receiverSession)
        {
            var state = _mailboxAlarmStates.GetValue(receiverSession, _ => new MailboxAlarmState());
            ushort count;
            lock (state.SyncRoot)
            {
                state.PendingCount = Math.Min(ushort.MaxValue, state.PendingCount + 1);
                if (state.NotificationSent)
                    return;
                state.NotificationSent = true;
                count = (ushort)Math.Max(1, state.PendingCount);
            }

            try
            {
                await receiverSession.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        MailboxAlarmNotificationType,
                        BuildMailboxAlarmNotification(count))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _mailboxAlarmStates.Remove(receiverSession);
                FileLogger.Log($"[Mailbox] SEND receiver alarm failed cid={receiverCharacterId}: {ex.Message}");
                // Let the protocol disconnect path own directory removal and
                // role teardown; pre-unregistering here can strand town/dungeon state.
                receiverSession.Close();
            }
        }

        internal static byte[] BuildMailboxAlarmNotification(ushort count)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(count);
            return writer.ToArray();
        }

        private async Task SendMailboxListRefresh(EnhancedClientSession session, int characterId, string reason)
        {
            if (session == null || characterId <= 0)
                return;

            var page = _mailboxService.LoadInboxPage(characterId, MailboxPageSize);
            var notLoaded = ClampUInt16(page.NotLoadedCount);
            await SendMailboxListNotifications(session, page.Entries, notLoaded).ConfigureAwait(false);
            FileLogger.Log($"[Mailbox] REFRESH reason={reason} cid={characterId} entries={page.Entries.Count} notLoaded={page.NotLoadedCount}");
        }

        private static async Task SendMailboxListNotifications(
            EnhancedClientSession session,
            IReadOnlyList<MailboxListEntry> entries,
            ushort notLoadedCount)
        {
            var bodies = BuildMailboxListNotificationBatches(entries, notLoadedCount);
            for (var i = 0; i < bodies.Count; i++)
            {
                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        MailboxListNotificationType,
                        bodies[i])).ConfigureAwait(false);
            }
        }

        private void ClearMailboxPageRemovalRefresh(EnhancedClientSession session)
        {
            if (session != null)
                _pageRemovalRefreshes.TryRemove(session.SessionId, out _);
        }

        private bool RegisterMailboxPageRemoval(EnhancedClientSession session, int messageId)
        {
            if (session == null || messageId <= 0)
                return false;

            var state = _pageRemovalRefreshes.GetOrAdd(session.SessionId, _ => new MailboxPageRemovalRefreshState());
            lock (state.SyncRoot)
            {
                if (!state.MessageIds.Add(messageId))
                    return false;

                if (state.MessageIds.Count < MailboxPageSize)
                    return false;

                state.MessageIds.Clear();
                return true;
            }
        }

        private static Task SendMailboxSendError(EnhancedClientSession session, ushort ackType, MailboxSendError error)
        {
            var errorCode = ToClientErrorCode(error, ackType);
            FileLogger.Log($"[Mailbox] SEND error ackType=0x{ackType:X4} reason={error} clientError={errorCode}");
            return SendSimpleResult(session, ackType, false, errorCode);
        }

        // Confirmed client mailbox claim (0x005F) failure codes:
        // 4   - attachment cannot be inserted. The client then performs its own
        //       inventory check and selects either resource 2057 (no space/weight)
        //       or resource 42077 (PVF per-item carry limit).
        // 10  - claiming mail gold would exceed the character gold carry limit.
        // 19  - account/character is trade-restricted.
        // 21  - no mail / mail no longer exists.
        // 60  - personal shop is open.
        // 219 - character is currently trading.
        // Confirmed client mailbox send (0x005E/0x013B) codes used here:
        // 3 receiver missing, 7 self-send, 10 insufficient gold, 14 receiver-level
        // gold limit, 17 invalid attachment, 22 empty content, 23 not tradable,
        // 24 deleted receiver, 70 daily gold limit, 90 blacklist, 114/115 trade
        // restriction, 159 illegal text, 214 locked item, 217 account-bound item,
        // 219 limited-period item, 227 level/send limit, 235 expired item.
        private static byte ToClientErrorCode(MailboxSendError error, ushort ackType)
        {
            if (ackType == 0x005F && error == MailboxSendError.InventoryFull)
                return MailboxClaimClientError.InventoryInsertFailed;

            if (ackType == 0x005F && error == MailboxSendError.ItemCarryLimitExceeded)
                return MailboxClaimClientError.ItemCarryLimitExceeded;

            if (ackType == 0x005F && error == MailboxSendError.MailNotFound)
                return MailboxClaimClientError.NoMail;

            if (ackType == MailboxChangeLetterStatType && error == MailboxSendError.MailboxStorageFull)
                return 22;

            if ((ackType == 0x005E || ackType == 0x013B) && error == MailboxSendError.EmptyContent)
                return 22;

            if ((ackType == 0x005E || ackType == 0x013B) && error == MailboxSendError.TradeRestricted)
                return 114;

            switch (error)
            {
                case MailboxSendError.ServerBusy:
                    return 2;
                case MailboxSendError.ReceiverNotFound:
                    return 3;
                case MailboxSendError.ReceiverDeleted:
                    return 24;
                case MailboxSendError.InsufficientGold:
                    return 10;
                case MailboxSendError.ReceiverGoldLimitExceeded:
                    return 14;
                case MailboxSendError.GoldCarryLimitExceeded:
                    return MailboxClaimClientError.GoldCarryLimitExceeded;
                case MailboxSendError.TradeRestricted:
                    return MailboxClaimClientError.TradeRestricted;
                case MailboxSendError.PersonalShopOpen:
                    return MailboxClaimClientError.PersonalShopOpen;
                case MailboxSendError.Trading:
                    return MailboxClaimClientError.Trading;
                case MailboxSendError.InvalidAttachment:
                    return 17;
                case MailboxSendError.TooManyAttachments:
                    return 17;
                case MailboxSendError.InventoryFull:
                    return MailboxClaimClientError.InventoryInsertFailed;
                case MailboxSendError.ItemCarryLimitExceeded:
                    return MailboxClaimClientError.ItemCarryLimitExceeded;
                case MailboxSendError.MailNotFound:
                    return 17;
                case MailboxSendError.NotTradable:
                    return 23;
                case MailboxSendError.AccountBound:
                    return 217;
                case MailboxSendError.LimitedPeriodItem:
                    return 219;
                case MailboxSendError.ExpiredItem:
                    return 235;
                case MailboxSendError.ItemLocked:
                    return 214;
                case MailboxSendError.DailyGoldLimitExceeded:
                    return 70;
                case MailboxSendError.Blacklisted:
                    return 90;
                case MailboxSendError.IllegalText:
                    return 159;
                case MailboxSendError.ReceiverTradeRestricted:
                    return 115;
                case MailboxSendError.SenderLevelOrSendLimit:
                    return 227;
                case MailboxSendError.SelfSendNotAllowed:
                    return QueryCharacterInfoSelfSendErrorCode;
                case MailboxSendError.EmptyContent:
                case MailboxSendError.InvalidRequest:
                default:
                    return 3;
            }
        }

        private static class MailboxClaimClientError
        {
            public const byte ItemCarryLimitExceeded = 4;
            public const byte InventoryInsertFailed = 4;
            public const byte GoldCarryLimitExceeded = 10;
            public const byte TradeRestricted = 19;
            public const byte NoMail = 21;
            public const byte PersonalShopOpen = 60;
            public const byte Trading = 219;
        }

        private static Task SendQueryCharacterInfoError(EnhancedClientSession session, ushort ackType, byte errorCode)
        {
            FileLogger.Log($"[Mailbox] QUERY_CHARAC_INFO_MAILBOX error ackType=0x{ackType:X4} error={errorCode}");
            return SendSimpleResult(session, ackType, false, errorCode);
        }

        private static byte[] BuildQueryCharacterInfoAck(CharacterRecord character, string fallbackName)
        {
            var nameBytes = GetResponseNameBytes(character, fallbackName);
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt32(nameBytes.Length);
            writer.WriteBytes(nameBytes);
            writer.WriteUInt16(character.Level);
            // Client sub_CD2A00 unconditionally reads three bytes after level.
            // The first is passed to sub_1195CF0's 0..12 base-job table. Keep
            // the server's existing GrowType + zero mapping for the remaining
            // two fields; this client UI path reads but does not consume them.
            writer.WriteByte(character.Job);
            writer.WriteByte(character.GrowType);
            writer.WriteByte(0x00);
            return writer.ToArray();
        }

        private static byte[] BuildOpenMailboxAck(ushort notLoadedCount)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteUInt16(notLoadedCount);
            return writer.ToArray();
        }

        private static byte[] BuildRemoveMailNotification(int messageId)
        {
            return BuildRemoveMailNotification(new[] { messageId });
        }

        internal static byte[] BuildRemoveMailNotification(IReadOnlyList<int> messageIds)
        {
            var writer = new GamePacketWriter();
            var count = Math.Min(messageIds?.Count ?? 0, 200);
            writer.WriteInt32(count);
            for (var i = 0; i < count; i++)
                writer.WriteInt32(messageIds[i]);
            return writer.ToArray();
        }

        internal static byte[] BuildMailboxListNotification(IReadOnlyList<MailboxListEntry> entries, bool isFirstLoad, ushort notLoadedCount)
        {
            var writer = new GamePacketWriter();
            var count = Math.Min(entries?.Count ?? 0, MailboxPageSize + MailboxStorageLimit);
            var summaryRecords = new List<MailboxSummaryRecord>();

            for (var i = 0; i < count; i++)
            {
                var entry = entries[i];
                var wroteAttachment = false;
                var attachments = entry?.Attachments ?? Array.Empty<MailboxAttachmentEntry>();

                if (MailboxSummaryAttachmentPreviewEnabled)
                {
                    for (var j = 0; j < attachments.Count && summaryRecords.Count < byte.MaxValue; j++)
                    {
                        var attachment = attachments[j];
                        if (attachment == null || attachment.ItemTemplateId <= 0 || attachment.ItemCount <= 0)
                            continue;

                        summaryRecords.Add(new MailboxSummaryRecord(entry, attachment, !wroteAttachment));
                        wroteAttachment = true;
                    }
                }

                if (!wroteAttachment && entry != null && entry.Gold > 0 && summaryRecords.Count < byte.MaxValue)
                {
                    summaryRecords.Add(new MailboxSummaryRecord(entry, null, true));
                    wroteAttachment = true;
                }

                // 纯正文邮件也要占一条 summary，给客户端当前邮件 id，避免剩余天数算成负数。
                if (!wroteAttachment && entry != null && !string.IsNullOrEmpty(entry.Body) && summaryRecords.Count < byte.MaxValue)
                    summaryRecords.Add(new MailboxSummaryRecord(entry, null, false, seedOnly: true));
            }

            writer.WriteByte((byte)summaryRecords.Count);
            writer.WriteByte(isFirstLoad ? (byte)1 : (byte)0);

            foreach (var record in summaryRecords)
                WriteMailboxSummary(writer, record.Entry, record.Attachment, record.IncludeGold, record.SeedOnly);

            writer.WriteUInt16(notLoadedCount);

            writer.WriteUInt16((ushort)count);
            for (var i = 0; i < count; i++)
                WriteMailboxLetterDetail(writer, entries[i]);

            return writer.ToArray();
        }

        internal static IReadOnlyList<byte[]> BuildMailboxListNotificationBatches(
            IReadOnlyList<MailboxListEntry> entries,
            ushort notLoadedCount)
        {
            var count = Math.Min(entries?.Count ?? 0, MailboxPageSize + MailboxStorageLimit);
            if (count == 0)
            {
                return new[]
                {
                    BuildMailboxListNotification(
                        Array.Empty<MailboxListEntry>(),
                        isFirstLoad: false,
                        notLoadedCount)
                };
            }

            // The client always inserts a newly decoded row at index zero. Rows
            // with gold/attachments are created in the summary pass, while
            // body-only rows (including fully claimed letters) are created in
            // the later detail pass. Mixing both kinds in one packet therefore
            // moves every body-only row above every summary row, regardless of
            // created_at. Split only at stage transitions: the first packet
            // clears both containers and later packets preserve existing rows.
            var result = new List<byte[]>();
            var batch = new List<MailboxListEntry>();
            var createsInSummary = CreatesMailboxRowInSummary(entries[0]);

            for (var i = 0; i < count; i++)
            {
                var entry = entries[i];
                var currentCreatesInSummary = CreatesMailboxRowInSummary(entry);
                if (batch.Count > 0 && currentCreatesInSummary != createsInSummary)
                {
                    result.Add(BuildMailboxListNotification(
                        batch,
                        isFirstLoad: result.Count > 0,
                        notLoadedCount));
                    batch = new List<MailboxListEntry>();
                    createsInSummary = currentCreatesInSummary;
                }

                batch.Add(entry);
            }

            if (batch.Count > 0)
            {
                result.Add(BuildMailboxListNotification(
                    batch,
                    isFirstLoad: result.Count > 0,
                    notLoadedCount));
            }

            return result;
        }

        private static bool CreatesMailboxRowInSummary(MailboxListEntry entry)
        {
            if (entry == null)
                return false;

            if (MailboxSummaryAttachmentPreviewEnabled)
            {
                var attachments = entry.Attachments ?? Array.Empty<MailboxAttachmentEntry>();
                for (var i = 0; i < attachments.Count; i++)
                {
                    var attachment = attachments[i];
                    if (attachment != null
                        && attachment.ItemTemplateId > 0
                        && attachment.ItemCount > 0)
                    {
                        return true;
                    }
                }
            }

            return entry.Gold > 0;
        }

        private static void WriteMailboxSummary(GamePacketWriter writer, MailboxListEntry entry, MailboxAttachmentEntry attachment, bool includeGold, bool seedOnly)
        {
            var remainSeconds = Math.Max(0, entry?.RemainSeconds ?? 0);
            var messageId = (int)(entry?.MessageId ?? 0);
            var senderCharacterId = GetMailboxSenderCharacterId(entry);
            var senderName = GetMailboxSenderName(entry);

            // 首字段是领取对象，末字段是同一封邮件的合并 key。
            var hasItemAttachment = MailboxSummaryAttachmentPreviewEnabled
                && attachment != null
                && attachment.ItemTemplateId > 0
                && attachment.ItemCount > 0;
            var claimObjectId = hasItemAttachment
                ? ClampInt32(MailboxRepository.AttachmentClaimFlag + attachment.AttachmentId)
                : messageId;

            writer.WriteInt32(claimObjectId);
            writer.WriteInt32(senderCharacterId);
            WriteMailboxString(writer, senderName, MailboxSenderNameSize);
            writer.WriteInt32(includeGold ? (entry?.Gold ?? 0) : 0);

            // 附件字段按 A21 列表读序写出。
            writer.WriteInt32(hasItemAttachment ? attachment.ItemTemplateId : 0);
            writer.WriteByte(hasItemAttachment ? (byte)1 : (byte)0);

            var itemCount = hasItemAttachment ? Math.Max(1, attachment.ItemCount) : 0;
            var itemCore = hasItemAttachment ? MailboxItemCoreCodec.Decode(attachment) : null;
            var isStackableAttachment = hasItemAttachment
                && itemCore != null
                && InventoryStackRuleService.IsStackable(itemCore);
            var equipmentType = hasItemAttachment
                ? EquipmentTypeInfo.ParseOrUnknown(
                    ItemMetadataResolver.Resolve(attachment.ItemTemplateId)?.EquipmentType)
                : EquipmentType.Unknown;
            // 时装为装备类型 0-11；宠物 extras 仅 type 25。无 PVF 类型时按 ItemKind 回退。
            var isAvatarAttachment = hasItemAttachment
                && (EquipmentTypeInfo.IsAvatarPart(equipmentType)
                    || (equipmentType == EquipmentType.Unknown
                        && itemCore?.ItemKind == ItemCore.KindAvatar));
            var isCreatureAttachment = hasItemAttachment
                && (equipmentType == EquipmentType.Creature
                    || (equipmentType == EquipmentType.Unknown
                        && itemCore?.ItemKind == ItemCore.KindCreature));
            var isEquipmentAttachment = hasItemAttachment
                && (itemCore?.ItemKind == ItemCore.KindEquipment
                    || itemCore?.ItemKind == ItemCore.KindCreatureEquipment);
            var isPetArtifactAttachment = isEquipmentAttachment
                && !isCreatureAttachment
                && itemCore?.ItemKind == ItemCore.KindCreatureEquipment;
            var structuredItem = itemCore;
            var instanceValue = hasItemAttachment
                ? (isStackableAttachment
                    ? itemCount
                    : (itemCore?.Value ?? attachment.InstanceValue))
                : 0;

            // 宠物装备耐久位写剩余秒数，0 表示永久；其他装备写耐久。
            var durability = isPetArtifactAttachment
                ? ResolvePetArtifactRemainingSeconds(itemCore?.ExpireTime ?? attachment.ExpireTime)
                : (structuredItem != null
                    ? structuredItem.Durability
                    : (hasItemAttachment ? ClampUInt16(attachment.Durability) : (ushort)0));
            var itemAttr = structuredItem?.Attr
                ?? (hasItemAttachment ? ClampByte(attachment.SealFlag) : (byte)0);
            // 附魔卡、强化次数、增幅类型与增幅值；堆叠附件数量走 instance 字段。
            var enchantOrCount = structuredItem != null ? structuredItem.EnchantCardId : itemCount;
            var enchantUpgradeCount = structuredItem != null ? structuredItem.EnchantUpgradeCount : (byte)0;
            var amplifyValue = structuredItem != null
                ? structuredItem.AmplifyValue
                : (hasItemAttachment ? ClampUInt16(attachment.Marker16) : (ushort)0);
            var expireTime = structuredItem != null
                ? structuredItem.ExpireTime
                : (hasItemAttachment ? attachment.ExpireTime : 0);
            // 宠物 type=25 在 expire 前写 i32 + flag，flag 固定 0。
            var typeSpecificExtra = isCreatureAttachment
                ? ResolveMailboxCreatureExtra(structuredItem)
                : 0;
            var protocolAmplifyType = structuredItem != null
                ? structuredItem.AmplifyType
                : (hasItemAttachment ? ClampByte(ReadAttachmentExtraInt(attachment, "mailboxFieldByte3", 0)) : (byte)0);
            var dfWord = structuredItem != null
                ? structuredItem.Rune
                : (hasItemAttachment ? ClampUInt16(ReadAttachmentExtraInt(attachment, "mailboxDfWord", 0)) : (ushort)0);
            var v47 = structuredItem?.GenuineUpgrade ?? ClampByte(ReadAttachmentExtraInt(attachment, "mailboxV47", 0));
            var v25 = structuredItem?.EmancipateEquipmentLevel ?? ClampByte(ReadAttachmentExtraInt(attachment, "mailboxV25", 0));
            var v48 = structuredItem?.TradeRestriction ?? ClampByte(ReadAttachmentExtraInt(attachment, "mailboxV48", 0));
            var tailWord = structuredItem?.TailUnknown0 ?? ClampUInt16(ReadAttachmentExtraInt(attachment, "mailboxTailWord", 0));
            var tailByte1 = structuredItem?.TailUnknown1 ?? ClampByte(ReadAttachmentExtraInt(attachment, "mailboxTailByte1", 0));
            var v19 = structuredItem?.TailUnknown2 ?? ClampByte(ReadAttachmentExtraInt(attachment, "mailboxV19", 0));
            var tailByte2 = structuredItem?.TailUnknown3 ?? ClampByte(ReadAttachmentExtraInt(attachment, "mailboxTailByte2", 0));
            var tailByte3 = structuredItem?.RemainUseCount ?? ClampByte(ReadAttachmentExtraInt(attachment, "mailboxTailByte3", 0));
            var tailByte4 = structuredItem?.SortLockFlag ?? ClampByte(ReadAttachmentExtraInt(attachment, "mailboxTailByte4", 0));
            var attrArray = structuredItem != null
                ? ReadEquipmentEmblemIds(structuredItem)
                : (hasItemAttachment ? ReadAttachmentExtraDwordArray(attachment, "mailboxAttrArray") : Array.Empty<int>());

            writer.WriteInt32(instanceValue);
            writer.WriteUInt16(durability);
            writer.WriteByte(itemAttr);
            writer.WriteInt32(enchantOrCount);
            writer.WriteByte(enchantUpgradeCount);
            writer.WriteByte(protocolAmplifyType);
            writer.WriteUInt16(amplifyValue);
            WriteA21MailboxCreatureExtras(writer, isCreatureAttachment, typeSpecificExtra);
            writer.WriteInt32(expireTime);
            WriteDwordArray(writer, attrArray);
            writer.WriteUInt16(dfWord);
            WriteMailboxSealData(writer, structuredItem);
            writer.WriteByte(v47);
            writer.WriteByte(v25);
            writer.WriteByte(v48);
            writer.WriteUInt16(tailWord);
            writer.WriteByte(tailByte1);
            writer.WriteByte(v19);
            writer.WriteByte(tailByte2);
            writer.WriteByte(tailByte3);
            writer.WriteByte(tailByte4);
            // 尾部固定两段长度前缀 blob；非时装 0/0，时装 18B 宝珠 + 4B 颜色。
            WriteA21MailboxItemBlobs(writer, isAvatarAttachment, attachment);
            writer.WriteByte(0);
            // 时装在 extra1 之后、remain 之前再写两段空长度前缀。
            WriteA21MailboxAvatarPostCreateBlobs(writer, isAvatarAttachment);
            writer.WriteInt32(remainSeconds);
            var summaryKey = seedOnly ? 0 : messageId;
            writer.WriteInt32(summaryKey);
            writer.WriteByte(0);
        }

        private static int ResolveMailboxCreatureExtra(ItemCore core)
        {
            if (core == null || core.Marker16 < 0)
                return 0;
            return core.Marker16;
        }

        private static void WriteA21MailboxCreatureExtras(
            GamePacketWriter writer,
            bool isCreatureAttachment,
            int typeSpecificExtra)
        {
            if (!isCreatureAttachment)
                return;

            writer.WriteInt32(typeSpecificExtra);
            writer.WriteByte(0);
        }

        private static void WriteA21MailboxItemBlobs(
            GamePacketWriter writer,
            bool isAvatarAttachment,
            MailboxAttachmentEntry attachment)
        {
            if (!isAvatarAttachment)
            {
                writer.WriteInt32(0);
                writer.WriteInt32(0);
                return;
            }

            var avatarDetail = MailboxItemDetailCodec
                .BuildCreateOptions(attachment?.DetailJson)
                ?.AvatarDetailTemplate;
            var jewelSocket = avatarDetail != null
                ? JewelSocket.FromBytes(avatarDetail.JewelSocket).ToBytes()
                : JewelSocket.FromBytes(
                    ReadAttachmentExtraHexBytes(attachment, "reserved2")).ToBytes();
            var legacyColor = avatarDetail == null
                ? ReadAttachmentExtraHexBytes(attachment, "tailData")
                : Array.Empty<byte>();
            var color1 = avatarDetail?.Color1
                ?? ReadUInt16LittleEndian(legacyColor, 0);
            var color2 = avatarDetail?.Color2
                ?? ReadUInt16LittleEndian(legacyColor, 2);

            writer.WriteInt32(ItemListProtocolWriter.A21AvatarJewelBytes);
            WriteFixedMailboxBytes(writer, jewelSocket, ItemListProtocolWriter.A21AvatarJewelBytes);
            writer.WriteInt32(4);
            writer.WriteUInt16(color1);
            writer.WriteUInt16(color2);
        }

        private static void WriteA21MailboxAvatarPostCreateBlobs(
            GamePacketWriter writer,
            bool isAvatarAttachment)
        {
            if (!isAvatarAttachment)
                return;

            writer.WriteInt32(0);
            writer.WriteInt32(0);
        }

        private static void WriteFixedMailboxBytes(GamePacketWriter writer, byte[] value, int length)
        {
            if (value == null || value.Length == 0)
            {
                writer.WriteZeroBytes(length);
                return;
            }

            if (value.Length == length)
            {
                writer.WriteBytes(value);
                return;
            }

            var buffer = new byte[length];
            Buffer.BlockCopy(value, 0, buffer, 0, Math.Min(value.Length, length));
            writer.WriteBytes(buffer);
        }

        private static ushort ReadUInt16LittleEndian(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 1 >= data.Length)
                return 0;

            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        private static byte[] ReadAttachmentExtraHexBytes(MailboxAttachmentEntry attachment, string propertyName)
        {
            if (attachment == null || string.IsNullOrWhiteSpace(attachment.ExtraJson))
                return Array.Empty<byte>();

            var token = "\"" + propertyName + "\":\"";
            var start = attachment.ExtraJson.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return Array.Empty<byte>();

            start += token.Length;
            var end = attachment.ExtraJson.IndexOf('"', start);
            if (end <= start)
                return Array.Empty<byte>();

            var hex = attachment.ExtraJson.Substring(start, end - start);
            if ((hex.Length & 1) != 0)
                return Array.Empty<byte>();

            try
            {
                return Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                return Array.Empty<byte>();
            }
        }

        private static int ReadAttachmentExtraInt(MailboxAttachmentEntry attachment, string propertyName, int fallback)
        {
            if (attachment == null || string.IsNullOrWhiteSpace(attachment.ExtraJson))
                return fallback;

            var token = "\"" + propertyName + "\":";
            var start = attachment.ExtraJson.IndexOf(token, StringComparison.Ordinal);
            if (start < 0)
                return fallback;

            start += token.Length;
            var end = attachment.ExtraJson.IndexOfAny(new[] { ',', '}' }, start);
            if (end < 0)
                end = attachment.ExtraJson.Length;

            var valueText = attachment.ExtraJson.Substring(start, end - start).Trim();
            return int.TryParse(valueText, out var value) ? value : fallback;
        }

        private static int[] ReadAttachmentExtraDwordArray(MailboxAttachmentEntry attachment, string propertyName)
        {
            if (attachment == null || string.IsNullOrWhiteSpace(attachment.ExtraJson))
                return Array.Empty<int>();

            var token = "\"" + propertyName + "\":\"";
            var start = attachment.ExtraJson.IndexOf(token, StringComparison.Ordinal);
            if (start < 0)
                return Array.Empty<int>();

            start += token.Length;
            var end = attachment.ExtraJson.IndexOf('"', start);
            if (end < 0)
                return Array.Empty<int>();

            var text = attachment.ExtraJson.Substring(start, end - start);
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<int>();

            var parts = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var values = new List<int>();
            foreach (var part in parts)
            {
                if (values.Count >= byte.MaxValue)
                    break;
                var valueText = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part.Substring(2) : part;
                if (int.TryParse(valueText, part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }

            return values.ToArray();
        }

        private static void WriteDwordArray(GamePacketWriter writer, IReadOnlyList<int> values)
        {
            var count = values == null ? 0 : Math.Min(values.Count, byte.MaxValue);
            writer.WriteByte((byte)count);
            for (var i = 0; i < count; i++)
                writer.WriteInt32(values[i]);
        }

        private static int[] ReadEquipmentEmblemIds(ItemCore core)
        {
            if (core == null || core.EmblemSocketCount == 0)
                return Array.Empty<int>();

            var count = Math.Min(core.EmblemSocketCount, (byte)2);
            var values = new List<int>(count);
            if (count > 0)
                values.Add(core.EmblemId1);
            if (count > 1)
                values.Add(core.EmblemId2);
            return values.ToArray();
        }

        private static void WriteMailboxSealData(GamePacketWriter writer, ItemCore equipment)
        {
            var randomOptions = equipment?.RandomOptions;
            if (randomOptions == null || randomOptions.Count == 0)
            {
                writer.WriteByte(0);
                return;
            }

            var count = Math.Min(randomOptions.Count, 3);
            writer.WriteByte((byte)count);
            for (var index = 0; index < count; index++)
            {
                writer.WriteByte(randomOptions[index].Type);
                writer.WriteByte(randomOptions[index].Value1);
                writer.WriteByte(randomOptions[index].Value2);
            }

            writer.WriteByte(equipment.RandomOptionState);
            writer.WriteByte(equipment.RandomOptionChangedIndex);
            if (equipment.RandomOptionChangedIndex != 0xFF)
            {
                writer.WriteByte(equipment.RandomOptionChangeState);
                writer.WriteByte(equipment.RandomOptionChange.Type);
                writer.WriteByte(equipment.RandomOptionChange.Value1);
                writer.WriteByte(equipment.RandomOptionChange.Value2);
            }
        }

        private static byte ReadByte(byte[] data, int offset)
        {
            return data != null && offset >= 0 && offset < data.Length ? data[offset] : (byte)0;
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            return data != null && offset >= 0 && offset + 2 <= data.Length
                ? BitConverter.ToUInt16(data, offset)
                : (ushort)0;
        }

        private static byte ClampByte(int value)
        {
            if (value <= byte.MinValue)
                return byte.MinValue;
            if (value >= byte.MaxValue)
                return byte.MaxValue;
            return (byte)value;
        }

        private static ushort ClampUInt16(int value)
        {
            if (value <= ushort.MinValue)
                return ushort.MinValue;
            if (value >= ushort.MaxValue)
                return ushort.MaxValue;
            return (ushort)value;
        }

        private static ushort ResolvePetArtifactRemainingSeconds(int expireTime)
        {
            if (expireTime < MinExpirationUnixTime)
                return 0;

            var remainingSeconds = (long)expireTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (remainingSeconds <= 0)
                return 0;

            return remainingSeconds >= ushort.MaxValue
                ? ushort.MaxValue
                : (ushort)remainingSeconds;
        }

        private static int ClampInt32(long value)
        {
            if (value <= int.MinValue)
                return int.MinValue;
            if (value >= int.MaxValue)
                return int.MaxValue;
            return (int)value;
        }

        private static void WriteMailboxLetterDetail(GamePacketWriter writer, MailboxListEntry entry)
        {
            // detail 的时间字段是创建 Unix 秒；剩余秒数只写在 summary。
            var createdAtUnixSeconds = Math.Max(0, entry?.CreatedAtUnixSeconds ?? 0);
            var letterStat = Math.Max(0, entry?.LetterStat ?? 0);
            var senderCharacterId = GetMailboxSenderCharacterId(entry);
            var senderName = GetMailboxSenderName(entry);

            writer.WriteInt32((int)(entry?.MessageId ?? 0));
            writer.WriteInt32(senderCharacterId);
            WriteMailboxString(writer, senderName, MailboxSenderNameSize);
            WriteMailboxString(writer, BuildMailboxDisplayText(entry), MailboxLetterTextSize);
            writer.WriteInt32(createdAtUnixSeconds);
            writer.WriteUInt16((ushort)letterStat);
            writer.WriteByte(0);
        }

        private static string BuildMailboxDisplayText(MailboxListEntry entry)
        {
            if (entry == null)
                return string.Empty;

            if (string.IsNullOrWhiteSpace(entry.Title)
                || string.Equals(entry.Title, entry.Body, StringComparison.Ordinal))
            {
                return entry.Body ?? string.Empty;
            }

            return string.IsNullOrWhiteSpace(entry.Body)
                ? entry.Title
                : entry.Title + "\n" + entry.Body;
        }

        private static int GetMailboxSenderCharacterId(MailboxListEntry entry)
        {
            return IsOfficialMailboxEntry(entry)
                ? OfficialMailSenderCharacterId
                : (entry?.SenderCharacterId ?? 0);
        }

        private static string GetMailboxSenderName(MailboxListEntry entry)
        {
            return IsOfficialMailboxEntry(entry)
                ? OfficialMailSenderName
                : (entry?.SenderName ?? string.Empty);
        }

        private static bool IsOfficialMailboxEntry(MailboxListEntry entry)
        {
            return entry != null && entry.MailType != 0;
        }

        private static void WriteMailboxString(GamePacketWriter writer, string value, int maxBytes)
        {
            // 邮件字符串用 GBK；长度按缓冲区预留结尾 0。
            var bytes = ClientTextEncoding.Truncate(value ?? string.Empty, Math.Max(0, maxBytes - 1));
            writer.WriteInt32(bytes.Length);
            writer.WriteBytes(bytes);
        }

        private static string ReadMailboxName(byte[] body)
        {
            if (body == null || body.Length < 4)
                return string.Empty;

            var length = BitConverter.ToInt32(body, 0);
            if (length <= 0 || body.Length < 4 + length)
                return string.Empty;

            return ClientTextEncoding.GetString(body, 4, length);
        }

        private static bool TryParseSendMailboxRequest(byte[] body, MailboxSendFormat format, out SendMailboxRequest request, out string error)
        {
            request = null;
            error = null;

            if (body == null)
            {
                error = "body is null";
                return false;
            }

            var offset = 0;
            if (!TryReadDstr(body, ref offset, out var receiverName, out error))
                return false;

            if (!TryReadInt32(body, ref offset, out var gold))
            {
                error = "missing gold";
                return false;
            }

            ushort attachmentCount = 1;
            if (format == MailboxSendFormat.MultiAttachment && !TryReadUInt16(body, ref offset, out attachmentCount))
            {
                error = "missing attachment count";
                return false;
            }

            if (attachmentCount > 10)
            {
                error = $"invalid attachment count={attachmentCount}";
                return false;
            }

            var attachments = new SendMailboxAttachment[Math.Min(attachmentCount, (ushort)10)];
            for (var i = 0; i < attachments.Length; i++)
            {
                byte itemType = 0;
                ushort itemSlot;
                int itemId;
                int itemCount;

                if (format == MailboxSendFormat.SingleAttachment && !TryReadByte(body, ref offset, out itemType))
                {
                    error = $"missing attachment[{i}] item type";
                    return false;
                }

                // Multi-mail omits the first main-list type; each later record prefixes its own list type.
                if (format == MailboxSendFormat.MultiAttachment && i > 0 && !TryReadByte(body, ref offset, out itemType))
                {
                    error = $"missing attachment[{i}] item type";
                    return false;
                }

                if (!TryReadUInt16(body, ref offset, out itemSlot))
                {
                    error = $"missing attachment[{i}] slot";
                    return false;
                }

                if (!TryReadInt32(body, ref offset, out itemId))
                {
                    error = $"missing attachment[{i}] item id";
                    return false;
                }

                if (!TryReadInt32(body, ref offset, out itemCount))
                {
                    error = $"missing attachment[{i}] count";
                    return false;
                }

                attachments[i] = new SendMailboxAttachment
                {
                    ItemType = itemType,
                    ItemSlot = itemSlot,
                    ItemId = itemId,
                    ItemCount = itemCount
                };
            }

            var text = string.Empty;
            if (offset < body.Length && !TryReadDstr(body, ref offset, out text, out error))
                return false;

            var tailLength = Math.Max(0, body.Length - offset);
            var tailBytes = new byte[tailLength];
            if (tailLength > 0)
                Buffer.BlockCopy(body, offset, tailBytes, 0, tailLength);

            request = new SendMailboxRequest
            {
                ReceiverName = receiverName,
                Gold = gold,
                AttachmentCount = attachmentCount,
                Attachments = attachments,
                Text = text,
                TailBytes = tailBytes
            };
            return true;
        }

        private static IReadOnlyList<MailboxSendAttachmentRequest> ConvertAttachments(SendMailboxAttachment[] attachments)
        {
            var list = new List<MailboxSendAttachmentRequest>();
            if (attachments == null)
                return list;

            foreach (var attachment in attachments)
            {
                if (attachment == null || attachment.ItemId <= 0 || attachment.ItemCount <= 0)
                    continue;

                list.Add(new MailboxSendAttachmentRequest
                {
                    ItemType = attachment.ItemType,
                    ItemSlot = attachment.ItemSlot,
                    ItemId = attachment.ItemId,
                    ItemCount = attachment.ItemCount
                });
            }

            return list;
        }

        private static string NormalizeMailboxText(string text)
        {
            if (string.Equals(text, " ", StringComparison.Ordinal))
                return DefaultMailboxSafetyText;
            return text ?? string.Empty;
        }

        private static bool TryReadDstr(byte[] body, ref int offset, out string value, out string error)
        {
            value = string.Empty;
            error = null;

            if (!TryReadInt32(body, ref offset, out var length))
            {
                error = "missing dstr length";
                return false;
            }

            if (length < 0 || body.Length - offset < length)
            {
                error = $"invalid dstr length={length} offset={offset} bodyLen={body.Length}";
                return false;
            }

            value = ClientTextEncoding.GetString(body, offset, length);
            offset += length;
            return true;
        }

        private static bool TryReadByte(byte[] body, ref int offset, out byte value)
        {
            value = 0;
            if (body == null || body.Length - offset < 1)
                return false;

            value = body[offset++];
            return true;
        }

        private static bool TryReadUInt16(byte[] body, ref int offset, out ushort value)
        {
            value = 0;
            if (body == null || body.Length - offset < 2)
                return false;

            value = BitConverter.ToUInt16(body, offset);
            offset += 2;
            return true;
        }

        private static bool TryReadInt32(byte[] body, ref int offset, out int value)
        {
            value = 0;
            if (body == null || body.Length - offset < 4)
                return false;

            value = BitConverter.ToInt32(body, offset);
            offset += 4;
            return true;
        }

        private static byte[] GetResponseNameBytes(CharacterRecord character, string fallbackName)
        {
            var nameBytes = character.Name != null && character.Name.Length > 0
                ? character.Name
                : ClientTextEncoding.GetBytes(fallbackName ?? string.Empty);

            var length = ClientTextEncoding.ClampPrefixLength(nameBytes, QueryCharacterInfoNameSize);
            var response = new byte[length];
            Buffer.BlockCopy(nameBytes, 0, response, 0, length);
            return response;
        }

        private enum MailboxSendFormat
        {
            SingleAttachment,
            MultiAttachment
        }

        private sealed class SendMailboxRequest
        {
            public string ReceiverName { get; set; }
            public int Gold { get; set; }
            public ushort AttachmentCount { get; set; }
            public SendMailboxAttachment[] Attachments { get; set; }
            public string Text { get; set; }
            public byte[] TailBytes { get; set; }
        }

        private sealed class SendMailboxAttachment
        {
            public byte ItemType { get; set; }
            public ushort ItemSlot { get; set; }
            public int ItemId { get; set; }
            public int ItemCount { get; set; }
        }

        private sealed class MailboxSummaryRecord
        {
            public MailboxSummaryRecord(MailboxListEntry entry, MailboxAttachmentEntry attachment, bool includeGold, bool seedOnly = false)
            {
                Entry = entry;
                Attachment = attachment;
                IncludeGold = includeGold;
                SeedOnly = seedOnly;
            }

            public MailboxListEntry Entry { get; }
            public MailboxAttachmentEntry Attachment { get; }
            public bool IncludeGold { get; }
            public bool SeedOnly { get; }
        }

        private sealed class MailboxPageRemovalRefreshState
        {
            public object SyncRoot { get; } = new object();
            public HashSet<int> MessageIds { get; } = new HashSet<int>();
        }

        private sealed class MailboxAlarmState
        {
            public object SyncRoot { get; } = new object();
            public int PendingCount;
            public bool NotificationSent;
        }
    }
}
