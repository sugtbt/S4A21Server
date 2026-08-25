using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Infrastructure;
using DfoServer.Network;

namespace DfoServer.SelfTests
{
    public static class A21ChannelProtocolSelfTest
    {
        private const string Key = "20260815000006";
        private const int HeaderSize = 11;

        public static int Run()
        {
            Console.WriteLine("=== A21_CHANNEL_PROTOCOL selftest ===");
            var failures = 0;
            var handler = new ChannelProtocolHandler();
            var channels = new List<ChannelProtocolHandler.ServerInfo>
            {
                new ChannelProtocolHandler.ServerInfo
                {
                    ChannelId = 11,
                    ChannelName = "ch.11",
                    MaxUserNum = 500,
                    Port = 10011
                },
                new ChannelProtocolHandler.ServerInfo
                {
                    ChannelId = 100,
                    ChannelName = "ch.100",
                    MaxUserNum = 900,
                    Port = 10161
                }
            };

            var plaintext = handler.BuildChannelListPlaintext(channels);
            var cursor = 0;
            var prefixReadable = TryReadUInt16(
                plaintext,
                ref cursor,
                out var group);
            var countReadable = TryReadInt32(
                plaintext,
                ref cursor,
                out var channelCount);
            var entries = new List<ParsedChannelEntry>();
            var entriesReadable = prefixReadable
                                  && countReadable
                                  && channelCount >= 0;
            if (entriesReadable)
            {
                for (var i = 0; i < channelCount; i++)
                {
                    if (!TryReadChannelEntry(
                            plaintext,
                            ref cursor,
                            out var entry))
                    {
                        entriesReadable = false;
                        break;
                    }

                    entries.Add(entry);
                }
            }

            Check(
                "A21 channel plaintext has reader prefix",
                prefixReadable
                && countReadable
                && group == 1
                && channelCount == channels.Count
                && cursor >= ChannelProtocolHandler.ChannelListPrefixSize,
                ref failures);
            Check(
                "A21 channel plaintext has fixed 48B entries",
                plaintext.Length
                    == ChannelProtocolHandler.ChannelListPrefixSize
                       + ChannelProtocolHandler.ChannelListEntrySize
                         * channels.Count,
                ref failures);
            Check(
                "A21 channel reader consumes every entry field",
                entriesReadable
                && entries.Count == channels.Count
                && cursor == plaintext.Length,
                ref failures);
            Check(
                "A21 channel reader gets both fixed-width names",
                entries.Count == 2
                && entries[0].Name == "ch.11"
                && entries[1].Name == "ch.100",
                ref failures);
            Check(
                "A21 channel reader gets both field_1 values",
                entries.Count == 2
                && entries[0].Field1 == 500
                && entries[1].Field1 == 900,
                ref failures);
            Check(
                "A21 channel reader gets both field_2 values",
                entries.Count == 2
                && entries[0].Field2 == 0
                && entries[1].Field2 == 0,
                ref failures);
            Check(
                "A21 channel reader gets both address fields",
                entries.Count == 2
                && entries[0].Address == GameNetworkConfig.AdvertisedGameIp
                && entries[1].Address == GameNetworkConfig.AdvertisedGameIp,
                ref failures);
            Check(
                "A21 channel reader gets both tail fields",
                entries.Count == 2
                && entries[0].Tail == 10011
                && entries[1].Tail == 10161,
                ref failures);

            var selectorCatalog = ChannelProtocolHandler.LoadChannels(
                json: null,
                includeFreeDuel: false);
            var selectorCatalogPlaintext = handler.BuildChannelListPlaintext(
                selectorCatalog);
            Check(
                "A21 selector catalog comes from channel_info.etc group 1",
                selectorCatalog.Count == 28
                && selectorCatalog.Any(channel => channel.ChannelId == 1)
                && selectorCatalog.Any(channel => channel.ChannelId == 11)
                && selectorCatalog.Any(channel => channel.ChannelId == 200)
                && !selectorCatalog.Any(channel =>
                    channel.ChannelId == GameNetworkConfig.FreeDuelChannelIndex)
                && selectorCatalog.Single(
                       channel => channel.ChannelId == 200).MaxUserNum == 250
                && selectorCatalog.All(channel =>
                    channel.ChannelName == $"#ch.{channel.ChannelId}"),
                ref failures);
            Check(
                "A21 selector catalog keeps the capture-verified 1350B body",
                selectorCatalogPlaintext.Length == 1350
                && BitConverter.ToInt32(selectorCatalogPlaintext, 2) == 28,
                ref failures);

            var encrypted = EncryptTool.EncryptData(plaintext, Key);
            var decrypted = EncryptTool.DecryptData(encrypted, Key);
            Check(
                "A21 channel AES/zlib round-trip preserves plaintext",
                encrypted.Length > 2
                && encrypted[0] == 0x78
                && encrypted[1] == 0x9C
                && decrypted.Length >= plaintext.Length
                && decrypted.Take(plaintext.Length).SequenceEqual(plaintext)
                && decrypted.Skip(plaintext.Length).All(value => value == 0),
                ref failures);

            var header = new ChannelPacketHeader
            {
                classification = 0x7C,
                msg_no = 0x12,
                sLength = (uint)(HeaderSize + encrypted.Length),
                check_sum = 0,
                ack = 1
            };
            var wire = new FlexiblePacket(header, encrypted).GetBytes();
            Check(
                "A21 SC_ASK_CHANNEL_INFO_NEW uses an 11B header",
                ((IPacketHeader)header).GetHeaderSize() == HeaderSize
                && wire.Length == HeaderSize + encrypted.Length
                && BitConverter.ToUInt32(wire, 2) == wire.Length
                && wire[0] == 0x7C
                && wire[1] == 0x12
                && wire[10] == 1,
                ref failures);

            var processor = new FlexiblePacketProcessor();
            var clientId = Guid.NewGuid();
            processor.SetClientPacketStructure(clientId, new ChannelPacketHeader());
            var packets = processor.ProcessReceivedData(
                clientId,
                wire,
                wire.Length);
            var parsed = packets.Count == 1 ? packets[0] : null;
            Check(
                "A21 channel wire packet survives TCP framing",
                parsed != null
                && parsed.GetHeader<ChannelPacketHeader>().msg_no == 0x12
                && parsed.BodyData != null
                && parsed.BodyData.SequenceEqual(encrypted),
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_CHANNEL_PROTOCOL selftest passed."
                    : $"A21_CHANNEL_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static string ReadFixedClientText(
            byte[] bytes,
            int offset,
            int count)
        {
            return ClientTextEncoding.GetString(bytes, offset, count);
        }

        private static bool TryReadChannelEntry(
            byte[] bytes,
            ref int cursor,
            out ParsedChannelEntry entry)
        {
            entry = null;
            if (!TryReadFixedClientText(
                    bytes,
                    ref cursor,
                    ChannelProtocolHandler.ChannelListNameSize,
                    out var name)
                || !TryReadInt32(bytes, ref cursor, out var field1)
                || !TryReadInt32(bytes, ref cursor, out var field2)
                || !TryReadFixedClientText(
                    bytes,
                    ref cursor,
                    ChannelProtocolHandler.ChannelListAddressSize,
                    out var address)
                || !TryReadInt32(bytes, ref cursor, out var tail))
            {
                return false;
            }

            entry = new ParsedChannelEntry
            {
                Name = name,
                Field1 = field1,
                Field2 = field2,
                Address = address,
                Tail = tail
            };
            return true;
        }

        private static bool TryReadFixedClientText(
            byte[] bytes,
            ref int cursor,
            int count,
            out string value)
        {
            value = string.Empty;
            if (bytes == null
                || cursor < 0
                || count < 0
                || bytes.Length - cursor < count)
            {
                return false;
            }

            value = ReadFixedClientText(bytes, cursor, count);
            cursor += count;
            return true;
        }

        private static bool TryReadUInt16(
            byte[] bytes,
            ref int cursor,
            out ushort value)
        {
            value = 0;
            if (bytes == null
                || cursor < 0
                || bytes.Length - cursor < sizeof(ushort))
            {
                return false;
            }

            value = BitConverter.ToUInt16(bytes, cursor);
            cursor += sizeof(ushort);
            return true;
        }

        private static bool TryReadInt32(
            byte[] bytes,
            ref int cursor,
            out int value)
        {
            value = 0;
            if (bytes == null
                || cursor < 0
                || bytes.Length - cursor < sizeof(int))
            {
                return false;
            }

            value = BitConverter.ToInt32(bytes, cursor);
            cursor += sizeof(int);
            return true;
        }

        private sealed class ParsedChannelEntry
        {
            public string Name { get; set; }
            public int Field1 { get; set; }
            public int Field2 { get; set; }
            public string Address { get; set; }
            public int Tail { get; set; }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
