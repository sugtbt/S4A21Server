using DfoServer.Game.ExpertJob;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public sealed class ExpertJobInfoBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x00CD;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            body = BuildBody(snapshot.InitializationSnapshot.ExpertJobInfo);
            return true;
        }

        internal static byte[] BuildProjectedBody(
            int expertJobType,
            ExpertJobState state,
            uint expertJobExperience)
        {
            var info = new ExpertJobInfoSnapshot();
            ExpertJobStateCodec.ProjectToSnapshot(
                expertJobType,
                state,
                info,
                expertJobExperience);
            return BuildBody(info);
        }

        internal static byte[] BuildBody(ExpertJobInfoSnapshot info)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(info.State0);
            writer.WriteByte(info.Mode);

            if (info.Mode == 1 || info.Mode == 2 || info.Mode == 4)
            {
                var entryCount = System.Math.Min(byte.MaxValue, info.Entries.Count);
                writer.WriteByte((byte)entryCount);
                for (var index = 0; index < entryCount; index++)
                    writer.WriteInt32(info.Entries[index]);

                if (info.Mode == 1)
                {
                    var qualificationCount = System.Math.Min(
                        byte.MaxValue,
                        info.CardQualificationLevels.Count);
                    writer.WriteByte((byte)qualificationCount);
                    for (var index = 0; index < qualificationCount; index++)
                        writer.WriteByte(info.CardQualificationLevels[index]);
                    writer.WriteInt32(info.EnchanterLevel);
                    writer.WriteInt32(info.EnchanterEndurance);
                }
            }
            else if (info.Mode == 3)
            {
                writer.WriteInt32(info.DisjointMachineGrade);
                writer.WriteInt32(info.DisjointMachineEndurance);
            }

            return writer.ToArray();
        }
    }
}
