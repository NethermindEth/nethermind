// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Finality as seen by the flat backend: it never advances past the last pre-activation block.</summary>
/// <remarks>
/// Flat's persistence seeds its walk with the canonical state root at the finalized boundary. After activation
/// that root is a PBT root flat holds no snapshot for, so without this clamp the states up to the activation
/// parent would never be persisted (not even by the shutdown flush) and would vanish on restart.
/// </remarks>
internal sealed class MigrationFlatFinalizedStateProvider(IStateHeaderProvider inner, IBlockTree blockTree, ISpecProvider specProvider)
    : IStateHeaderProvider
{
    private ulong? _lastMerkleBlock;

    public ulong FinalizedBlockNumber
    {
        get
        {
            if (_lastMerkleBlock is { } clamp) return clamp;
            ulong finalized = inner.FinalizedBlockNumber;
            BlockHeader? header = blockTree.FindHeader(finalized, BlockTreeLookupOptions.RequireCanonical);
            if (header is null || !specProvider.GetSpec(header).IsEip8347Enabled) return finalized;
            while (header is not null && specProvider.GetSpec(header).IsEip8347Enabled)
                header = header.IsGenesis ? null : blockTree.FindHeader(header.ParentHash!, BlockTreeLookupOptions.RequireCanonical);
            ulong lastMerkle = header?.Number ?? 0;
            // The activation parent is finalized, so the clamp never moves again.
            _lastMerkleBlock = lastMerkle;
            return lastMerkle;
        }
    }

    public BlockHeader? GetFinalizedHeader(ulong blockNumber)
    {
        BlockHeader? header = blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        return header is null || specProvider.GetSpec(header).IsEip8347Enabled ? null : inner.GetFinalizedHeader(blockNumber);
    }

    public BlockHeader? FindParentHeader(BlockHeader target) => inner.FindParentHeader(target);
}
