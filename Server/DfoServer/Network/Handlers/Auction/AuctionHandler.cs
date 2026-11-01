using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DfoServer.Game.Auction;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Parsers.Auction;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Handlers.Auction;

internal sealed class AuctionHandler
{
    private readonly AuctionRuntime runtime;
    private readonly CharacterTransitionCoordinator transitions;
    private readonly ConditionalWeakTable<EnhancedClientSession, AuctionNativeSessionState> states = new();
    internal AuctionHandler(AuctionRuntime runtime, CharacterTransitionCoordinator transitions)
    { this.runtime = runtime; this.transitions = transitions; }

    internal void Clear(EnhancedClientSession session) { if (session != null) states.Remove(session); }

    // Client handler 0x01189FB0 reads `u8 serviceType, u8 state` and requires
    // serviceType == 1 to open the window:  cmp byte[ebp-1],1 / jne closed-branch.
    // The legacy body {0, state} therefore always took the "service closed"
    // branch (dstr 0xa490/0xa491), which is what players saw as
    // "金币寄售系统暂时关闭".
    internal async Task SendService(EnhancedClientSession session)
    {
        if (!Current(session, out var lease)) return;
        var enabled = AuctionServiceNotificationBuilder.GoldConsignEnabled
            || runtime.EntryEnabled;
        foreach (var packet in AuctionServiceNotificationBuilder.BuildPackets(enabled))
        {
            await session.TrySendPacketAsync(packet, default,
                () => Current(session, out var current) && ReferenceEquals(current, lease));
        }
    }

    internal async Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
    {
        using var transition = await transitions.AcquireIfCurrentAsync(session);
        if (transition == null || !Current(session, out var lease)) return;
        var principal = new AuctionQueryPrincipal(session.SessionId, lease.Version, lease.AccountId, lease.CharacterId);
        if (!states.TryGetValue(session, out var state) || state.Principal != principal)
        {
            states.Remove(session);
            state = new(principal, runtime.Codec);
            states.Add(session, state);
        }
        var command = (CmdPacketTypeA21)header.type;
        Action afterSent = null;
        var rejection = IsMutationCommand(command)
            ? new MutationRejectionTrace((ushort)command, lease.CharacterId, lease.Version, body?.Length ?? -1) : null;
        await session.SendPreparedPacketBatchAsync(() => {
            if (!Current(session, out var owned) || !ReferenceEquals(owned, lease))
            {
                rejection?.Write(MutationRejectStage.RequestScope, "OwnedLeaseChangedBeforeReply");
                return Array.Empty<byte[]>();
            }
            lock (runtime.Gate)
            {
                try
                {
                    // ---- Gold consignment (金币寄售) ---------------------------
                    // Independent of the ordinary auction entry gate below: the
                    // consignment board must work even when AUCTION_ENABLED=0.
                    if (body != null && AuctionRequestParser.TryParse(command, body, out var consignRequest)
                        && consignRequest is GoldConsignAveragePriceRequest or GoldConsignRegisterRequest)
                    {
                        // Release escrow for listings whose persisted expiry has passed
                        // before answering, so a returning player sees a settled board.
                        // Driven by expires_at_unix, so it survives restarts.
                        GoldConsignment.SettleExpired(lease, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        // NOTE: GoldConsignment returns the BODY ONLY (2-3 bytes) and must
                        // go through Reply()/Frame() to become a real packet. Returning the
                        // raw body put "00 CE 00" on the wire, which the client cannot
                        // parse, so pressing the listing button appeared to do nothing.
                        if (consignRequest is GoldConsignAveragePriceRequest priceRequest)
                        {
                            // 官方规则：选定金币值后显示「近期交易价格」作为参考，所以这里
                            // 返回该档位的近期成交均价，而不是一个全局固定值。
                            return Reply(command,
                                AuctionOrdinaryBodyBuilder.GoldConsignPriceReference(
                                    GoldConsignment.ResolveReferencePrice(
                                        lease, priceRequest.ItemKey,
                                        DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
                        }
                        var walletBeforeList = ReadWallet(lease);
                        var listBody = GoldConsignment.Place(lease, command,
                            (GoldConsignRegisterRequest)consignRequest);
                        // Escrow debits the wallet server-side only, so push the new
                        // balance or the bag keeps showing the pre-listing amount.
                        return ReplyWithWalletRefresh(session, lease, command, listBody, walletBeforeList);
                    }
                    // ---- Gold consignment purchase (0x00B9) ----------------------
                    // The 购买 button on the 金币搜索 board sends the bidding opcode with a
                    // 17-byte consignment body:
                    //   01 | price(4) | listingId(4) | reserved(4) | ladderItem(4)
                    // Answered before the entry gate. Falls through when the id is not a
                    // purchasable consignment row.
                    if (body != null
                        && AuctionRequestParser.TryParse(command, body, out var goldBuy)
                        && goldBuy is GoldConsignPurchaseRequest purchaseRequest)
                    {
                        var walletBeforeBuy = ReadWallet(lease);
                        var buyBody = GoldConsignment.TryPurchase(lease, command, purchaseRequest);
                        if (buyBody != null)
                        {
                            // The buyer spent 点券 and gained gold. Push BOTH: the slot
                            // refresh carries the gold, and NOTIPACKET_CERA (0x0035) is the
                            // only packet that updates the client's 点券/代币券 readout.
                            var packets = new List<byte[]>(ReplyWithWalletRefresh(
                                session, lease, command, buyBody, walletBeforeBuy));
                            var cera = TryBuildCeraUpdate(lease);
                            if (cera != null) packets.Add(cera);
                            return packets;
                        }
                    }
                    // ---- Gold consignment cancel (0x00B8) ------------------------
                    // Same 9-byte body as the ordinary cancel but with selector 1:
                    //   01 | listingId:u64
                    // The ordinary auction's selector-0 invariant must stay intact, so this
                    // is detected here by reading body[0] instead of loosening the parser
                    // (doing that broke AuctionProtocolSelfTest's selector boundary).
                    // Falls through when the id is not a consignment listing of this
                    // character.
                    if (command == CmdPacketTypeA21.AUCTION_REGIST_CANCEL
                        && body != null
                        && body.Length == GoldConsignmentWire.CancelBodyLength
                        && body[0] == 0x01)
                    {
                        var listingId = (long)BitConverter.ToUInt64(body, 1);
                        var walletBeforeCancel = ReadWallet(lease);
                        var cancelBody = GoldConsignment.TryCancel(lease, command, listingId);
                        if (cancelBody != null)
                        {
                            // Refund also changes the wallet server-side only.
                            return ReplyWithWalletRefresh(
                                session, lease, command, cancelBody, walletBeforeCancel);
                        }
                    }
                    // ---- Gold consignment search (0x00BA) ------------------------
                    // The "金币搜索" tab sends the 14-byte consignment body. Other
                    // players must see consigned gold here, so answer before the entry
                    // gate. Falls through when the board is empty.
                    if (body != null && AuctionRequestParser.TryParse(command, body, out var goldSearch)
                        && goldSearch is GoldConsignSearchRequest searchRequest)
                    {
                        GoldConsignment.SettleExpired(lease, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        // searchRequest.Marker is the ladder step the player picked, and
                        // the board must show only that step. An empty result still has to
                        // be a valid consignment reply (selector 1 + zero count), otherwise
                        // the client falls back to the ordinary search and shows nothing.
                        var board = GoldConsignment.TryBuildSearchReply(
                            lease, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), searchRequest.Marker);
                        return Reply(command, board ?? Network.Builders.Auction
                            .GoldConsignmentSearchBuilder.EmptyBody());
                    }
                    // ---- Gold consignment listings (0x00BC) ---------------------
                    // The consignment board shares 0x00BC with the ordinary auction but
                    // must work when AUCTION_ENABLED=0, so it is answered before the
                    // entry gate below. Falls through untouched when the seller has no
                    // consignment rows.
                    if (command == CmdPacketTypeA21.AUCTION_MY_REGISTED_ITEM_INFO)
                    {
                        var consignBody = GoldConsignment.TryBuildListingsReply(
                            lease, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        if (consignBody != null && consignBody.Length > 0)
                            return Reply(command, consignBody);
                    }
                    if (!runtime.EntryEnabled)
                    {
                        rejection?.Write(MutationRejectStage.Availability, "EntryDisabled");
                        // A legitimate Source1 preview still needs both authoritative spaces on refusal.
                        if (body != null && AuctionRequestParser.TryParse(command, body, out var disabledRequest)
                            && disabledRequest is AuctionRegisterRequest disabledRegister
                            && AuctionNativeRegistrationAdapter.TryResolveSource(disabledRegister, out var disabledList, out _)
                            && disabledList != InventoryListType.Main)
                            return MutationFailure(session, lease, command, true,
                                includeAvatar: disabledList == InventoryListType.Avatar,
                                extraList: disabledList == InventoryListType.Avatar ? null : disabledList);
                        return Reply(command, AuctionMutationReplyBuilder.Failure(command, true));
                    }
                    if (body == null || !AuctionRequestParser.TryParse(command, body, out var request))
                    {
                        rejection?.Write(MutationRejectStage.Parse, "InvalidRequestShape");
                        return Reply(command, AuctionMutationReplyBuilder.Failure(command));
                    }
                    rejection?.ReadNative(request);
                    return request switch {
                        AuctionSearchByItemKeyRequest search => Search(state, command,
                            AuctionQueryEngine.Normalize(search), search.Position, ref afterSent),
                        AuctionSearchWithoutItemKeyRequest search => Search(state, command,
                            AuctionQueryEngine.Normalize(search), search.Position, ref afterSent),
                        AuctionMyListingsRequest => Owned(state, command, AuctionQueryView.OwnActiveListings, ref afterSent),
                        AuctionMyBidsRequest => Owned(state, command, AuctionQueryView.CurrentHighestBids, ref afterSent),
                        AuctionAveragePriceRequest average => Reference(state, command, average),
                        _ => Mutate(session, lease, state, header, body, request, rejection, ref afterSent)
                    };
                }
                catch (Exception ex) when (ex is IOException || ex is SqliteException
                    || ex is InvalidOperationException || ex is ArgumentException || ex is OverflowException)
                {
                    if (rejection != null) rejection.Write(MutationRejectStage.Exception, ExceptionCode(ex));
                    else FileLogger.Log($"[Auction] Request unavailable command={command} cid={lease.CharacterId} error={ex.GetType().Name}");
                    if (body != null && AuctionRequestParser.TryParse(command, body, out var failedRequest)
                        && failedRequest is AuctionRegisterRequest failedRegister
                        && AuctionNativeRegistrationAdapter.TryResolveSource(failedRegister, out var failedList, out _)
                        && failedList != InventoryListType.Main)
                        return MutationFailure(session, lease, command, true,
                            includeAvatar: failedList == InventoryListType.Avatar,
                            extraList: failedList == InventoryListType.Avatar ? null : failedList);
                    return Reply(command, AuctionMutationReplyBuilder.Failure(command, true));
                }
            }
        }, onSent: () => {
            if (Current(session, out var owned) && ReferenceEquals(owned, lease)) afterSent?.Invoke();
        });
    }

    private IReadOnlyList<byte[]> Search(AuctionNativeSessionState state, CmdPacketTypeA21 command,
        AuctionQueryResult<AuctionQueryCriteria> normalized, uint position, ref Action afterSent)
    {
        if (!normalized.IsAvailable) return Failure(command);
        var criteria = normalized.Value;
        long now = runtime.NowUnix;
        if (position == 0)
        {
            var created = Capture(state.Principal, criteria, now);
            if (!created.IsAvailable) return Failure(command, true);
            state.Queries[criteria.View] = created.Value;
        }
        state.Queries.TryGetValue(criteria.View, out var snapshot);
        var page = runtime.Query.Page(snapshot, state.Principal, criteria, position, now);
        if (!page.IsAvailable || !AuctionReadReplyProjector.TrySearch(page.Value, runtime.Codec, now, out var body, out _))
            return Failure(command, true);
        afterSent = () => state.Observations.RecordSentRows(criteria.View, snapshot, page.Value.Rows, runtime.NowUnix, out _);
        return Reply(command, body);
    }

    private IReadOnlyList<byte[]> Owned(AuctionNativeSessionState state, CmdPacketTypeA21 command,
        AuctionQueryView view, ref Action afterSent)
    {
        long now = runtime.NowUnix;
        var captured = Capture(state.Principal, AuctionQueryEngine.OwnedCriteria(view), now);
        if (!captured.IsAvailable) return Failure(command, true);
        var snapshot = captured.Value;
        byte[] body;
        bool ready = view == AuctionQueryView.OwnActiveListings
            ? AuctionReadReplyProjector.TryListings(snapshot.Rows, runtime.Codec, now, out body, out _)
            : AuctionReadReplyProjector.TryBids(snapshot.Rows, runtime.Codec, now, out body, out _);
        if (!ready) return Failure(command, true);
        state.Queries[view] = snapshot;
        afterSent = () => state.Observations.RecordSentRows(view, snapshot, snapshot.Rows, runtime.NowUnix, out _);
        return Reply(command, body);
    }

    private AuctionQueryResult<AuctionQuerySnapshot> Capture(AuctionQueryPrincipal principal,
        AuctionQueryCriteria criteria, long now)
    {
        var scope = criteria.View switch {
            AuctionQueryView.OwnActiveListings => AuctionOrderReadScope.SellerActive,
            AuctionQueryView.CurrentHighestBids => AuctionOrderReadScope.CurrentHighestBidderActive,
            _ => AuctionOrderReadScope.PublicActive
        };
        var read = runtime.Store.ReadQueryCandidates(scope, principal.AccountId, principal.CharacterId, now);
        return read.Available ? runtime.Query.CreateSnapshot(read.Orders, principal, criteria, now)
            : new(AuctionQueryStatus.Unavailable, read.Reason, null);
    }

    private IReadOnlyList<byte[]> Reference(AuctionNativeSessionState state, CmdPacketTypeA21 command,
        AuctionAveragePriceRequest request)
    {
        long now = runtime.NowUnix;
        var read = runtime.Store.ReadQueryCandidates(AuctionOrderReadScope.PublicActive,
            state.Principal.AccountId, state.Principal.CharacterId, now);
        if (!read.Available) return Failure(command, true);
        var reference = runtime.Query.PriceReference(read.Orders, state.Principal, request, now);
        return reference.IsAvailable && AuctionReadReplyProjector.TryPriceReference(reference.Value, out var body, out _)
            ? Reply(command, body) : Failure(command, true);
    }

    private IReadOnlyList<byte[]> Mutate(EnhancedClientSession session, InventoryLease lease,
        AuctionNativeSessionState state, GamePacketHeader header, byte[] body, AuctionRequest native,
        MutationRejectionTrace rejection, ref Action afterSent)
    {
        var command = (CmdPacketTypeA21)header.type;
        bool refreshAvatar = native is AuctionRegisterRequest sourceRequest
            && AuctionNativeRegistrationAdapter.TryResolveSource(sourceRequest, out var requestedList, out _)
            && requestedList == InventoryListType.Avatar;
        InventoryListType? extraList = native is AuctionRegisterRequest bagRequest
            && AuctionNativeRegistrationAdapter.TryResolveSource(bagRequest, out var bagList, out _)
            && bagList is InventoryListType.Pet or InventoryListType.GuildMedal ? bagList : null;
        AuctionNativeSessionState.Mutation mutation;
        bool resumed = false;
        if (state.TryGetMutation(header, body, out mutation))
        {
            // A second success ACK would apply the client's local gold/count
            // delta twice. A lost/uncompleted send can still replay one result.
            rejection?.ReadRequest(mutation.Request);
            if (mutation.Acknowledged)
            {
                rejection?.Write(MutationRejectStage.DuplicateAck, "RequestAlreadyAcknowledged");
                return MutationFailure(session, lease, command, includeAvatar: refreshAvatar, extraList: extraList);
            }
            resumed = true;
            mutation.Result = runtime.Application.Resume(mutation.Request.OperationId);
        }
        else
        {
            if (!state.CanRememberMutation || runtime.Store.HasUnresolvedOperationForCharacter(lease.CharacterId))
            {
                rejection?.Write(MutationRejectStage.Availability, "RequestBudgetOrUnresolvedOperation");
                return MutationFailure(session, lease, command, true, includeAvatar: refreshAvatar, extraList: extraList);
            }
            if (!ClientTextEncoding.TryGetStringStrict(session.Player.Name, out var actorName))
            {
                rejection?.Write(MutationRejectStage.Actor, "ActorNameDecodeRejected");
                return MutationFailure(session, lease, command, includeAvatar: refreshAvatar, extraList: extraList);
            }
            actorName = actorName.TrimEnd('\0');
            if (!AuctionSellerNameTag13.TryEncodeFrozenRoleName(actorName, out _))
            {
                rejection?.Write(MutationRejectStage.Actor, "ActorNameTagRejected");
                return MutationFailure(session, lease, command, includeAvatar: refreshAvatar, extraList: extraList);
            }
            var actor = new AuctionParty(lease.CharacterId, lease.AccountId, actorName);
            var operation = Guid.NewGuid();
            rejection?.ReadOperation(operation);
            AuctionNativeMutationResult normalized;
            if (native is AuctionRegisterRequest register)
            {
                lock (lease.SyncRoot)
                {
                    if (!AuctionNativeRegistrationAdapter.TryResolveSource(register, out var sourceList, out var sourceSlot))
                    {
                        rejection?.Write(MutationRejectStage.Source, "SourceSpaceOrSlotRejected");
                        return MutationFailure(session, lease, command, includeAvatar: refreshAvatar, extraList: extraList);
                    }
                    var source = lease.Inventory.GetItem(sourceList, sourceSlot);
                    if (source == null || !runtime.Catalog.TryGet(source.ItemId, out var template))
                    {
                        rejection?.Write(MutationRejectStage.Source, "SourceOrTemplateUnavailable");
                        return MutationFailure(session, lease, command, includeAvatar: refreshAvatar, extraList: extraList);
                    }
                    normalized = AuctionNativeRegistrationAdapter.Normalize(actor, operation, register, source, template);
                }
            }
            else
            {
                ulong identity = native switch {
                    AuctionCancelRequest cancel => cancel.IdentityBits,
                    AuctionBidRequest bid => bid.IdentityBits,
                    AuctionBuyApieceRequest piece => piece.IdentityBits,
                    _ => 0
                };
                if (identity == 0 || identity > long.MaxValue)
                {
                    rejection?.Write(MutationRejectStage.Observation, "InvalidOpaqueOrderIdentity");
                    return Failure(command, true);
                }
                if (!state.Observations.TryGetObservedOrderSnapshot((long)identity, runtime.NowUnix, out var observed, out var observationReason))
                {
                    rejection?.Write(MutationRejectStage.Observation, observationReason);
                    return Failure(command, true);
                }
                rejection?.ReadObservation(observed);
                normalized = native switch {
                    AuctionCancelRequest cancel => AuctionNativeMutationAdapter.NormalizeCancel(state.Principal, actor, observed, operation, cancel, runtime.NowUnix),
                    AuctionBidRequest bid => AuctionNativeMutationAdapter.NormalizeBidOrBuyout(state.Principal, actor, observed, operation, bid, runtime.NowUnix),
                    AuctionBuyApieceRequest piece => AuctionNativeMutationAdapter.NormalizeBuyPiece(state.Principal, actor, observed, operation, piece, runtime.NowUnix),
                    _ => new(AuctionNativeMutationStatus.Rejected, "UnsupportedMutation", null)
                };
            }
            if (!normalized.IsReady)
            {
                rejection?.Write(MutationRejectStage.Normalize, normalized.Reason, (int)normalized.Status);
                return MutationFailure(session, lease, command, includeAvatar: refreshAvatar, extraList: extraList);
            }
            rejection?.ReadRequest(normalized.Request);
            mutation = state.RememberMutation(header, body, normalized.Request);
            mutation.Result = runtime.Application.Execute(mutation.Request, lease);
        }
        if (mutation.Result.Status != AuctionMarketStatus.Committed)
        {
            rejection?.Write(resumed ? MutationRejectStage.Resume : MutationRejectStage.Application,
                mutation.Result.Reason, (int)mutation.Result.Status, mutation.Result.Order?.Version ?? 0);
            return MutationFailure(session, lease, command, mutation.Result.Status is AuctionMarketStatus.Pending
                or AuctionMarketStatus.Quarantined, includeAvatar: refreshAvatar, extraList: extraList);
        }
        runtime.PublishCommitted(mutation.Result);
        var refreshes = PrepareMutationItemRefreshes(session, lease, refreshAvatar, extraList);
        if (refreshes == null)
        {
            rejection?.Write(MutationRejectStage.Reply, "AuthoritativeRefreshUnavailableAfterCommit");
            session.Close(); return Array.Empty<byte[]>();
        }
        afterSent = () => {
            mutation.Acknowledged = true;
            if (mutation.Request.Action != AuctionOperationAction.List)
                state.Observations.AcknowledgeCommitted(mutation.Request, mutation.Result.Order, runtime.NowUnix, out _);
        };
        // Native success first applies its UI delta; authoritative inventory then
        // restores the final wallet and source slots from the committed model.
        return new[] { Frame(command, AuctionMutationReplyBuilder.Success(command)) }.Concat(refreshes).ToArray();
    }

    private IReadOnlyList<byte[]> MutationFailure(EnhancedClientSession session, InventoryLease lease,
        CmdPacketTypeA21 command, bool busy = false, bool includeAvatar = false, InventoryListType? extraList = null)
    {
        // Public commit may be durable while private finalization is still
        // Pending. Correct native wallet/source presentation after the error;
        // never manufacture a success ACK or promote an observed order here.
        var refreshes = PrepareMutationItemRefreshes(session, lease, includeAvatar, extraList);
        if (refreshes == null) { session.Close(); return Array.Empty<byte[]>(); }
        return new[] { Frame(command, AuctionMutationReplyBuilder.Failure(command, busy)) }.Concat(refreshes).ToArray();
    }

    private byte[][] PrepareMutationItemRefreshes(EnhancedClientSession session, InventoryLease lease, bool includeAvatar,
        InventoryListType? extraList = null)
    {
        // The existing outer prepared-send batch remains the sole sending owner.
        try
        {
            lock (lease.SyncRoot)
            {
                bool Guard() => Current(session, out var current) && ReferenceEquals(current, lease);
                if (!Guard()) return null;
                var avatar = includeAvatar ? InventoryRefreshSender.PrepareOwnedItemListRefresh(
                    session, lease, InventoryListType.Avatar, Guard) : null;
                var main = InventoryRefreshSender.PrepareOwnedItemListRefresh(session, lease, InventoryListType.Main, Guard);
                var extra = extraList.HasValue ? InventoryRefreshSender.PrepareOwnedItemListRefresh(
                    session, lease, extraList.Value, Guard) : null;
                if (main == null || (includeAvatar && avatar == null) || (extraList.HasValue && extra == null) || !Guard()) return null;
                return new[] { avatar, extra, main }.Where(packet => packet != null).ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException || ex is SqliteException
            || ex is InvalidOperationException || ex is ArgumentException || ex is OverflowException)
        {
            // Caller closes the session without sending any half-prepared batch.
            return null;
        }
    }


    private static bool IsMutationCommand(CmdPacketTypeA21 command)
        => command is CmdPacketTypeA21.AUCTION_REGIST_ITEM or CmdPacketTypeA21.AUCTION_REGIST_CANCEL
            or CmdPacketTypeA21.AUCTION_BIDDING or CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE;

    private static string ExceptionCode(Exception error) => error switch {
        SqliteException => "SqliteException", IOException => "IOException", OverflowException => "OverflowException",
        ArgumentException => "ArgumentException", InvalidOperationException => "InvalidOperationException", _ => "Exception"
    };

    internal enum MutationRejectStage { RequestScope, Availability, Parse, Actor, Source, Observation,
        Normalize, DuplicateAck, Resume, Application, Reply, Exception }

    // Diagnostic values only: no request body, seller/actor names or tags retained.
    // The optional sink is an internal pure-test seam; production uses FileLogger.
    internal sealed class MutationRejectionTrace
    {
        private readonly ushort command;
        private readonly long cid, leaseVersion;
        private readonly int bodyLength;
        private bool written;
        private Guid operation;
        private ulong wireOrder;
        private long wireAmount = -1, order, offeredGold = -1, expectedVersion = -1, observedVersion = -1;
        private int action;
        private int sourceSpace = -1, sourceSlot = -1;
        private uint templateId, quantity;
        internal MutationRejectionTrace(ushort command, long cid, long leaseVersion, int bodyLength)
        { this.command = command; this.cid = cid; this.leaseVersion = leaseVersion; this.bodyLength = bodyLength; }
        internal void ReadOperation(Guid value) => operation = value;
        internal void ReadNative(AuctionRequest value)
        {
            switch (value)
            {
                case AuctionRegisterRequest register:
                    sourceSpace = register.Space; sourceSlot = register.Slot;
                    templateId = register.ItemKey; quantity = register.Quantity; break;
                case AuctionCancelRequest cancel: wireOrder = cancel.IdentityBits; break;
                case AuctionBidRequest bid: wireOrder = bid.IdentityBits; wireAmount = bid.Amount; break;
                case AuctionBuyApieceRequest piece: wireOrder = piece.IdentityBits; wireAmount = piece.Amount; break;
            }
        }
        internal void ReadObservation(AuctionQuerySnapshot snapshot)
        {
            if (snapshot != null && !snapshot.Rows.IsDefaultOrEmpty)
                observedVersion = snapshot.Rows[0]?.OrderVersion ?? -1;
        }
        internal void ReadRequest(AuctionMarketRequest request)
        {
            if (request == null) return;
            operation = request.OperationId; order = request.OrderId; action = (int)request.Action;
            offeredGold = request.OfferedGold; expectedVersion = request.ExpectedVersion;
        }
        internal void Write(MutationRejectStage stage, string reason, int status = -1,
            long resultVersion = -1, Action<string> sink = null)
        {
            if (written) return;
            written = true;
            // Include formatting and initial FileLogger static initialization in
            // the isolation boundary. A failed diagnostic never alters the reply.
            try
            {
                var safeStage = Enum.IsDefined(stage) ? stage.ToString() : "UnknownStage";
                var line = FormattableString.Invariant($"[AuctionReject] stage={safeStage} reason={SafeReason(reason)} command={command} cid={cid} leaseVersion={leaseVersion} operation={operation:N} wireOrder={wireOrder} wireAmount={wireAmount} order={order} action={action} offeredGold={offeredGold} expectedVersion={expectedVersion} observedVersion={observedVersion} resultVersion={resultVersion} status={status} bodyLength={bodyLength} sourceSpace={sourceSpace} sourceSlot={sourceSlot} templateId={templateId} quantity={quantity}");
                if (sink == null) FileLogger.Log(line); else sink(line);
            }
            catch (Exception) { /* Diagnostics cannot change transaction/send behavior. */ }
        }
        internal static string SafeReason(string value) => value switch {
            "OwnedLeaseChangedBeforeReply" or "EntryDisabled" or "InvalidRequestShape"
                or "RequestBudgetOrUnresolvedOperation" or "ActorNameDecodeRejected" or "ActorNameTagRejected"
                or "SourceSpaceOrSlotRejected" or "SourceOrTemplateUnavailable" or "RequestAlreadyAcknowledged"
                or "AuthoritativeRefreshUnavailableAfterCommit" or "InvalidOpaqueOrderIdentity"
                or "InvalidObservationLookup" or "ConflictingObservedOrderFacts" or "OrderWasNotSentOrObservationExpired"
                or "MissingBidRequest" or "MissingCancelRequest" or "MissingPieceRequest"
                or "SellerTagMismatch" or "InvalidBidAmount" or "SellerAccountCannotBuyOrBid"
                or "AuthenticatedActorAndOperationRequired" or "ObservedSnapshotRequired"
                or "ObservedSnapshotOwnerMismatch" or "InvalidObservedSnapshot" or "MutationTimeBeforeSnapshot"
                or "ObservedSnapshotExpired" or "InvalidObservedRow" or "AmbiguousObservedOrder"
                or "OrderWasNotObserved" or "InvalidObservedOrderFacts" or "ObservedOrderExpired"
                or "CancelSellerIdentityMismatch" or "FundedBidCannotCancel" or "UnsupportedMutation"
                or "PieceTemplateMismatch" or "PieceRequiresFixedPriceWithoutBid" or "ExactFrozenUnitPriceRequired"
                or "InvalidPieceQuantityOrAmount" or "InconsistentObservedFixedPrice" or "PieceAmountMismatch"
                or "PieceAmountOverflow" or "CurrentOwnedLeaseRequired" or "StaleInventoryLease"
                or "InvalidRequestIdentity" or "OperationRequestConflict" or "UnknownOperation"
                or "BaselineSaveFailed" or "PublicTransactionNotCommitted" or "InventoryRollbackUnconfirmed"
                or "CommittedReceiptMissing" or "EconomicQuoteUnavailable" or "FixturePolicyNotEnabled"
                or "MalformedTradeDeclaration" or "TemplateProhibitsAuction" or "TemplateNotTradable"
                or "LockedOrRestrictedInstance" or "QuantityExceedsOwnedSource" or "SourceBagKindMismatch"
                or "TemplateAndInstanceKindMismatch" or "TimedAuctionTransferUnverified"
                or "NativeListingRuleUnavailable" or "NativeClassRequiresActiveAuctionPremium"
                or "NativePackagableZero" or "PackagableFieldUnavailable" or "TemplateTemporalTagsPresentUnsupported"
                or "NativeListingDisplayUnavailable" or "RegistrationSourceOrQuantityMismatch"
                or "RegistrationPriceMismatch" or "RegistrationAggregatePriceOutOfRange"
                or "SqliteException" or "IOException" or "OverflowException" or "ArgumentException"
                or "InvalidOperationException" or "Exception" => value,
            "Order is unavailable for the quote." => "OrderUnavailableForQuote",
            "Order is not available at the expected version." => "ExpectedOrderVersionUnavailable",
            "Order deadline does not permit this action." => "OrderDeadlineRejected",
            "Buyout total mismatch." => "BuyoutTotalMismatch",
            "Buyout does not match the frozen total." => "FrozenBuyoutTotalMismatch",
            "Invalid funded bid plan." => "InvalidFundedBidPlan",
            "Fixed-price order does not accept bids." => "FixedPriceDoesNotAcceptBids",
            "Seller account cannot buy or bid." => "SellerAccountCannotBuyOrBid",
            "An account cannot bid on or buy its own auction." => "SellerAccountCannotBuyOrBid",
            "Buyout equality must use the buyout plan; bids cannot exceed buyout." => "BidAtOrAboveBuyout",
            "Bid must strictly exceed current price and meet the native threshold." => "BidBelowThreshold",
            "Funded bid exceeds buyout." => "FundedBidExceedsBuyout",
            "Highest bidder identity and funded money must agree." => "HighestBidderFundingMismatch",
            "Quote binding, provenance or validity mismatch." => "QuoteBindingInvalid",
            "Invalid fill quantity or proceeds." => "InvalidFillQuantityOrProceeds",
            "Funded bid exceeds purchase gross." => "FundedBidExceedsPurchaseGross",
            "Unbalanced closing deposit." => "UnbalancedClosingDeposit",
            "Persisted cumulative fee is inconsistent with locked rate and gross." => "PersistedCumulativeFeeMismatch",
            "Seller account cannot own the funded bid." => "SellerAccountOwnsFundedBid",
            "A character cannot change account identity." => "CharacterAccountIdentityMismatch",
            _ => "Unclassified"
        };
    }

    private bool Current(EnhancedClientSession session, out InventoryLease lease)
    {
        lease = null;
        return session?.Account?.AccountId > 0 && session.TcpClient?.Connected == true
            && transitions.IsCurrent(session)
            && InventoryContext.TryGetOwnedLease(session.SessionId, session.Player.CharacterId, out lease)
            && lease.AccountId == session.Account.AccountId;
    }
    private static byte[] Frame(CmdPacketTypeA21 command, byte[] body)
        => GamePacketEnvelopeBuilder.Build(1, (ushort)AuctionMutationReplyBuilder.ReplyCommand(command), body);
    private static IReadOnlyList<byte[]> Reply(CmdPacketTypeA21 command, byte[] body) => new[] { Frame(command, body) };
    private static IReadOnlyList<byte[]> Failure(CmdPacketTypeA21 command, bool busy = false)
        => Reply(command, AuctionMutationReplyBuilder.Failure(command, busy));

    /// <summary>Current online gold count, or 0 when the wallet slot is absent.</summary>
    private static int ReadWallet(InventoryLease lease)
        => lease?.Inventory?.GetMainVirtualCount(
            InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;

    /// <summary>
    /// Builds NOTIPACKET_CERA (0x0035) with the account's current
    /// (cera, tokenCera, happyTokenCera). This is the only packet that refreshes the
    /// client's 点券/代币券 readout; crediting or debiting the DB alone leaves the
    /// on-screen numbers stale. Returns null when the balances cannot be read.
    /// </summary>
    private static byte[] TryBuildCeraUpdate(InventoryLease lease)
    {
        var database = lease?.Inventory?.Database;
        if (database == null) return null;
        try
        {
            var (cera, token, happy) = database.Read(connection =>
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT cera, token_cera, happy_token_cera FROM accounts "
                    + "WHERE account_id = @aid;";
                cmd.Parameters.AddWithValue("@aid", lease.AccountId);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return (0, 0, 0);
                return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
            });
            return GamePacketEnvelopeBuilder.Build(0x00, (ushort)NotiPacketTypeA21.CERA,
                CeraUpdateBuilder.Build(cera, token, happy));
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException
            || ex is OverflowException || ex is IOException || ex is SqliteException)
        {
            FileLogger.Log($"[GoldConsign] cera update build failed cid={lease.CharacterId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sends the mutation ACK and, when the gold wallet actually changed, a
    /// slot-level 0x000E UPDATE_ITEM_LIST for the currency slot.
    ///
    /// Gold escrow/refund only touches the online model and SQL; nothing pushed the
    /// new balance, so the bag UI kept showing the old number until some other
    /// action re-synced it (that is why cancelling appeared to "fix" the display).
    /// Re-sending only slot 0 keeps this to one small entry instead of a full list.
    /// </summary>
    private IReadOnlyList<byte[]> ReplyWithWalletRefresh(
        EnhancedClientSession session,
        InventoryLease lease,
        CmdPacketTypeA21 command,
        byte[] body,
        int walletBefore)
    {
        var ack = Frame(command, body);
        // body[0] == 1 means success; a rejection must not push a stale/identical update.
        if (body == null || body.Length == 0 || body[0] != 1) return new[] { ack };

        byte[] update = null;
        try
        {
            lock (lease.SyncRoot)
            {
                if (Current(session, out var owned) && ReferenceEquals(owned, lease)
                    && InventoryContext.IsCurrentLease(lease, session.SessionId, session.Player.CharacterId))
                {
                    var after = lease.Inventory.GetMainVirtualCount(
                        InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                    if (after != walletBefore)
                    {
                        update = GamePacketEnvelopeBuilder.Build(
                            0x00,
                            (ushort)NotiPacketTypeA21.UPDATE_ITEM_LIST,
                            ItemListUpdateBuilder.BuildItemSpaceUpdateBody(
                                lease.Inventory,
                                InventoryListType.Main,
                                new[] { InventoryService.MainVirtualCurrencySlotStart }));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException
            || ex is OverflowException || ex is IOException || ex is SqliteException)
        {
            // The ACK is authoritative; a failed refresh must not break the mutation.
            FileLogger.Log($"[GoldConsign] wallet refresh failed cid={lease.CharacterId}: {ex.Message}");
        }

        return update == null ? new[] { ack } : new[] { ack, update };
    }
}
