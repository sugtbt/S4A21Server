using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Generic;
using System.IO;
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

            Console.WriteLine(
                failures == 0
                    ? "A21_MAILBOX_PROTOCOL selftest passed."
                    : $"A21_MAILBOX_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
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
