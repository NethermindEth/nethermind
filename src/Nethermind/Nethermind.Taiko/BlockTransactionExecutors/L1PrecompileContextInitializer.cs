// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Taiko.Precompiles;

namespace Nethermind.Taiko.BlockTransactionExecutors;

internal static class L1PrecompileContextInitializer
{
    // 4 selector + 32 uint48-word (first field of (uint48,bytes32,bytes32) tuple — minimum bytes to read anchorBlockId).
    private const int AnchorV4MinimumLength = 68;
    private const int AnchorV4CheckpointWordEnd = 36;

    /// <summary>
    /// Parses the AnchorV4 calldata and sets <see cref="L1PrecompileExecutionContext"/> so L1 precompile
    /// calls later in the block can enforce the 256-block lookback window. Reads calldata only — safe
    /// to call before the anchor tx executes.
    /// </summary>
    /// <returns><c>true</c> if the context was set; <c>false</c> for non-anchor txs, too-short data,
    /// non-V4 selectors, or when <c>L1BlockHeight</c> is null/zero (preconf block).</returns>
    public static bool TrySetFromAnchorTransaction(int txIndex, Transaction tx, long blockNumber, IL1OriginStore l1OriginStore)
    {
        if (txIndex != 0 || tx.Data.Length < AnchorV4MinimumLength)
            return false;

        ReadOnlySpan<byte> selector = tx.Data.Span[..4];
        bool isAnchorV4 =
            TaikoBlockValidator.AnchorV4Selector.AsSpan().SequenceEqual(selector)
            || TaikoBlockValidator.AnchorV4WithSignalSlotsSelector.AsSpan().SequenceEqual(selector);

        if (!isAnchorV4)
            return false;

        UInt256 anchorBlockId = new(tx.Data.Span[4..AnchorV4CheckpointWordEnd], isBigEndian: true);
        if (l1OriginStore.ReadL1Origin((UInt256)blockNumber)?.L1BlockHeight is long l1BlockHeight && l1BlockHeight > 0)
        {
            L1PrecompileExecutionContext.Set(anchorBlockId, (UInt256)l1BlockHeight);
            return true;
        }

        return false;
    }
}
