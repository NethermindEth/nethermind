// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Taiko.Precompiles;

namespace Nethermind.Taiko.BlockTransactionExecutors;

internal static class L1PrecompileContextInitializer
{
    private const int AnchorV4MinimumLength = 68;
    private const int AnchorV4CheckpointWordEnd = 36;

    public static void TrySetFromAnchorTransaction(int txIndex, Transaction tx, long blockNumber, IL1OriginStore l1OriginStore)
    {
        if (txIndex != 0 || tx.Data.Length < AnchorV4MinimumLength)
            return;

        ReadOnlySpan<byte> selector = tx.Data.Span[..4];
        bool isAnchorV4 =
            TaikoBlockValidator.AnchorV4Selector.AsSpan().SequenceEqual(selector)
            || TaikoBlockValidator.AnchorV4WithSignalSlotsSelector.AsSpan().SequenceEqual(selector);

        if (!isAnchorV4)
            return;

        UInt256 anchorBlockId = new(tx.Data.Span[4..AnchorV4CheckpointWordEnd], isBigEndian: true);
        if (l1OriginStore.ReadL1Origin((UInt256)blockNumber)?.L1BlockHeight is long l1BlockHeight && l1BlockHeight > 0)
        {
            L1PrecompileExecutionContext.Set(anchorBlockId, (UInt256)l1BlockHeight);
        }
    }
}
