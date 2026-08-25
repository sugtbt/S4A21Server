using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Shop;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.CeraShop;
using DfoServer.Network.Parsers.CeraShop;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class CeraShopHandler
    {
        private readonly SqliteSelectCharacterDataSource _sqliteSelectCharacterDataSource;
        private readonly InventoryRefreshSender _refresh;
        private readonly IGameDatabase _database;
        private readonly MailboxInventoryOverflowRewardSink _overflowRewardSink;

        public string ProtocolName => "GameProtocol";

        public CeraShopHandler(
            SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource,
            InventoryRefreshSender refresh,
            IGameDatabase database = null)
            : this(
                sqliteSelectCharacterDataSource,
                refresh,
                database,
                overflowRewardSink: null)
        {
        }

        internal CeraShopHandler(
            SqliteSelectCharacterDataSource sqliteSelectCharacterDataSource,
            InventoryRefreshSender refresh,
            IGameDatabase database,
            MailboxInventoryOverflowRewardSink overflowRewardSink)
        {
            _sqliteSelectCharacterDataSource = sqliteSelectCharacterDataSource ?? throw new ArgumentNullException(nameof(sqliteSelectCharacterDataSource));
            _refresh = refresh;
            _database = database ?? GameDatabase.CreateDefault();
            _overflowRewardSink = overflowRewardSink;
        }

        public Task HandleGenCeraTicket(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            // 从台服借来的数据包 `Dispatcher_GenCeraTicket::dispatch_sig`
            // 只能防止客户端卡住，同时还有一个副作用，会让鼠标消失，只要将鼠标移动到邮箱等会让指针变化的地方就会再出现
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            var now = DateTime.UtcNow;
            var ts = new DateTimeOffset(now).ToUnixTimeSeconds();
            var s = $"1234{ts:D10}00000";
            writer.WriteClientDstr(s);
            writer.WriteUInt32(123456);
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, writer.ToArray()));
        }


        public async Task HandleCeraShopPurchase(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY raw body({body?.Length ?? 0}): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!CeraShopPurchaseRequest.TryParse(body, out var request))
            {
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: parse failed");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0040, CeraShopPurchaseAckBuilder.BuildError()));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY parsed: {request.CommodityNos.Count} item(s) [{string.Join(", ", request.CommodityNos)}] paymentMode={request.PaymentMode} coupon={(request.CouponSelected ? $"0x{request.CouponItemId:X8}@{request.CouponSlot}" : "none")}");
            var cid = session.Player?.CharacterId ?? 0;
            var aid = session.Account?.AccountId ?? 0;
            if (cid <= 0 || aid <= 0)
            {
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: invalid owner cid={cid} aid={aid}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0040, CeraShopPurchaseAckBuilder.BuildError(request)));
                return;
            }

            if (!InventoryContext.TryGetLease(cid, out var lease) || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: online inventory missing cid={cid} aid={aid}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0040, CeraShopPurchaseAckBuilder.BuildError(request)));
                return;
            }

            var results = new List<InventoryMutationResult>();
            var successItems = new List<Tuple<int, InventoryMutationResult>>();
            var contractItems = new List<(int itemTemplateId, int count)>();
            var skillTreeExpansionUnlocked = false;
            var failure = CeraShopPurchaseFailure.Unknown;

            if (request.CommodityNos.Count == 1
                && CeraShopProductCatalog.TryResolve(
                    request.CommodityNos[0],
                    out var singleProduct)
                && singleProduct != null
                && !Game.Premium.PremiumService.IsContractItem(singleProduct.ItemTemplateId))
            {
                var attrValue = request.AttributeValues.Count > 0
                    ? request.AttributeValues[0]
                    : (byte)0;
                var itemOptions = request.ItemOptions.Count > 0
                    ? request.ItemOptions[0]
                    : null;
                if (!CeraShopRuntimePurchaseCommitService.TryPurchase(
                        lease,
                        aid,
                        request.CommodityNos[0],
                        request.PaymentMode,
                        attrValue,
                        request.CouponSelected ? request.CouponItemId : 0,
                        request.CouponSelected ? request.CouponSlot : (short)-1,
                        itemOptions,
                        _overflowRewardSink,
                        out var singleResult,
                        out var singleFailure))
                {
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x01,
                        0x0040,
                        CeraShopPurchaseAckBuilder.BuildError(
                            ResolvePurchaseErrorCode(singleFailure),
                            request)));
                    return;
                }

                results.Add(singleResult);
                successItems.Add(Tuple.Create(request.CommodityNos[0], singleResult));
                TrackConsumedSpecialItems(
                    singleResult,
                    contractItems,
                    ref skillTreeExpansionUnlocked);
            }

            for (var idx = results.Count > 0 ? request.CommodityNos.Count : 0; idx < request.CommodityNos.Count; idx++)
            {
                var commodityNo = request.CommodityNos[idx];
                var attrValue = idx < request.AttributeValues.Count ? request.AttributeValues[idx] : (byte)0;
                var itemOptions = idx < request.ItemOptions.Count ? request.ItemOptions[idx] : null;

                var (dcOk, dcResult) = await Game.Premium.PremiumService.TryBuyDevilContract(
                    session,
                    commodityNo,
                    request.PaymentMode,
                    request.CouponSelected,
                    _sqliteSelectCharacterDataSource,
                    _database);
                if (dcOk)
                {
                    successItems.Add(Tuple.Create(commodityNo, dcResult));
                    results.Add(dcResult);
                    continue;
                }

                InventoryMutationResult result;
                CeraShopPurchaseFailure itemFailure;
                var ok = CeraShopRuntimePurchaseCommitService.TryPurchase(
                    lease,
                    aid,
                    commodityNo,
                    request.PaymentMode,
                    attrValue,
                    request.CouponSelected ? request.CouponItemId : 0,
                    request.CouponSelected ? request.CouponSlot : (short)-1,
                    itemOptions,
                    _overflowRewardSink,
                    out result,
                    out itemFailure);

                if (ok && result != null)
                {
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: OK commodityNo={commodityNo} slot={result.SlotIndex} item=0x{result.ItemTemplateId:X8} count={result.AppliedCount} coin={result.UpdatedCoin} extra={result.ExtraResults.Count}");
                    results.Add(result);
                    successItems.Add(Tuple.Create(commodityNo, result));
                    TrackConsumedSpecialItems(result, contractItems, ref skillTreeExpansionUnlocked);
                }
                else
                {
                    if (itemFailure == CeraShopPurchaseFailure.InsufficientCera)
                        failure = itemFailure;
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: FAILED commodityNo={commodityNo} avatarChoices={itemOptions?.AvatarChoices.Count ?? 0} selections={itemOptions?.SelectionChoices.Count ?? 0}");
                }
            }

            if (results.Count == 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0040,
                    CeraShopPurchaseAckBuilder.BuildError(ResolvePurchaseErrorCode(failure), request)));
                return;
            }

            var last = results[results.Count - 1];
            foreach (var item in successItems)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x0040,
                    CeraShopPurchaseAckBuilder.BuildSuccess(item.Item1, item.Item2)));
            }

            var refreshSlots = new Dictionary<InventoryListType, HashSet<short>>();
            var refreshAccountCargo = false;
            var refreshPersonalCargo = false;
            var mailboxAlarmNeeded = false;
            foreach (var result in results)
            {
                foreach (var updateResult in EnumerateResultGroup(result))
                {
                    if (updateResult.DeliveredByMail)
                        mailboxAlarmNeeded = true;

                    if (updateResult.ConsumedOnPurchase)
                    {
                        if (updateResult.ListType == InventoryListType.AccountCargo)
                        {
                            SyncOnlineCargoCapacity(session, cid, updateResult);
                            refreshAccountCargo = true;
                        }
                        else if (updateResult.ListType == InventoryListType.PersonalCargo)
                        {
                            SyncOnlineCargoCapacity(session, cid, updateResult);
                            refreshPersonalCargo = true;
                        }
                        continue;
                    }

                    QueueItemListRefresh(refreshSlots, updateResult.ListType, updateResult.SlotIndex);
                }

                if (result.GoldSpent)
                {
                    QueueItemListRefresh(refreshSlots, InventoryListType.Main, InventoryService.MainVirtualCurrencySlotStart);
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: gold refresh queued gold={result.UpdatedGold}");
                }
            }

            if (refreshAccountCargo)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0132,
                    CommonPacketBodyBuilder.BuildSuccessAck()));
                await SendItemListRefresh(session, cid, aid, InventoryListType.AccountCargo);
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: account cargo upgrade ACK and ITEM_LIST refresh sent");
            }

            if (refreshPersonalCargo)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0198,
                    CommonPacketBodyBuilder.BuildSuccessAck()));
                await SendItemListRefresh(session, cid, aid, InventoryListType.PersonalCargo);
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: personal cargo upgrade ACK and ITEM_LIST refresh sent");
            }

            await SendQueuedItemListUpdates(session, refreshSlots);

            if (mailboxAlarmNeeded)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.MAILBOX_ALARM,
                    MailboxHandler.BuildMailboxAlarmNotification(1)));
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: mailbox alarm sent for overflow rewards");
            }

            if (_refresh != null && results.Exists(r => r.NameTagEquipped))
            {
                _refresh.ReloadSubtype0Tail(session);
                await _refresh.SendNoti2AppearanceUpdate(session);
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: name tag appearance refresh sent");
            }

            if (contractItems.Count > 0)
                await Game.Premium.PremiumService.ActivateAndNotify(
                    session,
                    contractItems,
                    _sqliteSelectCharacterDataSource,
                    _database);

            if (skillTreeExpansionUnlocked)
            {
                var refreshed = _sqliteSelectCharacterDataSource.Load(cid, aid);
                var record = refreshed?.CharacterRecord;
                if (record != null)
                {
                    session.Player.Subtype0Tail = record.Subtype0Tail;
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x0002,
                        UserInfoSubtype0Builder.BuildNotificationBody(record)));
                    FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: skill-tree expansion USERINFO subtype0 refresh sent cid={cid}");
                }
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0035,
                CeraUpdateBuilder.Build(last.UpdatedCoin, last.UpdatedTokenCera, last.UpdatedHappyTokenCera)));
        }

        private async Task SendItemListRefresh(
            EnhancedClientSession session,
            int characterId,
            int accountId,
            InventoryListType listType)
        {
            if (_refresh != null)
            {
                await _refresh.SendItemListRefresh(session, listType);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x000D,
                ItemListPacketBuilder.BuildBody(characterId, accountId, listType)));
        }

        private static byte ResolvePurchaseErrorCode(CeraShopPurchaseFailure failure)
        {
            return failure == CeraShopPurchaseFailure.InsufficientCera
                ? CeraShopPurchaseAckBuilder.ErrorCodeInsufficientCera
                : CeraShopPurchaseAckBuilder.ErrorCodeInventoryFull;
        }

        private async Task SendQueuedItemListUpdates(
            EnhancedClientSession session,
            IReadOnlyDictionary<InventoryListType, HashSet<short>> refreshSlots)
        {
            if (_refresh == null || refreshSlots == null)
                return;

            foreach (var listType in GetItemListRefreshOrder())
            {
                if (!refreshSlots.TryGetValue(listType, out var slots) || slots.Count == 0)
                    continue;

                await _refresh.SendUpdateItemList(session, listType, slots);
                FileLogger.Log($"[{ProtocolName}] CERA_SHOP_BUY: ITEM_LIST update sent list={listType} count={slots.Count}");
            }
        }

        private static void QueueItemListRefresh(
            Dictionary<InventoryListType, HashSet<short>> refreshSlots,
            InventoryListType listType,
            short slotIndex)
        {
            if (refreshSlots == null || slotIndex < 0)
                return;

            if (!refreshSlots.TryGetValue(listType, out var slots))
            {
                slots = new HashSet<short>();
                refreshSlots[listType] = slots;
            }

            slots.Add(slotIndex);
        }

        private static IEnumerable<InventoryListType> GetItemListRefreshOrder()
        {
            yield return InventoryListType.Main;
            yield return InventoryListType.Avatar;
            yield return InventoryListType.Pet;
            yield return InventoryListType.Equipment;
            yield return InventoryListType.PersonalCargo;
            yield return InventoryListType.AccountCargo;
        }

        private static void TrackConsumedSpecialItems(
            InventoryMutationResult result,
            List<(int itemTemplateId, int count)> contractItems,
            ref bool skillTreeExpansionUnlocked)
        {
            foreach (var item in EnumerateResultGroup(result))
            {
                if (item.ConsumedOnPurchase
                    && item.ItemTemplateId == Game.Skills.SkillTreeExpansionState.ExpansionItemTemplateId)
                    skillTreeExpansionUnlocked = true;

                if (Game.Premium.PremiumService.IsContractItem(item.ItemTemplateId))
                    contractItems.Add((item.ItemTemplateId, Math.Max(1, (int)item.AppliedCount)));
            }
        }

        private static IEnumerable<InventoryMutationResult> EnumerateResultGroup(InventoryMutationResult result)
        {
            if (result == null)
                yield break;

            yield return result;
            foreach (var extra in result.ExtraResults)
            {
                if (extra != null)
                    yield return extra;
            }
        }

        private static void SyncOnlineCargoCapacity(
            EnhancedClientSession session,
            int characterId,
            InventoryMutationResult update)
        {
            if (session == null || update == null || characterId <= 0)
                return;

            if (update.ListType != InventoryListType.AccountCargo
                && update.ListType != InventoryListType.PersonalCargo)
                return;

            var capacity = update.RemainingStackCount > 0
                ? update.RemainingStackCount
                : update.InstanceValue;
            if (capacity <= 0 || capacity > ushort.MaxValue)
                return;

            if (!InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
                return;

            lock (lease.SyncRoot)
                lease.Inventory.SetListParam16(update.ListType, (ushort)capacity);
        }
    }
}
