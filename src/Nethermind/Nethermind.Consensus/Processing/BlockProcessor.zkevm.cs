// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    private BlockHeader? _suggestedHeader;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static partial bool ShouldCalculateReceiptsInBackground(TxReceipt[] receipts) => false;

    partial void SetSuggestedHeader(BlockHeader header) => _suggestedHeader = header;

    /// <inheritdoc/>
    /// <remarks>
    /// Takes the hash of the suggested header when the two headers encode the same, see
    /// <see cref="ProcessedHeaderHash.Calculate"/>. The stateless executor checks the hash of the suggested header before
    /// processing, for both of its inputs.
    /// </remarks>
    private partial Hash256 CalculateHeaderHash(BlockHeader header) =>
        _suggestedHeader is { } suggested ? ProcessedHeaderHash.Calculate(header, suggested) : header.CalculateHash();

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
