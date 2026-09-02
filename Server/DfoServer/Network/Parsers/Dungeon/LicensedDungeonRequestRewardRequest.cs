using System;

namespace DfoServer.Network.Parsers.Dungeon
{
    internal sealed class LicensedDungeonRequestRewardRequest
    {
        private LicensedDungeonRequestRewardRequest()
        {
        }

        internal static bool TryParse(
            byte[] body,
            out LicensedDungeonRequestRewardRequest request,
            out string failureReason)
        {
            request = null;
            failureReason = string.Empty;
            if (body != null && body.Length != 0)
            {
                failureReason = "licensed request-reward body must be empty";
                return false;
            }

            request = new LicensedDungeonRequestRewardRequest();
            return true;
        }
    }
}
