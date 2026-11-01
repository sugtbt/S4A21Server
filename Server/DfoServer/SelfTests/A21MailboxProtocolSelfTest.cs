using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    // A21 0x0061：空列表 6B 全 0；有邮件发完整列表。
    // summary 尾部两段长度前缀 blob + extra1；时装再写两段空长度后写 remain/key/extra2。
    // 宠物 type=25 在 expire 前写 i32+u8。detail 在 letterStat 后多 1 字节。
    // 本客户端邮件 dstr 为 GBK(936)。
    public static class A21MailboxProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_MAILBOX_PROTOCOL selftest ===");
            var failures = 0;
            var encoding = ClientTextEncoding.Encoding;

            Check(
                "MAILBOX_MAIL_LIST opcode comes from PacketTypesA21",
                (ushort)NotiPacketTypeA21.MAILBOX_MAIL_LIST == 0x0061
                && (ushort)CmdPacketTypeA21.MAILBOX_OPEN == 0x0060
                && (ushort)CmdPacketTypeA21.MAILBOX_EXTRACT_ITEM == 0x005F,
                ref failures);

            var test4Recipient = new CharacterRecord
            {
                Name = ClientTextEncoding.GetBytes("test4"),
                Level = 1,
                Job = 7,
                GrowType = 0,
            };
            var fupderRecipient = new CharacterRecord
            {
                Name = ClientTextEncoding.GetBytes("fupder"),
                Level = 86,
                Job = 4,
                GrowType = 33,
            };
            Check(
                "0x0144 recipient ack has reserved byte before level and trailing padding",
                MailboxHandler.BuildQueryCharacterInfoAck(test4Recipient, "test4").SequenceEqual(new byte[]
                {
                    0x01, 0x05, 0x00, 0x00, 0x00,
                    (byte)'t', (byte)'e', (byte)'s', (byte)'t', (byte)'4',
                    0x00, 0x01, 0x00, 0x07, 0x00, 0x00, 0x00,
                })
                && MailboxHandler.BuildQueryCharacterInfoAck(fupderRecipient, "fupder").SequenceEqual(new byte[]
                {
                    0x01, 0x06, 0x00, 0x00, 0x00,
                    (byte)'f', (byte)'u', (byte)'p', (byte)'d', (byte)'e', (byte)'r',
                    0x00, 0x56, 0x00, 0x04, 0x21, 0x00, 0x00,
                }),
                ref failures);

            var empty = MailboxHandler.BuildMailboxListNotification(
                Array.Empty<MailboxListEntry>(),
                isFirstLoad: false,
                notLoadedCount: 0);
            Check(
                "empty inbox is 6-byte zero seed",
                empty.Length == 6 && IsAllZero(empty),
                ref failures);

            var challengeText = "深渊72级挑战书";
            var stackable = new MailboxListEntry
            {
                MessageId = 1000001,
                SenderCharacterId = 0,
                SenderName = "GM",
                MailType = 0,
                Body = challengeText,
                RemainSeconds = 2591424,
                CreatedAtUnixSeconds = 1710000000,
                LetterStat = 2,
                Attachments = new[]
                {
                    new MailboxAttachmentEntry
                    {
                        AttachmentId = 7,
                        ItemTemplateId = 3330,
                        ItemKind = "stackable",
                        ItemCount = 80,
                        InstanceValue = 80,
                    },
                },
            };

            var stackableBody = MailboxHandler.BuildMailboxListNotification(
                new[] { stackable },
                isFirstLoad: false,
                notLoadedCount: 0);
            Check(
                "stackable letter parses as A21 layout",
                TryParseA21MailboxList(stackableBody, encoding, out var stackableParsed)
                && stackableParsed.SummaryCount == 1
                && stackableParsed.IsFirstLoad == 0
                && stackableParsed.DetailCount == 1
                && stackableParsed.Summaries[0].SenderName == "GM"
                && stackableParsed.Summaries[0].ItemId == 3330
                && stackableParsed.Summaries[0].InstanceValue == 80
                && stackableParsed.Summaries[0].Blob1Length == 0
                && stackableParsed.Summaries[0].Blob2Length == 0
                && stackableParsed.Summaries[0].Extra1 == 0
                && stackableParsed.Summaries[0].RemainSeconds == 2591424
                && stackableParsed.Summaries[0].SummaryKey == 1000001
                && stackableParsed.Summaries[0].Extra2 == 0
                && stackableParsed.Summaries[0].ClaimObjectId
                    == (int)(MailboxRepository.AttachmentClaimFlag + 7)
                && stackableParsed.Details[0].MessageId == 1000001
                && stackableParsed.Details[0].Text == challengeText
                && stackableParsed.Details[0].LetterStat == 2
                && stackableParsed.Details[0].Extra == 0,
                ref failures);

            var utf8Text = Encoding.UTF8.GetBytes(challengeText);
            var gbkText = ClientTextEncoding.GetBytes(challengeText);
            Check(
                "mailbox letter text is GBK",
                gbkText.Length == 14
                && ContainsBytes(stackableBody, gbkText)
                && !ContainsBytes(stackableBody, utf8Text),
                ref failures);

            var avatar = new MailboxListEntry
            {
                MessageId = 2000002,
                SenderName = "GM",
                MailType = 0,
                Body = "avatar",
                RemainSeconds = 100,
                CreatedAtUnixSeconds = 1710000001,
                LetterStat = 1,
                Attachments = new[]
                {
                    new MailboxAttachmentEntry
                    {
                        AttachmentId = 8,
                        ItemTemplateId = 199999001,
                        ItemKind = "avatar",
                        ItemCount = 1,
                        InstanceValue = 0,
                    },
                },
            };
            var avatarBody = MailboxHandler.BuildMailboxListNotification(
                new[] { avatar },
                isFirstLoad: false,
                notLoadedCount: 0);
            Check(
                "avatar summary writes A21 18B jewel + 4B color blobs",
                TryParseA21MailboxList(avatarBody, encoding, out var avatarParsed, avatarPostCreateBlob: true)
                && avatarParsed.Summaries.Count == 1
                && avatarParsed.Summaries[0].Blob1Length == ItemListProtocolWriter.A21AvatarJewelBytes
                && avatarParsed.Summaries[0].Blob1.Length == ItemListProtocolWriter.A21AvatarJewelBytes
                && avatarParsed.Summaries[0].Blob2Length == 4
                && avatarParsed.Summaries[0].Blob2.Length == 4
                && avatarParsed.Summaries[0].PostCreateBlobLength == 0
                && avatarParsed.Summaries[0].RemainSeconds == 100
                && avatarParsed.Summaries[0].SummaryKey == 2000002,
                ref failures);

            var creature = new MailboxListEntry
            {
                MessageId = 3000003,
                SenderName = "GM",
                MailType = 0,
                Body = "creature",
                RemainSeconds = 200,
                CreatedAtUnixSeconds = 1710000002,
                LetterStat = 1,
                Attachments = new[]
                {
                    new MailboxAttachmentEntry
                    {
                        AttachmentId = 9,
                        ItemTemplateId = 199999002,
                        ItemKind = "creature",
                        ItemCount = 1,
                        InstanceValue = 0,
                        Marker16 = 77,
                    },
                },
            };
            var creatureBody = MailboxHandler.BuildMailboxListNotification(
                new[] { creature },
                isFirstLoad: false,
                notLoadedCount: 0);
            Check(
                "pet summary writes type-25 i32+u8 extras before expire",
                TryParseA21MailboxList(creatureBody, encoding, out var creatureParsed, creatureExtras: true)
                && creatureParsed.Summaries.Count == 1
                && creatureParsed.Summaries[0].CreatureExtra == 77
                && creatureParsed.Summaries[0].CreatureFlag == 0
                && creatureParsed.Summaries[0].RemainSeconds == 200
                && creatureParsed.Summaries[0].SummaryKey == 3000003
                && creatureParsed.Details[0].Extra == 0,
                ref failures);

            Check(
                "avatar/creature UID allocate reuses the ambient IMMEDIATE connection",
                TryAllocateUidsOnImmediateTransaction(),
                ref failures);

            VerifyGuildMedalMailClaims(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_MAILBOX_PROTOCOL selftest passed."
                    : $"A21_MAILBOX_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyGuildMedalMailClaims(ref int failures)
        {
            const int characterId = 959001;
            const int accountId = 959000;
            var sessionId = Guid.NewGuid();
            var tempDir = Path.Combine(Path.GetTempPath(), "a21-mailbox-medal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var database = new GameDatabase(Path.Combine(tempDir, "inventory.db"), ServerPaths.SchemaFilePath);
                ExecuteSql(database, @"
INSERT INTO accounts (account_id, m_id, password_hash) VALUES (959000, 'mailbox-medal-selftest', '');
INSERT INTO characters (character_id, account_id, name, level) VALUES (959001, 959000, 'mailbox-medal', 86);");
                var lease = InventoryContext.Register(sessionId, new InventoryService(characterId, accountId, database));
                var mailbox = new MailboxService(new MailboxRepository(database));
                var sent = SendGuildMail(mailbox, characterId, new[] { 100380051, 100380058, 90023, 90053 });
                Check("GM medal/gem system mail is created", sent.Success, ref failures);
                if (!sent.Success)
                    return;

                var attachments = mailbox.LoadInbox(characterId, 20).Single(x => x.MessageId == sent.MessageId).Attachments;
                var expectedSlots = new short[] { 0, 1, 49, 50 };
                for (var index = 0; index < attachments.Count; index++)
                {
                    var attachment = attachments[index];
                    var claimObjectId = MailboxRepository.AttachmentClaimFlag + attachment.AttachmentId;
                    var result = mailbox.ClaimMail(characterId, claimObjectId, lease);
                    var refresh = MailboxHandler.BuildClaimRefreshSlots(result);
                    var slot = expectedSlots[index];
                    Check(
                        $"claim item {attachment.ItemTemplateId} refreshes its GuildMedal slot {slot}",
                        result.Success && result.ClaimedAttachmentCount == 1
                        && result.UpdatedMainSlots.Count == 0
                        && refresh.Count == 1
                        && refresh.TryGetValue(InventoryListType.GuildMedal, out var slots)
                        && slots.SequenceEqual(new[] { slot }),
                        ref failures);

                    var body = ItemListUpdateBuilder.BuildUpdateBody(characterId, accountId, InventoryListType.GuildMedal, slot);
                    Check(
                        $"claim item {attachment.ItemTemplateId} produces a populated GuildMedal update",
                        body.Length == 3 + ItemListProtocolWriter.CommonEntrySize
                        && body[0] == (byte)InventoryListType.GuildMedal
                        && BitConverter.ToUInt16(body, 1) == 1
                        && BitConverter.ToInt16(body, 3) == slot
                        && BitConverter.ToInt32(body, 5) == attachment.ItemTemplateId,
                        ref failures);

                    var duplicate = mailbox.ClaimMail(characterId, claimObjectId, lease);
                    Check(
                        $"duplicate claim of item {attachment.ItemTemplateId} grants nothing",
                        !duplicate.Success && duplicate.Error == MailboxSendError.MailNotFound
                        && MailboxHandler.BuildClaimRefreshSlots(duplicate).Count == 0
                        && lease.Inventory.GetItems(InventoryListType.GuildMedal).Count(x => x.Value.ItemId > 0) == index + 1,
                        ref failures);
                }

                using (var connection = database.OpenConnection())
                {
                    var reloaded = InventoryService.LoadFromDb(connection, characterId, accountId, database);
                    Check(
                        "relogin reload preserves every claimed medal/gem core in GuildMedal",
                        attachments.Select((attachment, index) =>
                            reloaded.GetItem(InventoryListType.GuildMedal, expectedSlots[index])?.ToBytes()
                                .SequenceEqual(MailboxItemCoreCodec.Decode(attachment).ToBytes()) == true).All(x => x),
                        ref failures);
                }

                // 代币券凭证邮件：附件会转换成账户货币，gold 字段只用于邮件窗口底部显示。
                // 客户端「全部接收」会先按附件 claimObjectId 领一次，再按 messageId 领金币；
                // 两次都不能发放金币，且邮件必须被标记成完全领取，否则会残留在邮箱里。
                {
                    const int voucherAmount = 950;
                    var voucherMail = SendVoucherMail(
                        mailbox, characterId, accountId, voucherAmount);
                    Check("代币券凭证邮件创建成功", voucherMail.Success, ref failures);
                    if (voucherMail.Success)
                    {
                        var goldBefore = lease.Inventory
                            .GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                        var tokenBefore = ReadAccountTokenCera(database, accountId);
                        var voucherAttachment = mailbox.LoadInbox(characterId, 20)
                            .Single(x => x.MessageId == voucherMail.MessageId).Attachments.Single();

                        var claimAttachment = mailbox.ClaimMail(characterId,
                            MailboxRepository.AttachmentClaimFlag + voucherAttachment.AttachmentId, lease);
                        Check("领取代币券凭证成功", claimAttachment.Success, ref failures);
                        Check("凭证转换成账户代币券",
                            ReadAccountTokenCera(database, accountId) == tokenBefore + voucherAmount,
                            ref failures);
                        Check("领取凭证不发放金币",
                            (lease.Inventory.GetMainVirtualCount(
                                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0) == goldBefore,
                            ref failures);

                        // 第二步：客户端会再按 messageId 领「金币」。这一步绝不能发钱。
                        mailbox.ClaimMail(characterId, voucherMail.MessageId, lease);
                        Check("再按 messageId 领取不会发放金币",
                            (lease.Inventory.GetMainVirtualCount(
                                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0) == goldBefore,
                            ref failures);
                        Check("凭证邮件领取后可从邮箱删除（gold 已标记为已领）",
                            mailbox.DeleteMail(characterId, voucherMail.MessageId).Success,
                            ref failures);
                    }
                }

                var originalGem = lease.Inventory.GetItem(InventoryListType.GuildMedal, 50).Copy();
                var carryLimit = ItemMetadataResolver.Resolve(originalGem.ItemId).StackLimit;
                Check("guardian gem fixture has a PVF carry limit", carryLimit > 0, ref failures);
                if (carryLimit <= 0)
                    return;
                var cappedGem = originalGem.Copy();
                cappedGem.Count = carryLimit;
                lease.Inventory.SetItem(InventoryListType.GuildMedal, 50, cappedGem);
                var carryMail = SendGuildMail(mailbox, characterId, new[] { originalGem.ItemId });
                Check("carry-limit mail is created", carryMail.Success, ref failures);
                if (!carryMail.Success)
                    return;
                var carryRejected = mailbox.ClaimMail(characterId, carryMail.MessageId, lease);
                Check(
                    "gem carry limit counts the GuildMedal container and keeps the attachment",
                    !carryRejected.Success && carryRejected.Error == MailboxSendError.ItemCarryLimitExceeded
                    && lease.Inventory.GetItem(InventoryListType.GuildMedal, 50)?.Count == carryLimit
                    && mailbox.LoadInbox(characterId, 20).Single(x => x.MessageId == carryMail.MessageId).Attachments.Count == 1,
                    ref failures);
                lease.Inventory.SetItem(InventoryListType.GuildMedal, 50, originalGem);
                Check("restored gem baseline is persisted", InventoryPersistenceService.SaveDirty(lease), ref failures);

                var retryMail = SendGuildMail(mailbox, characterId, new[] { 100380044, 90042 });
                Check("retry mail is created", retryMail.Success, ref failures);
                if (!retryMail.Success)
                    return;
                ExecuteSql(database, @"
CREATE TRIGGER fail_medal_claim BEFORE INSERT ON character_inventory_items
WHEN NEW.list_type = 38 BEGIN SELECT RAISE(ABORT, 'mailbox-medal-selftest'); END;");
                var persistenceFailed = false;
                try
                {
                    mailbox.ClaimMail(characterId, retryMail.MessageId, lease);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    persistenceFailed = true;
                }
                Check(
                    "failed persistence rolls back both inventory and mail reservations",
                    persistenceFailed
                    && lease.Inventory.GetItems(InventoryListType.GuildMedal).Count(x => x.Value.ItemId > 0) == 4
                    && mailbox.LoadInbox(characterId, 20).Single(x => x.MessageId == retryMail.MessageId).Attachments.Count == 2,
                    ref failures);
                ExecuteSql(database, "DROP TRIGGER fail_medal_claim;");

                for (short slot = 0; slot <= 48; slot++)
                    lease.Inventory.SetItem(InventoryListType.GuildMedal, slot,
                        new ItemCore { ItemKind = ItemCore.KindGuildMedal, ItemId = 100380051 });
                var fullMedals = mailbox.ClaimMail(characterId, retryMail.MessageId, lease);
                Check(
                    "full medal range leaves both attachments unclaimed",
                    !fullMedals.Success && fullMedals.Error == MailboxSendError.InventoryFull
                    && lease.Inventory.GetItem(InventoryListType.GuildMedal, 51) == null
                    && mailbox.LoadInbox(characterId, 20).Single(x => x.MessageId == retryMail.MessageId).Attachments.Count == 2,
                    ref failures);

                lease.Inventory.RemoveItem(InventoryListType.GuildMedal, 48);
                for (short slot = 49; slot <= 97; slot++)
                    lease.Inventory.SetItem(InventoryListType.GuildMedal, slot,
                        new ItemCore { ItemKind = ItemCore.KindGuardianGem, ItemId = 90032, Count = 1 });
                var fullGems = mailbox.ClaimMail(characterId, retryMail.MessageId, lease);
                Check(
                    "full gem range does not partially grant the medal",
                    !fullGems.Success && fullGems.Error == MailboxSendError.InventoryFull
                    && lease.Inventory.GetItem(InventoryListType.GuildMedal, 48) == null
                    && mailbox.LoadInbox(characterId, 20).Single(x => x.MessageId == retryMail.MessageId).Attachments.Count == 2,
                    ref failures);

                lease.Inventory.RemoveItem(InventoryListType.GuildMedal, 97);
                var retry = mailbox.ClaimMail(characterId, retryMail.MessageId, lease);
                var retryRefresh = MailboxHandler.BuildClaimRefreshSlots(retry);
                Check(
                    "retry after freeing both ranges atomically grants and refreshes both items",
                    retry.Success && retry.ClaimedAttachmentCount == 2
                    && retryRefresh.Count == 1
                    && retryRefresh.TryGetValue(InventoryListType.GuildMedal, out var retrySlots)
                    && retrySlots.SequenceEqual(new short[] { 48, 97 })
                    && lease.Inventory.GetItem(InventoryListType.GuildMedal, 48)?.ItemId == 100380044
                    && lease.Inventory.GetItem(InventoryListType.GuildMedal, 97)?.ItemId == 90042,
                    ref failures);

            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] medal/gem mail claim: " + ex);
                failures++;
            }
            finally
            {
                InventoryContext.Unregister(sessionId);
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { Console.WriteLine("[WARN] medal/gem mail temp cleanup: " + ex.Message); }
            }
        }

        /// <summary>
        /// 造一封「代币券凭证」邮件。附件 2681762 会被 SpecialRewardRouter 转换成
        /// accounts.token_cera；gold 只用于邮件窗口底部「代币券 N」的显示。
        /// </summary>
        private static MailboxSendResult SendVoucherMail(
            MailboxService mailbox, int characterId, int accountId, int amount)
        {
            var core = new ItemCore
            {
                ItemKind = ItemMetadataResolver.TryResolveItemKind(2681762, out var kind)
                    ? kind
                    : ItemCore.KindMaterial,
                ItemId = 2681762,
                Value = amount
            };
            return mailbox.SendSystemMail(new MailboxSendRequest
            {
                SenderCharacterId = characterId,
                SenderAccountId = accountId,
                SenderName = "金币寄售",
                ReceiverCharacterId = characterId,
                ReceiverAccountId = accountId,
                Title = "金币寄售",
                Text = "voucher claim selftest",
                MailType = 1,
                Gold = amount,
                Unlimited = true,
                IdempotencyKey = "voucher-selftest:" + Guid.NewGuid().ToString("N"),
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = 0,
                        ItemId = 2681762,
                        ItemCount = amount,
                        ItemCoreData = core.ToBytes()
                    }
                }
            });
        }

        private static int ReadAccountTokenCera(GameDatabase database, int accountId)
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT token_cera FROM accounts WHERE account_id=@aid;";
            command.Parameters.AddWithValue("@aid", accountId);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private static MailboxSendResult SendGuildMail(MailboxService mailbox, int characterId, int[] itemIds)
        {
            return mailbox.SendSystemMail(new MailboxSendRequest
            {
                SenderCharacterId = characterId,
                SenderName = "GM",
                ReceiverCharacterId = characterId,
                Text = "medal/gem selftest",
                Attachments = itemIds.Select(itemId => new MailboxSendAttachmentRequest
                {
                    ItemId = itemId,
                    ItemCount = 1,
                }).ToArray(),
            });
        }

        private static void ExecuteSql(GameDatabase database, string sql)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static bool TryAllocateUidsOnImmediateTransaction()
        {
            var tempDir = Path.Combine(
                Path.GetTempPath(),
                "a21-mailbox-uid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var database = new GameDatabase(
                    Path.Combine(tempDir, "inventory.db"),
                    Path.Combine(AppContext.BaseDirectory, "Sqlite", "item_schema.sql"));
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                using (InventoryUidAllocationContext.Enter(connection, transaction))
                {
                    var avatarUid = AvatarDetailRepository.AllocateAvatarUid(database);
                    var creatureUid = CreatureDetailRepository.AllocateCreatureUid(database);
                    if (avatarUid <= 0 || creatureUid <= 0)
                        return false;

                    transaction.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[info] uid ambient allocate failed: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                }
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static bool IsAllZero(byte[] data)
        {
            if (data == null)
                return false;
            for (var i = 0; i < data.Length; i++)
            {
                if (data[i] != 0)
                    return false;
            }

            return true;
        }

        private static bool ContainsBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || haystack.Length < needle.Length)
                return false;

            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var matched = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                    return true;
            }

            return false;
        }

        private static bool TryParseA21MailboxList(
            byte[] body,
            Encoding encoding,
            out ParsedMailboxList parsed,
            bool creatureExtras = false,
            bool avatarPostCreateBlob = false)
        {
            parsed = new ParsedMailboxList();
            if (body == null || body.Length < 6)
                return false;

            var offset = 0;
            parsed.SummaryCount = body[offset++];
            parsed.IsFirstLoad = body[offset++];
            parsed.Summaries = new List<ParsedMailboxSummary>(parsed.SummaryCount);
            for (var i = 0; i < parsed.SummaryCount; i++)
            {
                if (!TryReadSummary(
                    body,
                    ref offset,
                    encoding,
                    creatureExtras,
                    avatarPostCreateBlob,
                    out var summary))
                    return false;
                parsed.Summaries.Add(summary);
            }

            if (offset + 4 > body.Length)
                return false;
            parsed.NotLoaded = BitConverter.ToUInt16(body, offset);
            offset += 2;
            parsed.DetailCount = BitConverter.ToUInt16(body, offset);
            offset += 2;
            parsed.Details = new List<ParsedMailboxDetail>(parsed.DetailCount);
            for (var i = 0; i < parsed.DetailCount; i++)
            {
                if (!TryReadDetail(body, ref offset, encoding, out var detail))
                    return false;
                parsed.Details.Add(detail);
            }

            return offset == body.Length;
        }

        private static bool TryReadSummary(
            byte[] body,
            ref int offset,
            Encoding encoding,
            bool creatureExtras,
            bool avatarPostCreateBlob,
            out ParsedMailboxSummary summary)
        {
            summary = new ParsedMailboxSummary();
            if (!TryReadInt32(body, ref offset, out summary.ClaimObjectId)
                || !TryReadInt32(body, ref offset, out summary.SenderId)
                || !TryReadDstr(body, ref offset, encoding, out summary.SenderName)
                || !TryReadInt32(body, ref offset, out summary.Gold)
                || !TryReadInt32(body, ref offset, out summary.ItemId)
                || !TryReadByte(body, ref offset, out summary.HasItem)
                || !TryReadInt32(body, ref offset, out summary.InstanceValue)
                || !TryReadUInt16(body, ref offset, out summary.Durability)
                || !TryReadByte(body, ref offset, out summary.Attr)
                || !TryReadInt32(body, ref offset, out summary.Enchant)
                || !TryReadByte(body, ref offset, out summary.EnchantUpgrade)
                || !TryReadByte(body, ref offset, out summary.AmplifyType)
                || !TryReadUInt16(body, ref offset, out summary.AmplifyValue))
            {
                return false;
            }

            if (creatureExtras)
            {
                if (!TryReadInt32(body, ref offset, out summary.CreatureExtra)
                    || !TryReadByte(body, ref offset, out summary.CreatureFlag))
                {
                    return false;
                }

                if (summary.CreatureFlag == 1)
                    offset += 12;
            }

            if (!TryReadInt32(body, ref offset, out summary.ExpireTime)
                || !TryReadByte(body, ref offset, out var emblemCount))
            {
                return false;
            }

            offset += emblemCount * 4;
            if (offset > body.Length
                || !TryReadUInt16(body, ref offset, out summary.DfWord)
                || !TryReadByte(body, ref offset, out var sealCount))
            {
                return false;
            }

            offset += sealCount * 3;
            if (sealCount > 0)
            {
                if (!TryReadByte(body, ref offset, out _)
                    || !TryReadByte(body, ref offset, out var changedIndex))
                {
                    return false;
                }

                if (changedIndex != 0xFF)
                    offset += 4;
            }

            // v47, v25, v48, tailWord, 5 tail bytes
            if (offset + 10 > body.Length)
                return false;
            offset += 10;

            if (!TryReadInt32(body, ref offset, out summary.Blob1Length)
                || !TryReadBytes(body, ref offset, summary.Blob1Length, out summary.Blob1)
                || !TryReadInt32(body, ref offset, out summary.Blob2Length)
                || !TryReadBytes(body, ref offset, summary.Blob2Length, out summary.Blob2)
                || !TryReadByte(body, ref offset, out summary.Extra1))
            {
                return false;
            }

            if (avatarPostCreateBlob)
            {
                if (!TryReadInt32(body, ref offset, out summary.PostCreateBlobLength)
                    || !TryReadBytes(body, ref offset, summary.PostCreateBlobLength, out _)
                    || !TryReadInt32(body, ref offset, out var secondBlobLength)
                    || !TryReadBytes(body, ref offset, secondBlobLength, out _))
                {
                    return false;
                }
            }

            if (!TryReadInt32(body, ref offset, out summary.RemainSeconds)
                || !TryReadInt32(body, ref offset, out summary.SummaryKey)
                || !TryReadByte(body, ref offset, out summary.Extra2))
            {
                return false;
            }

            return true;
        }

        private static bool TryReadDetail(
            byte[] body,
            ref int offset,
            Encoding encoding,
            out ParsedMailboxDetail detail)
        {
            detail = new ParsedMailboxDetail();
            return TryReadInt32(body, ref offset, out detail.MessageId)
                && TryReadInt32(body, ref offset, out detail.SenderId)
                && TryReadDstr(body, ref offset, encoding, out detail.SenderName)
                && TryReadDstr(body, ref offset, encoding, out detail.Text)
                && TryReadInt32(body, ref offset, out detail.CreatedAt)
                && TryReadUInt16(body, ref offset, out detail.LetterStat)
                && TryReadByte(body, ref offset, out detail.Extra);
        }

        private static bool TryReadByte(byte[] body, ref int offset, out byte value)
        {
            value = 0;
            if (body == null || offset >= body.Length)
                return false;
            value = body[offset++];
            return true;
        }

        private static bool TryReadUInt16(byte[] body, ref int offset, out ushort value)
        {
            value = 0;
            if (body == null || offset + 2 > body.Length)
                return false;
            value = BitConverter.ToUInt16(body, offset);
            offset += 2;
            return true;
        }

        private static bool TryReadInt32(byte[] body, ref int offset, out int value)
        {
            value = 0;
            if (body == null || offset + 4 > body.Length)
                return false;
            value = BitConverter.ToInt32(body, offset);
            offset += 4;
            return true;
        }

        private static bool TryReadBytes(byte[] body, ref int offset, int length, out byte[] value)
        {
            value = Array.Empty<byte>();
            if (length < 0 || body == null || offset + length > body.Length)
                return false;
            value = new byte[length];
            Buffer.BlockCopy(body, offset, value, 0, length);
            offset += length;
            return true;
        }

        private static bool TryReadDstr(byte[] body, ref int offset, Encoding encoding, out string value)
        {
            value = string.Empty;
            if (!TryReadInt32(body, ref offset, out var length) || length < 0)
                return false;
            if (!TryReadBytes(body, ref offset, length, out var bytes))
                return false;
            value = encoding.GetString(bytes).TrimEnd('\0');
            return true;
        }

        private sealed class ParsedMailboxList
        {
            public byte SummaryCount;
            public byte IsFirstLoad;
            public ushort NotLoaded;
            public ushort DetailCount;
            public List<ParsedMailboxSummary> Summaries = new List<ParsedMailboxSummary>();
            public List<ParsedMailboxDetail> Details = new List<ParsedMailboxDetail>();
        }

        private sealed class ParsedMailboxSummary
        {
            public int ClaimObjectId;
            public int SenderId;
            public string SenderName = string.Empty;
            public int Gold;
            public int ItemId;
            public byte HasItem;
            public int InstanceValue;
            public ushort Durability;
            public byte Attr;
            public int Enchant;
            public byte EnchantUpgrade;
            public byte AmplifyType;
            public ushort AmplifyValue;
            public int CreatureExtra;
            public byte CreatureFlag;
            public int ExpireTime;
            public ushort DfWord;
            public int Blob1Length;
            public byte[] Blob1 = Array.Empty<byte>();
            public int Blob2Length;
            public byte[] Blob2 = Array.Empty<byte>();
            public byte Extra1;
            public int PostCreateBlobLength;
            public int RemainSeconds;
            public int SummaryKey;
            public byte Extra2;
        }

        private sealed class ParsedMailboxDetail
        {
            public int MessageId;
            public int SenderId;
            public string SenderName = string.Empty;
            public string Text = string.Empty;
            public int CreatedAt;
            public ushort LetterStat;
            public byte Extra;
        }
    }
}
