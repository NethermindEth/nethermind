// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.Blockchain;

/// <summary>Serves <c>BLOCKHASH</c> for a call whose block number is overridden from the ancestry of the block it runs on.</summary>
/// <remarks>
/// A block override changes only the number of the header it is applied to, which keeps the hash and parent hash of
/// the base block. Left to the inner provider, the base block's ancestry would be read at depths measured from the
/// overridden number. This follows Geth's <c>GetHashFn</c>: the window stays the 256 blocks below the overridden
/// number, hashes come from the base block's ancestry, and numbers at or above the base resolve to none, except the
/// base itself when the override is its immediate successor (go-ethereum#32175).
/// </remarks>
public sealed class BlockNumberOverrideBlockhashProvider(IBlockhashProvider inner, IBlockFinder blockFinder) : IBlockhashProvider
{
    private Anchor? _anchor;

    public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
    {
        Anchor anchor = GetAnchor(currentBlock);
        if (anchor.Base is not { } @base) return inner.GetBlockhash(currentBlock, number, spec);

        if (number >= currentBlock.Number || currentBlock.Number - number > BlockhashProvider.MaxDepth) return null;
        if (number >= @base.Number) return number == @base.Number && currentBlock.Number == @base.Number + 1 ? @base.Hash : null;
        return anchor.Ancestor is null ? null : inner.GetBlockhash(anchor.Ancestor, number, spec);
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => GetAnchor(currentBlock) switch
    {
        { Base: null } => inner.Prefetch(currentBlock, token),
        { Ancestor: { } ancestor } => inner.Prefetch(ancestor, token),
        _ => Task.CompletedTask
    };

    private Anchor GetAnchor(BlockHeader currentBlock)
    {
        Anchor? anchor = _anchor;
        // The override may be applied in place to a header that already served the transactions before the call.
        if (anchor is null || !ReferenceEquals(anchor.Current, currentBlock) || anchor.Number != currentBlock.Number)
        {
            _anchor = anchor = CreateAnchor(currentBlock);
        }

        return anchor;
    }

    private Anchor CreateAnchor(BlockHeader currentBlock)
    {
        BlockHeader? @base = currentBlock.Hash is null ? null : blockFinder.FindHeader(currentBlock.Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        return @base is null || @base.Number == currentBlock.Number
            ? new Anchor(currentBlock, currentBlock.Number, null, null)
            : new Anchor(currentBlock, currentBlock.Number, @base, @base.Number < currentBlock.Number ? @base : FindAncestor(@base, currentBlock.Number));
    }

    /// <summary>Finds the ancestor of <paramref name="header"/> at <paramref name="number"/>, from which the inner provider reaches the whole window.</summary>
    private BlockHeader? FindAncestor(BlockHeader? header, ulong number)
    {
        while (header is not null && header.Number > number && !blockFinder.IsMainChain(header))
        {
            header = blockFinder.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        return header is null || header.Number == number ? header : blockFinder.FindHeader(number, BlockTreeLookupOptions.RequireCanonical);
    }

    /// <param name="Current">The header the call runs on.</param>
    /// <param name="Number">The number of <paramref name="Current"/> when the anchor was created.</param>
    /// <param name="Base">The block <paramref name="Current"/> was cloned from, or <c>null</c> when its number is not overridden.</param>
    /// <param name="Ancestor">The header of the base block's ancestry the inner provider serves the window from.</param>
    private sealed record Anchor(BlockHeader Current, ulong Number, BlockHeader? Base, BlockHeader? Ancestor);
}
