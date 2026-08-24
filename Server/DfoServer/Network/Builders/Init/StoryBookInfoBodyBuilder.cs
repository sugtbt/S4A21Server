using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class StoryBookInfoBodyBuilder : IInitPacketBuilder
    {
        private const int FirstStoryId = 0x0C1C;
        private const int LastStoryId = 0x0C48;
        private const int StoryCount = LastStoryId - FirstStoryId + 1;
        private const int RecordLength = 48;

        public const int ReplayBodyLength = 4 + (StoryCount * RecordLength);

        private static readonly byte[] CapturedTitleBytes =
        {
            0xBD, 0xC7, 0xC9, 0xAB, 0xC3, 0xFB, 0x00, 0x00,
        };

        private static readonly byte[] Body = BuildReplayBody();

        public ushort NotiType => (ushort)NotiPacketTypeA21.STORY_BOOK_INFO;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = (byte[])Body.Clone();
            return true;
        }

        private static byte[] BuildReplayBody()
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(StoryCount);

            // A21 实机抓包：故事簿 0x0C1C..0x0C48 全部按开启状态回放。
            for (var storyId = FirstStoryId; storyId <= LastStoryId; storyId++)
            {
                writer.WriteInt32(1);
                writer.WriteInt32(storyId);
                writer.WriteInt32(1);
                writer.WriteBytes(CapturedTitleBytes);
                writer.WriteZeroBytes(RecordLength - 12 - CapturedTitleBytes.Length);
            }

            var body = writer.ToArray();
            if (body.Length != ReplayBodyLength)
                throw new InvalidOperationException("STORY_BOOK_INFO replay body length mismatch.");

            return body;
        }
    }
}
