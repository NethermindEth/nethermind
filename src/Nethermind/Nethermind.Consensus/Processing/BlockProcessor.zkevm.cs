// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool ShouldCalculateReceiptsInBackground(TxReceipt[] receipts) => false;

    /// <inheritdoc/>
    /// <remarks>
    /// The DAO transition is block 1,920,000 on mainnet and the guest serves blocks far past it, from
    /// the fork range the build is compiled for, so the transition is assumed unreachable and nothing
    /// is applied here. Implementing it would name the Dao fork and pull it and its ancestor chain into
    /// the image.
    /// </remarks>
    private partial void ApplyDaoTransition(Block block)
    {
    }
}
