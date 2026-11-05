using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders.Auction;

internal static class AuctionMutationReplyBuilder
{
    // Both native search senders feed the same ordinary result cache. The
    // complete auction ACK registration maps result BA to0111F970, not BB.
    internal static CmdPacketTypeA21 ReplyCommand(CmdPacketTypeA21 request)
        => request == CmdPacketTypeA21.AUCTION_SEARCH_BY_NOITEMKEY
            ? CmdPacketTypeA21.AUCTION_SEARCH_BY_ITEMKEY : request;

    internal static byte[] Success(CmdPacketTypeA21 command)
        => command switch {
            CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE => new byte[] { 1 },
            CmdPacketTypeA21.AUCTION_REGIST_ITEM or CmdPacketTypeA21.AUCTION_REGIST_CANCEL
                or CmdPacketTypeA21.AUCTION_BIDDING => new byte[] { 1, 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    // Gold consignment shares 0x00B7 with the item auction, so the ACK handler must
    // be told which window to update. Native 0x01126CD0 success branch:
    //     0x01126D8F  lea edx,[ebp-0x405]; push 1; push edx; call 0x27C5230  ; read u8
    //     0x01126DA6  cmp al,1
    //     0x01126DA8  jne 0x1126E20          ; != 1 -> control 7 -> 0x2345CB0 (ITEM)
    //     0x01126DB5  push 8 ; 0x1458960     ; == 1 -> control 8 -> consignment
    // Sending {1,0} therefore pops "拍卖物品的上架" instead of the consignment
    // confirmation. The consignment selector byte must be 1.
    internal const byte ConsignmentSelector = 1;

    internal static byte[] ConsignmentSuccess()
        => new byte[] { 1, ConsignmentSelector };

    // Result codes the native auction-family ACK handlers actually recognise.
    //
    // 0x00B7 ACK 0x01126CD0 (siblings 0x01126E50 / 0x01126FB0) and the 0x00B6 ACK
    // 0x0112CBB0 (failure path 0x0112CF7D) accept:
    //     cmp bl, 0xd2 / 0x69 / 0x7e / 0xce / 0x7b / 0x89
    // Those six reach the consignment controller but all resolve to the SILENT
    // outcome: controller 0x1455D60 indexes a jump table by the code
    //     index  = byte[0x14560D0 + (code-11)] ; target = dword[0x1456074 + idx*4]
    // and each of the six maps to index 22 -> 0x01456061, which returns without
    // showing any prompt.
    //
    // 22 codes DO carry a dedicated prompt; the block 0x8C..0x9E is one contiguous
    // message set. Verified in game: 0x9E = "当前拍卖行服务器的处理量过多，请稍后再试"
    // (a BUSY message). The rest of the block is NOT mapped to meaning yet - the
    // dstr payloads are encrypted and these numbers are reused ~2000 times across
    // unrelated subsystems, so their text cannot be inferred from static analysis.
    //
    // Insufficient gold now uses 0x90, confirmed in game: "所持金币不足，将无法进行上架或竞拍".
    // The rest of the block (0x8C..0x9E) is an unrelated grab-bag of generic prompts
    // (0x8C = "当前拍卖行为关闭状态", 0x9E = "处理量过多"), so codes must be verified
    // one by one in game rather than inferred. GOLD_CONSIGN_FAIL_CODE remains as an
    // override for mapping further codes.
    internal const byte GenericFailure = 0x7B;   // 123 (silent)
    internal const byte BusyFailure = 0xCE;      // 206 (silent)

    /// <summary>
    /// Insufficient gold. Verified in game: the client shows
    /// "所持金币不足，将无法进行上架或竞拍".
    /// </summary>
    internal const byte InsufficientGoldFailure = 0x90;

    /// <summary>Busy / throughput limit. Verified: "当前拍卖行服务器的处理量过多，请稍后再试".</summary>
    internal const byte ThroughputFailure = 0x9E;

    /// <summary>Invalid bid/buyout value. Verified: "最低竞拍价或一口价中输入了错误数值".</summary>
    internal const byte InvalidPriceFailure = 0x98;

    /// <summary>Listing count over the limit. Verified: "已超过上架数量上限，需要拍卖行优惠券才能继续上架".</summary>
    internal const byte ListingCapFailure = 0x97;

    /// <summary>Target item no longer exists. Verified: "拍卖行中不存在该物品".</summary>
    internal const byte ItemMissingFailure = 0x96;

    /// <summary>Service disabled. Verified: "当前拍卖行为关闭状态".</summary>
    internal const byte ServiceDisabledFailure = 0x8C;

    /// <summary>
    /// Cannot bid on / buy your own listing. Verified code table entry 0x9B
    /// maps to "无法竞拍自己".
    /// </summary>
    internal const byte OwnListingFailure = 0x9B;

    /// <summary>
    /// Insufficient 点券 (cash cera) for a purchase. 0x90 is the verified
    /// "所持金币不足" prompt; for cera the same insufficiency prompt is the closest
    /// verified string, so it is reused rather than inventing an unmapped code.
    /// </summary>
    internal const byte InsufficientCeraFailure = 0x90;

    /// <summary>
    /// Optional override for the consignment rejection code. Returns 0 (meaning
    /// "use the caller-selected default") unless GOLD_CONSIGN_FAIL_CODE names a code
    /// inside the 0x8C..0x9E block that renders a prompt.
    /// </summary>
    internal static byte ConsignmentFailureCode
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("GOLD_CONSIGN_FAIL_CODE");
            if (byte.TryParse(raw, out var v) && v >= 0x0B && v <= 0xD6) return v;
            return 0;
        }
    }

    // ---- temporary code-mapping harness -------------------------------------
    // GOLD_CONSIGN_FAIL_SWEEP="140,141,142,..." makes each consignment rejection
    // advance to the next code and log its sequence number, so a single in-game
    // session can map the whole prompt table by clicking repeatedly.
    private static readonly object SweepGate = new();
    private static int sweepIndex;

    internal static byte NextSweepCode(out int sequence, out int total)
    {
        sequence = 0;
        total = 0;
        var raw = Environment.GetEnvironmentVariable("GOLD_CONSIGN_FAIL_SWEEP");
        if (string.IsNullOrWhiteSpace(raw)) return 0;

        var parts = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var codes = new List<byte>();
        foreach (var p in parts)
        {
            if (byte.TryParse(p.Trim(), out var v)) codes.Add(v);
        }
        if (codes.Count == 0) return 0;

        lock (SweepGate)
        {
            total = codes.Count;
            var idx = Math.Min(sweepIndex, codes.Count - 1);
            sequence = idx + 1;
            sweepIndex++;
            return codes[idx];
        }
    }

    // Generic vs busy avoid assigning a command-specific error the wrong meaning.
    // PIECE has no ordinary selector in either result branch.
    internal static byte[] Failure(CmdPacketTypeA21 command, bool busy = false)
        => command == CmdPacketTypeA21.AUCTION_BUY_ITEM_APIECE
            ? new byte[] { 0, busy ? BusyFailure : GenericFailure }
            : new byte[] { 0, busy ? BusyFailure : GenericFailure, 0 };

    /// <summary>
    /// Consignment rejection with an explicit prompt code (e.g. insufficient gold).
    /// Precedence: sweep list (mapping harness) > GOLD_CONSIGN_FAIL_CODE > promptCode.
    /// </summary>
    internal static byte[] ConsignmentRejection(CmdPacketTypeA21 command, byte promptCode)
    {
        var code = NextSweepCode(out var sequence, out var total);
        if (code != 0)
        {
            FileLogger.Log($"[GoldConsignProbe] sweep {sequence}/{total} code=0x{code:X2} ({code})");
        }
        else
        {
            code = ConsignmentFailureCode;
            if (code == 0) code = promptCode;
        }
        return new byte[] { 0, code, 0 };
    }
}
