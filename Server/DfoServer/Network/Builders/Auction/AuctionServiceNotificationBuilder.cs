using System;
using DfoServer.Network;

namespace DfoServer.Network.Builders.Auction
{
    public static class AuctionServiceNotificationBuilder
    {
        // Client handler 0x01189FB0 reads `u8 serviceType, u8 state`:
        //   cmp byte[ebp-1], 1
        //   jne 0x118A02F   -> the "service unavailable" branch (dstr 0xa490/0xa491)
        // So serviceType MUST be 1 for the auction / gold-consignment window to
        // open; the second byte selects the available-state flavour.
        private const byte ServiceType0 = 0x00;
        private const byte ServiceType1 = 0x01;
        private const byte OpenState = 0x01;
        private const byte ClosedState = 0x00;

        /// <summary>
        /// Whether gold consignment (金币寄售) is exposed. Deliberately a separate
        /// switch from AUCTION_ENABLED: consignment is its own feature, not a
        /// sub-feature of the ordinary gold auction.
        /// </summary>
        internal static bool GoldConsignEnabled =>
            Environment.GetEnvironmentVariable("GOLD_CONSIGN_ENABLED") == "1";

        /// <summary>
        /// Builds the service-state notifications sent on character select.
        /// <paramref name="enabled"/> controls only the state byte; serviceType 1
        /// is always emitted because the client requires it to leave the
        /// "service closed" branch.
        /// </summary>
        public static byte[][] BuildPackets(bool enabled)
        {
            var state = enabled ? OpenState : ClosedState;
            return new[]
            {
                BuildPacket(ServiceType1, state)
            };
        }

        /// <summary>Legacy open-only helper kept for callers that expect both types.</summary>
        public static byte[][] BuildOpenPackets()
        {
            return new[]
            {
                BuildPacket(ServiceType0, OpenState),
                BuildPacket(ServiceType1, OpenState)
            };
        }

        private static byte[] BuildPacket(byte serviceType, byte state)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(serviceType);
            writer.WriteByte(state);
            return GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.AUCTION_NOTIFY_AUCTION_SERVICE,
                writer.ToArray());
        }
    }
}
