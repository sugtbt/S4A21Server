using DfoServer.Game.Premium;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class PremiumServiceInitBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.PREMIUM_SERVICE;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex,
            out byte[] body)
        {
            var init = snapshot?.InitializationSnapshot;
            if (occurrenceIndex != 0 || init?.PremiumServiceData == null)
            {
                body = null;
                return false;
            }

            body = PremiumService.BuildPremiumServiceStateBody(
                init.PremiumServiceType,
                init.PremiumServiceData);
            return true;
        }
    }
}
