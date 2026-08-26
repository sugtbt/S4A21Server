using System;
using DfoServer.Game.Events;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders
{
    public sealed class EventInfoBodyBuilder : IInitPacketBuilder
    {
        private readonly GameEventRepository _repository;

        public EventInfoBodyBuilder()
            : this(GameDatabase.CreateDefault())
        {
        }

        internal EventInfoBodyBuilder(IGameDatabase database)
        {
            _repository = new GameEventRepository(
                database ?? throw new ArgumentNullException(nameof(database)));
        }

        public ushort NotiType => (ushort)NotiPacketTypeA21.EVENT_INFO;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex,
            out byte[] body)
        {
            body = Build(_repository.LoadEventInfoSnapshot());
            return true;
        }

        internal static byte[] Build(GameEventInfoSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            var entries = snapshot?.Events ?? Array.Empty<GameEventInfoEntry>();
            var extraEntries = snapshot?.ExtraEntries
                ?? Array.Empty<GameEventExtraInfoEntry>();

            writer.WriteUInt16((ushort)Math.Min(ushort.MaxValue, entries.Count));
            for (var index = 0; index < entries.Count && index < ushort.MaxValue; index++)
            {
                var entry = entries[index];
                writer.WriteUInt16(entry.EventId);
                writer.WriteUInt32(entry.Unknown0);
                WriteDstr(writer, entry.StartNotice);
                WriteDstr(writer, entry.EndNotice);
                writer.WriteByte(entry.HasDetail ? (byte)1 : (byte)0);
                if (!entry.HasDetail)
                    continue;

                writer.WriteByte(entry.FlagA);
                writer.WriteByte(entry.FlagB);
                WriteDstr(writer, entry.Title);
                WriteDstr(writer, entry.ShortName);
                WriteDstr(writer, entry.ReservedOrIcon);
                writer.WriteUInt32(entry.StartUnixTime);
                writer.WriteUInt32(entry.EndUnixTime);
                WriteDstr(writer, entry.LinkKey);
                WriteDstr(writer, entry.Description);
                writer.WriteByte(entry.DetailEnabled ? (byte)1 : (byte)0);
            }

            writer.WriteByte((byte)Math.Min(byte.MaxValue, extraEntries.Count));
            for (var index = 0; index < extraEntries.Count && index < byte.MaxValue; index++)
            {
                var extra = extraEntries[index];
                writer.WriteUInt16(extra.EventId);
                var parameters = extra.Parameters ?? Array.Empty<uint>();
                for (var parameterIndex = 0; parameterIndex < 12; parameterIndex++)
                {
                    writer.WriteUInt32(parameterIndex < parameters.Count
                        ? parameters[parameterIndex]
                        : 0);
                }
            }

            return writer.ToArray();
        }

        private static void WriteDstr(GamePacketWriter writer, string value)
        {
            writer.WriteDstr(ClientTextEncoding.GetBytes(value ?? string.Empty));
        }
    }
}
