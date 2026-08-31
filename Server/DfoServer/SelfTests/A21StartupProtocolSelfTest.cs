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

            // 模拟一条已保存的账号主选项（idx55 FullAvatar 关闭，应被强制修补）。
            var hiddenAvatar = new byte[(AccountSettings.FullAvatarOptionIndex + 12) * 2];
            for (var i = 0; i + 1 < hiddenAvatar.Length; i += 2)
                hiddenAvatar[i] = 1;
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

            var emptyOption = AccountSettingsPacketBuilder.BuildSelectScreenGameOption(
                new AccountSettings(),
                out var emptyPersistedMain);
            Check(
                "A21 select-screen 00AD is omitted when no settings saved",
                emptyOption == null && emptyPersistedMain == null,
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
                    + UserInfoSubtype0Builder.A21AfterAliveMoodValueOffset) == 0
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

            var honorTailUserInfo = UserInfoSubtype0Builder.BuildNotificationBody(
                new CharacterRecord
                {
                    CharacterId = 7,
                    Name = new byte[] { (byte)'a' },
                    Subtype0Tail = new UserInfoMinimumTailSnapshot
                    {
                        ProgressA = 0x11223344,
                        ProgressB = 0x55667788,
                    },
                });
            var honorAfterAliveOffset = honorTailUserInfo.Length
                - UserInfoSubtype0Builder.A21AfterAliveLength;
            Check(
                "A21 USERINFO0 carries honor level/exp in the 64-byte tail ProgressA/ProgressB slots",
                honorAfterAliveOffset >= 0
                && BitConverter.ToUInt32(
                    honorTailUserInfo,
                    honorAfterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveProgressAOffset) == 0x11223344
                && BitConverter.ToUInt32(
                    honorTailUserInfo,
                    honorAfterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveProgressBOffset) == 0x55667788,
                ref failures);

            var unlockedSkillTreeUserInfo = UserInfoSubtype0Builder.BuildNotificationBody(
                new CharacterRecord
                {
                    CharacterId = 7,
                    Name = new byte[] { (byte)'a' },
                    Subtype0Tail = new UserInfoMinimumTailSnapshot
                    {
                        SkillTreeIndex = 0,
                    },
                });
            var skillTreeAfterAliveOffset = unlockedSkillTreeUserInfo.Length
                - UserInfoSubtype0Builder.A21AfterAliveLength;
            Check(
                "A21 USERINFO0 64-byte tail mirrors skill-tree expansion state at +61",
                skillTreeAfterAliveOffset >= 0
                && unlockedSkillTreeUserInfo[skillTreeAfterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveSkillTreeIndexOffset] == 0
                && honorTailUserInfo[honorAfterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveSkillTreeIndexOffset]
                    == DfoServer.Game.Skills.SkillTreeExpansionState.LockedWireValue,
                ref failures);

            var defaultMoodUserInfo = UserInfoSubtype0Builder.BuildNotificationBody(
                new CharacterRecord
                {
                    CharacterId = 7,
                    Name = new byte[] { (byte)'a' },
                    Subtype0Tail = new UserInfoMinimumTailSnapshot
                    {
                        ChannelId = 11,
                    },
                });
            var defaultMoodAfterAliveOffset = defaultMoodUserInfo.Length
                - UserInfoSubtype0Builder.A21AfterAliveLength;
            Check(
                "A21 USERINFO0 64-byte tail defaults mood popup to 0 at +59",
                defaultMoodAfterAliveOffset >= 0
                && BitConverter.ToUInt16(
                    defaultMoodUserInfo,
                    defaultMoodAfterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveMoodValueOffset) == 0,
                ref failures);

            var setMoodUserInfo = UserInfoSubtype0Builder.BuildNotificationBody(
                new CharacterRecord
                {
                    CharacterId = 7,
                    Name = new byte[] { (byte)'a' },
                    Subtype0Tail = new UserInfoMinimumTailSnapshot
                    {
                        ChannelId = 11,
                        MoodValue = 6,
                        SkillTreeIndex = 0,
                    },
                });
            var setMoodAfterAliveOffset = setMoodUserInfo.Length
                - UserInfoSubtype0Builder.A21AfterAliveLength;
            Check(
                "A21 USERINFO0 writes MoodValue at 64B-tail +59 without channel_id",
                setMoodAfterAliveOffset >= 0
                && BitConverter.ToUInt16(
                    setMoodUserInfo,
                    setMoodAfterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveMoodValueOffset) == 6
                && setMoodUserInfo[setMoodAfterAliveOffset
                    + UserInfoSubtype0Builder.A21AfterAliveSkillTreeIndexOffset] == 0,
                ref failures);

            var skillTreeBroadcastDbPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dfo_a21_startup_skilltree_{Guid.NewGuid():N}.db");
            try
            {
                var skillTreeDb = new DfoServer.Infrastructure.GameDatabase(
                    skillTreeBroadcastDbPath,
                    DfoServer.Infrastructure.ServerPaths.SchemaFilePath);
                using (var connection = skillTreeDb.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash) VALUES(9601, 'a21-startup-skilltree', '');
INSERT INTO characters(character_id, account_id, name, job) VALUES(9602, 9601, 'a21-startup-skilltree-c', 0);";
                    command.ExecuteNonQuery();
                }

                var subtype0Repository = new DfoServer.Game.CharacterData.SqliteSubtype0FieldsRepository(skillTreeDb);
                Check(
                    "subtype0 broadcast tail defaults to locked skill tree before purchase",
                    subtype0Repository.Load(9602)?.SkillTreeIndex
                        == DfoServer.Game.Skills.SkillTreeExpansionState.LockedWireValue,
                    ref failures);

                new DfoServer.Game.CharacterData.SqliteSubtype1Repository(skillTreeDb)
                    .UpdateSkillTreeIndex(9602, 1);
                Check(
                    "subtype0 broadcast tail carries purchased skill-tree page",
                    subtype0Repository.Load(9602)?.SkillTreeIndex == 1,
                    ref failures);
            }
            finally
            {
                try { if (System.IO.File.Exists(skillTreeBroadcastDbPath)) System.IO.File.Delete(skillTreeBroadcastDbPath); }
                catch { }
            }

            var moodDbPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dfo_a21_startup_mood_{Guid.NewGuid():N}.db");
            try
            {
                var moodDb = new DfoServer.Infrastructure.GameDatabase(
                    moodDbPath,
                    DfoServer.Infrastructure.ServerPaths.SchemaFilePath);
                using (var connection = moodDb.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash) VALUES(9611, 'a21-startup-mood', '');
INSERT INTO characters(character_id, account_id, name, job) VALUES(9612, 9611, 'a21-startup-mood-c', 0);";
                    command.ExecuteNonQuery();
                }

                var moodStateRepo = new DfoServer.Game.CharacterData.SqliteCharacterStateRepository(moodDb);
                var moodSubtype0Repo = new DfoServer.Game.CharacterData.SqliteSubtype0FieldsRepository(moodDb);
                moodStateRepo.SaveMoodValue(9612, 6);
                var persistedMoodTail = moodSubtype0Repo.Load(9612);
                var persistedMoodUserInfo = UserInfoSubtype0Builder.BuildNotificationBody(
                    new CharacterRecord
                    {
                        CharacterId = 9612,
                        Name = new byte[] { (byte)'a' },
                        Subtype0Tail = persistedMoodTail,
                    });
                var persistedMoodAfterAliveOffset = persistedMoodUserInfo.Length
                    - UserInfoSubtype0Builder.A21AfterAliveLength;
                Check(
                    "CHANGE_EMOTION mood_value round-trips into USERINFO0 +59",
                    persistedMoodTail != null
                    && persistedMoodTail.MoodValue == 6
                    && persistedMoodTail.ChannelId == 2
                    && persistedMoodTail.EmotionIndex == 0
                    && persistedMoodAfterAliveOffset >= 0
                    && BitConverter.ToUInt16(
                        persistedMoodUserInfo,
                        persistedMoodAfterAliveOffset
                        + UserInfoSubtype0Builder.A21AfterAliveMoodValueOffset) == 6,
                    ref failures);
            }
            finally
            {
                try { if (System.IO.File.Exists(moodDbPath)) System.IO.File.Delete(moodDbPath); }
                catch { }
            }

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

            var unlockedExpansionUserInfo1 = UserInfoSubtype1Builder.BuildFromSnapshot(
                new UserInfoAdditionSnapshot
                {
                    SkillTreeIndex = 0,
                },
                null);
            Check(
                "A21 USERINFO1 skill-tree byte mirrors snapshot expansion state",
                userInfo1.Length == unlockedExpansionUserInfo1.Length
                && userInfo1[110] == DfoServer.Game.Skills.SkillTreeExpansionState.LockedWireValue
                && unlockedExpansionUserInfo1[110] == 0,
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

            var hotkeyIndex = initSequence.FindIndex(entry =>
                entry.Kind == SelectCharacterPacketTemplateKind.Raw
                && entry.Command == 0x00
                && entry.Type == 0x01C7);
            var townUserInfo0Index = initSequence.FindIndex(entry =>
                entry.Kind == SelectCharacterPacketTemplateKind.Raw
                && entry.Command == 0x00
                && entry.Type == (ushort)NotiPacketTypeA21.USERINFO
                && entry.OccurrenceIndex == 3);
            var firstUserInfo0Index = initSequence.FindIndex(entry =>
                entry.Kind == SelectCharacterPacketTemplateKind.Raw
                && entry.Command == 0x00
                && entry.Type == (ushort)NotiPacketTypeA21.USERINFO
                && entry.OccurrenceIndex == 0);
            Check(
                "A21 init sends a second USERINFO0 immediately before HOTKEY 0x01C7",
                firstUserInfo0Index >= 0
                && townUserInfo0Index > firstUserInfo0Index
                && hotkeyIndex == townUserInfo0Index + 1,
                ref failures);
            var townUserInfoBuilder = new UserInfoBodyBuilder();
            Check(
                "A21 USERINFO occ=3 rebuilds subtype 0 for the town update",
                townUserInfoBuilder.TryBuild(
                    new SelectCharacterDataSnapshot
                    {
                        CharacterRecord = new CharacterRecord
                        {
                            CharacterId = 7,
                            Name = new byte[] { (byte)'a' },
                            Subtype0Tail = new UserInfoMinimumTailSnapshot { MoodValue = 6 },
                        },
                    },
                    3,
                    out var townUserInfo0)
                && townUserInfo0 != null
                && townUserInfo0[0] == 0
                && BitConverter.ToUInt16(
                    townUserInfo0,
                    townUserInfo0.Length
                    - UserInfoSubtype0Builder.A21AfterAliveLength
                    + UserInfoSubtype0Builder.A21AfterAliveMoodValueOffset) == 6,
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
