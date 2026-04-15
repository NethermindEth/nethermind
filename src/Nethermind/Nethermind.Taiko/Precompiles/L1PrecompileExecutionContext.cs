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
}
