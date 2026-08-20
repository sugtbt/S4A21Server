using DfoServer.Game.Currency;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace DfoServer.Game.Mailbox
{
    public sealed class MailboxRepository
    {
        internal const string DailyTradeGoldCounterKey = "mailbox_trade_gold_sent";

        // claimObjectId 命名空间标记: 邮件列表(0x0061)摘要行的领取对象有两类——
        // 附件行以 AttachmentClaimFlag + attachment_id 编码, 纯金币/文本行原样 messageId。
        // 协议上该字段是单一 i32(Reverse/CMD_PACKET/95.md), 两条 AUTOINCREMENT 序列
        // 必然相撞(MR46 #1), 用高位标记把它们隔进两个不交叉的空间(邮件 ID 永远达不到该量级)。
        internal const long AttachmentClaimFlag = 0x40000000L;
        private static readonly ConcurrentDictionary<int, int> ItemWeightCache = new ConcurrentDictionary<int, int>();

        private readonly string _connectionString;

        public MailboxRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public MailboxRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public MailboxSendResult SendMail(MailboxSendRequest request)
        {
            if (request == null)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            return InventoryContext.TryGetLease(request.SenderCharacterId, out var lease)
                ? SendMail(request, lease)
                : MailboxSendResult.Fail(MailboxSendError.ServerBusy);
        }

        internal MailboxSendResult SendMail(MailboxSendRequest request, InventoryLease lease)
        {
            if (request == null || lease == null || lease.CharacterId != request.SenderCharacterId)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            lock (lease.SyncRoot)
            {
                if (!FlushPendingInventoryChanges(lease))
                    return MailboxSendResult.Fail(MailboxSendError.ServerBusy);

                var inventoryMutated = false;
                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        using (InventoryUidAllocationContext.Enter(connection, transaction))
                        {
                            var result = SendMail(connection, transaction, request, lease, out inventoryMutated);
                            if (result.Success)
                            {
                                transaction.Commit();
                                lease.Inventory.ClearDirtyState();
                                return result;
                            }

                            transaction.Rollback();
                            if (inventoryMutated)
                                ReloadOnlineInventoryAfterRollback(lease);
                            return result;
                        }
                    }
                }
                catch
                {
                    if (inventoryMutated)
                        ReloadOnlineInventoryAfterRollback(lease);
                    throw;
                }
            }
        }

        public MailboxSendResult SendSystemMail(MailboxSendRequest request)
        {
            if (request == null)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var result = SendSystemMail(connection, transaction, request);
                    if (result.Success)
                        transaction.Commit();
                    return result;
                }
            }
        }

        public MailboxSendResult SendSystemMails(IReadOnlyList<MailboxSendRequest> requests)
        {
            if (requests == null || requests.Count == 0)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    MailboxSendResult last = null;
                    foreach (var request in requests)
                    {
                        var result = SendSystemMail(connection, transaction, request);
                        if (!result.Success)
                            return result;

                        last = result;
                    }

                    transaction.Commit();
                    return last ?? MailboxSendResult.Fail(MailboxSendError.InvalidRequest);
                }
            }
        }

        internal MailboxSendResult SendSystemMails(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyList<MailboxSendRequest> requests)
        {
            if (connection == null || transaction == null || requests == null || requests.Count == 0)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            MailboxSendResult last = null;
            foreach (var request in requests)
            {
                var result = SendSystemMail(connection, transaction, request);
                if (!result.Success)
                    return result;

                last = result;
            }

            return last ?? MailboxSendResult.Fail(MailboxSendError.InvalidRequest);
        }

        public IReadOnlyList<MailboxListEntry> LoadInbox(int characterId, int limit)
        {
            return LoadInboxPage(characterId, limit).Entries;
        }

        public MailboxExpirationBatchResult MaintainExpiredMail(int expireBatchSize = 200, int purgeBatchSize = 100)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var result = MaintainExpiredMailBatch(
                        connection,
                        transaction,
                        Math.Max(1, expireBatchSize),
                        Math.Max(1, purgeBatchSize));
                    transaction.Commit();
                    return result;
                }
            }
        }

        public MailboxInboxPage LoadInboxPage(int characterId, int limit)
        {
            var entries = new List<MailboxListEntry>();
            if (characterId <= 0 || limit <= 0)
                return new MailboxInboxPage { Entries = entries, TotalCount = 0, LoadedInboxCount = 0 };

            var totalCount = 0;
            var loadedInboxCount = 0;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    ExpireNormalInbox(connection, transaction, characterId);
                    using (var countCommand = connection.CreateCommand())
                    {
                        countCommand.Transaction = transaction;
                        countCommand.CommandText = @"
SELECT COUNT(*)
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id = @cid
  AND r.folder = 0
  AND r.saved_flag = 0
  AND r.deleted_flag = 0
  AND (m.unlimited_flag != 0 OR m.expire_at > CURRENT_TIMESTAMP);";
                        countCommand.Parameters.AddWithValue("@cid", characterId);
                        totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
WITH inbox AS (
    SELECT r.recipient_id, 0 AS mailbox_group
    FROM mailbox_recipients r
    JOIN mailbox_messages m ON m.message_id = r.message_id
    WHERE r.character_id = @cid
      AND r.folder = 0
      AND r.saved_flag = 0
      AND r.deleted_flag = 0
      AND (m.unlimited_flag != 0 OR m.expire_at > CURRENT_TIMESTAMP)
    ORDER BY datetime(m.created_at) ASC, m.message_id ASC
    LIMIT @limit
),
stored AS (
    SELECT r.recipient_id, 1 AS mailbox_group
    FROM mailbox_recipients r
    JOIN mailbox_messages m ON m.message_id = r.message_id
    WHERE r.character_id = @cid
      AND r.folder = 0
      AND r.saved_flag = 1
      AND r.deleted_flag = 0
    ORDER BY datetime(COALESCE(r.saved_at, r.created_at)) ASC, m.message_id ASC
    LIMIT 10
),
selected AS (
    SELECT recipient_id, mailbox_group FROM inbox
    UNION ALL
    SELECT recipient_id, mailbox_group FROM stored
)
SELECT
    m.message_id,
    m.sender_character_id,
    m.sender_name,
    m.title,
    m.body,
    CASE WHEN r.received_gold_flag = 0 THEN m.gold ELSE 0 END AS gold,
    CASE
        -- Keep compatibility with mailbox rows written by older tools, which
        -- represented permanent administrator mail only with the year-9999
        -- expiration sentinel and did not populate unlimited_flag.
        WHEN m.unlimited_flag != 0 OR m.expire_at >= '9999-01-01 00:00:00' THEN 0
        ELSE MIN(
            2147483647,
            MAX(0, CAST(strftime('%s', m.expire_at) AS INTEGER) - CAST(strftime('%s', 'now') AS INTEGER)))
    END AS remain_seconds,
    CAST(strftime('%s', m.created_at) AS INTEGER) AS created_at_unix_seconds,
    r.read_flag,
    r.saved_flag,
    m.mail_type,
    m.source_protocol,
    s.mailbox_group
FROM selected s
JOIN mailbox_recipients r ON r.recipient_id = s.recipient_id
JOIN mailbox_messages m ON m.message_id = r.message_id
-- The client prepends each decoded row. Send each selected page newest-first so
-- the rendered UI remains oldest-first while still selecting the oldest page.
ORDER BY s.mailbox_group ASC, datetime(m.created_at) DESC, m.message_id DESC;";
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.Parameters.AddWithValue("@limit", limit);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var read = reader.GetInt32(8) != 0;
                                var saved = reader.GetInt32(9) != 0;
                                entries.Add(new MailboxListEntry
                                {
                                    MessageId = reader.GetInt64(0),
                                    SenderCharacterId = reader.GetInt32(1),
                                    SenderName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                    Title = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                                    Body = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                                    Gold = reader.GetInt32(5),
                                    RemainSeconds = reader.GetInt32(6),
                                    CreatedAtUnixSeconds = reader.GetInt32(7),
                                    LetterStat = saved ? 3 : (read ? 2 : 1),
                                    MailType = reader.GetInt32(10),
                                    SourceProtocol = reader.GetInt32(11)
                                });
                                if (!saved)
                                    loadedInboxCount++;
                            }
                        }
                    }

                    var attachmentsByMessage = LoadMailboxAttachments(connection, transaction, entries);
                    foreach (var entry in entries)
                    {
                        if (!attachmentsByMessage.TryGetValue(entry.MessageId, out var attachments))
                            attachments = new List<MailboxAttachmentEntry>();

                        ApplyMailboxAttachments(entry, attachments);
                    }

                    transaction.Commit();
                }
            }

            return new MailboxInboxPage
            {
                Entries = entries,
                TotalCount = totalCount,
                LoadedInboxCount = loadedInboxCount
            };
        }

        private static Dictionary<long, List<MailboxAttachmentEntry>> LoadMailboxAttachments(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyList<MailboxListEntry> entries)
        {
            var result = new Dictionary<long, List<MailboxAttachmentEntry>>();
            if (entries == null || entries.Count == 0)
                return result;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                var sql = new System.Text.StringBuilder(@"
SELECT
    message_id,
    attachment_id,
    ordinal,
    item_type,
    source_list_type,
    source_slot_index,
    source_item_uid,
    item_template_id,
    item_kind,
    item_count,
    instance_value,
    durability,
    seal_flag,
    option_value,
    expire_time,
    marker_16,
    pet_serial_or_handle,
    extra_json,
    item_core,
    detail_json
FROM mailbox_attachments
WHERE claimed_flag = 0
  AND message_id IN (");

                for (var i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                        sql.Append(',');
                    var parameterName = "@messageId" + i;
                    sql.Append(parameterName);
                    command.Parameters.AddWithValue(parameterName, entries[i].MessageId);
                }

                sql.Append(") ORDER BY message_id, ordinal, attachment_id;");
                command.CommandText = sql.ToString();

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var messageId = reader.GetInt64(0);
                        if (!result.TryGetValue(messageId, out var attachments))
                        {
                            attachments = new List<MailboxAttachmentEntry>();
                            result.Add(messageId, attachments);
                        }

                        attachments.Add(new MailboxAttachmentEntry
                        {
                            AttachmentId = reader.GetInt64(1),
                            Ordinal = reader.GetInt32(2),
                            ItemType = (byte)reader.GetInt32(3),
                            SourceListType = reader.GetInt32(4),
                            SourceSlotIndex = reader.GetInt32(5),
                            SourceItemUid = reader.GetInt64(6),
                            ItemTemplateId = reader.GetInt32(7),
                            ItemKind = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                            ItemCount = reader.GetInt32(9),
                            InstanceValue = reader.GetInt32(10),
                            Durability = reader.GetInt32(11),
                            SealFlag = reader.GetInt32(12),
                            OptionValue = reader.GetInt32(13),
                            ExpireTime = reader.GetInt32(14),
                            Marker16 = reader.GetInt32(15),
                            PetSerialOrHandle = reader.GetInt32(16),
                            ExtraJson = reader.IsDBNull(17) ? "{}" : reader.GetString(17),
                            ItemCoreData = reader.IsDBNull(18) ? Array.Empty<byte>() : (byte[])reader.GetValue(18),
                            DetailJson = reader.IsDBNull(19) ? string.Empty : reader.GetString(19)
                        });
                    }
                }
            }

            return result;
        }

        private static void ApplyMailboxAttachments(MailboxListEntry entry, List<MailboxAttachmentEntry> attachments)
        {
            entry.Attachments = attachments;
            entry.AttachmentCount = attachments.Count;
            if (attachments.Count == 0)
                return;

            var first = attachments[0];
            entry.FirstAttachmentItemId = first.ItemTemplateId;
            entry.FirstAttachmentItemCount = first.ItemCount;
            entry.FirstAttachmentItemKind = first.ItemKind;
            entry.FirstAttachmentInstanceValue = first.InstanceValue;
            entry.FirstAttachmentDurability = first.Durability;
            entry.FirstAttachmentSealFlag = first.SealFlag;
            entry.FirstAttachmentOptionValue = first.OptionValue;
            entry.FirstAttachmentExpireTime = first.ExpireTime;
            entry.FirstAttachmentMarker16 = first.Marker16;
            entry.FirstAttachmentPetSerialOrHandle = first.PetSerialOrHandle;
        }

        private static List<MailboxAttachmentEntry> LoadMailboxAttachments(SqliteConnection connection, long messageId, SqliteTransaction transaction = null)
        {
            var attachments = new List<MailboxAttachmentEntry>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT
    attachment_id,
    ordinal,
    item_type,
    source_list_type,
    source_slot_index,
    source_item_uid,
    item_template_id,
    item_kind,
    item_count,
    instance_value,
    durability,
    seal_flag,
    option_value,
    expire_time,
    marker_16,
    pet_serial_or_handle,
    extra_json,
    item_core,
    detail_json
FROM mailbox_attachments
WHERE message_id = @messageId
  AND claimed_flag = 0
ORDER BY ordinal, attachment_id;";
                command.Parameters.AddWithValue("@messageId", messageId);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        attachments.Add(new MailboxAttachmentEntry
                        {
                            AttachmentId = reader.GetInt64(0),
                            Ordinal = reader.GetInt32(1),
                            ItemType = (byte)reader.GetInt32(2),
                            SourceListType = reader.GetInt32(3),
                            SourceSlotIndex = reader.GetInt32(4),
                            SourceItemUid = reader.GetInt64(5),
                            ItemTemplateId = reader.GetInt32(6),
                            ItemKind = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                            ItemCount = reader.GetInt32(8),
                            InstanceValue = reader.GetInt32(9),
                            Durability = reader.GetInt32(10),
                            SealFlag = reader.GetInt32(11),
                            OptionValue = reader.GetInt32(12),
                            ExpireTime = reader.GetInt32(13),
                            Marker16 = reader.GetInt32(14),
                            PetSerialOrHandle = reader.GetInt32(15),
                            ExtraJson = reader.IsDBNull(16) ? "{}" : reader.GetString(16),
                            ItemCoreData = reader.IsDBNull(17) ? Array.Empty<byte>() : (byte[])reader.GetValue(17),
                            DetailJson = reader.IsDBNull(18) ? string.Empty : reader.GetString(18)
                        });
                    }
                }
            }

            return attachments;
        }

        public MailboxClaimResult ClaimMail(int characterId, long claimObjectId)
        {
            return InventoryContext.TryGetLease(characterId, out var lease)
                ? ClaimMail(characterId, claimObjectId, lease)
                : MailboxClaimResult.Fail(MailboxSendError.ServerBusy);
        }

        internal MailboxClaimResult ClaimMail(int characterId, long claimObjectId, InventoryLease lease)
        {
            if (characterId <= 0 || claimObjectId <= 0 || lease == null || lease.CharacterId != characterId)
                return MailboxClaimResult.Fail(MailboxSendError.InvalidRequest);

            lock (lease.SyncRoot)
            {
                if (!FlushPendingInventoryChanges(lease))
                    return MailboxClaimResult.Fail(MailboxSendError.ServerBusy);

                var inventoryMutated = false;
                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction(deferred: false))
                        using (InventoryUidAllocationContext.Enter(connection, transaction))
                        {
                            var result = ClaimMail(
                                connection,
                                transaction,
                                characterId,
                                claimObjectId,
                                lease,
                                out inventoryMutated);
                            if (result.Success)
                            {
                                transaction.Commit();
                                lease.Inventory.ClearDirtyState();
                                return result;
                            }

                            transaction.Rollback();
                            if (inventoryMutated)
                                ReloadOnlineInventoryAfterRollback(lease);
                            return result;
                        }
                    }
                }
                catch
                {
                    if (inventoryMutated)
                        ReloadOnlineInventoryAfterRollback(lease);
                    throw;
                }
            }
        }

        public MailboxDeleteResult DeleteMail(int characterId, long messageId)
        {
            if (characterId <= 0 || messageId <= 0)
                return MailboxDeleteResult.Fail(MailboxSendError.InvalidRequest);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var state = LoadDeleteMailState(connection, transaction, characterId, messageId);
                    if (state == null)
                        return MailboxDeleteResult.Fail(MailboxSendError.MailNotFound);

                    if ((state.Gold > 0 && !state.ReceivedGold) || state.UnclaimedAttachmentCount > 0)
                        return MailboxDeleteResult.Fail(MailboxSendError.InvalidRequest);

                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
UPDATE mailbox_recipients
SET deleted_flag = 1,
    read_flag = 1,
    read_at = COALESCE(read_at, CURRENT_TIMESTAMP)
WHERE character_id = @cid
  AND message_id = @messageId
  AND folder = 0
  AND deleted_flag = 0;";
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.Parameters.AddWithValue("@messageId", messageId);
                        if (command.ExecuteNonQuery() <= 0)
                            return MailboxDeleteResult.Fail(MailboxSendError.MailNotFound);
                    }

                    transaction.Commit();
                    return new MailboxDeleteResult
                    {
                        Success = true,
                        Error = MailboxSendError.None,
                        MessageId = messageId
                    };
                }
            }
        }

        public MailboxDeleteResult MarkMailRead(int characterId, long messageId)
        {
            if (characterId <= 0 || messageId <= 0)
                return MailboxDeleteResult.Fail(MailboxSendError.InvalidRequest);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
UPDATE mailbox_recipients
SET read_flag = 1,
    read_at = COALESCE(read_at, CURRENT_TIMESTAMP)
WHERE character_id = @cid
  AND message_id = @messageId
  AND folder = 0
  AND deleted_flag = 0
  AND EXISTS (
      SELECT 1
      FROM mailbox_messages m
      WHERE m.message_id = mailbox_recipients.message_id
        AND (mailbox_recipients.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'))
  );";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@messageId", messageId);
                    if (command.ExecuteNonQuery() <= 0)
                        return MailboxDeleteResult.Fail(MailboxSendError.MailNotFound);
                }
            }

            return new MailboxDeleteResult
            {
                Success = true,
                Error = MailboxSendError.None,
                MessageId = messageId
            };
        }

        public MailboxDeleteResult SaveMail(int characterId, long messageId)
        {
            if (characterId <= 0 || messageId <= 0)
                return MailboxDeleteResult.Fail(MailboxSendError.InvalidRequest);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var alreadySaved = false;
                    using (var stateCommand = connection.CreateCommand())
                    {
                        stateCommand.Transaction = transaction;
                        stateCommand.CommandText = @"
SELECT r.saved_flag
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id = @cid
  AND r.message_id = @messageId
  AND r.folder = 0
  AND r.deleted_flag = 0
  AND (r.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'))
LIMIT 1;";
                        stateCommand.Parameters.AddWithValue("@cid", characterId);
                        stateCommand.Parameters.AddWithValue("@messageId", messageId);
                        var value = stateCommand.ExecuteScalar();
                        if (value == null || value == DBNull.Value)
                            return MailboxDeleteResult.Fail(MailboxSendError.MailNotFound);
                        alreadySaved = Convert.ToInt32(value) != 0;
                    }

                    if (!alreadySaved)
                    {
                        using (var countCommand = connection.CreateCommand())
                        {
                            countCommand.Transaction = transaction;
                            countCommand.CommandText = @"
SELECT COUNT(*)
FROM mailbox_recipients
WHERE character_id = @cid
  AND folder = 0
  AND saved_flag = 1
  AND deleted_flag = 0;";
                            countCommand.Parameters.AddWithValue("@cid", characterId);
                            if (Convert.ToInt32(countCommand.ExecuteScalar()) >= 10)
                                return MailboxDeleteResult.Fail(MailboxSendError.MailboxStorageFull);
                        }

                        using (var updateCommand = connection.CreateCommand())
                        {
                            updateCommand.Transaction = transaction;
                            updateCommand.CommandText = @"
UPDATE mailbox_recipients
SET read_flag = 1,
    saved_flag = 1,
    read_at = COALESCE(read_at, CURRENT_TIMESTAMP),
    saved_at = COALESCE(saved_at, CURRENT_TIMESTAMP)
WHERE character_id = @cid
  AND message_id = @messageId
  AND folder = 0
  AND deleted_flag = 0;";
                            updateCommand.Parameters.AddWithValue("@cid", characterId);
                            updateCommand.Parameters.AddWithValue("@messageId", messageId);
                            if (updateCommand.ExecuteNonQuery() <= 0)
                                return MailboxDeleteResult.Fail(MailboxSendError.MailNotFound);
                        }
                    }

                    transaction.Commit();
                }
            }

            return new MailboxDeleteResult
            {
                Success = true,
                Error = MailboxSendError.None,
                MessageId = messageId
            };
        }

        private static DeleteMailState LoadDeleteMailState(SqliteConnection connection, SqliteTransaction transaction, int characterId, long messageId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT
    m.gold,
    r.received_gold_flag,
    (
        SELECT COUNT(*)
        FROM mailbox_attachments a
        WHERE a.message_id = m.message_id
          AND a.claimed_flag = 0
    ) AS unclaimed_attachment_count
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id = @cid
  AND r.message_id = @messageId
  AND r.folder = 0
  AND r.deleted_flag = 0
  AND (r.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'))
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@messageId", messageId);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new DeleteMailState
                    {
                        Gold = reader.GetInt32(0),
                        ReceivedGold = reader.GetInt32(1) != 0,
                        UnclaimedAttachmentCount = reader.GetInt32(2)
                    };
                }
            }
        }

        private static ClaimAttachmentTarget LoadClaimAttachmentTarget(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long claimObjectId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT
    a.message_id,
    a.attachment_id,
    a.ordinal,
    a.item_type,
    a.source_list_type,
    a.source_slot_index,
    a.source_item_uid,
    a.item_template_id,
    a.item_kind,
    a.item_count,
    a.instance_value,
    a.durability,
    a.seal_flag,
    a.option_value,
    a.expire_time,
    a.marker_16,
    a.pet_serial_or_handle,
    a.extra_json,
    a.item_core,
    a.detail_json
FROM mailbox_attachments a
JOIN mailbox_recipients r ON r.message_id = a.message_id
JOIN mailbox_messages m ON m.message_id = a.message_id
WHERE a.attachment_id = @claimObjectId
  AND a.claimed_flag = 0
  AND r.character_id = @cid
  AND r.folder = 0
  AND r.deleted_flag = 0
  AND (r.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'))
LIMIT 1;";
                command.Parameters.AddWithValue("@claimObjectId", claimObjectId);
                command.Parameters.AddWithValue("@cid", characterId);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new ClaimAttachmentTarget
                    {
                        MessageId = reader.GetInt64(0),
                        Attachment = new MailboxAttachmentEntry
                        {
                            AttachmentId = reader.GetInt64(1),
                            Ordinal = reader.GetInt32(2),
                            ItemType = (byte)reader.GetInt32(3),
                            SourceListType = reader.GetInt32(4),
                            SourceSlotIndex = reader.GetInt32(5),
                            SourceItemUid = reader.GetInt64(6),
                            ItemTemplateId = reader.GetInt32(7),
                            ItemKind = reader.IsDBNull(8) ? "unknown" : reader.GetString(8),
                            ItemCount = reader.GetInt32(9),
                            InstanceValue = reader.GetInt32(10),
                            Durability = reader.GetInt32(11),
                            SealFlag = reader.GetInt32(12),
                            OptionValue = reader.GetInt32(13),
                            ExpireTime = reader.GetInt32(14),
                            Marker16 = reader.GetInt32(15),
                            PetSerialOrHandle = reader.GetInt32(16),
                            ExtraJson = reader.IsDBNull(17) ? "{}" : reader.GetString(17),
                            ItemCoreData = reader.IsDBNull(18) ? Array.Empty<byte>() : (byte[])reader.GetValue(18),
                            DetailJson = reader.IsDBNull(19) ? string.Empty : reader.GetString(19)
                        }
                    };
                }
            }
        }

        private static ClaimMailState LoadClaimMailState(SqliteConnection connection, SqliteTransaction transaction, int characterId, long messageId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT m.gold, r.received_gold_flag
FROM mailbox_recipients r
JOIN mailbox_messages m ON m.message_id = r.message_id
WHERE r.character_id = @cid
  AND r.message_id = @messageId
  AND r.folder = 0
  AND r.deleted_flag = 0
  AND (r.saved_flag = 1 OR m.unlimited_flag != 0 OR datetime(m.expire_at) > datetime('now'))
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@messageId", messageId);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new ClaimMailState
                    {
                        Gold = reader.GetInt32(0),
                        ReceivedGold = reader.GetInt32(1) != 0
                    };
                }
            }
        }

        private static MailboxClaimResult ClaimMail(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long claimObjectId,
            InventoryLease lease,
            out bool inventoryMutated)
        {
            inventoryMutated = false;
            var inventory = lease?.Inventory;
            if (inventory == null)
                return MailboxClaimResult.Fail(MailboxSendError.InvalidRequest);

            // 附件行(带 AttachmentClaimFlag 标记): 严格按附件查, 已领/不存在明确失败——
            // 不允许降级到邮件路径, 否则会把同数值的另一封邮件错领(MR46 #1 实证场景)。
            var target = claimObjectId >= AttachmentClaimFlag
                ? LoadClaimAttachmentTarget(
                    connection,
                    transaction,
                    characterId,
                    claimObjectId - AttachmentClaimFlag)
                : null;
            if (claimObjectId >= AttachmentClaimFlag && target?.Attachment == null)
                return MailboxClaimResult.Fail(MailboxSendError.MailNotFound);

            var messageId = target?.MessageId ?? claimObjectId;
            var mailState = LoadClaimMailState(connection, transaction, characterId, messageId);
            if (mailState == null)
                return MailboxClaimResult.Fail(MailboxSendError.MailNotFound);

            var attachments = target?.Attachment != null
                ? new List<MailboxAttachmentEntry> { target.Attachment }
                : LoadMailboxAttachments(connection, messageId, transaction);
            var claimsGold = mailState.Gold > 0 && !mailState.ReceivedGold;
            if (attachments.Count == 0 && !claimsGold)
                return MailboxClaimResult.Fail(MailboxSendError.MailNotFound);

            var requests = new List<InventoryRewardGrantRequest>(attachments.Count);
            long addedMainWeight = 0;
            var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var attachment in attachments)
            {
                var core = MailboxItemCoreCodec.Decode(attachment);
                if (core == null || core.ItemId <= 0 || attachment.ItemCount <= 0)
                    return MailboxClaimResult.Fail(MailboxSendError.InvalidAttachment);
                if (core.ExpireTime > 0 && core.ExpireTime <= nowUnixSeconds)
                    return MailboxClaimResult.Fail(MailboxSendError.ExpiredItem);

                var count = Math.Max(1, attachment.ItemCount);
                if (InventoryStackRuleService.IsStackable(core))
                {
                    var metadata = ItemMetadataResolver.Resolve(core.ItemId);
                    var targetList = ResolveCoreTargetList(core);
                    if (metadata?.StackLimit > 0
                        && WouldExceedCarryLimit(
                            CountInventoryItem(inventory, targetList, core.ItemId),
                            count,
                            metadata.StackLimit))
                        return MailboxClaimResult.Fail(MailboxSendError.ItemCarryLimitExceeded);
                    core.Count = count;
                }
                else if (core.ItemKind == ItemCore.KindAvatar)
                {
                    core.AvatarUid = 0;
                }
                else if (core.ItemKind == ItemCore.KindCreature)
                {
                    core.CreatureUid = 0;
                }

                if (ResolveCoreTargetList(core) == InventoryListType.Main)
                    addedMainWeight = checked(addedMainWeight + (long)GetItemWeight(core.ItemId) * count);

                requests.Add(InventoryRewardGrantRequest.Existing(
                    core,
                    count,
                    ItemCreateReason.MailAttachment,
                    BuildClaimCreateOptions(attachment, core)));
            }

            var capacity = LoadClaimCapacity(connection, transaction, inventory);
            if (addedMainWeight > capacity.WeightLimit - capacity.CurrentWeight)
                return MailboxClaimResult.Fail(MailboxSendError.InventoryFull);

            if (claimsGold)
            {
                var currentGold = inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                var goldLimit = CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(
                    connection,
                    transaction,
                    characterId);
                if (mailState.Gold > Math.Max(0, goldLimit) - currentGold)
                    return MailboxClaimResult.Fail(MailboxSendError.GoldCarryLimitExceeded);
            }

            if (!InventoryRewardGrantService.TryPlanBatch(inventory, requests, out var plan))
                return MailboxClaimResult.Fail(MapGrantError(plan?.Error ?? InventoryRewardGrantError.InsertPlanFailed));

            if (!AllocateDetailUidsInTransaction(connection, transaction, plan))
                return MailboxClaimResult.Fail(MailboxSendError.ServerBusy);

            if (!ReserveClaimState(connection, transaction, characterId, messageId, claimsGold, attachments))
                return MailboxClaimResult.Fail(MailboxSendError.MailNotFound);

            if (!InventoryRewardGrantService.TryApplyPreparedBatch(inventory, plan, out var grantResult))
            {
                inventoryMutated = grantResult?.Changes?.HasChanges == true;
                return MailboxClaimResult.Fail(MapGrantError(
                    grantResult?.Error ?? InventoryRewardGrantError.InsertApplyFailed));
            }

            inventoryMutated = grantResult.Changes.HasChanges;
            if (claimsGold)
            {
                var currentGold = inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                if (!inventory.SetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart,
                        checked(currentGold + mailState.Gold)))
                    return MailboxClaimResult.Fail(MailboxSendError.GoldCarryLimitExceeded);
                inventoryMutated = true;
            }

            if (!InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, lease))
                return MailboxClaimResult.Fail(MailboxSendError.ServerBusy);

            MarkMailClaimed(connection, transaction, characterId, messageId, claimsGold, attachments);

            var updatedMainSlots = new List<short>();
            var updatedAvatarSlots = new List<short>();
            var updatedPetSlots = new List<short>();
            foreach (var change in grantResult.Changes.Slots)
                AddUpdatedSlot(change.ListType, change.SlotIndex, updatedMainSlots, updatedAvatarSlots, updatedPetSlots);

            return new MailboxClaimResult
            {
                Success = true,
                Error = MailboxSendError.None,
                MessageId = messageId,
                ClaimedGold = claimsGold ? mailState.Gold : 0,
                ClaimedAttachmentCount = attachments.Count,
                RemovedFromInbox = false,
                UpdatedMainSlots = updatedMainSlots,
                UpdatedAvatarSlots = updatedAvatarSlots,
                UpdatedPetSlots = updatedPetSlots,
                InventoryMutations = BuildClaimInventoryMutations(inventory, grantResult),
            };
        }

        private static IReadOnlyList<InventoryMutationResult> BuildClaimInventoryMutations(
            InventoryService inventory,
            InventoryRewardGrantBatchResult grantResult)
        {
            var mutations = new List<InventoryMutationResult>();
            if (inventory == null || grantResult == null || !grantResult.Success)
                return mutations;

            foreach (var grant in grantResult.Results)
            {
                var mutation = InventoryMutationResultFactory.FromGrant(
                    inventory,
                    grant);
                if (mutation != null)
                    mutations.Add(mutation);
            }
            return mutations;
        }

        private static bool AllocateDetailUidsInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryRewardGrantBatchPlan plan)
        {
            if (connection == null || transaction == null || plan == null)
                return false;

            foreach (var entry in plan.Entries)
            {
                var core = entry?.Core;
                if (core == null)
                    continue;

                if (core.ItemKind == ItemCore.KindAvatar && core.AvatarUid <= 0)
                {
                    var uid = AvatarDetailRepository.AllocateAvatarUid(connection, transaction);
                    if (uid <= 0 || uid > int.MaxValue)
                        return false;
                    core.AvatarUid = checked((int)uid);
                }
                else if (core.ItemKind == ItemCore.KindCreature && core.CreatureUid <= 0)
                {
                    var uid = CreatureDetailRepository.AllocateCreatureUid(connection, transaction);
                    if (uid <= 0 || uid > int.MaxValue)
                        return false;
                    core.CreatureUid = checked((int)uid);
                }
            }

            return true;
        }

        private static InventoryCreateOptions BuildClaimCreateOptions(
            MailboxAttachmentEntry attachment,
            ItemCore core)
        {
            var options = MailboxItemDetailCodec.BuildCreateOptions(
                attachment != null ? attachment.DetailJson : string.Empty);
            if (core == null || core.ExpireTime <= 0)
                return options;

            if (options == null)
                options = new InventoryCreateOptions();
            if (options.ExpireTime <= 0)
                options.ExpireTime = core.ExpireTime;
            return options;
        }

        internal static bool WouldExceedCarryLimit(int currentCount, int incomingCount, int carryLimit)
        {
            return carryLimit > 0 && incomingCount > carryLimit - Math.Max(0, currentCount);
        }

        private static ClaimCapacity LoadClaimCapacity(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory)
        {
            var result = new ClaimCapacity { Level = 1, WeightLimit = long.MaxValue };
            var characterId = inventory?.CharacterId ?? 0;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT c.level, COALESCE(s.stat_inventory_limit, 0)
FROM characters c
LEFT JOIN character_subtype1_fields s ON s.character_id = c.character_id
WHERE c.character_id = @cid
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        result.Level = Math.Max(1, reader.GetInt32(0));
                        var limit = reader.GetInt64(1);
                        if (limit > 0)
                            result.WeightLimit = limit;
                    }
                }
            }

            if (inventory == null)
                return result;

            foreach (var pair in inventory.GetItems(InventoryListType.Main))
            {
                var core = pair.Value;
                if (core == null || core.ItemId <= 0)
                    continue;
                var count = InventoryStackRuleService.IsStackable(core) ? Math.Max(1, core.Count) : 1;
                var addedWeight = (long)GetItemWeight(core.ItemId) * count;
                result.CurrentWeight = addedWeight >= long.MaxValue - result.CurrentWeight
                    ? long.MaxValue
                    : result.CurrentWeight + addedWeight;
            }

            return result;
        }

        private static InventoryListType ResolveCoreTargetList(ItemCore core)
        {
            if (core == null)
                return InventoryListType.Main;
            if (core.ItemKind == ItemCore.KindAvatar)
                return InventoryListType.Avatar;
            if (core.ItemKind == ItemCore.KindCreature
                || core.ItemKind == ItemCore.KindCreatureEquipment
                || core.ItemKind == ItemCore.KindCreatureConsumable)
                return InventoryListType.Pet;
            return InventoryListType.Main;
        }

        private static int CountInventoryItem(
            InventoryService inventory,
            InventoryListType listType,
            int itemId)
        {
            if (inventory == null || itemId <= 0)
                return 0;
            if (listType == InventoryListType.Main)
                return inventory.CountMainItem(itemId);

            long total = 0;
            foreach (var pair in inventory.GetItems(listType))
            {
                var core = pair.Value;
                if (core == null || core.ItemId != itemId)
                    continue;
                total += InventoryStackRuleService.IsStackable(core)
                    ? Math.Max(0, core.Count)
                    : 1;
                if (total >= int.MaxValue)
                    return int.MaxValue;
            }
            return (int)total;
        }

        private static MailboxSendError MapGrantError(InventoryRewardGrantError error)
        {
            switch (error)
            {
                case InventoryRewardGrantError.InsertPlanFailed:
                case InventoryRewardGrantError.InsertApplyFailed:
                    return MailboxSendError.InventoryFull;
                case InventoryRewardGrantError.InvalidCount:
                    return MailboxSendError.ItemCarryLimitExceeded;
                case InventoryRewardGrantError.InvalidItem:
                case InventoryRewardGrantError.CreateFailed:
                case InventoryRewardGrantError.DetailCreateFailed:
                    return MailboxSendError.InvalidAttachment;
                default:
                    return MailboxSendError.ServerBusy;
            }
        }

        private static int GetItemWeight(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return 0;

            return ItemWeightCache.GetOrAdd(itemTemplateId, id =>
            {
                try
                {
                    return Math.Max(0, ItemMetadataResolver.Resolve(id).Weight);
                }
                catch
                {
                    return 0;
                }
            });
        }

        private static void ExpireNormalInbox(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var hasExpired = false;
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = @"
SELECT EXISTS (
    SELECT 1
    FROM mailbox_recipients r
    JOIN mailbox_messages m ON m.message_id = r.message_id
    WHERE r.character_id = @cid
      AND r.folder = 0
      AND r.saved_flag = 0
      AND r.deleted_flag = 0
      AND m.unlimited_flag = 0
       AND m.expire_at <= CURRENT_TIMESTAMP
);";
                check.Parameters.AddWithValue("@cid", characterId);
                hasExpired = Convert.ToInt32(check.ExecuteScalar()) != 0;
            }

            if (!hasExpired)
                return;

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
UPDATE mailbox_recipients
SET deleted_flag = 2,
    deleted_at = COALESCE(deleted_at, CURRENT_TIMESTAMP)
WHERE character_id = @cid
  AND folder = 0
  AND saved_flag = 0
  AND deleted_flag = 0
  AND message_id IN (
      SELECT message_id
      FROM mailbox_messages
      WHERE unlimited_flag = 0
        AND expire_at <= CURRENT_TIMESTAMP
  );";
                update.Parameters.AddWithValue("@cid", characterId);
                update.ExecuteNonQuery();
            }
        }

        private static MailboxExpirationBatchResult MaintainExpiredMailBatch(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int expireBatchSize,
            int purgeBatchSize)
        {
            var expiredByCharacter = new Dictionary<int, List<long>>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = @"
WITH expired AS (
    SELECT m.message_id
    FROM mailbox_messages m
    WHERE m.unlimited_flag = 0
      AND m.expire_at <= CURRENT_TIMESTAMP
      AND EXISTS (
          SELECT 1
          FROM mailbox_recipients active
          WHERE active.message_id = m.message_id
            AND active.folder = 0
            AND active.saved_flag = 0
            AND active.deleted_flag = 0
      )
    ORDER BY m.expire_at, m.message_id
    LIMIT @batch
)
SELECT r.character_id, r.message_id
FROM mailbox_recipients r
JOIN expired e ON e.message_id = r.message_id
WHERE r.folder = 0
  AND r.saved_flag = 0
  AND r.deleted_flag = 0
ORDER BY r.character_id, r.message_id;";
                select.Parameters.AddWithValue("@batch", Math.Max(1, expireBatchSize));
                using (var reader = select.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var characterId = reader.GetInt32(0);
                        if (!expiredByCharacter.TryGetValue(characterId, out var messageIds))
                        {
                            messageIds = new List<long>();
                            expiredByCharacter.Add(characterId, messageIds);
                        }
                        messageIds.Add(reader.GetInt64(1));
                    }
                }
            }

            var expiredRecipientCount = 0;
            using (var expire = connection.CreateCommand())
            {
                expire.Transaction = transaction;
                expire.CommandText = @"
WITH expired AS (
    SELECT m.message_id
    FROM mailbox_messages m
    WHERE m.unlimited_flag = 0
      AND m.expire_at <= CURRENT_TIMESTAMP
      AND EXISTS (
          SELECT 1
          FROM mailbox_recipients active
          WHERE active.message_id = m.message_id
            AND active.folder = 0
            AND active.saved_flag = 0
            AND active.deleted_flag = 0
      )
    ORDER BY m.expire_at, m.message_id
    LIMIT @batch
)
UPDATE mailbox_recipients
SET deleted_flag = 2,
    deleted_at = COALESCE(deleted_at, CURRENT_TIMESTAMP)
WHERE folder = 0
  AND saved_flag = 0
  AND deleted_flag = 0
  AND message_id IN (SELECT message_id FROM expired);";
                expire.Parameters.AddWithValue("@batch", Math.Max(1, expireBatchSize));
                expiredRecipientCount = expire.ExecuteNonQuery();
            }

            var purgedMessageCount = 0;
            using (var purge = connection.CreateCommand())
            {
                purge.Transaction = transaction;
                purge.CommandText = @"
DELETE FROM mailbox_messages
WHERE message_id IN (
    SELECT message_id
    FROM mailbox_messages
    WHERE mailbox_messages.unlimited_flag = 0
      AND mailbox_messages.expire_at <= datetime('now', '-30 days')
      AND NOT EXISTS (
          SELECT 1
          FROM mailbox_recipients r
          WHERE r.message_id = mailbox_messages.message_id
            AND r.folder = 0
            AND r.saved_flag = 1
            AND r.deleted_flag = 0
      )
    ORDER BY expire_at, message_id
    LIMIT @batch
);";
                purge.Parameters.AddWithValue("@batch", Math.Max(1, purgeBatchSize));
                purgedMessageCount = purge.ExecuteNonQuery();
            }

            var recipients = new List<MailboxExpirationRecipient>(expiredByCharacter.Count);
            foreach (var pair in expiredByCharacter)
            {
                recipients.Add(new MailboxExpirationRecipient
                {
                    CharacterId = pair.Key,
                    MessageIds = pair.Value
                });
            }

            return new MailboxExpirationBatchResult
            {
                ExpiredRecipientCount = expiredRecipientCount,
                PurgedMessageCount = purgedMessageCount,
                Recipients = recipients
            };
        }

        public MailboxCampaignBatchResult ProcessSystemMailCampaignBatch(
            string campaignId,
            MailboxSendRequest template,
            int batchSize = 500)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || template == null || template.SenderCharacterId <= 0)
                return MailboxCampaignBatchResult.Fail(campaignId, MailboxSendError.InvalidRequest);

            campaignId = campaignId.Trim();
            batchSize = Math.Max(1, Math.Min(1000, batchSize));
            var payloadHash = ComputeCampaignPayloadHash(template);

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var campaignMaxCharacterId = 0;
                    using (var maximum = connection.CreateCommand())
                    {
                        maximum.Transaction = transaction;
                        maximum.CommandText = "SELECT COALESCE(MAX(character_id), 0) FROM characters WHERE delete_flag = 0;";
                        campaignMaxCharacterId = Convert.ToInt32(maximum.ExecuteScalar());
                    }

                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = @"
INSERT OR IGNORE INTO mailbox_campaigns (campaign_id, payload_hash, max_character_id)
VALUES (@campaignId, @payloadHash, @maxCharacterId);";
                        insert.Parameters.AddWithValue("@campaignId", campaignId);
                        insert.Parameters.AddWithValue("@payloadHash", payloadHash);
                        insert.Parameters.AddWithValue("@maxCharacterId", campaignMaxCharacterId);
                        insert.ExecuteNonQuery();
                    }

                    var lastCharacterId = 0;
                    var completed = false;
                    using (var load = connection.CreateCommand())
                    {
                        load.Transaction = transaction;
                        load.CommandText = @"
SELECT payload_hash, last_character_id, status, max_character_id
FROM mailbox_campaigns
WHERE campaign_id = @campaignId;";
                        load.Parameters.AddWithValue("@campaignId", campaignId);
                        using (var reader = load.ExecuteReader())
                        {
                            if (!reader.Read() || !string.Equals(reader.GetString(0), payloadHash, StringComparison.Ordinal))
                                return MailboxCampaignBatchResult.Fail(campaignId, MailboxSendError.InvalidRequest);
                            lastCharacterId = reader.GetInt32(1);
                            completed = reader.GetInt32(2) == 1;
                            campaignMaxCharacterId = reader.GetInt32(3);
                        }
                    }

                    if (completed)
                    {
                        transaction.Commit();
                        return new MailboxCampaignBatchResult
                        {
                            Success = true,
                            Error = MailboxSendError.None,
                            CampaignId = campaignId,
                            LastCharacterId = lastCharacterId,
                            Completed = true
                        };
                    }

                    var recipients = new List<(int CharacterId, int AccountId, string Name, int Level)>();
                    using (var select = connection.CreateCommand())
                    {
                        select.Transaction = transaction;
                        select.CommandText = @"
SELECT character_id, account_id, name, level
FROM characters
WHERE delete_flag = 0
  AND character_id > @lastCharacterId
  AND character_id <= @maxCharacterId
ORDER BY character_id
LIMIT @batchSize;";
                        select.Parameters.AddWithValue("@lastCharacterId", lastCharacterId);
                        select.Parameters.AddWithValue("@maxCharacterId", campaignMaxCharacterId);
                        select.Parameters.AddWithValue("@batchSize", batchSize);
                        using (var reader = select.ExecuteReader())
                        {
                            while (reader.Read())
                                recipients.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3)));
                        }
                    }

                    var deliveredCount = 0;
                    foreach (var recipient in recipients)
                    {
                        var request = CloneCampaignRequest(template, campaignId, recipient);
                        var send = SendSystemMail(connection, transaction, request);
                        if (!send.Success)
                            return MailboxCampaignBatchResult.Fail(campaignId, send.Error);

                        using (var delivery = connection.CreateCommand())
                        {
                            delivery.Transaction = transaction;
                            delivery.CommandText = @"
INSERT OR IGNORE INTO mailbox_campaign_deliveries
    (campaign_id, character_id, message_id)
VALUES (@campaignId, @characterId, @messageId);";
                            delivery.Parameters.AddWithValue("@campaignId", campaignId);
                            delivery.Parameters.AddWithValue("@characterId", recipient.CharacterId);
                            delivery.Parameters.AddWithValue("@messageId", send.MessageId);
                            delivery.ExecuteNonQuery();
                        }

                        lastCharacterId = recipient.CharacterId;
                        deliveredCount++;
                    }

                    var hasMore = false;
                    using (var remaining = connection.CreateCommand())
                    {
                        remaining.Transaction = transaction;
                        remaining.CommandText = @"
SELECT EXISTS (
    SELECT 1 FROM characters
    WHERE delete_flag = 0
      AND character_id > @lastCharacterId
      AND character_id <= @maxCharacterId
);";
                        remaining.Parameters.AddWithValue("@lastCharacterId", lastCharacterId);
                        remaining.Parameters.AddWithValue("@maxCharacterId", campaignMaxCharacterId);
                        hasMore = Convert.ToInt32(remaining.ExecuteScalar()) != 0;
                    }

                    using (var update = connection.CreateCommand())
                    {
                        update.Transaction = transaction;
                        update.CommandText = @"
UPDATE mailbox_campaigns
SET last_character_id = @lastCharacterId,
    status = @status,
    updated_at = CURRENT_TIMESTAMP,
    completed_at = CASE WHEN @status = 1 THEN CURRENT_TIMESTAMP ELSE completed_at END
WHERE campaign_id = @campaignId
  AND payload_hash = @payloadHash;";
                        update.Parameters.AddWithValue("@lastCharacterId", lastCharacterId);
                        update.Parameters.AddWithValue("@status", hasMore ? 0 : 1);
                        update.Parameters.AddWithValue("@campaignId", campaignId);
                        update.Parameters.AddWithValue("@payloadHash", payloadHash);
                        if (update.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException($"Mailbox campaign CAS failed: {campaignId}.");
                    }

                    transaction.Commit();
                    return new MailboxCampaignBatchResult
                    {
                        Success = true,
                        Error = MailboxSendError.None,
                        CampaignId = campaignId,
                        DeliveredCount = deliveredCount,
                        LastCharacterId = lastCharacterId,
                        Completed = !hasMore
                    };
                }
            }
        }

        private static bool ReserveClaimState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long messageId,
            bool reserveGold,
            IReadOnlyList<MailboxAttachmentEntry> attachments)
        {
            if (attachments != null)
            {
                foreach (var attachment in attachments)
                {
                    if (attachment == null || attachment.AttachmentId <= 0
                        || attachment.ItemTemplateId <= 0 || attachment.ItemCount <= 0)
                        return false;

                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
UPDATE mailbox_attachments
SET claimed_flag = 2
WHERE attachment_id = @attachmentId
  AND message_id = @messageId
  AND claimed_flag = 0;";
                        command.Parameters.AddWithValue("@attachmentId", attachment.AttachmentId);
                        command.Parameters.AddWithValue("@messageId", messageId);
                        if (command.ExecuteNonQuery() != 1)
                            return false;
                    }
                }
            }

            if (!reserveGold)
                return true;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE mailbox_recipients
SET received_gold_flag = 2
WHERE message_id = @messageId
  AND character_id = @cid
  AND folder = 0
  AND deleted_flag = 0
  AND received_gold_flag = 0;";
                command.Parameters.AddWithValue("@messageId", messageId);
                command.Parameters.AddWithValue("@cid", characterId);
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static void MarkMailClaimed(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            long messageId,
            bool markGold,
            IReadOnlyList<MailboxAttachmentEntry> attachments)
        {
            if (attachments != null && attachments.Count > 0)
            {
                foreach (var attachment in attachments)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
UPDATE mailbox_attachments
SET claimed_flag = 1,
    claimed_at = CURRENT_TIMESTAMP
WHERE attachment_id = @attachmentId
  AND message_id = @messageId
  AND claimed_flag = 2;";
                        command.Parameters.AddWithValue("@attachmentId", attachment.AttachmentId);
                        command.Parameters.AddWithValue("@messageId", messageId);
                        if (command.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException($"Mailbox attachment claim CAS failed: {attachment.AttachmentId}.");
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE mailbox_recipients
SET read_flag = 1,
    received_gold_flag = CASE WHEN @markGold != 0 THEN 1 ELSE received_gold_flag END,
    read_at = COALESCE(read_at, CURRENT_TIMESTAMP)
WHERE message_id = @messageId
  AND character_id = @cid
  AND folder = 0
  AND (@markGold = 0 OR received_gold_flag = 2);";
                command.Parameters.AddWithValue("@messageId", messageId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@markGold", markGold ? 1 : 0);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException($"Mailbox recipient claim CAS failed: {messageId}.");
            }

        }

        private static void AddUpdatedSlot(
            InventoryListType listType,
            short slot,
            List<short> updatedMainSlots,
            List<short> updatedAvatarSlots,
            List<short> updatedPetSlots)
        {
            if (listType == InventoryListType.Avatar)
            {
                AddUniqueSlot(updatedAvatarSlots, slot);
                return;
            }

            if (listType == InventoryListType.Pet)
            {
                AddUniqueSlot(updatedPetSlots, slot);
                return;
            }

            AddUniqueSlot(updatedMainSlots, slot);
        }

        private static void AddUniqueSlot(List<short> slots, short slot)
        {
            if (!slots.Contains(slot))
                slots.Add(slot);
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

        private static MailboxSendResult SendMail(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MailboxSendRequest request,
            InventoryLease lease,
            out bool inventoryMutated)
        {
            inventoryMutated = false;
            if (request.SenderCharacterId <= 0 || request.ReceiverCharacterId <= 0)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);
            if (lease?.Inventory == null || lease.CharacterId != request.SenderCharacterId)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            var inventory = lease.Inventory;

            var validAttachments = new List<MailboxSendAttachmentRequest>();
            if (request.Attachments != null)
            {
                foreach (var attachment in request.Attachments)
                {
                    if (attachment == null || attachment.ItemId <= 0 || attachment.ItemCount <= 0)
                        return MailboxSendResult.Fail(MailboxSendError.InvalidAttachment);
                    validAttachments.Add(attachment);
                }
            }

            if (validAttachments.Count > 10)
                return MailboxSendResult.Fail(MailboxSendError.TooManyAttachments);

            if (request.Gold < 0)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            if (request.Gold == 0 && validAttachments.Count == 0 && string.IsNullOrEmpty(request.Text))
                return MailboxSendResult.Fail(MailboxSendError.EmptyContent);

            var requestHash = ComputeSendRequestHash(request);
            var currentGold = inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            if (TryLoadIdempotentSend(connection, transaction, request, requestHash, currentGold, out var replay))
                return replay;

            var feeGold = CalculateFeeGold(request.Gold, validAttachments.Count);
            var totalGoldCost = (long)request.Gold + feeGold;
            if (totalGoldCost > int.MaxValue)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);
            if (currentGold < totalGoldCost)
                return MailboxSendResult.Fail(MailboxSendError.InsufficientGold);

            var snapshots = new List<MailboxAttachmentSnapshot>();
            for (var i = 0; i < validAttachments.Count; i++)
            {
                var snapshot = LoadAttachmentSnapshot(inventory, i, validAttachments[i]);
                if (snapshot == null)
                    return MailboxSendResult.Fail(MailboxSendError.InvalidAttachment);
                if (IsSourceAttachmentLocked(inventory, snapshot))
                    return MailboxSendResult.Fail(MailboxSendError.ItemLocked);

                var transferCore = MailboxItemCoreCodec.Decode(snapshot);
                var policyError = MailboxSendPolicy.ValidateAttachment(request, transferCore);
                if (policyError != MailboxSendError.None)
                    return MailboxSendResult.Fail(policyError);

                // A player-to-player mail is a successful asset transfer. PVF
                // [trade limit] consumes one remaining transfer, and the receiver
                // must see the decremented instance state in both 0x0061 and DB.
                var metadata = ItemMetadataResolver.Resolve(snapshot.ItemTemplateId);
                if (MailboxSendPolicy.IsTradeLimitItem(metadata))
                {
                    transferCore = MailboxSendPolicy.SetRemainingTradeCount(
                        transferCore,
                        MailboxSendPolicy.GetRemainingTradeCount(transferCore) - 1);
                    snapshot.ItemCoreData = MailboxItemCoreCodec.Encode(transferCore);
                }

                snapshots.Add(snapshot);
            }

            var deferredPolicyError = MailboxSendPolicy.ValidateDeferredPolicies(request);
            if (deferredPolicyError != MailboxSendError.None)
                return MailboxSendResult.Fail(deferredPolicyError);

            var goldPolicyError = ApplyPlayerGoldSendPolicies(connection, transaction, request);
            if (goldPolicyError != MailboxSendError.None)
                return MailboxSendResult.Fail(goldPolicyError);

            if (!inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    currentGold - (int)totalGoldCost))
                return MailboxSendResult.Fail(MailboxSendError.InsufficientGold);
            inventoryMutated = totalGoldCost > 0;
            foreach (var snapshot in snapshots)
            {
                if (!ConsumeSourceAttachment(inventory, snapshot))
                    return MailboxSendResult.Fail(MailboxSendError.InvalidAttachment);
                inventoryMutated = true;
            }

            if (!InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, lease))
                return MailboxSendResult.Fail(MailboxSendError.ServerBusy);

            var messageId = InsertMessage(
                connection,
                transaction,
                request,
                feeGold,
                requestHash,
                unlimited: false,
                expireAtUtc: DateTimeOffset.UtcNow.AddDays(15));
            InsertRecipient(connection, transaction, messageId, request.ReceiverCharacterId, 0);
            InsertRecipient(connection, transaction, messageId, request.SenderCharacterId, 1);

            foreach (var snapshot in snapshots)
                InsertAttachment(connection, transaction, messageId, snapshot);

            return new MailboxSendResult
            {
                Success = true,
                Error = MailboxSendError.None,
                MessageId = messageId,
                FeeGold = feeGold,
                UpdatedGold = currentGold - (int)totalGoldCost
            };
        }

        private static MailboxSendError ApplyPlayerGoldSendPolicies(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MailboxSendRequest request)
        {
            if (request.Gold <= 0)
                return MailboxSendError.None;

            if (!TryLoadActiveCharacterLevel(connection, transaction, request.SenderCharacterId, out var senderLevel)
                || !TryLoadActiveCharacterLevel(connection, transaction, request.ReceiverCharacterId, out var receiverLevel))
                return MailboxSendError.InvalidRequest;

            var receiverLimit = GoldLimitDataProvider.GetBaseCarryLimit(receiverLevel);
            if (request.Gold > receiverLimit)
                return MailboxSendError.ReceiverGoldLimitExceeded;

            var dailyLimit = CalculateDailyTradeGoldLimit(senderLevel);
            if (!DailyResetService.TryAddCounterAtomic(
                    connection,
                    transaction,
                    request.SenderCharacterId,
                    DailyTradeGoldCounterKey,
                    request.Gold,
                    dailyLimit))
                return MailboxSendError.DailyGoldLimitExceeded;

            return MailboxSendError.None;
        }

        internal static long CalculateDailyTradeGoldLimit(int characterLevel)
        {
            var normalizedLevel = Math.Max(1L, characterLevel);
            try
            {
                return checked(10000L * normalizedLevel * normalizedLevel);
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        private static bool TryLoadActiveCharacterLevel(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            out int level)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT level
FROM characters
WHERE character_id = @cid
  AND delete_flag = 0
LIMIT 1;";
                command.Parameters.AddWithValue("@cid", characterId);
                var raw = command.ExecuteScalar();
                if (raw == null || raw == DBNull.Value)
                {
                    level = 0;
                    return false;
                }

                level = Convert.ToInt32(raw);
                return true;
            }
        }

        private static MailboxSendResult SendSystemMail(SqliteConnection connection, SqliteTransaction transaction, MailboxSendRequest request)
        {
            if (request.SenderCharacterId <= 0 || request.ReceiverCharacterId <= 0)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            if (request.Gold < 0)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            var snapshots = new List<MailboxAttachmentSnapshot>();
            if (request.Attachments != null)
            {
                var ordinal = 0;
                foreach (var attachment in request.Attachments)
                {
                    if (attachment == null || attachment.ItemId <= 0 || attachment.ItemCount <= 0)
                        return MailboxSendResult.Fail(MailboxSendError.InvalidAttachment);
                    if (snapshots.Count >= 10)
                        return MailboxSendResult.Fail(MailboxSendError.TooManyAttachments);

                    if (!TryCreateSystemAttachmentSnapshot(ordinal++, attachment, out var snapshot))
                        return MailboxSendResult.Fail(MailboxSendError.InvalidAttachment);
                    snapshots.Add(snapshot);
                }
            }

            if (request.Gold == 0 && snapshots.Count == 0 && string.IsNullOrWhiteSpace(request.Text))
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            var requestHash = ComputeSendRequestHash(request);
            if (TryLoadIdempotentSend(connection, transaction, request, requestHash, 0, out var replay))
                return replay;

            var unlimited = request.Unlimited ?? true;
            var expireAtUtc = request.ExpireAtUtc ?? DateTimeOffset.UtcNow.AddDays(15);
            if (!unlimited && expireAtUtc <= DateTimeOffset.UtcNow)
                return MailboxSendResult.Fail(MailboxSendError.InvalidRequest);

            var messageId = InsertMessage(
                connection,
                transaction,
                request,
                0,
                requestHash,
                unlimited,
                expireAtUtc);
            InsertRecipient(connection, transaction, messageId, request.ReceiverCharacterId, 0);
            foreach (var snapshot in snapshots)
                InsertAttachment(connection, transaction, messageId, snapshot);
            InsertSystemMailAudit(connection, transaction, messageId, request, requestHash, unlimited, expireAtUtc, snapshots);

            return new MailboxSendResult
            {
                Success = true,
                Error = MailboxSendError.None,
                MessageId = messageId,
                FeeGold = 0,
                UpdatedGold = 0
            };
        }

        private static bool TryCreateSystemAttachmentSnapshot(
            int ordinal,
            MailboxSendAttachmentRequest request,
            out MailboxAttachmentSnapshot snapshot)
        {
            snapshot = null;
            var metadata = ItemMetadataResolver.Resolve(request.ItemId);
            var itemCount = Math.Max(1, request.ItemCount);
            if (TryCreateExplicitSystemAttachmentSnapshot(ordinal, request, metadata, itemCount, out snapshot))
                return true;

            if (!InventoryRewardGrantService.TryCreateOnly(
                request.ItemId,
                ItemCreateReason.MailAttachment,
                itemCount,
                out var createResult)
                || createResult.Kind != InventoryRewardGrantKind.InventoryItem
                || createResult.Core == null)
            {
                return false;
            }

            var core = createResult.Core;
            if (InventoryStackRuleService.IsStackable(core))
                core.Count = itemCount;
            else if (request.InstanceValue != 0)
                core.Value = request.InstanceValue;

            if (request.Durability != 0)
                core.Durability = ClampUInt16(request.Durability);
            core.SealFlag = ClampByte(request.SealFlag);
            if (request.OptionValue != 0 && core.ItemKind == ItemCore.KindAvatar)
                core.AbilityNo = ClampUInt16(request.OptionValue);
            if (request.ExpireTime > 0)
                core.ExpireTime = request.ExpireTime;
            core.Marker16 = request.Marker16;
            if (request.PetSerialOrHandle != 0 && core.ItemKind == ItemCore.KindCreature)
                core.CreatureUid = request.PetSerialOrHandle;

            var sourceListType = ResolveMailboxAttachmentListType(request.ItemType, request.ItemId, metadata);
            if (MailboxSendPolicy.IsTradeLimitItem(metadata) && core.StackTradeCount == 0)
            {
                // System mail creates a new item rather than transferring a
                // player's instance, so initialize the PVF maximum instead of
                // consuming one use. Explicit caller-provided instance data wins.
                core = MailboxSendPolicy.SetRemainingTradeCount(core, metadata.TradeLimitMax);
            }

            snapshot = new MailboxAttachmentSnapshot
            {
                Ordinal = ordinal,
                ItemType = request.ItemType,
                SourceListType = sourceListType,
                SourceSlotIndex = request.ItemSlot,
                SourceItemUid = 0,
                ItemTemplateId = request.ItemId,
                ItemKind = MailboxItemCoreCodec.GetLegacyKindName(core),
                ItemCount = itemCount,
                InstanceValue = core.Value,
                Durability = core.Durability,
                SealFlag = core.SealFlag,
                OptionValue = request.OptionValue,
                EquipmentLockId = 0,
                ExpireTime = core.ExpireTime,
                Marker16 = core.Marker16,
                PetSerialOrHandle = core.CreatureUid,
                ExtraJson = string.IsNullOrWhiteSpace(request.ExtraJson) ? "{}" : request.ExtraJson,
                ItemCoreData = MailboxItemCoreCodec.Encode(core),
                DetailJson = request.DetailJson ?? string.Empty
            };
            return true;
        }

        private static bool TryCreateExplicitSystemAttachmentSnapshot(
            int ordinal,
            MailboxSendAttachmentRequest request,
            ItemMetadata metadata,
            int itemCount,
            out MailboxAttachmentSnapshot snapshot)
        {
            snapshot = null;
            if (request?.ItemCoreData == null || request.ItemCoreData.Length < ItemCore.Size)
                return false;

            var core = ItemCore.FromBytes(request.ItemCoreData);
            if (core == null || core.ItemId <= 0 || core.ItemId != request.ItemId)
                return false;

            core = core.Copy();
            if (InventoryStackRuleService.IsStackable(core))
                core.Count = itemCount;
            else
                itemCount = 1;

            core.SortLockFlag = 0;
            core.EquipmentLockId = 0;
            if (core.ItemKind == ItemCore.KindAvatar)
                core.AvatarUid = 0;
            else if (core.ItemKind == ItemCore.KindCreature)
                core.CreatureUid = 0;

            var sourceListType = ResolveMailboxAttachmentListType(request.ItemType, core.ItemId, metadata);
            snapshot = new MailboxAttachmentSnapshot
            {
                Ordinal = ordinal,
                ItemType = request.ItemType,
                SourceListType = sourceListType,
                SourceSlotIndex = request.ItemSlot,
                SourceItemUid = 0,
                ItemTemplateId = core.ItemId,
                ItemKind = MailboxItemCoreCodec.GetLegacyKindName(core),
                ItemCount = itemCount,
                InstanceValue = core.Value,
                Durability = core.Durability,
                SealFlag = core.SealFlag,
                OptionValue = core.AbilityNo,
                EquipmentLockId = 0,
                ExpireTime = core.ExpireTime,
                Marker16 = core.Marker16,
                PetSerialOrHandle = core.CreatureUid,
                ExtraJson = string.IsNullOrWhiteSpace(request.ExtraJson) ? "{}" : request.ExtraJson,
                ItemCoreData = MailboxItemCoreCodec.Encode(core),
                DetailJson = request.DetailJson ?? string.Empty
            };
            return true;
        }

        private static int ResolveMailboxAttachmentListType(byte itemType, int itemTemplateId, ItemMetadata metadata)
        {
            var requestedList = MapMailboxItemType(itemType);
            if (requestedList == (int)InventoryListType.Pet || metadata == null)
                return requestedList;

            var isPetConsumable = ItemMetadataResolver.IsPetConsumableItem(metadata);
            var isPetEquipment = string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal)
                && ItemMetadataResolver.IsPetInventoryEquipment(itemTemplateId);
            return isPetConsumable || isPetEquipment
                ? (int)InventoryListType.Pet
                : requestedList;
        }

        public static int CalculateFeeGold(int sendGold, int attachmentCount)
        {
            var normalizedAttachmentCount = Math.Max(0, attachmentCount);
            var baseFee = normalizedAttachmentCount > 0
                ? checked(normalizedAttachmentCount * 1000)
                : 100;
            var goldFee = sendGold <= 0
                ? 0L
                : Math.Min(10000L, (long)sendGold * 5L / 100L);
            return checked(baseFee + (int)goldFee);
        }

        private static MailboxAttachmentSnapshot LoadAttachmentSnapshot(
            InventoryService inventory,
            int ordinal,
            MailboxSendAttachmentRequest request)
        {
            if (inventory == null || request == null)
                return null;

            var listType = (InventoryListType)MapMailboxItemType(request.ItemType);
            var core = inventory.GetItem(listType, (short)request.ItemSlot);
            if (core == null || core.ItemId != request.ItemId)
                return null;

            var stackable = InventoryStackRuleService.IsStackable(core);
            var requestedCount = stackable ? request.ItemCount : 1;
            if (requestedCount <= 0 || (stackable && core.Count < requestedCount))
                return null;

            var transferredCore = core.Copy();
            if (stackable)
                transferredCore.Count = requestedCount;
            transferredCore.SortLockFlag = 0;
            transferredCore.EquipmentLockId = 0;

            return new MailboxAttachmentSnapshot
            {
                Ordinal = ordinal,
                ItemType = request.ItemType,
                SourceListType = (int)listType,
                SourceSlotIndex = request.ItemSlot,
                SourceItemUid = core.Uid,
                ItemTemplateId = core.ItemId,
                ItemKind = MailboxItemCoreCodec.GetLegacyKindName(core),
                ItemCount = requestedCount,
                InstanceValue = transferredCore.Value,
                Durability = transferredCore.Durability,
                SealFlag = transferredCore.SealFlag,
                OptionValue = transferredCore.AbilityNo,
                EquipmentLockId = transferredCore.EquipmentLockId,
                ExpireTime = transferredCore.ExpireTime,
                Marker16 = transferredCore.Marker16,
                PetSerialOrHandle = transferredCore.CreatureUid,
                ExtraJson = "{}",
                ItemCoreData = MailboxItemCoreCodec.Encode(transferredCore),
                DetailJson = MailboxItemDetailCodec.Capture(inventory, core)
            };
        }

        internal static bool IsSourceAttachmentLocked(
            InventoryService inventory,
            MailboxAttachmentSnapshot snapshot)
        {
            if (snapshot == null || inventory == null)
                return false;

            var core = inventory.GetItem(
                (InventoryListType)snapshot.SourceListType,
                (short)snapshot.SourceSlotIndex);
            if (core == null)
                return false;
            if (core.SortLockFlag != 0)
                return true;
            return core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static bool ConsumeSourceAttachment(
            InventoryService inventory,
            MailboxAttachmentSnapshot snapshot)
        {
            return snapshot != null
                && InventoryDeleteService.TryConsumeFromSlot(
                    inventory,
                    (InventoryListType)snapshot.SourceListType,
                    (short)snapshot.SourceSlotIndex,
                    snapshot.ItemTemplateId,
                    snapshot.ItemCount,
                    out var result)
                && result.Success;
        }

        private static bool TryLoadIdempotentSend(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MailboxSendRequest request,
            string requestHash,
            int updatedGold,
            out MailboxSendResult result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                return false;

            long messageId;
            int feeGold;
            string storedHash;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT message_id, fee_gold, request_hash
FROM mailbox_messages
WHERE sender_character_id = @senderCid
  AND idempotency_key = @key
LIMIT 1;";
                command.Parameters.AddWithValue("@senderCid", request.SenderCharacterId);
                command.Parameters.AddWithValue("@key", request.IdempotencyKey.Trim());
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    messageId = reader.GetInt64(0);
                    feeGold = reader.GetInt32(1);
                    storedHash = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                }
            }

            if (!string.Equals(storedHash, requestHash, StringComparison.Ordinal))
            {
                result = MailboxSendResult.Fail(MailboxSendError.InvalidRequest);
                return true;
            }

            result = new MailboxSendResult
            {
                Success = true,
                Error = MailboxSendError.None,
                MessageId = messageId,
                FeeGold = feeGold,
                UpdatedGold = updatedGold
            };
            return true;
        }

        private static string ComputeSendRequestHash(MailboxSendRequest request)
        {
            var builder = new StringBuilder(512);
            AppendHashField(builder, request.SenderCharacterId.ToString());
            AppendHashField(builder, request.SenderAccountId.ToString());
            AppendHashField(builder, request.ReceiverCharacterId.ToString());
            AppendHashField(builder, request.ReceiverAccountId.ToString());
            AppendHashField(builder, request.Gold.ToString());
            AppendHashField(builder, request.MailType.ToString());
            AppendHashField(builder, request.SourceProtocol.ToString());
            AppendHashField(builder, request.Text);
            if (!string.IsNullOrWhiteSpace(request.Title)
                && !string.Equals(request.Title, request.Text, StringComparison.Ordinal))
            {
                AppendHashField(builder, request.Title);
            }
            // Preserve hashes produced before the explicit lifetime fields existed.
            // Only finite/custom system mail extends the payload identity.
            if (request.Unlimited.HasValue || request.ExpireAtUtc.HasValue)
            {
                AppendHashField(builder, request.Unlimited?.ToString());
                AppendHashField(
                    builder,
                    request.ExpireAtUtc.HasValue
                        ? request.ExpireAtUtc.Value.ToUniversalTime().ToString("O")
                        : null);
            }

            var attachments = request.Attachments ?? Array.Empty<MailboxSendAttachmentRequest>();
            AppendHashField(builder, attachments.Count.ToString());
            foreach (var attachment in attachments)
            {
                if (attachment == null)
                {
                    AppendHashField(builder, null);
                    continue;
                }

                AppendHashField(builder, attachment.ItemType.ToString());
                AppendHashField(builder, attachment.ItemSlot.ToString());
                AppendHashField(builder, attachment.ItemId.ToString());
                AppendHashField(builder, attachment.ItemCount.ToString());
                AppendHashField(builder, attachment.InstanceValue.ToString());
                AppendHashField(builder, attachment.Durability.ToString());
                AppendHashField(builder, attachment.SealFlag.ToString());
                AppendHashField(builder, attachment.OptionValue.ToString());
                AppendHashField(builder, attachment.ExpireTime.ToString());
                AppendHashField(builder, attachment.Marker16.ToString());
                AppendHashField(builder, attachment.PetSerialOrHandle.ToString());
                AppendHashField(builder, attachment.ExtraJson);
                if ((attachment.ItemCoreData != null && attachment.ItemCoreData.Length > 0)
                    || !string.IsNullOrEmpty(attachment.DetailJson))
                {
                    AppendHashField(builder, attachment.ItemCoreData != null && attachment.ItemCoreData.Length > 0
                        ? Convert.ToHexString(attachment.ItemCoreData)
                        : string.Empty);
                    AppendHashField(builder, attachment.DetailJson);
                }
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static string ComputeCampaignPayloadHash(MailboxSendRequest template)
        {
            return ComputeSendRequestHash(new MailboxSendRequest
            {
                SenderCharacterId = template.SenderCharacterId,
                SenderAccountId = template.SenderAccountId,
                SenderName = template.SenderName,
                SenderLevel = template.SenderLevel,
                Gold = template.Gold,
                Title = template.Title,
                Text = template.Text,
                MailType = template.MailType,
                SourceProtocol = template.SourceProtocol,
                Unlimited = template.Unlimited,
                ExpireAtUtc = template.ExpireAtUtc,
                Attachments = template.Attachments ?? Array.Empty<MailboxSendAttachmentRequest>()
            });
        }

        private static MailboxSendRequest CloneCampaignRequest(
            MailboxSendRequest template,
            string campaignId,
            (int CharacterId, int AccountId, string Name, int Level) recipient)
        {
            return new MailboxSendRequest
            {
                SenderCharacterId = template.SenderCharacterId,
                SenderAccountId = template.SenderAccountId,
                SenderName = template.SenderName,
                SenderLevel = template.SenderLevel,
                ReceiverCharacterId = recipient.CharacterId,
                ReceiverAccountId = recipient.AccountId,
                ReceiverName = recipient.Name,
                ReceiverLevel = recipient.Level,
                Gold = template.Gold,
                Title = template.Title,
                Text = template.Text,
                MailType = template.MailType,
                SourceProtocol = template.SourceProtocol,
                Unlimited = template.Unlimited,
                ExpireAtUtc = template.ExpireAtUtc,
                AuditActor = template.AuditActor,
                AuditReason = string.IsNullOrWhiteSpace(template.AuditReason)
                    ? $"campaign:{campaignId}"
                    : template.AuditReason,
                IdempotencyKey = $"campaign:{campaignId}:{recipient.CharacterId}",
                Attachments = template.Attachments ?? Array.Empty<MailboxSendAttachmentRequest>()
            };
        }

        private static void AppendHashField(StringBuilder builder, string value)
        {
            value = value ?? string.Empty;
            builder.Append(value.Length).Append(':').Append(value).Append('|');
        }

        private static long InsertMessage(
            SqliteConnection connection,
            SqliteTransaction transaction,
            MailboxSendRequest request,
            int feeGold,
            string requestHash,
            bool unlimited,
            DateTimeOffset expireAtUtc)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO mailbox_messages (
    sender_character_id, sender_account_id, sender_name,
    receiver_character_id, receiver_account_id, receiver_name,
    title, body, gold, fee_gold, mail_type, source_protocol,
    idempotency_key, request_hash, unlimited_flag, expire_at
) VALUES (
    @senderCid, @senderAid, @senderName,
    @receiverCid, @receiverAid, @receiverName,
    @title, @body, @gold, @feeGold, @mailType, @sourceProtocol,
    @idempotencyKey, @requestHash, @unlimited, @expireAt
);
SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("@senderCid", request.SenderCharacterId);
                command.Parameters.AddWithValue("@senderAid", request.SenderAccountId);
                command.Parameters.AddWithValue("@senderName", request.SenderName ?? string.Empty);
                command.Parameters.AddWithValue("@receiverCid", request.ReceiverCharacterId);
                command.Parameters.AddWithValue("@receiverAid", request.ReceiverAccountId);
                command.Parameters.AddWithValue("@receiverName", request.ReceiverName ?? string.Empty);
                command.Parameters.AddWithValue("@title", string.IsNullOrWhiteSpace(request.Title)
                    ? request.Text ?? string.Empty
                    : request.Title);
                command.Parameters.AddWithValue("@body", request.Text ?? string.Empty);
                command.Parameters.AddWithValue("@gold", request.Gold);
                command.Parameters.AddWithValue("@feeGold", feeGold);
                command.Parameters.AddWithValue("@mailType", request.MailType);
                command.Parameters.AddWithValue("@sourceProtocol", (int)request.SourceProtocol);
                command.Parameters.AddWithValue("@idempotencyKey", string.IsNullOrWhiteSpace(request.IdempotencyKey)
                    ? (object)DBNull.Value
                    : request.IdempotencyKey.Trim());
                command.Parameters.AddWithValue("@requestHash", requestHash);
                command.Parameters.AddWithValue("@unlimited", unlimited ? 1 : 0);
                command.Parameters.AddWithValue(
                    "@expireAt",
                    unlimited
                        ? "9999-12-31 23:59:59"
                        : expireAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static void InsertRecipient(SqliteConnection connection, SqliteTransaction transaction, long messageId, int characterId, int folder)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO mailbox_recipients (message_id, character_id, folder)
VALUES (@messageId, @characterId, @folder);";
                command.Parameters.AddWithValue("@messageId", messageId);
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@folder", folder);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertSystemMailAudit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long messageId,
            MailboxSendRequest request,
            string requestHash,
            bool unlimited,
            DateTimeOffset expireAtUtc,
            IReadOnlyList<MailboxAttachmentSnapshot> attachments)
        {
            long auditId;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO mailbox_system_mail_audit (
    message_id, actor_account_id, actor_character_id, actor_name, audit_reason,
    receiver_account_id, receiver_character_id, receiver_name,
    gold, attachment_count, mail_type, source_protocol,
    idempotency_key, request_hash, unlimited_flag, expire_at
) VALUES (
    @messageId, @actorAccountId, @actorCharacterId, @actorName, @auditReason,
    @receiverAccountId, @receiverCharacterId, @receiverName,
    @gold, @attachmentCount, @mailType, @sourceProtocol,
    @idempotencyKey, @requestHash, @unlimited, @expireAt
);
SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("@messageId", messageId);
                command.Parameters.AddWithValue("@actorAccountId", request.SenderAccountId);
                command.Parameters.AddWithValue("@actorCharacterId", request.SenderCharacterId);
                command.Parameters.AddWithValue("@actorName", string.IsNullOrWhiteSpace(request.AuditActor)
                    ? request.SenderName ?? string.Empty
                    : request.AuditActor.Trim());
                command.Parameters.AddWithValue("@auditReason", string.IsNullOrWhiteSpace(request.AuditReason)
                    ? "system-mail"
                    : request.AuditReason.Trim());
                command.Parameters.AddWithValue("@receiverAccountId", request.ReceiverAccountId);
                command.Parameters.AddWithValue("@receiverCharacterId", request.ReceiverCharacterId);
                command.Parameters.AddWithValue("@receiverName", request.ReceiverName ?? string.Empty);
                command.Parameters.AddWithValue("@gold", request.Gold);
                command.Parameters.AddWithValue("@attachmentCount", attachments?.Count ?? 0);
                command.Parameters.AddWithValue("@mailType", request.MailType);
                command.Parameters.AddWithValue("@sourceProtocol", (int)request.SourceProtocol);
                command.Parameters.AddWithValue("@idempotencyKey", string.IsNullOrWhiteSpace(request.IdempotencyKey)
                    ? (object)DBNull.Value
                    : request.IdempotencyKey.Trim());
                command.Parameters.AddWithValue("@requestHash", requestHash ?? string.Empty);
                command.Parameters.AddWithValue("@unlimited", unlimited ? 1 : 0);
                command.Parameters.AddWithValue("@expireAt", unlimited
                    ? "9999-12-31 23:59:59"
                    : expireAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                auditId = Convert.ToInt64(command.ExecuteScalar());
            }

            if (attachments == null)
                return;

            foreach (var attachment in attachments)
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO mailbox_system_mail_audit_attachments (
    audit_id, ordinal, item_template_id, item_kind, item_count,
    instance_value, seal_flag, expire_time, pet_serial_or_handle, extra_json
) VALUES (
    @auditId, @ordinal, @itemTemplateId, @itemKind, @itemCount,
    @instanceValue, @sealFlag, @expireTime, @petSerialOrHandle, @extraJson
);";
                    command.Parameters.AddWithValue("@auditId", auditId);
                    command.Parameters.AddWithValue("@ordinal", attachment.Ordinal);
                    command.Parameters.AddWithValue("@itemTemplateId", attachment.ItemTemplateId);
                    command.Parameters.AddWithValue("@itemKind", attachment.ItemKind ?? "unknown");
                    command.Parameters.AddWithValue("@itemCount", attachment.ItemCount);
                    command.Parameters.AddWithValue("@instanceValue", attachment.InstanceValue);
                    command.Parameters.AddWithValue("@sealFlag", attachment.SealFlag);
                    command.Parameters.AddWithValue("@expireTime", attachment.ExpireTime);
                    command.Parameters.AddWithValue("@petSerialOrHandle", attachment.PetSerialOrHandle);
                    command.Parameters.AddWithValue("@extraJson", attachment.ExtraJson ?? "{}");
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void InsertAttachment(SqliteConnection connection, SqliteTransaction transaction, long messageId, MailboxAttachmentSnapshot snapshot)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO mailbox_attachments (
    message_id, ordinal, item_type, source_list_type, source_slot_index, source_item_uid,
    item_template_id, item_kind, item_count, instance_value, durability, seal_flag,
    option_value, equipment_lock_id, expire_time, marker_16, pet_serial_or_handle, extra_json,
    item_core,
    detail_json
) VALUES (
    @messageId, @ordinal, @itemType, @sourceListType, @sourceSlotIndex, @sourceItemUid,
    @itemTemplateId, @itemKind, @itemCount, @instanceValue, @durability, @sealFlag,
    @optionValue, @equipmentLockId, @expireTime, @marker16, @petSerialOrHandle, @extraJson,
    @itemCore,
    @detailJson
);";
                command.Parameters.AddWithValue("@messageId", messageId);
                command.Parameters.AddWithValue("@ordinal", snapshot.Ordinal);
                command.Parameters.AddWithValue("@itemType", (int)snapshot.ItemType);
                command.Parameters.AddWithValue("@sourceListType", snapshot.SourceListType);
                command.Parameters.AddWithValue("@sourceSlotIndex", snapshot.SourceSlotIndex);
                command.Parameters.AddWithValue("@sourceItemUid", snapshot.SourceItemUid);
                command.Parameters.AddWithValue("@itemTemplateId", snapshot.ItemTemplateId);
                command.Parameters.AddWithValue("@itemKind", snapshot.ItemKind ?? "unknown");
                command.Parameters.AddWithValue("@itemCount", snapshot.ItemCount);
                command.Parameters.AddWithValue("@instanceValue", snapshot.InstanceValue);
                command.Parameters.AddWithValue("@durability", snapshot.Durability);
                command.Parameters.AddWithValue("@sealFlag", snapshot.SealFlag);
                command.Parameters.AddWithValue("@optionValue", snapshot.OptionValue);
                command.Parameters.AddWithValue("@equipmentLockId", snapshot.EquipmentLockId);
                command.Parameters.AddWithValue("@expireTime", snapshot.ExpireTime);
                command.Parameters.AddWithValue("@marker16", snapshot.Marker16);
                command.Parameters.AddWithValue("@petSerialOrHandle", snapshot.PetSerialOrHandle);
                command.Parameters.AddWithValue("@extraJson", snapshot.ExtraJson ?? "{}");
                command.Parameters.Add("@itemCore", SqliteType.Blob).Value =
                    snapshot.ItemCoreData != null && snapshot.ItemCoreData.Length > 0
                        ? (object)snapshot.ItemCoreData
                        : DBNull.Value;
                command.Parameters.AddWithValue("@detailJson", snapshot.DetailJson ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        private static int MapMailboxItemType(byte itemType)
        {
            switch (itemType)
            {
                case 1:
                    return (int)InventoryListType.Avatar;
                case 3:
                case 7:
                    return (int)InventoryListType.Pet;
                case 2:
                case 0:
                default:
                    return (int)InventoryListType.Main;
            }
        }

        private bool FlushPendingInventoryChanges(InventoryLease lease)
        {
            if (lease?.Inventory == null)
                return false;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    if (!InventoryPersistenceService.SaveDirtyInTransaction(connection, transaction, lease))
                        return false;
                    transaction.Commit();
                    lease.Inventory.ClearDirtyState();
                    return true;
                }
            }
        }

        private void ReloadOnlineInventoryAfterRollback(InventoryLease lease)
        {
            InventoryRollbackRecoveryService.ReloadOnlineInventory(
                _connectionString,
                lease);
        }

        private sealed class ClaimMailState
        {
            public int Gold { get; set; }
            public bool ReceivedGold { get; set; }
        }

        private sealed class ClaimCapacity
        {
            public int Level { get; set; }
            public long CurrentWeight { get; set; }
            public long WeightLimit { get; set; }
        }

        private sealed class ClaimAttachmentTarget
        {
            public long MessageId { get; set; }
            public MailboxAttachmentEntry Attachment { get; set; }
        }

        private sealed class DeleteMailState
        {
            public int Gold { get; set; }
            public bool ReceivedGold { get; set; }
            public int UnclaimedAttachmentCount { get; set; }
        }
    }
}
