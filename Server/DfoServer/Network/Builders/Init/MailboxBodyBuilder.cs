using DfoServer.Game.Mailbox;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using System;

namespace DfoServer.Network.Builders
{
    public sealed class MailboxBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.MAILBOX_MAIL_LIST;
        private const int MailboxPageSize = 20;
        private readonly MailboxRepository _repository;

        public MailboxBodyBuilder()
            : this(GameDatabase.CreateDefault())
        {
        }

        public MailboxBodyBuilder(IGameDatabase database)
        {
            _repository = new MailboxRepository(
                database ?? throw new ArgumentNullException(nameof(database)));
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var characterId = snapshot.CharacterRecord?.CharacterId ?? 0;
            if (characterId <= 0)
            {
                body = new byte[6];
                return true;
            }

            try
            {
                // 有邮件发完整 0x0061；空收件箱为 6 字节全 0。
                var page = _repository.LoadInboxPage(characterId, MailboxPageSize);
                var notLoaded = ClampUInt16(page.NotLoadedCount);
                body = MailboxHandler.BuildMailboxListNotification(page.Entries, isFirstLoad: false, notLoadedCount: notLoaded);
                FileLogger.Log($"[MailboxInit] cid={characterId} entries={page.Entries.Count} notLoaded={page.NotLoadedCount}");
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[MailboxInit] full build failed cid={characterId}: {ex.Message}");
                body = new byte[6];
                return true;
            }
        }

        private static ushort ClampUInt16(int value)
        {
            if (value <= ushort.MinValue)
                return ushort.MinValue;
            if (value >= ushort.MaxValue)
                return ushort.MaxValue;
            return (ushort)value;
        }
    }
}
