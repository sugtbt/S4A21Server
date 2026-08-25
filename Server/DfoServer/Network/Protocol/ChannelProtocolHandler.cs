using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DfoServer.Infrastructure;

namespace DfoServer.Network
{
    public class ChannelProtocolHandler : BaseProtocolHandler
    {
        public override string ProtocolName => "ChannelProtocol";

        public string ScriptVersion => "66";

        public string AesEncryptionKey => DateTime.Now.ToString("yyyyMMdd") + "000006";

        public string EtcFilePath => ServerPaths.ChannelInfoFilePath;

        public string TestServerIP => GameNetworkConfig.AdvertisedGameIp;

        public int TestServerPort => 10011;

        // A21 SC_ASK_CHANNEL_INFO_NEW 明文布局(抓包校准):
        // 2B 组号 + 4B 条目数,每条 20B 名称 + 4B 容量 + 4B 在线人数 + 16B IP + 4B 端口。
        internal const int ChannelListPrefixSize = 6;
        internal const int ChannelListNameSize = 20;
        internal const int ChannelListAddressSize = 16;
        internal const int ChannelListEntrySize = 48;

        private enum PACKETS : int
        {
            CS_ASK_CHANNEL_INFO = 0x1,
            CS_UPDATE_CHANNEL_INFO = 0x2,
            SC_ASK_CHANNEL_INFO = 0x3,
            CS_NOTICE_CHANNEL_SERVER = 0x4,
            CS_CHECK_SCRIPT_VERSION = 0x5,
            SC_CHECK_SCRIPT_VERSION = 0x6,
            CS_ASK_CHANNEL_SCRIPT = 0x7,
            SC_ASK_CHANNEL_SCRIPT = 0x8,
            CS_GET_SCRIPT = 0x9,
            SC_GET_SCRIPT = 0xA,
            CS_CONNECT = 0xB,
            SC_CONNECT = 0xC,
            CS_GET_GC_INFO = 0xD,
            SC_GET_GC_INFO = 0xE,
            CB_GET_CHANNEL_INFO = 0xF,
            BC_GET_CHANNEL_INFO = 0x10,
            CS_ASK_CHANNEL_INFO_NEW = 0x11,
            SC_ASK_CHANNEL_INFO_NEW = 0x12,
        }

        internal sealed class ServerInfo
        {
            public int ChannelId { get; set; }
            public string ChannelName { get; set; }
            public int MaxUserNum { get; set; }
            public int Port { get; set; }
        }

        public override async Task OnClientConnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Client connected: {session.SessionId}");
            FileLogger.Log(AesEncryptionKey);
            await Task.CompletedTask;
        }

        public override Task OnClientDisconnected(EnhancedClientSession session)
        {
            FileLogger.Log($"[{ProtocolName}] Client disconnected: {session.SessionId}");
            return Task.CompletedTask;
        }

        public override async Task OnPacketReceived(EnhancedClientSession session, FlexiblePacket packet)
        {
            var header = packet.GetHeader<ChannelPacketHeader>();
            var msgType = (PACKETS)header.msg_no;
            var body = packet.BodyData;

            FileLogger.Log($"[{ProtocolName}] Packet received from {session.SessionId}:, Type={msgType}, Length={packet.TotalLength}");

            if (packet.BodyData != null && packet.BodyData.Length > 0)
                FileLogger.Log($"[{ProtocolName}] Packet body (hex): {BitConverter.ToString(packet.BodyData).Replace("-", " ")}");
            else
                FileLogger.Log($"[{ProtocolName}] Packet body is empty.");

            switch (msgType)
            {
                case PACKETS.CS_ASK_CHANNEL_INFO_NEW:
                    await HandleCS_ASK_CHANNEL_INFO_NEW(session, body);
                    break;
                case PACKETS.CS_CHECK_SCRIPT_VERSION:
                    await HandleCS_CHECK_SCRIPT_VERSION(session, body);
                    break;
                case PACKETS.CS_GET_SCRIPT:
                    await HandleCS_GET_SCRIPT(session, body);
                    break;
                case PACKETS.CS_CONNECT:
                    await HandleCS_CONNECT(session, body);
                    break;
                default:
                    FileLogger.Log($"[{ProtocolName}] Unknown message type: {msgType}");
                    break;
            }
        }

        private async Task SendResponsePacket(EnhancedClientSession session, PACKETS msgType, byte[] data)
        {
            var header = new ChannelPacketHeader()
            {
                classification = 0x7C,
                msg_no = (byte)msgType,
                sLength = (uint)(Marshal.SizeOf<ChannelPacketHeader>() + data.Length),
                check_sum = 0,
                ack = 1
            };
            var responsePacket = new FlexiblePacket(header, data);
            var responseBytes = responsePacket.GetBytes();
            await session.SendPacketAsync(responseBytes);
        }

        private async Task HandleCS_CONNECT(EnhancedClientSession session, byte[] packet)
        {
            var list = new List<byte>();
            list.AddRange(new byte[] { 0, 0, 0, 0 });
            list.AddRange(Encoding.ASCII.GetBytes(AesEncryptionKey));
            list.AddRange(new byte[32 - AesEncryptionKey.Length]);
            var data = list.ToArray();

            await SendResponsePacket(session, PACKETS.SC_CONNECT, data);
        }

        private async Task HandleCS_GET_SCRIPT(EnhancedClientSession session, byte[] packet)
        {
            var data = EncryptTool.EncryptData(
                File.ReadAllBytes(EtcFilePath),
                AesEncryptionKey);
            await SendResponsePacket(session, PACKETS.SC_GET_SCRIPT, data);
        }

        private async Task HandleCS_ASK_CHANNEL_INFO_NEW(EnhancedClientSession session, byte[] packet)
        {
            var channels = LoadChannels(
                json: null,
                includeFreeDuel: GameNetworkConfig.FreeDuelListenerEnabled);
            var plaintext = BuildChannelListPlaintext(channels);
            var data = EncryptTool.EncryptData(plaintext, AesEncryptionKey);
            await SendResponsePacket(session, PACKETS.SC_ASK_CHANNEL_INFO_NEW, data);
        }

        internal byte[] BuildChannelListPlaintext(
            IReadOnlyList<ServerInfo> channels)
        {
            if (channels == null)
                throw new ArgumentNullException(nameof(channels));

            var expectedLength = checked(
                ChannelListPrefixSize
                + ChannelListEntrySize * channels.Count);
            var list = new List<byte>(expectedLength);

            WriteUInt16(list, 1);
            WriteInt32(list, channels.Count);
            foreach (var channel in channels)
            {
                WriteFixedField(list, channel.ChannelName, ChannelListNameSize);
                WriteInt32(list, channel.MaxUserNum);
                WriteInt32(list, 0);
                WriteFixedField(list, TestServerIP, ChannelListAddressSize);
                WriteInt32(list, channel.Port);
            }

            if (list.Count != expectedLength)
                throw new InvalidOperationException(
                    $"A21 channel list layout mismatch: "
                    + $"actual={list.Count}, expected={expectedLength}");

            return list.ToArray();
        }

        internal static List<ServerInfo> LoadChannels(
            string json,
            bool includeFreeDuel)
        {
            var result = new List<ServerInfo>();
            var channelIds = new HashSet<int>();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in document.RootElement.EnumerateArray())
                        {
                            if (element.ValueKind != JsonValueKind.Object)
                                continue;

                            var channelId = ReadChannelId(element);
                            if (channelId < byte.MinValue ||
                                channelId > byte.MaxValue ||
                                (!includeFreeDuel &&
                                 GameNetworkConfig.IsFreeDuelChannel(channelId)) ||
                                !channelIds.Add(channelId))
                            {
                                continue;
                            }

                            var name = element.TryGetProperty("name", out var nameValue) &&
                                       nameValue.ValueKind == JsonValueKind.String &&
                                       !string.IsNullOrWhiteSpace(nameValue.GetString())
                                ? nameValue.GetString()
                                : $"#ch.{channelId}";
                            var maxUser = ReadMaxUser(element);
                            result.Add(
                                new ServerInfo
                                {
                                    ChannelId = channelId,
                                    ChannelName = name,
                                    MaxUserNum = maxUser,
                                    Port = ResolveSelectorPort(channelId)
                                });
                        }
                    }
                }
                catch (JsonException ex)
                {
                    FileLogger.Log(
                        $"[{nameof(ChannelProtocolHandler)}] invalid channel list: " +
                        ex.Message);
                    result.Clear();
                    channelIds.Clear();
                }

                foreach (var channel in
                         GameNetworkConfig.BuildGameChannels(includeFreeDuel))
                {
                    if (channelIds.Add(channel.ChannelId))
                        result.Add(CreateDefaultChannel(channel.ChannelId));
                }

                return result;
            }

            // 当前版本运行目录中的 channel_info.etc 是频道目录真源。
            var scriptText = File.Exists(ServerPaths.ChannelInfoFilePath)
                ? File.ReadAllText(ServerPaths.ChannelInfoFilePath, Encoding.UTF8)
                : null;
            var channelIdsFromScript =
                scriptText != null ? ParseScriptChannelIds(scriptText) : null;
            if (channelIdsFromScript != null && channelIdsFromScript.Count > 0)
            {
                foreach (var channelId in channelIdsFromScript)
                {
                    if (!channelIds.Add(channelId))
                        continue;

                    result.Add(
                        new ServerInfo
                        {
                            ChannelId = channelId,
                            ChannelName = $"#ch.{channelId}",
                            MaxUserNum = ResolveChannelCapacity(channelId),
                            Port = ResolveSelectorPort(channelId)
                        });
                }
            }
            else
            {
                foreach (var channel in
                         GameNetworkConfig.BuildGameChannels(includeFreeDuel: false))
                {
                    if (channelIds.Add(channel.ChannelId))
                        result.Add(CreateDefaultChannel(channel.ChannelId));
                }
            }

            if (includeFreeDuel
                && channelIds.Add(GameNetworkConfig.FreeDuelChannelIndex))
            {
                result.Add(
                    CreateDefaultChannel(
                        GameNetworkConfig.FreeDuelChannelIndex));
            }

            return result;
        }

        // 解析 channel_info.etc [server] 组 1 的频道 id(每行一条: id `名称` type `[tag]` ...)。
        // FreeDuel 频道不在此出,由运行时按监听器开关追加到末尾。
        internal static List<int> ParseScriptChannelIds(string text)
        {
            var ids = new List<int>();
            var inServer = false;
            var groupMatched = false;
            using var reader = new StringReader(text);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (trimmed == "[server]")
                {
                    inServer = true;
                    groupMatched = false;
                    continue;
                }
                if (trimmed == "[/server]")
                {
                    inServer = false;
                    continue;
                }
                if (!inServer || trimmed.Length == 0 || trimmed.StartsWith("//"))
                    continue;
                if (!groupMatched)
                {
                    groupMatched = trimmed == "1";
                    continue;
                }

                var tokenEnd = 0;
                while (tokenEnd < trimmed.Length
                       && !char.IsWhiteSpace(trimmed[tokenEnd]))
                {
                    tokenEnd++;
                }
                var idToken = trimmed.Substring(0, tokenEnd);
                if (int.TryParse(
                        idToken,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var channelId)
                    && channelId >= byte.MinValue
                    && channelId <= byte.MaxValue
                    && trimmed.Contains('`')
                    && !GameNetworkConfig.IsFreeDuelChannel(channelId))
                {
                    ids.Add(channelId);
                }
            }

            return ids;
        }

        // 容量字段不在 etc 格式内,按抓包标定:ch.20/21→150、ch.200→250、其余 100。
        private static int ResolveChannelCapacity(int channelId)
        {
            switch (channelId)
            {
                case 20:
                case 21:
                    return 150;
                case GameNetworkConfig.RaidChannelIndex:
                    return 250;
                default:
                    return 100;
            }
        }

        private static int ReadChannelId(JsonElement element)
        {
            if (element.TryGetProperty("id", out var id))
            {
                if (id.ValueKind == JsonValueKind.Number &&
                    id.TryGetInt32(out var numericId))
                    return numericId;
                if (id.ValueKind == JsonValueKind.String &&
                    int.TryParse(id.GetString(), out var stringId))
                    return stringId;
            }

            return GameNetworkConfig.NormalChannelIndex;
        }

        private static int ReadMaxUser(JsonElement element)
        {
            if (!element.TryGetProperty("maxUser", out var maxUser))
                return 500;
            if (maxUser.ValueKind == JsonValueKind.Number &&
                maxUser.TryGetInt32(out var numeric))
                return Math.Max(0, numeric);
            if (maxUser.ValueKind == JsonValueKind.String &&
                int.TryParse(maxUser.GetString(), out var text))
                return Math.Max(0, text);
            return 500;
        }

        private static ServerInfo CreateDefaultChannel(int channelId)
            => new ServerInfo
            {
                ChannelId = channelId,
                ChannelName = $"#ch.{channelId}",
                MaxUserNum = 500,
                Port = ResolveSelectorPort(channelId)
            };

        private static int ResolveSelectorPort(int channelId)
        {
            var channel = GameNetworkConfig.FindGameChannel(channelId)
                          ?? GameNetworkConfig.FindGameChannel(
                              GameNetworkConfig.NormalChannelIndex);
            return channel.PublicGamePort;
        }

        private static void WriteFixedField(
            List<byte> target,
            string value,
            int size)
        {
            var bytes = ClientTextEncoding.GetBytes(value ?? string.Empty);
            target.AddRange(bytes.Take(size));
            if (bytes.Length < size)
                target.AddRange(new byte[size - bytes.Length]);
        }

        private static void WriteUInt16(List<byte> target, ushort value)
        {
            target.AddRange(BitConverter.GetBytes(value));
        }

        private static void WriteInt32(List<byte> target, int value)
        {
            target.AddRange(BitConverter.GetBytes(value));
        }

        private async Task HandleCS_CHECK_SCRIPT_VERSION(EnhancedClientSession session, byte[] packet)
        {
            var list = new List<byte>();
            list.AddRange(new byte[] { 0, 0, 0, 0 });
            list.AddRange(Encoding.ASCII.GetBytes(ScriptVersion));
            list.AddRange(new byte[16 - ScriptVersion.Length]);
            var data = EncryptTool.EncryptData(list.ToArray(), AesEncryptionKey, false);
            await SendResponsePacket(session, PACKETS.SC_CHECK_SCRIPT_VERSION, data);
        }
    }
}
