using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class GoldLimitHandler
    {
        private readonly CharacterGoldLimitRepository _repository;
        private readonly InventoryRefreshSender _refresh;

        public GoldLimitHandler(CharacterGoldLimitRepository repository, InventoryRefreshSender refresh = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _refresh = refresh;
        }

        public async Task HandleUpgradeAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0)
                return;

            var result = _repository.TryUpgrade(characterId);
            FileLogger.Log(
                $"[GoldLimit] upgrade cid={characterId} status={result.Status} " +
                $"level={result.Limits?.UpgradeLevel ?? 0} carry={result.Limits?.GoldCarryLimit ?? 0} " +
                $"auction={result.Limits?.AuctionGoldLimit ?? 0} goldAfter={result.GoldAfter}");

            if (result.Limits != null)
            {
                // A21 upgrade notification: 0x039B UPGRADE_CARRY_GOLD.
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.UPGRADE_CARRY_GOLD,
                    new[] { result.Limits.UpgradeLevel }));
            }

            if (result.Status == GoldLimitUpgradeStatus.Success)
            {
                if (_refresh != null)
                    await _refresh.SendGoldUpdate(session, result.GoldAfter);
            }
        }
    }
}
