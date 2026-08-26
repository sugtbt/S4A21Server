using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal static class LuckyStarClientNotifier
    {
        // Shop purchases need the reward ACK plus a rental panel refresh.
        internal static async Task SyncPurchaseAsync(
            EnhancedClientSession session,
            int characterId,
            ushort changeCount,
            ushort totalLuckyStar,
            IRentalTimeProvider rentalTimeProvider,
            byte[] requestBody = null)
        {
            if (session == null || characterId <= 0 || changeCount == 0)
                return;

            await NotifyRewardAsync(session, characterId, changeCount, totalLuckyStar, rentalTimeProvider, requestBody);
        }

        // Non-shop rewards only need the reward ACK and lucky-star panel refresh.
        internal static async Task NotifyRewardAsync(
            EnhancedClientSession session,
            int characterId,
            ushort changeCount,
            ushort totalLuckyStar,
            IRentalTimeProvider rentalTimeProvider,
            byte[] requestBody = null)
        {
            if (session == null || characterId <= 0 || changeCount == 0)
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.CHARGE_RENTPOINT,
                BuildChargeRentPointSuccessBody(changeCount, totalLuckyStar, requestBody)));
            await RentalInfoPanelNotifier.SyncAsync(session, characterId, totalLuckyStar, rentalTimeProvider);
        }

        internal static byte[] BuildChargeRentPointSuccessBody(
            ushort changeCount,
            ushort totalLuckyStar,
            byte[] requestBody)
        {
            var mode = 2;
            var resultOrQuantity = 0;
            if (requestBody != null
                && requestBody.Length >= RentalCatalogCodec.ChargeRentPointRequestSize)
            {
                mode = BitConverter.ToInt32(
                    requestBody,
                    RentalCatalogCodec.ChargeRentPointModeOffset);
                resultOrQuantity = BitConverter.ToInt32(
                    requestBody,
                    RentalCatalogCodec.ChargeRentPointQuantityOffset);
            }

            var body = new byte[13];
            body[0] = 0x01;
            Buffer.BlockCopy(BitConverter.GetBytes(mode), 0, body, 1, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(resultOrQuantity), 0, body, 5, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((int)totalLuckyStar), 0, body, 9, 4);
            return body;
        }
    }
}
