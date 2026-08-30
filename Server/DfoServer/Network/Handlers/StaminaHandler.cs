using System;
using System.Threading.Tasks;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// 疲劳虚弱恢复。扣金币走在线背包，角色尾部状态仍写 subtype0 字段。
    /// </summary>
    public sealed class StaminaHandler
    {
        private const string ProtocolLogName = "GameProtocol";

        private readonly InventoryRefreshSender _refresh;
        private readonly IGameDatabase _database;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly DevilContractUsagePolicy _devilContractUsage;

        public StaminaHandler(
            InventoryRefreshSender refresh = null,
            IGameDatabase database = null)
        {
            _refresh = refresh;
            _database = database ?? GameDatabase.CreateDefault();
            _subtype0Repository = new SqliteSubtype0FieldsRepository(_database);
            _devilContractUsage = new DevilContractUsagePolicy(_database);
        }

        public async Task Handle_ENUM_CMDPACKET_RECOVER_STAMINA(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: uid={session?.Player?.UserId ?? 0} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");

            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0)
                return;

            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                await SendRecoverStaminaErrorAsync(session, 4);
                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: online inventory missing cid={characterId}");
                return;
            }

            try
            {
                var tail = _subtype0Repository.Load(characterId) ?? session.Player.Subtype0Tail;
                if (tail == null || tail.Stamina == 0)
                {
                    await SendRecoverStaminaErrorAsync(session, 18);
                    FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: no weakness state cid={characterId}");
                    return;
                }

                var staminaBefore = tail.Stamina;
                var normalCost = CalculateRecoverStaminaGoldCost(
                    session.Player.Level,
                    staminaBefore);
                var cost = normalCost;
                var freeByContract = false;
                var updatedGold = 0;
                byte errorCode = 0;
                string rejectLog = null;

                var success = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "stamina-recover",
                    (connection, transaction) =>
                    {
                        freeByContract = _devilContractUsage.TryConsume(
                            connection,
                            transaction,
                            characterId,
                            session.Account?.AccountId ?? 0,
                            DevilContractUsagePolicy.WeaknessRecoverySlot);
                        cost = freeByContract ? 0 : normalCost;
                        var currentGold = lease.Inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart);
                        if (currentGold < cost)
                        {
                            errorCode = 22;
                            rejectLog = $"[{ProtocolLogName}] RECOVER_STAMINA: insufficient gold cid={characterId} need={cost} have={currentGold} stamina={staminaBefore}";
                            return false;
                        }

                        updatedGold = currentGold;
                        if (cost > 0)
                        {
                            if (!lease.Inventory.TryConsumeMainItem(
                                    InventoryService.MainVirtualCurrencySlotStart,
                                    cost,
                                    out var consumed)
                                || !consumed.Success)
                            {
                                errorCode = 22;
                                rejectLog = $"[{ProtocolLogName}] RECOVER_STAMINA: TrySpendGold refused cid={characterId} need={cost}";
                                return false;
                            }

                            updatedGold = consumed.RemainingCount;
                        }

                        if (!SqliteSubtype0FieldsRepository.ResetStaminaInTransaction(
                                connection,
                                transaction,
                                characterId))
                        {
                            rejectLog = $"[{ProtocolLogName}] RECOVER_STAMINA: subtype0 reset failed cid={characterId}";
                            return false;
                        }

                        return true;
                    });

                if (!success)
                {
                    FileLogger.Log(rejectLog ?? $"[{ProtocolLogName}] RECOVER_STAMINA: transaction failed cid={characterId}");
                    await SendRecoverStaminaErrorAsync(session, errorCode == 0 ? (byte)4 : errorCode);
                    return;
                }

                if (session.Player.Subtype0Tail != null)
                {
                    session.Player.Subtype0Tail.Stamina = 0;
                    session.Player.Subtype0Tail.FatiguePenalty = 0;
                }

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0021, new[] { (byte)100 }));
                if (_refresh != null)
                    await _refresh.SendGoldUpdate(session);
                if (freeByContract)
                {
                    await PremiumService.SendPremiumServiceRefresh(
                        session,
                        session.Account?.AccountId ?? 0,
                        _database);
                }

                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA: success cid={characterId} cost={cost} freeContract={freeByContract} gold={updatedGold}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolLogName}] RECOVER_STAMINA ERROR: cid={characterId} {ex}");
                await SendRecoverStaminaErrorAsync(session, 4);
            }
        }

        internal static int CalculateRecoverStaminaGoldCost(byte level, byte stamina)
        {
            if (stamina == 0)
                return 0;

            var basePrice = RecoverStaminaPriceProvider.GetBasePrice(level);
            var normalizedStamina = Math.Min((byte)10, stamina);
            var officialCurrentStamina = Math.Max(0, 100 - normalizedStamina * 9);
            var cost = basePrice * (100 - officialCurrentStamina) / 90;
            return Math.Max(0, cost);
        }

        private static Task SendRecoverStaminaErrorAsync(EnhancedClientSession session, byte errorCode)
        {
            if (session == null || session.TcpClient == null || !session.TcpClient.Connected)
                return Task.CompletedTask;

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0009, new[] { (byte)0, errorCode, (byte)0 }));
        }
    }
}
