// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Int256;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// Thread-local holder for the current block's anchor and L1 origin IDs.
/// Set after the anchor tx succeeds; read by L1 precompile providers to enforce
/// the 256-block lookback window. Mirrors alethia-reth's
/// <c>CURRENT_ANCHOR_BLOCK_ID</c> / <c>CURRENT_L1_ORIGIN_BLOCK_ID</c> pattern.
/// </summary>
public static class L1PrecompileExecutionContext
{
    private static readonly AsyncLocal<(UInt256 Anchor, UInt256 L1Origin)?> _ctx = new();

    public static void Set(UInt256 anchor, UInt256 l1Origin) => _ctx.Value = (anchor, l1Origin);
    public static (UInt256 Anchor, UInt256 L1Origin)? Get() => _ctx.Value;
    public static void Clear() => _ctx.Value = null;

    public static (bool IsValid, string? Reason) ValidateBlockRange(UInt256 blockNumber)
    {
        if (Get() is not { } ctx)
            return (true, null);

        if (ctx.L1Origin < ctx.Anchor)
            return (false, $"context invariant: l1Origin={ctx.L1Origin} < anchor={ctx.Anchor}");

        if (blockNumber > ctx.L1Origin)
            return (false, $"block {blockNumber} > l1Origin {ctx.L1Origin}");

        if (ctx.L1Origin - blockNumber > (UInt256)L1PrecompileConstants.MaxBlockLookback)
        {
            return (
                false,
                $"block {blockNumber} exceeds {L1PrecompileConstants.MaxBlockLookback}-block lookback from l1Origin {ctx.L1Origin}"
            );
        }

        return (true, null);
    }
}
