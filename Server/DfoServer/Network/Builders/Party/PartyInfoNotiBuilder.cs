using DfoServer.Network;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Network.Builders.Party
{
    // PARTY_INFO (NOTI 0x0009), matched to the target A21 client's unpacked
    // runtime parser at VA 0x01172000..0x011726B9:
    //   u16 blockCount; each block = u16 partyId + u8 type
    //   type 0/1: info0; when info0==0, an empty raw dstr; then info1..info11
    //   type 0/2: 8 slots of { u16 uid; u8; u8; u8 }, then three tail bytes
    //   type <=2: u8 hasExtra; zero means no following u32-pair records
    public static class PartyInfoNotiBuilder
    {
        public static byte[] Build(Game.Party.Party party, byte type)
        {
            if (party == null)
                throw new ArgumentNullException(nameof(party));

            var w = new GamePacketWriter();
            w.WriteUInt16(1);                       // 块计数(恒 1; 名册在单块内)
            WriteBlock(w, party, type);
            return w.ToArray();
        }

        public static byte[] BuildList(
            IReadOnlyList<Game.Party.Party> parties)
            => BuildList(parties, Array.Empty<int>());

        public static byte[] BuildList(
            IReadOnlyList<Game.Party.Party> parties,
            IReadOnlyList<int> removedPartyIds)
        {
            parties ??= Array.Empty<Game.Party.Party>();
            removedPartyIds ??= Array.Empty<int>();
            if ((long)parties.Count + removedPartyIds.Count > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(parties));

            var w = new GamePacketWriter();
            w.WriteUInt16((ushort)(parties.Count + removedPartyIds.Count));
            foreach (var partyId in removedPartyIds)
            {
                if (partyId <= 0 || partyId > ushort.MaxValue)
                    throw new ArgumentOutOfRangeException(
                        nameof(removedPartyIds));
                WriteBlock(w, new Game.Party.Party(partyId), 3);
            }
            foreach (var party in parties)
            {
                if (party == null)
                    throw new ArgumentException(
                        "Party list cannot contain null entries.",
                        nameof(parties));
                WriteBlock(w, party, 0);
            }
            return w.ToArray();
        }

        private static void WriteBlock(
            GamePacketWriter w,
            Game.Party.Party party,
            byte type)
        {
            w.WriteUInt16((ushort)party.PartyId);   // partyId
            w.WriteByte(type);

            if (type == 0 || type == 1)
            {
                var info = party.PartyInfoBlock;
                if (info == null || info.Length != 12)
                    info = new byte[] { 0, 0, 4, 0, 0, 0, 0, 5, 0, 0, 0xFF, 0xFF };

                w.WriteByte(info[0]);
                if (info[0] == 0)
                {
                    // Runtime VA 0x01172152..0x01172174 enters the zero-info0
                    // string branch and 0x0274FCB0 consumes a raw-dstr length.
                    w.WriteUInt32(0);
                }
                for (var i = 1; i < info.Length; i++)
                    w.WriteByte(info[i]);
            }

            if (type == 0 || type == 2)
            {
                var members = party.MembersBySlot();
                for (byte i = 0; i < 8; i++)
                {
                    var m = members.FirstOrDefault(x => x.SlotIndex == i);
                    w.WriteUInt16(m != null ? m.UserId : (ushort)0xFFFF);
                    w.WriteByte(0);
                    w.WriteByte(0);
                    w.WriteByte(0);
                }
                var leaderSlot = party.GetMember(party.LeaderUserId)
                    ?.SlotIndex ?? (byte)0;
                w.WriteByte(0);
                // A21 parser stores the second roster-tail byte as manager slot.
                w.WriteByte(leaderSlot);
                w.WriteByte(0);
            }

            if (type == 5)
                w.WriteByte(0);

            if (type <= 2)
                w.WriteByte(0);
        }
    }
}
