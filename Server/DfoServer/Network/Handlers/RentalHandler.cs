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
    /// 租赁商店租赁武器。幸运星扣减直接写账号状态，物品写在线背包。
    /// </summary>
    public sealed class RentalHandler
    {
        public const ushort CommandType = (ushort)CmdPacketTypeA21.RENT_EQUIPMENT_ITEM;

        private readonly IRentalTimeProvider _rentalTimeProvider;
        private readonly SqliteSelectCharacterDataSource _dataSource;
        private readonly InventoryRefreshSender _refresh;

        public RentalHandler(
            SqliteSelectCharacterDataSource dataSource,
            IRentalTimeProvider rentalTimeProvider = null,
            InventoryRefreshSender refresh = null,
            IGameDatabase database = null)
        {
            _dataSource = dataSource;
            _rentalTimeProvider = rentalTimeProvider ?? SystemRentalTimeProvider.Instance;
            _refresh = refresh;
        }

        public async Task HandleRentWeapon(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, accountId) = InventoryHandler.ResolveOwner(session);
            if (characterId <= 0 || accountId <= 0)
                return;

            if (!RentalWeaponRequestCodec.TryParse(body, out var inventoryTemplateId, out var clientContext, out var starCost))
            {
                var tail = body == null || body.Length == 0
                    ? string.Empty
                    : BitConverter.ToString(body, Math.Max(0, body.Length - 8));
                var head = body == null || body.Length == 0
                    ? string.Empty
                    : BitConverter.ToString(body, 0, Math.Min(RentalWeaponRequestCodec.ItemTemplateOffset, body.Length));
                var detail = RentalWeaponRequestCodec.DescribeParseFailure(body);
                FileLogger.Log($"[Rental] REJECT 0x{CommandType:X4} char={characterId} parse failed bodyLen={body?.Length ?? 0} head={head} tail={tail} detail={detail}");
                await SendFailure(session);
                return;
            }

            FileLogger.Log($"[Rental] RENT_WEAPON request char={characterId} item=0x{inventoryTemplateId:X8} clientContext=0x{clientContext:X8} cost={starCost}");

            if (!InventoryContext.TryGetLease(characterId, out var lease) || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[Rental] RENT_WEAPON: online inventory missing item=0x{inventoryTemplateId:X8} char={characterId}");
                await SendFailure(session);
                return;
            }

            var inventoryTemplateIdValue = (int)inventoryTemplateId;
            var expireTime = (int)ResolveRentalExpireTime();
            var luckyStar = (ushort)0;
            InventoryMutationResult rentResult = null;
            string rejectLog = null;
            var failureResult = 0u;

            var success = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "rental-weapon-purchase",
                (connection, transaction) =>
                {
                    if (!InventoryShopRuntimeService.CanRentWeapon(lease.Inventory, inventoryTemplateIdValue))
                    {
                        failureResult = RentalWeaponPacketBuilder.InventoryFullResult;
                        rejectLog = $"[Rental] RENT_WEAPON: plan FAILED (inventory full or invalid) item=0x{inventoryTemplateId:X8} char={characterId}";
                        return false;
                    }

                    var currentLuckyStar = CurrencyService.LoadWallet(connection, transaction, characterId).LuckyStar;
                    if (!CurrencyService.TrySpendLuckyStar(connection, transaction, accountId, starCost))
                    {
                        rejectLog = $"[Rental] RENT_WEAPON: insufficient stars need={starCost} have={currentLuckyStar} char={characterId}";
                        return false;
                    }

                    luckyStar = (ushort)Math.Max(0, currentLuckyStar - starCost);

                    if (!InventoryShopRuntimeService.TryRentWeapon(
                            lease.Inventory,
                            inventoryTemplateIdValue,
                            expireTime,
                            out rentResult,
                            connection,
                            transaction))
                    {
                        rejectLog = $"[Rental] RENT_WEAPON: apply FAILED item=0x{inventoryTemplateId:X8} char={characterId}";
                        return false;
                    }

                    return true;
                });

            if (!success || rentResult == null)
            {
                FileLogger.Log(rejectLog ?? $"[Rental] RENT_WEAPON: failed item=0x{inventoryTemplateId:X8} char={characterId}");
                await SendFailure(session, failureResult);
                return;
            }

            FileLogger.Log($"[Rental] RENT_WEAPON: added/refreshed item=0x{inventoryTemplateId:X8} list={rentResult.ListType} slot={rentResult.SlotIndex} char={characterId}");
            FileLogger.Log($"[Rental] RENT_WEAPON: char={characterId} item=0x{inventoryTemplateId:X8} cost={starCost} starsLeft={luckyStar} expire={expireTime}");

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, CommandType, RentalWeaponPacketBuilder.BuildSuccessAck()));
            await RentalInfoPanelNotifier.SyncAsync(session, characterId, luckyStar, _rentalTimeProvider);

            if (_refresh != null && rentResult.SlotIndex >= 0)
                await _refresh.SendUpdateItemList(session, rentResult.ListType, rentResult.SlotIndex);
        }

        private static async Task SendFailure(EnhancedClientSession session, uint result = 0)
        {
            var body = result == 0
                ? RentalWeaponPacketBuilder.BuildFailureAck()
                : RentalWeaponPacketBuilder.BuildResultAck(result);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, CommandType, body));
        }

        private uint ResolveRentalExpireTime()
        {
            return _rentalTimeProvider.UtcNowUnixSeconds() + (uint)RentalWeaponRequestCodec.RentalDurationSeconds;
        }
    }
}
