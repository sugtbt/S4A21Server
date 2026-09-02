using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal sealed class LicensedDungeonPlayResultRequest
    {
        internal const int BusinessWireLength = 11;
        internal const int CapturedWireLength = 16;

        private LicensedDungeonPlayResultRequest(byte[] body)
        {
            Body = body;
        }

        // The official capture carries a 16-byte body. The first 11 bytes are
        // the currently known command fields; the final 5 bytes are retained
        // as opaque transport data until a field-level capture proves them.
        internal byte[] Body { get; }

        internal static bool TryParse(
            byte[] body,
            out LicensedDungeonPlayResultRequest request,
            out string failureReason)
        {
            request = null;
            failureReason = string.Empty;
            if (body == null
                || (body.Length != BusinessWireLength
                    && body.Length != CapturedWireLength))
            {
                failureReason =
                    $"licensed play-result body must be {BusinessWireLength} "
                    + $"or {CapturedWireLength} bytes";
                return false;
            }

            var capturedBody = new byte[body.Length];
            Buffer.BlockCopy(body, 0, capturedBody, 0, body.Length);
            request = new LicensedDungeonPlayResultRequest(capturedBody);
            return true;
        }
    }
}
