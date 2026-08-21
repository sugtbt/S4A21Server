using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using System;
using System.Text;

namespace DfoServer.SelfTests
{
    public static class OtherUserInfoProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== OTHER_USER_INFO_PROTOCOL selftest ===");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var failures = 0;

            Check(
                "inspect opcodes match PacketTypesA21",
                (ushort)CmdPacketTypeA21.GET_USERINFO == 0x0008
                && (ushort)NotiPacketTypeA21.USERINFO == 0x0002
                && (ushort)CmdPacketTypeA21.OTHER_USER_TITLE_BOOK_LIST == 0x01A8
                && (ushort)NotiPacketTypeA21.TITLE_BOOK_LIST == 0x0166,
                ref failures);

            CheckEmptyLayout(ref failures);
            CheckHeaderAndTrailer(ref failures);
            CheckSkillPagesAreBothWritten(ref failures);
            CheckFashionMergeUsesAppearance(ref failures);
            CheckInspectStaysOnSameChannel(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "OTHER_USER_INFO_PROTOCOL: PASS"
                    : $"OTHER_USER_INFO_PROTOCOL: FAIL count={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckEmptyLayout(ref int failures)
        {
            var body = UserInfoSubtype3Builder.BuildNotificationBody(
                0x19A8,
                new UserInfoAdditionSnapshot(),
                null,
                null);
            Check(
                "empty subtype 3 is 165B with 88B combat blob",
                body.Length == 165
                && body[0] == UserInfoSubtype3Builder.Subtype
                && BitConverter.ToUInt16(body, 1) == UserInfoSubtype3Builder.Version
                && BitConverter.ToUInt16(body, 3) == 0
                && body[5] == 0 && body[6] == 0 && body[7] == 0
                && body[8] == 0 && body[9] == 0
                && BitConverter.ToUInt16(body, 10) == 0x19A8
                && BitConverter.ToInt32(body, 16) == CombatStatBlobWriter.BlobLength
                && BitConverter.ToUInt16(body, 76) == CombatStatBlobWriter.MiddleMarker
                && BitConverter.ToUInt32(body, 102) == CombatStatBlobWriter.TrailingConstant
                && body[108] == 0
                && body[109] == 0
                && body[123] == 0
                && body[124] == 0
                && body[159] == UserInfoSubtype3Builder.InspectGuildMarker
                && BitConverter.ToUInt32(body, 160) == 0
                && body[164] == 0,
                ref failures);
        }

        private static void CheckHeaderAndTrailer(ref int failures)
        {
            var addition = new UserInfoAdditionSnapshot
            {
                ManageLevel = 6,
                CharacExp = 1046561640,
                StatHpMax = 43500,
                StatMpMax = 64000,
                ExEquipSlotStat = 3,
                SkillTreeIndex = SkillTreeExpansionState.LockedWireValue,
                EquippedCreatureLevel = 50,
            };
            addition.SpecialRewardQuestIds.Add(0x34BE);
            addition.SpecialRewardQuestIds.Add(0x34C0);

            var record = new CharacterRecord
            {
                Subtype0Tail = new UserInfoMinimumTailSnapshot
                {
                    GuildLevel = 0,
                    GuildNameBytes = Encoding.GetEncoding("GBK").GetBytes("时光挚友"),
                },
            };

            var body = UserInfoSubtype3Builder.BuildNotificationBody(
                0x19A8,
                addition,
                null,
                record);

            Check(
                "header writes manageLevel before uid and uses an 88B combat blob",
                body[0] == 3
                && BitConverter.ToUInt16(body, 3) == 6
                && BitConverter.ToUInt16(body, 10) == 0x19A8
                && BitConverter.ToUInt32(body, 12) == 1046561640u
                && BitConverter.ToInt32(body, 16) == 88
                && BitConverter.ToUInt32(body, 20) == 43500
                && BitConverter.ToUInt32(body, 24) == 64000
                && body[108] == 3
                && body[109] == 0
                && body[122] == 0xFF
                && body[125] == 50,
                ref failures);

            var contextOffset = 126;
            var contextZeros = true;
            for (var index = 0; index < UserInfoSubtype3Builder.InspectContextLength; index++)
            {
                if (body[contextOffset + index] != 0)
                    contextZeros = false;
            }

            var guildOffset = contextOffset + UserInfoSubtype3Builder.InspectContextLength;
            var guildLen = BitConverter.ToInt32(body, guildOffset);
            var guildBytes = new byte[guildLen];
            Buffer.BlockCopy(body, guildOffset + 4, guildBytes, 0, guildLen);
            var afterGuild = guildOffset + 4 + guildLen;
            Check(
                "trailer keeps 27B context, GBK guild dstr, 0x6F marker and quest ids",
                contextZeros
                && guildLen == 8
                && Encoding.GetEncoding("GBK").GetString(guildBytes) == "时光挚友"
                && body[afterGuild] == 0
                && body[afterGuild + 1] == 0
                && body[afterGuild + 2] == UserInfoSubtype3Builder.InspectGuildMarker
                && BitConverter.ToUInt32(body, afterGuild + 3) == 2
                && BitConverter.ToUInt32(body, afterGuild + 7) == 0x34BE
                && BitConverter.ToUInt32(body, afterGuild + 11) == 0x34C0
                && body[afterGuild + 15] == 0
                && body.Length == afterGuild + 16,
                ref failures);
        }

        private static void CheckSkillPagesAreBothWritten(ref int failures)
        {
            var skills = new SkillInfoSnapshot();
            var page = new SkillInfoPageSnapshot();
            page.Entries.Add(new SkillInfoEntrySnapshot { SkillId = 179, Level = 7 });
            page.Entries.Add(new SkillInfoEntrySnapshot { SkillId = 174, Level = 1 });
            skills.Pages.Add(page);
            skills.Pages.Add(page);

            var body = UserInfoSubtype3Builder.BuildNotificationBody(
                1,
                new UserInfoAdditionSnapshot(),
                skills,
                null);
            Check(
                "subtype 3 writes both skill pages even when they are copies",
                body[123] == 2
                && BitConverter.ToUInt16(body, 124) == 179
                && body[126] == 7
                && BitConverter.ToUInt16(body, 127) == 174
                && body[129] == 1
                && body[130] == 2
                && BitConverter.ToUInt16(body, 131) == 179
                && body[133] == 7,
                ref failures);
        }

        private static void CheckFashionMergeUsesAppearance(ref int failures)
        {
            var addition = new UserInfoAdditionSnapshot();
            var record = new CharacterRecord
            {
                Appearance = new[]
                {
                    new CharacterAppearanceEntry(
                        0,
                        54601,
                        4,
                        new byte[4],
                        0,
                        0,
                        0,
                        0),
                },
            };
            var body = UserInfoSubtype3Builder.BuildNotificationBody(
                1,
                addition,
                null,
                record);
            Check(
                "subtype 3 merges missing fashion from appearance into the equipment list",
                body[109] == 1
                && body[110] == 0
                && BitConverter.ToInt32(body, 111) == 54601,
                ref failures);
        }

        private static void CheckInspectStaysOnSameChannel(ref int failures)
        {
            var sessions = new SessionDirectory();
            var requester = CreateDirectorySession(1014, 10010);
            var sameChannel = CreateDirectorySession(1004, 10010);
            var otherChannel = CreateDirectorySession(1005, 10011);
            sessions.Register(sameChannel.Player.CharacterId, sameChannel);
            sessions.Register(otherChannel.Player.CharacterId, otherChannel);

            Check(
                "inspect lookup hits the same-channel uid",
                ReferenceEquals(
                    CharacterSelectHandler.FindInspectableOnlineByUserId(
                        sessions,
                        requester,
                        1004),
                    sameChannel),
                ref failures);
            Check(
                "inspect lookup ignores a different-channel uid",
                CharacterSelectHandler.FindInspectableOnlineByUserId(
                    sessions,
                    requester,
                    1005) == null,
                ref failures);
        }

        private static EnhancedClientSession CreateDirectorySession(
            int characterId,
            int listenerPort)
        {
            var session = new EnhancedClientSession(
                null,
                new GamePacketHeader(),
                listenerPort);
            session.Player.CharacterId = characterId;
            session.Player.UserId = unchecked((ushort)characterId);
            return session;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
