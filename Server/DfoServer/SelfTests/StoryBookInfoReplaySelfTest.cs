using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Security.Cryptography;

namespace DfoServer.SelfTests
{
    public static class StoryBookInfoReplaySelfTest
    {
        private const string CapturedBodySha256 =
            "AC73232EF753FF3BF8AC7CA65C8A1C30C87FEB1B5A69D75C44A1988EDBC64DE1";

        public static int Run()
        {
            Console.WriteLine("=== STORY_BOOK_INFO_REPLAY selftest ===");
            var failures = 0;

            Check(
                "select-character init places STORY_BOOK_INFO after TITLE_BOOK_LIST and before 0x0167",
                VerifyInitSequenceOrder(),
                ref failures);

            Check(
                "STORY_BOOK_INFO replay body matches captured all-open story book packet",
                VerifyReplayBody(),
                ref failures);

            Check(
                "STORY_BOOK_INFO envelope rebuilds the captured 2179-byte packet length",
                VerifyEnvelopeLength(),
                ref failures);

            Console.WriteLine(
                failures == 0
                    ? "STORY_BOOK_INFO_REPLAY selftest passed."
                    : $"STORY_BOOK_INFO_REPLAY selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool VerifyInitSequenceOrder()
        {
            var sequence = NewCharacterInitSequence.Build();
            var storyIndex = sequence.FindIndex(packet =>
                packet.Command == 0x00
                && packet.Type == (ushort)NotiPacketTypeA21.STORY_BOOK_INFO);
            var lastTitleBookIndex = sequence.FindLastIndex(packet =>
                packet.Command == 0x00
                && packet.Type == (ushort)NotiPacketTypeA21.TITLE_BOOK_LIST);
            var achievementIndex = sequence.FindIndex(packet =>
                packet.Command == 0x00
                && packet.Type == 0x0167);

            return storyIndex >= 0
                && lastTitleBookIndex >= 0
                && achievementIndex >= 0
                && lastTitleBookIndex < storyIndex
                && storyIndex < achievementIndex;
        }

        private static bool VerifyReplayBody()
        {
            var registry = new InitPacketBuilderRegistry();
            if (!registry.TryBuild(
                    (ushort)NotiPacketTypeA21.STORY_BOOK_INFO,
                    new SelectCharacterDataSnapshot(),
                    0,
                    out var body)
                || body == null)
            {
                return false;
            }

            var lastRecordOffset = 4 + (44 * 48);
            return body.Length == StoryBookInfoBodyBuilder.ReplayBodyLength
                && BitConverter.ToInt32(body, 0) == 45
                && BitConverter.ToInt32(body, 4) == 1
                && BitConverter.ToInt32(body, 8) == 0x0C1C
                && BitConverter.ToInt32(body, 12) == 1
                && body[16] == 0xBD
                && body[23] == 0x00
                && BitConverter.ToInt32(body, lastRecordOffset) == 1
                && BitConverter.ToInt32(body, lastRecordOffset + 4) == 0x0C48
                && BitConverter.ToInt32(body, lastRecordOffset + 8) == 1
                && Convert.ToHexString(SHA256.HashData(body)) == CapturedBodySha256;
        }

        private static bool VerifyEnvelopeLength()
        {
            var registry = new InitPacketBuilderRegistry();
            if (!registry.TryBuild(
                    (ushort)NotiPacketTypeA21.STORY_BOOK_INFO,
                    new SelectCharacterDataSnapshot(),
                    0,
                    out var body)
                || body == null)
            {
                return false;
            }

            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.STORY_BOOK_INFO,
                body);
            return packet.Length == 2179
                && packet[0] == 0x00
                && BitConverter.ToUInt16(packet, 1) == 0x02BF
                && BitConverter.ToInt32(packet, 3) == 2179
                && packet[15] == 0x2D;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
