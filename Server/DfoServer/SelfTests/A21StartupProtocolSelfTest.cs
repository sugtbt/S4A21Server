using DfoServer.Game.Settings;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    public static class A21StartupProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_STARTUP_PROTOCOL selftest ===");
            var failures = 0;

            Check(
                "A21 cmd/noti table sizes are 1271/1218",
                GameNetworkConfig.CommandPacketCount == 1271
                && GameNetworkConfig.NotificationPacketCount == 1218,
                ref failures);
            Check(
                "A21 loopback advertisement uses the working selector alias",
                GameNetworkConfig.AdvertisedGameIp == "127.0.0.2",
                ref failures);

            IPacketHeader header = new GamePacketHeader
            {
                cmd = 1,
                type = 0x04DD,
                length = 14,
                checksum = 0x11223344,
                seq = 7,
                extra = 0x5A
            };
            var headerBytes = header.GetBytes();
            Check(
                "A21 game receive header is 14B",
                header.GetHeaderSize() == 14 && headerBytes.Length == 14,
                ref failures);

            var parsed = new FlexiblePacket(
                new GamePacketHeader
                {
                    cmd = 1,
                    type = 0x04DD,
                    length = 14,
                    checksum = 0x11223344,
                    seq = 7,
                    extra = 0x5A
                }).GetHeader<GamePacketHeader>();
            Check(
                "A21 header is the only game dispatch header",
                parsed.cmd == 1
                && parsed.type == 0x04DD
                && parsed.length == 14
                && parsed.checksum == 0x11223344
                && parsed.seq == 7
                && parsed.extra == 0x5A,
                ref failures);

            var initial = LoginPacketBuilder.BuildInitialLoginNotice();
            Check(
                "A21 initial notice follows the client reader layout",
                HasValidInitialLoginNoticeLayout(
                    initial,
                    GameNetworkConfig.NormalGamePort),
                ref failures);
            Check(
                "A21 initial notice advertises selector loopback alias",
                Encoding.ASCII.GetString(initial).Contains("127.0.0.2"),
                ref failures);

            var loginSuccess = LoginPacketBuilder.BuildLoginSuccess();
            Check(
                "A21 login success second byte is 20",
                loginSuccess.Length > 2 && loginSuccess[1] == 20,
                ref failures);
            Check(
                "A21 login success uses same advertised address",
                Encoding.ASCII.GetString(loginSuccess).Contains("127.0.0.2"),
                ref failures);

            var initSequence = NewCharacterInitSequence.Build();
            var weddingIndex = initSequence.FindIndex(entry =>
                entry.Kind == SelectCharacterPacketTemplateKind.Raw
                && entry.Command == 0x01
                && entry.Type == (ushort)CmdPacketTypeA21.WEDDING_CHARAC);
            Check(
                "A21 init sequence places DIMENSION_GATE_ENTRANCE_INFO after WEDDING_CHARAC",
                weddingIndex >= 0
                && weddingIndex + 1 < initSequence.Count
                && initSequence[weddingIndex + 1].Kind == SelectCharacterPacketTemplateKind.Raw
                && initSequence[weddingIndex + 1].Command == 0x00
                && initSequence[weddingIndex + 1].Type
                    == (ushort)NotiPacketTypeA21.DIMENSION_GATE_ENTRANCE_INFO,
                ref failures);

            var dimensionGateBody = DimensionGateEntranceInfoBodyBuilder.Build(4, 2);
            Check(
                "A21 DIMENSION_GATE_ENTRANCE_INFO writes remaining then extra",
                dimensionGateBody.Length == 8
                && BitConverter.ToUInt32(dimensionGateBody, 0) == 4
                && BitConverter.ToUInt32(dimensionGateBody, 4) == 2,
                ref failures);

            var hiddenAvatar = Copy(AccountSettings.DefaultMainGameOption);
            var fullAvatarOffset = AccountSettings.FullAvatarOptionIndex * 2;
            hiddenAvatar[fullAvatarOffset] = 0;
            hiddenAvatar[fullAvatarOffset + 1] = 0;
            var option = AccountSettingsPacketBuilder.BuildSelectScreenGameOption(
                new AccountSettings { MainGameOption = hiddenAvatar },
                out var persistedMain);
            var mainLength = hiddenAvatar.Length;
            Check(
                "A21 select-screen 00AD keeps three length-prefixed banks",
                option.Length == mainLength + 12
                && BitConverter.ToInt32(option, 0) == mainLength
                && BitConverter.ToInt32(option, 4 + mainLength) == 0
                && BitConverter.ToInt32(option, 8 + mainLength) == 0,
                ref failures);
            Check(
                "A21 select-screen forces FullAvatar visible",
                persistedMain != null
                && persistedMain[fullAvatarOffset] == 1
                && persistedMain[fullAvatarOffset + 1] == 0,
                ref failures);

            var channelHandler = new ChannelProtocolHandler();
            var channelList = channelHandler.BuildChannelListPlaintext(
                new List<ChannelProtocolHandler.ServerInfo>
                {
                    new ChannelProtocolHandler.ServerInfo
                    {
                        ChannelId = 11,
                        ChannelName = "ch.11",
                        MaxUserNum = 500,
                        Port = 10011
                    }
                });
            Check(
                "A21 ASK plaintext starts with group 1 and count 1",
                channelList.Length >= 6
                && BitConverter.ToUInt16(channelList, 0) == 1
                && BitConverter.ToInt32(channelList, 2) == 1,
                ref failures);
            Check(
                "A21 ASK uses the same advertised address",
                Encoding.ASCII.GetString(channelList).Contains("127.0.0.2"),
                ref failures);

            var userInfo0 = UserInfoSubtype0Builder.BuildNotificationBody(
                new CharacterRecord
                {
                    CharacterId = 7,
                    Name = new byte[] { (byte)'a' },
                });
            var userInfo0PrefixValid = userInfo0.Length >= 41;
            if (userInfo0PrefixValid)
            {
                for (var i = 3; i < 41; i++)
                {
                    if (userInfo0[i] != 0)
                    {
                        userInfo0PrefixValid = false;
                        break;
                    }
                }
            }
            Check(
                "A21 USERINFO0 reserves the fixed 38-byte header",
                userInfo0PrefixValid
                && BitConverter.ToUInt16(userInfo0, 41) == 7,
                ref failures);

            var expertJobChangeRecord = new CharacterRecord
            {
                CharacterId = 7,
                Name = new byte[] { (byte)'a' },
            };
            var expertJobChangeUserInfo = QuestNotificationProjector
                .BuildExpertJobChangeUserInfoBody(expertJobChangeRecord);
            Check(
                "A21 expert-job-change USERINFO0 reuses the login 38-byte header",
                expertJobChangeUserInfo != null
                && expertJobChangeUserInfo.Length == userInfo0.Length
                && expertJobChangeUserInfo.SequenceEqual(userInfo0),
                ref failures);

            var headerlessWriter = new GamePacketWriter();
            headerlessWriter.WriteByte(0);
            headerlessWriter.WriteUInt16(1);
            headerlessWriter.WriteUInt16((ushort)expertJobChangeRecord.CharacterId);
            headerlessWriter.WriteDstr(expertJobChangeRecord.Name);
            headerlessWriter.WriteBytes(
                UserInfoSubtype0Builder.BuildRemainingBytes(expertJobChangeRecord));
            var headerlessUserInfo = headerlessWriter.ToArray();
            Check(
                "A21 expert-job-change USERINFO0 is not the A12 headerless layout",
                expertJobChangeUserInfo.Length == headerlessUserInfo.Length + 38
                && BitConverter.ToUInt16(headerlessUserInfo, 3) == 7,
                ref failures);

            var expertJobUserInfo = UserInfoSubtype0Builder.BuildNotificationBody(
                new CharacterRecord
                {
                    CharacterId = 7,
                    Name = new byte[] { (byte)'a' },
                    Subtype0Tail = new UserInfoMinimumTailSnapshot
                    {
                        ExpertJobType = 3,
                        ExpertJobExp = 0x10203040,
                    },
                });
            var afterAliveOffset = expertJobUserInfo.Length
                - UserInfoSubtype0Builder.A21AfterAliveLength;
            Check(
                "A21 USERINFO0 carries expert job type and exp in the 64-byte tail",
                afterAliveOffset >= 0
                && expertJobUserInfo[afterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveExpertJobTypeOffset] == 3
                && BitConverter.ToUInt32(
                    expertJobUserInfo,
                    afterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveExpertJobExpOffset) == 0x10203040
                && expertJobUserInfo[afterAliveOffset + 52] == 0x64
                && BitConverter.ToUInt16(
                    expertJobUserInfo,
                    afterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveChannelIdOffset) == 2
                && expertJobUserInfo[afterAliveOffset + 61] == 0xFF,
                ref failures);

            var noExpertJobUserInfo = UserInfoSubtype0Builder.BuildNotificationBody(
                new CharacterRecord
                {
                    CharacterId = 7,
                    Name = new byte[] { (byte)'a' },
                    Subtype0Tail = new UserInfoMinimumTailSnapshot
                    {
                        ExpertJobType = 0,
                        ExpertJobExp = uint.MaxValue,
                    },
                });
            var noJobAfterAliveOffset = noExpertJobUserInfo.Length
                - UserInfoSubtype0Builder.A21AfterAliveLength;
            Check(
                "A21 USERINFO0 writes 0 expert-job exp when the job was given up",
                noJobAfterAliveOffset >= 0
                && noExpertJobUserInfo[noJobAfterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveExpertJobTypeOffset] == 0
                && BitConverter.ToUInt32(
                    noExpertJobUserInfo,
                    noJobAfterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveExpertJobExpOffset) == 0,
                ref failures);

            var expertJobChangeInfo = ExpertJobInfoBodyBuilder.BuildProjectedBody(
                3,
                new ExpertJobState
                {
                    DisjointMachine = new DisjointMachineState
                    {
                        MachineGrade = 1,
                        Endurance = 100,
                    },
                },
                0);
            Check(
                "A21 expert-job-change 0x00CD uses login machine layout",
                (ushort)NotiPacketTypeA21.EXPERT_JOB_INFO == 0x00CD
                && expertJobChangeInfo.Length == 10
                && expertJobChangeInfo[0] == 0
                && expertJobChangeInfo[1] == 3
                && BitConverter.ToInt32(expertJobChangeInfo, 2) == 1
                && BitConverter.ToInt32(expertJobChangeInfo, 6) == 100,
                ref failures);

            var enchanterChangeInfo = ExpertJobInfoBodyBuilder.BuildBody(
                new ExpertJobInfoSnapshot
                {
                    Mode = 1,
                    EnchanterLevel = 1,
                    EnchanterEndurance = 50,
                });
            Check(
                "A21 enchanter 0x00CD writes initial level with empty recipes",
                enchanterChangeInfo.Length == 12
                && enchanterChangeInfo[1] == 1
                && enchanterChangeInfo[2] == 0
                && enchanterChangeInfo[3] == 0
                && BitConverter.ToInt32(enchanterChangeInfo, 4) == 1
                && BitConverter.ToInt32(enchanterChangeInfo, 8) == 50,
                ref failures);

            var userInfo1 = UserInfoSubtype1Builder.BuildFromSnapshot(
                new UserInfoAdditionSnapshot(),
                null);
            Check(
                "A21 USERINFO1 uses the 88-byte stat block and fixed dimension tail",
                userInfo1.Length == 301
                && BitConverter.ToInt32(userInfo1, 4) == 88
                && userInfo1[275] == 0x6F
                && BitConverter.ToUInt32(userInfo1, 276) == 0
                && userInfo1[280] == 0,
                ref failures);

            var specialRewardAddition = new UserInfoAdditionSnapshot
            {
                ManageLevel = 4,
                ManagePoint = 120,
            };
            specialRewardAddition.SpecialRewardQuestIds.Add(0x34BE);
            specialRewardAddition.SpecialRewardQuestIds.Add(0x34C0);
            var specialRewardUserInfo1 = UserInfoSubtype1Builder.BuildFromSnapshot(
                specialRewardAddition,
                null);
            Check(
                "A21 USERINFO1 restores completed special-reward quest effects",
                specialRewardUserInfo1.Length == 309
                && specialRewardUserInfo1[275] == 0x6F
                && BitConverter.ToUInt32(specialRewardUserInfo1, 276) == 2
                && BitConverter.ToUInt32(specialRewardUserInfo1, 280) == 0x34BE
                && BitConverter.ToUInt32(specialRewardUserInfo1, 284) == 0x34C0
                && specialRewardUserInfo1[288] == 4
                && BitConverter.ToUInt32(specialRewardUserInfo1, 289) == 120,
                ref failures);

            var auraLockedPrefix = BuildUserInfo1PrefixForSelfTest(123, 7, 0);
            var auraOpenedPrefix = BuildUserInfo1PrefixForSelfTest(123, 7, 1);
            Check(
                "A21 USERINFO1 prefix[14] mirrors aura skin open flag",
                auraLockedPrefix.Length == 20
                && auraOpenedPrefix.Length == 20
                && auraLockedPrefix[9] == 7
                && auraOpenedPrefix[9] == 7
                && auraLockedPrefix[17] == 0
                && auraOpenedPrefix[17] == 1,
                ref failures);

            var roster = AccountCharacterListBodyBuilder.Build(
                new[]
                {
                    new CharacterRecord
                    {
                        CharacterId = 0,
                        SlotIndex = 0,
                        Name = new byte[] { (byte)'a' },
                        Job = 1,
                        GrowType = 2,
                        Level = 1,
                    },
                },
                new GetUserInfoTemplate
                {
                    GateOrCount1 = 32,
                    GateOrCount2 = 32,
                },
                out _,
                accountId: 0);
            Check(
                "A21 type=2 roster uses a zero-based slot and explicit count",
                roster.Length >= 20
                && roster[0] == 2
                && BitConverter.ToUInt16(roster, 16) == 1
                && BitConverter.ToUInt16(roster, 18) == 0,
                ref failures);

            var singleRecordLength = roster.Length - 18;
            var twoCharacterRoster = AccountCharacterListBodyBuilder.Build(
                new[]
                {
                    new CharacterRecord
                    {
                        CharacterId = 0,
                        SlotIndex = 0,
                        Name = new byte[] { (byte)'a' },
                        Job = 1,
                        GrowType = 2,
                        Level = 1,
                    },
                    new CharacterRecord
                    {
                        CharacterId = 0,
                        SlotIndex = 1,
                        Name = new byte[] { (byte)'b' },
                        Job = 2,
                        GrowType = 3,
                        Level = 1,
                    },
                },
                new GetUserInfoTemplate
                {
                    GateOrCount1 = 32,
                    GateOrCount2 = 32,
                },
                out _,
                accountId: 0);
            Check(
                "A21 type=2 keeps adjacent zero-based roster slots distinct",
                BitConverter.ToUInt16(twoCharacterRoster, 16) == 2
                && BitConverter.ToUInt16(twoCharacterRoster, 18) == 0
                && BitConverter.ToUInt16(twoCharacterRoster, 18 + singleRecordLength) == 1,
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_STARTUP_PROTOCOL selftest passed."
                    : $"A21_STARTUP_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] Copy(byte[] source)
        {
            var result = new byte[source.Length];
            Buffer.BlockCopy(source, 0, result, 0, source.Length);
            return result;
        }

        private static bool HasValidInitialLoginNoticeLayout(
            byte[] body,
            int listenerGamePort)
        {
            if (body == null)
                return false;

            var channel =
                GameNetworkConfig.ResolveGameChannel(listenerGamePort);
            var offset = 0;

            if (!TryReadByte(body, ref offset, out var success)
                || success != 0x01
                || !TryReadAsciiDstr(body, ref offset, out var channelName)
                || channelName != channel.LoginName
                || !TryReadInt32(body, ref offset, out var opaqueA)
                || opaqueA != 0
                || !TryReadInt32(body, ref offset, out var opaqueB)
                || opaqueB != 0
                || !TryReadByte(body, ref offset, out var serverIndex)
                || serverIndex != GameNetworkConfig.ChannelServerIndex
                || !TryReadByte(body, ref offset, out var channelId)
                || channelId != channel.ChannelId
                || !TryReadByte(body, ref offset, out var reserved)
                || reserved != 0
                || !TryReadInt32(body, ref offset, out _)
                || !TryReadInt32(body, ref offset, out var addressCount)
                || addressCount != 1
                || !TryReadAsciiDstr(body, ref offset, out var address)
                || address != GameNetworkConfig.AdvertisedGameIp
                || !TryReadInt32(body, ref offset, out var udpPort1)
                || udpPort1 != GameNetworkConfig.InitialUdpPort1
                || !TryReadInt32(body, ref offset, out var udpPort2)
                || udpPort2 != GameNetworkConfig.InitialUdpPort2
                || !TryReadInt32(body, ref offset, out var extraAddressCount)
                || extraAddressCount != 0
                || !TryReadByte(body, ref offset, out var markerA)
                || markerA != (byte)'0'
                || !TryReadByte(body, ref offset, out var markerB)
                || markerB != (byte)'0'
                || !TryReadInt32(body, ref offset, out var commandCount)
                || commandCount != GameNetworkConfig.CommandPacketCount
                || !TryReadInt32(body, ref offset, out var notificationCount)
                || notificationCount
                    != GameNetworkConfig.NotificationPacketCount
                || !TryReadInt32(body, ref offset, out var trailing)
                || trailing != 0)
            {
                return false;
            }

            return offset == body.Length;
        }

        private static byte[] BuildUserInfo1PrefixForSelfTest(
            ushort characterId,
            byte manageLevel,
            byte auraSkinFlag)
        {
            var writer = new GamePacketWriter();
            UserInfoBodyBuilder.WriteA21Subtype1Prefix(
                writer,
                characterId,
                manageLevel,
                auraSkinFlag);
            return writer.ToArray();
        }

        private static bool TryReadByte(
            byte[] body,
            ref int offset,
            out byte value)
        {
            value = 0;
            if (offset >= body.Length)
                return false;

            value = body[offset++];
            return true;
        }

        private static bool TryReadInt32(
            byte[] body,
            ref int offset,
            out int value)
        {
            value = 0;
            if (offset < 0 || offset > body.Length - sizeof(int))
                return false;

            value = BitConverter.ToInt32(body, offset);
            offset += sizeof(int);
            return true;
        }

        private static bool TryReadAsciiDstr(
            byte[] body,
            ref int offset,
            out string value)
        {
            value = null;
            if (!TryReadInt32(body, ref offset, out var length)
                || length < 0
                || offset > body.Length - length)
            {
                return false;
            }

            value = Encoding.ASCII.GetString(body, offset, length);
            offset += length;
            return true;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
