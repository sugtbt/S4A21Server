using System;

namespace DfoServer.Network.Parsers.Party
{
    // A21 SET_PARTY_INFO (0x000C) has two captured request shapes:
    //   create: 12 direct settings bytes;
    //   edit:   2 leading settings bytes + raw-dstr title + 10 trailing bytes.
    // Normalize both to the same 12-byte settings block consumed by
    // PARTY_INFO(type 0/1); keep the title separate.
    public sealed class SetPartyInfoRequest
    {
        public byte TitleIndex { get; set; }
        public byte[] Title { get; set; } = Array.Empty<byte>();
        public byte UserMax { get; set; }
        public ushort DungIndex { get; set; }
        public byte DungDiffi { get; set; }
        public byte[] Raw { get; set; } = Array.Empty<byte>();

        public static bool TryParse(byte[] body, out SetPartyInfoRequest req)
        {
            req = new SetPartyInfoRequest();
            if (body == null)
                return false;

            if (body.Length == 12)
            {
                req.Raw = (byte[])body.Clone();
                req.TitleIndex = body[1];
                req.UserMax = body[2];
                return true;
            }

            // Captured edit sample:
            // 01-00-0C-00-00-00-<12B UTF-8 title>-<10B settings tail>.
            if (body.Length < 16 || body[0] > 2 || body[1] != 0)
                return false;
            var titleLength = BitConverter.ToInt32(body, 2);
            if (titleLength < 0 || body.Length != 16 + titleLength)
                return false;

            req.Title = new byte[titleLength];
            if (titleLength > 0)
                Buffer.BlockCopy(body, 6, req.Title, 0, titleLength);
            req.Raw = new byte[12];
            req.Raw[0] = body[0];
            req.Raw[1] = body[1];
            Buffer.BlockCopy(
                body,
                6 + titleLength,
                req.Raw,
                2,
                10);
            req.TitleIndex = body[1];
            req.UserMax = req.Raw[2];
            return true;
        }
    }
}
