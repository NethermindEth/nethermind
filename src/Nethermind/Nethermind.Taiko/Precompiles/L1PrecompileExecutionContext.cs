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

    /// <summary>
    /// Validates a block number against the current execution context's 256-block lookback window.
    /// </summary>
    /// <remarks>
    /// Returns <c>(true, null)</c> when no context is set. This permissive fall-through exists so that
    /// callers outside the block-processing pipeline — <c>eth_call</c>, <c>debug_traceCall</c>, unit tests,
    /// and preconf blocks where <c>L1BlockHeight == 0/null</c> and no anchor tx can be parsed — are not
    /// blocked by a missing context. Block-validation paths must call <see cref="Set"/> for every block
    /// they process (anchor tx at <c>i==0</c>); <see cref="BlockTransactionExecutors.L1PrecompileContextInitializer"/>
    /// is the canonical setter.
    /// </remarks>
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
