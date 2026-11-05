using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Market;
using DfoServer.Game.Auction.Queries;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld.Auction;
using DfoServer.Network;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Handlers.Auction;
using DfoServer.Network.Parsers.Auction;
using PvfLib;

namespace DfoServer.SelfTests;

// Pure entry-boundary tests. Uses the real PVF parser/catalog over explicit
// fixture scripts; no archive, database, game process, runtime registration or
// production quote is opened. Native provenance: REGISTER-ADAPTER-M4B.md and
// MUTATION-CONTRACT-M4.md, A21 auction-audit-20261023/m2/protocol.
public static class AuctionNativeEntrySelfTest
{
    private const string Fingerprint = "963cd331342a0066c01cb189ec2cd822b8b8206065b109ec63ca664adb3da8d8";

    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine((condition ? "[PASS] " : "[FAIL] ") + "auction native entry: " + name);
            if (!condition) failures++;
        }
        Console.WriteLine("=== AUCTION_NATIVE_ENTRY selftest ===");
        try
        {
            CheckRegistration(Check);
            CheckAvatarSource(Check);
            CheckReplies(Check);
            var trace = new AuctionHandler.MutationRejectionTrace((ushort)CmdPacketTypeA21.AUCTION_REGIST_ITEM, 1, 1, 37);
            trace.ReadNative(new AuctionRegisterRequest(7, 123, 90001, 3, 10, 100, 100, 24, default, default, default));
            string line = null;
            trace.Write(AuctionHandler.MutationRejectStage.Normalize, "RegistrationPriceMismatch", sink:value => line=value);
            Check("listing refusal retains safe numeric source and precise reason", line != null
                && line.Contains("sourceSpace=7 sourceSlot=123 templateId=90001 quantity=3", StringComparison.Ordinal)
                && line.Contains("reason=RegistrationPriceMismatch", StringComparison.Ordinal));
            CheckSessionCache(Check);
        }
        catch (Exception exception)
        {
            Console.WriteLine("[FAIL] unexpected exception: " + exception);
            failures++;
        }
        Console.WriteLine("AUCTION_NATIVE_ENTRY failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void CheckRegistration(Action<string, bool> check)
    {
        var catalog = FixtureCatalog();
        var actor = new AuctionParty(78241, 78242, "入口夹具");
        var operation = Guid.NewGuid();
        var stack = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3028, Count = 10 };
        var equipment = new ItemCore { ItemKind = ItemCore.KindEquipment, ItemId = 101, Value = 0, Durability = 37 };
        var stackTemplate = catalog.Templates[3028];
        var equipmentTemplate = catalog.Templates[101];
        var request = new AuctionRegisterRequest((byte)InventoryListType.Main,
            checked((ushort)InventoryService.MainSlotStart), 3028, 3,
            uint.MaxValue, 300, 300, 24, default, default, default);
        var original = stack.ToBytes();
        AuctionNativeMutationResult Normalize(AuctionRegisterRequest r, ItemCore item = null,
            AuctionTemplateProjection template = null)
            => AuctionNativeRegistrationAdapter.Normalize(actor, operation, r, item ?? stack, template ?? stackTemplate);
        bool Rejected(AuctionRegisterRequest r, ItemCore item = null, AuctionTemplateProjection template = null)
            => Normalize(r, item, template).Status == AuctionNativeMutationStatus.Rejected;

        var ready = Normalize(request);
        check("proven Main per-item input becomes aggregate price exactly once", ready.Status == AuctionNativeMutationStatus.Ready
            && ready.Request.OperationId == operation && ready.Request.Actor == actor
            && ready.Request.Quantity == 3 && ready.Request.OpeningBidGold == null
            && ready.Request.BuyoutGold == 900 && ready.Request.UnitPriceGold == 300
            && ready.Request.SourceList == InventoryListType.Main
            && ready.Request.SourceSlot == InventoryService.MainSlotStart && ready.Request.DurationHours == 24);
        var indivisibleInput = Normalize(request with { Wire16 = 301, Wire20 = 301, Wire24 = 48 });
        check("per-item price need not divide quantity and never becomes a truncated bundle", indivisibleInput.IsReady
            && indivisibleInput.Request.BuyoutGold == 903 && indivisibleInput.Request.UnitPriceGold == 301 && indivisibleInput.Request.DurationHours == 48);
        var actualUi = Normalize(request with { Quantity = 2, Wire16 = 22, Wire20 = 22 });
        check("observed native P22 Q2 tooltip44 matches aggregate44 rather than22", actualUi.IsReady
            && actualUi.Request.BuyoutGold == 44 && actualUi.Request.UnitPriceGold == 22);
        var splitTax = Normalize(request with { Quantity = 2, Wire16 = 11, Wire20 = 11 });
        check("two-item total22 tax regression requires per-item input11", splitTax.IsReady
            && splitTax.Request.BuyoutGold == 22 && splitTax.Request.UnitPriceGold == 11);
        var competitive = Normalize(request with { Wire12 = 100 });
        check("competitive listing never receives piece eligibility", competitive.Status == AuctionNativeMutationStatus.Ready
            && competitive.Request.OpeningBidGold == 300 && competitive.Request.BuyoutGold == 900
            && competitive.Request.UnitPriceGold == null);
        var bidOnly = Normalize(request with { Wire12 = 100, Wire16 = uint.MaxValue, Wire20 = 0 });
        check("bid-only sentinel preserves absent buyout", bidOnly.Status == AuctionNativeMutationStatus.Ready
            && bidOnly.Request.OpeningBidGold == 300 && bidOnly.Request.BuyoutGold == null && bidOnly.Request.UnitPriceGold == null);
        check("missing both prices is rejected", Rejected(request with { Wire16 = uint.MaxValue, Wire20 = 0 }));
        check("zero bid is not the absent bid sentinel", Rejected(request with { Wire12 = 0 }));
        check("zero buyout is not the absent buyout sentinel", Rejected(request with { Wire16 = 0, Wire20 = 0 }));
        check("bid at or above buyout rejected", Rejected(request with { Wire12 = 300 })
            && Rejected(request with { Wire12 = 301 }));
        check("scaled A21 sender branch normalizes the already multiplied total once",
            Normalize(request with { Wire16 = 900 }).Request.BuyoutGold == 900
            && Normalize(request with { Wire20 = 100 }).Request.BuyoutGold == 300);
        check("inconsistent price tuple remains rejected", Rejected(request with { Wire20 = 299 })
            && Rejected(request with { Wire16 = uint.MaxValue, Wire20 = 300, Wire12 = 100 }));
        check("unsigned amounts beyond signed gold range rejected", Rejected(request with { Wire16 = 0x80000000, Wire20 = 0x80000000 })
            && Rejected(request with { Wire12 = 0x80000000, Wire16 = uint.MaxValue, Wire20 = 0 }));
        check("valid individual input with overflowing aggregate is rejected", Rejected(request with { Quantity = 2, Wire16 = 0x40000000, Wire20 = 0x40000000 })
            && Rejected(request with { Quantity = 2, Wire12 = 0x40000000, Wire16 = uint.MaxValue, Wire20 = 0 }));
        check("same material price normalization is independent of whole PVF hash", Normalize(request,
            template: FixtureCatalog(new string('a', 64)).Templates[3028]).IsReady);
        check("quantity zero, overflow and source excess rejected", Rejected(request with { Quantity = 0 })
            && Rejected(request with { Quantity = 11 }) && Rejected(request with { Quantity = uint.MaxValue }));
        check("request key and template key must match actual source", Rejected(request with { ItemKey = 101 })
            && Rejected(request, template: equipmentTemplate));
        check("only supported expiry inputs accepted", Rejected(request with { Wire24 = 0 })
            && Rejected(request with { Wire24 = 25 }) && Rejected(request with { Wire24 = uint.MaxValue }));
        check("out-of-range Main slots unavailable", Normalize(request with { Slot = checked((ushort)(InventoryService.MainSlotStart - 1)) }).Status
            == AuctionNativeMutationStatus.Unavailable
            && Normalize(request with { Slot = checked((ushort)(InventoryService.MainSlotEnd + 1)) }).Status
            == AuctionNativeMutationStatus.Unavailable);
        foreach (byte space in new byte[] { 2, 3, 255 })
            check("source " + space + " never aliases Main", Normalize(request with { Space = space }).Status
                == AuctionNativeMutationStatus.Unavailable);
        foreach (byte space in new byte[] { 7, 38 })
            check("carried bag cannot alias an item from another kind " + space,
                Normalize(request with { Space = space }).Status == AuctionNativeMutationStatus.Rejected);

        check("Source1 with a Main material core is rejected rather than aliased", Normalize(request with { Space = 1 }).Status == AuctionNativeMutationStatus.Rejected);

        var equipmentRequest = request with { ItemKey = 101, Quantity = 1 };
        var equipmentReady = Normalize(equipmentRequest, equipment, equipmentTemplate);
        check("equipment quantity one preserves both equal price fields", equipmentReady.Status == AuctionNativeMutationStatus.Ready
            && equipmentReady.Request.Quantity == 1 && equipmentReady.Request.BuyoutGold == 300
            && equipmentReady.Request.UnitPriceGold == null);
        check("equipment multi-quantity rejected", Rejected(equipmentRequest with { Quantity = 2 }, equipment, equipmentTemplate));
        check("equipment cannot use stackable catalog kind", Rejected(equipmentRequest, equipment, stackTemplate));
        var wrongKind = new ItemCore { ItemKind = ItemCore.KindEquipment, ItemId = 3028, Count = 10 };
        check("stack key with equipment core kind rejected", Rejected(request, wrongKind));
        foreach (byte kind in new byte[] { ItemCore.KindUnknown, 255 })
        {
            var invalid = new ItemCore { ItemKind = kind, ItemId = 3028, Count = 10 };
            check("invalid canonical kind " + kind + " rejected despite legal stack key", Rejected(request, invalid));
        }
        var hints = request with { FilterA = new(30000, 255), FilterB = new(65535, 1), FilterC = new(1234, 77) };
        var hinted = Normalize(hints);
        check("untrusted random category hints do not rewrite owned item or price", hinted.Status == AuctionNativeMutationStatus.Ready
            && hinted.Request == ready.Request && stack.ToBytes().SequenceEqual(original));
        check("empty operation or unauthenticated actor rejected",
            AuctionNativeRegistrationAdapter.Normalize(actor, Guid.Empty, request, stack, stackTemplate).Status == AuctionNativeMutationStatus.Rejected
            && AuctionNativeRegistrationAdapter.Normalize(actor with { AccountId = 0 }, operation, request, stack, stackTemplate).Status
            == AuctionNativeMutationStatus.Rejected);
    }

    private static void CheckAvatarSource(Action<string, bool> check)
    {
        // Pure boundary fixture parsed by the real catalog/PVF model. This is
        // not an actual native qualification proof and grants no eligibility.
        const string avatarScript = "[name]\n`入口装扮`\n[equipment type]\n`[coat avatar]` 0\n[attach type]\n`[free]`";
        const string unknownScript = "[name]\n`未知类型`\n[equipment type]\n`[unknown native avatar type]` 0\n[attach type]\n`[free]`";
        var scripts = new Dictionary<string, string> {
            ["equipment/equipment.lst"] = "102 `avatar/native-entry.equ`\n103 `at_avatar/unknown.equ`",
            ["stackable/stackable.lst"] = "# no stackable entries in this Avatar-only fixture",
            ["equipment/avatar/native-entry.equ"] = avatarScript,
            ["equipment/at_avatar/unknown.equ"] = unknownScript
        };
        var catalog = AuctionTemplateCatalog.Load(path => scripts.TryGetValue(path, out var text) ? text : null,
            Fingerprint, id => id == 102 ? EquipmentFile.Parse(avatarScript)
                : id == 103 ? EquipmentFile.Parse(unknownScript) : null, _ => null);
        var template = catalog.Templates[102];
        var actor = new AuctionParty(78241, 78242, "装扮入口夹具");
        var avatar = new ItemCore { ItemKind = ItemCore.KindAvatar, ItemId = 102,
            AvatarUid = 654321, AbilityNo = 321, SealFlag = 1 };
        var request = new AuctionRegisterRequest((byte)InventoryListType.Avatar, 0, 102, 1,
            uint.MaxValue, 651, 651, 24, default, default, default);
        var original = avatar.ToBytes();
        AuctionNativeMutationResult Normalize(AuctionRegisterRequest candidate, ItemCore actual = null,
            AuctionTemplateProjection projection = null)
            => AuctionNativeRegistrationAdapter.Normalize(actor, Guid.NewGuid(), candidate,
                actual ?? avatar, projection ?? template);
        foreach (ushort slot in new ushort[] { 0, 209 })
        {
            var candidate = request with { Slot = slot };
            var result = Normalize(candidate);
            check("Source1 Avatar boundary " + slot + " resolves the actual bag and keeps Q1",
                AuctionNativeRegistrationAdapter.TryResolveSource(candidate, out var list, out var resolvedSlot)
                && list == InventoryListType.Avatar && resolvedSlot == slot
                && result.IsReady && result.Request.SourceList == InventoryListType.Avatar
                && result.Request.SourceSlot == slot && result.Request.Quantity == 1
                && result.Request.BuyoutGold == 651 && result.Request.UnitPriceGold == null);
        }
        foreach (ushort slot in new ushort[] { 210, ushort.MaxValue })
            check("Source1 invalid slot " + slot + " refuses before narrowing or lookup",
                !AuctionNativeRegistrationAdapter.TryResolveSource(request with { Slot = slot }, out _, out _)
                && Normalize(request with { Slot = slot }).Status == AuctionNativeMutationStatus.Unavailable);
        check("Source1 multi-quantity and zero reject despite Avatar UID being large",
            Normalize(request with { Quantity = 2 }).Status == AuctionNativeMutationStatus.Rejected
            && Normalize(request with { Quantity = 0 }).Status == AuctionNativeMutationStatus.Rejected);
        check("Avatar cannot list through a same-slot Main source",
            Normalize(request with { Space = (byte)InventoryListType.Main, Slot = 13 }).Status == AuctionNativeMutationStatus.Rejected);
        foreach (byte space in new byte[] { 2, 3, 255 })
            check("Avatar source " + space + " cannot alias bag1 or Main",
                Normalize(request with { Space = space, Slot = 13 }).Status == AuctionNativeMutationStatus.Unavailable);
        foreach (byte space in new byte[] { 7, 38 })
            check("Avatar cannot alias another carried bag " + space,
                Normalize(request with { Space = space, Slot = 13 }).Status == AuctionNativeMutationStatus.Rejected);
        foreach (byte kind in new byte[] { ItemCore.KindEquipment, ItemCore.KindMaterial, ItemCore.KindCreature, ItemCore.KindUnknown, 255 })
        {
            var decoy = avatar.Copy(); decoy.ItemKind = kind;
            check("Source1 same-key decoy kind " + kind + " is not an owned Avatar",
                Normalize(request with { Slot = 13 }, decoy).Status == AuctionNativeMutationStatus.Rejected);
        }
        var weaponTemplate = FixtureCatalog().Templates[101];
        var disguisedWeapon = avatar.Copy(); disguisedWeapon.ItemId = 101;
        check("KindAvatar cannot disguise a real ordinary equipment template",
            Normalize(request with { ItemKey = 101 }, disguisedWeapon, weaponTemplate).Status == AuctionNativeMutationStatus.Rejected);
        var unknown = avatar.Copy(); unknown.ItemId = 103;
        check("avatar resource path with unknown equipment type is never type authority",
            Normalize(request with { ItemKey = 103 }, unknown, catalog.Templates[103]).Status == AuctionNativeMutationStatus.Rejected);
        check("adapter preserves the full positive owned UID and AbilityNo instead of writing Count1",
            avatar.ToBytes().SequenceEqual(original) && avatar.AvatarUid == 654321 && avatar.AbilityNo == 321);
    }

    private static void CheckReplies(Action<string, bool> check)
    {
        check("no-item-key search uses registered BA result command", AuctionMutationReplyBuilder.ReplyCommand(
            CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY) == CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY);
        check("item-key search keeps registered BA result command", AuctionMutationReplyBuilder.ReplyCommand(
            CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY) == CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY);
        foreach (var command in new[] { CmdPacketTypeA21.AUCTION_REGIST_ITEM,
            CmdPacketTypeA21.AUCTION_REGIST_CANCEL, CmdPacketTypeA21.AUCTION_BIDDING })
        {
            check(command + " success includes ordinary selector", AuctionMutationReplyBuilder.Success(command).SequenceEqual(new byte[] { 1, 0 }));
            check(command + " generic failure exact selector", AuctionMutationReplyBuilder.Failure(command).SequenceEqual(new byte[] { 0, 0x7B, 0 }));
            check(command + " busy failure exact selector", AuctionMutationReplyBuilder.Failure(command, true).SequenceEqual(new byte[] { 0, 0xCE, 0 }));
        }
        var piece = CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE;
        check("piece success has no selector", AuctionMutationReplyBuilder.Success(piece).SequenceEqual(new byte[] { 1 }));
        check("piece generic failure has no selector", AuctionMutationReplyBuilder.Failure(piece).SequenceEqual(new byte[] { 0, 0x7B }));
        check("piece busy failure has no selector", AuctionMutationReplyBuilder.Failure(piece, true).SequenceEqual(new byte[] { 0, 0xCE }));
        var mutated = AuctionMutationReplyBuilder.Success(piece);
        mutated[0] = 99;
        check("reply buffers do not share mutable storage", AuctionMutationReplyBuilder.Success(piece).SequenceEqual(new byte[] { 1 }));
    }

    private static void CheckSessionCache(Action<string, bool> check)
    {
        var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
        var principal = new AuctionQueryPrincipal(Guid.NewGuid(), 1, 78242, 78241);
        var state = new AuctionNativeSessionState(principal, codec);
        var actor = new AuctionParty(principal.CharacterId, principal.AccountId, "缓存夹具");
        var header = new GamePacketHeader { type = (ushort)CmdPacketTypeA21.AUCTION_BIDDING, seq = 7, checksum = 1234 };
        byte[] body = { 0, 1, 2, 3, 4 };
        AuctionMarketRequest Request() => new(Guid.NewGuid(), AuctionOperationAction.Bid, 1, 1, actor, 1, 100, 0);
        check("new native request absent from cache", !state.TryGetMutation(header, body, out _));
        var first = state.RememberMutation(header, body, Request());
        check("same header and equal body retain original request UUID", state.TryGetMutation(header, body.ToArray(), out var replay)
            && ReferenceEquals(first, replay) && replay.Request.OperationId == first.Request.OperationId);
        check("duplicate direct insertion cannot create another intent", Throws<ArgumentException>(() => state.RememberMutation(header, body, Request()))
            && state.TryGetMutation(header, body, out replay) && ReferenceEquals(first, replay));
        var nextHeader = header;
        nextHeader.seq++;
        check("different sequence is a fresh intent", !state.TryGetMutation(nextHeader, body, out _));
        var next = state.RememberMutation(nextHeader, body, Request());
        check("different sequence obtains another request UUID", next.Request.OperationId != first.Request.OperationId);
        var differentBody = body.ToArray(); differentBody[4] ^= 1;
        check("same header different body is not a replay", !state.TryGetMutation(header, differentBody, out _));
        var nextChecksum = header; nextChecksum.checksum++;
        var nextCommand = header; nextCommand.type = (ushort)CmdPacketTypeA21.AUCTION_REGIST_CANCEL;
        check("checksum and command participate in replay key", !state.TryGetMutation(nextChecksum, body, out _)
            && !state.TryGetMutation(nextCommand, body, out _));

        var generation2 = new AuctionNativeSessionState(principal with { Generation = 2 }, codec);
        check("new principal generation does not inherit requests", !generation2.TryGetMutation(header, body, out _)
            && generation2.Principal.Generation == 2 && state.Principal.Generation == 1);
        var oldSnapshot = new AuctionQuerySnapshot(principal, "fixture", Fingerprint, 100, 200,
            ImmutableArray<AuctionQueryRow>.Empty);
        check("new generation refuses old generation observed snapshot", !generation2.Observations.RecordSentRows(
            AuctionQueryView.Search, oldSnapshot, Array.Empty<AuctionQueryRow>(), 100, out _));
        var anotherSession = new AuctionNativeSessionState(principal with { SessionId = Guid.NewGuid() }, codec);
        check("different session does not inherit requests", !anotherSession.TryGetMutation(header, body, out _));

        var budget = new AuctionNativeSessionState(principal, codec);
        AuctionNativeSessionState.Mutation earliest = null;
        var budgetHeader = header;
        for (int index = 0; index < 4096; index++)
        {
            budgetHeader.seq = checked((ushort)index);
            var remembered = budget.RememberMutation(budgetHeader, body, Request());
            if (index == 0) earliest = remembered;
        }
        budgetHeader.seq = 0;
        check("full 4096 budget retains earliest request without eviction", !budget.CanRememberMutation
            && budget.TryGetMutation(budgetHeader, body, out var retained) && ReferenceEquals(retained, earliest));
        budgetHeader.seq = 4096;
        check("request 4097 refuses capacity instead of evicting", Throws<InvalidOperationException>(
            () => budget.RememberMutation(budgetHeader, body, Request())) && !budget.TryGetMutation(budgetHeader, body, out _));
        budgetHeader.seq = 0;
        check("capacity refusal still preserves first UUID", budget.TryGetMutation(budgetHeader, body, out retained)
            && retained.Request.OperationId == earliest.Request.OperationId);
    }

    private static AuctionTemplateCatalog FixtureCatalog(string fingerprint = Fingerprint)
    {
        const string equipment = "[name]\n`入口武器`\n[equipment type]\n`[weapon]` 0\n[attach type]\n`[trade]`";
        const string stackable = "[name]\n`入口材料`\n[stackable type]\n`[material]` 1\n[attach type]\n`[free]`";
        var files = new Dictionary<string, string> {
            ["equipment/equipment.lst"] = "101 `weapon/native-entry.equ`",
            ["stackable/stackable.lst"] = "3028 `material/piece_leather.stk`",
            ["equipment/weapon/native-entry.equ"] = equipment,
            ["stackable/material/piece_leather.stk"] = stackable
        };
        return AuctionTemplateCatalog.Load(path => files.TryGetValue(path, out var text) ? text : null,
            fingerprint, id => id == 101 ? EquipmentFile.Parse(equipment) : null,
            id => id == 3028 ? StackableItemFile.Parse(stackable) : null);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
