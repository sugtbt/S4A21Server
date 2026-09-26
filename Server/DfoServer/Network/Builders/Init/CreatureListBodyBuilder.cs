using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class CreatureListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0069;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var list = snapshot.InitializationSnapshot.CreatureItemList;
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)list.Entries.Count);
            foreach (var entry in list.Entries)
                WriteCreatureEntry(writer, entry);

            body = writer.ToArray();
            return true;
        }

        internal static void WriteCreatureEntry(GamePacketWriter writer, CreatureItemEntrySnapshot entry)
        {
            writer.WriteInt32(entry.CreatureKey);
            writer.WriteByte(entry.Field04);
            writer.WriteByte(entry.ModeFlag);
            writer.WriteInt32(entry.ProgressValue32);
            if (entry.ModeFlag == 1)
            {
                writer.WriteByte(entry.Mode1Field0A);
                writer.WriteByte(entry.Mode1Field0B);
            }
            writer.WriteByte(entry.FieldAfterValue32);
            writer.WriteDstr(entry.CreatureTextBytes);
            writer.WriteByte(entry.TailFlag);
        }
    }
}
