using System;
using System.Threading.Tasks;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// 租赁商店购买幸运星。金币走在线背包 slot0，幸运星仍是账号级货币。
    /// </summary>
    public sealed class LuckyStarHandler
    {
        private readonly SqliteSelectCharacterDataSource _dataSource;
        private readonly IRentalTimeProvider _rentalTimeProvider;
        private readonly InventoryRefreshSender _refresh;

        public LuckyStarHandler(
            SqliteSelectCharacterDataSource dataSource,
            IRentalTimeProvider rentalTimeProvider = null,
            InventoryRefreshSender refresh = null,
            IGameDatabase database = null)
        {
            _dataSource = dataSource;
            _rentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
            _refresh = refresh;
        }

        public async Task HandleShopPurchasePacket(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, _) = InventoryHandler.ResolveOwner(session);
            if (characterId <= 0 || body == null || body.Length < RentalCatalogCodec.ChargeRentPointRequestSize)
                return;

            if (!RentalCatalogCodec.TryParseShopPacketBuyCount(body, out var buyCount))
            {
                FileLogger.Log($"[LuckyStar] REJECT CHARGE_RENTPOINT char={characterId} invalid qty bodyLen={body.Length} tail={BitConverter.ToString(body, Math.Max(0, body.Length - 8))}");
                await SendChargeRentPointError(session);
                return;
            }

            await ExecuteLuckyStarPurchase(session, buyCount, body);
        }

        private async Task ExecuteLuckyStarPurchase(EnhancedClientSession session, int buyCount, byte[] purchaseRequestBody)
        {
            var (characterId, accountId) = InventoryHandler.ResolveOwner(session);
            if (characterId <= 0 || accountId <= 0)
                return;

            FileLogger.Log($"[LuckyStar] BUY request: char={characterId} buyCount={buyCount} via=CHARGE_RENTPOINT");
            var totalGoldCost = RentalCatalogCodec.GoldCostPerStar * buyCount;

            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[LuckyStar] BUY: online inventory missing char={characterId}");
                await SendChargeRentPointError(session);
                return;
            }

            var newGold = 0;
            var newLuckyStar = (ushort)0;
            string rejectLog = null;

            var success = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "lucky-star-purchase",
                (connection, transaction) =>
                {
                    var wallet = CurrencyService.LoadWallet(connection, transaction, characterId);
                    if (wallet.LuckyStar + buyCount > RentalCatalogCodec.MaxLuckyStar)
                    {
                        rejectLog = $"[LuckyStar] BUY: star limit exceeded have={wallet.LuckyStar} add={buyCount} char={characterId}";
                        return false;
                    }

                    var currentGold = lease.Inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart);
                    if (currentGold < totalGoldCost)
                    {
                        rejectLog = $"[LuckyStar] BUY: insufficient gold need={totalGoldCost} have={currentGold} char={characterId}";
                        return false;
                    }

                    if (totalGoldCost > 0
                        && (!lease.Inventory.TryConsumeMainItem(
                                InventoryService.MainVirtualCurrencySlotStart,
                                totalGoldCost,
                                out var consumed)
                            || !consumed.Success))
                    {
                        rejectLog = $"[LuckyStar] BUY: spend gold refused need={totalGoldCost} char={characterId}";
                        return false;
                    }

                    newGold = lease.Inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart);
                    newLuckyStar = (ushort)(wallet.LuckyStar + buyCount);
                    CurrencyService.GrantLuckyStar(connection, transaction, accountId, buyCount);
                    return true;
                });

            if (!success)
            {
                if (!string.IsNullOrEmpty(rejectLog))
                    FileLogger.Log(rejectLog);
                await SendChargeRentPointError(session);
                return;
            }

            FileLogger.Log($"[LuckyStar] BUY: char={characterId} count={buyCount} gold=-{totalGoldCost} -> {newGold} stars={newLuckyStar}");

            await LuckyStarClientNotifier.SyncPurchaseAsync(
                session,
                characterId,
                (ushort)buyCount,
                newLuckyStar,
                _rentalTimeProvider,
                purchaseRequestBody);
            if (_refresh != null)
                await _refresh.SendGoldUpdate(session);
        }

        private static async Task SendChargeRentPointError(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.CHARGE_RENTPOINT,
                new byte[] { 0x00 }));
        }
    }
}
