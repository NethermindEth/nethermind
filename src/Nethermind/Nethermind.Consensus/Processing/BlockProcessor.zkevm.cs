// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool ShouldCalculateReceiptsInBackground(TxReceipt[] receipts) => false;

    /// <inheritdoc/>
    /// <remarks>
    /// The DAO transition is block 1,920,000 on mainnet and the guest serves post-merge blocks, so
    /// the transition can never apply. Refusing a block that would need it keeps the omission loud;
    /// implementing it would name the Dao fork and pull every fork singleton into the image.
    /// </remarks>
    private partial void ApplyDaoTransition(Block block)
    {
        if (_specProvider.DaoBlockNumber == block.Header.Number)
            throw new NotSupportedException("The zkEVM guest does not implement the DAO transition.");
    }
}
