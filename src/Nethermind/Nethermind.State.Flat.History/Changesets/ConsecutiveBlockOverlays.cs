// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>The chains of consecutive blocks traced whole, kept by the block each ends at, so that a trace of the
/// next block chains onto the right one. A chain is cut when it reaches <see cref="DefaultMaxBlocks"/> blocks or
/// <see cref="DefaultMaxEntries"/> written entries, whichever comes first: beyond that a lookup walks too many nodes
/// and the chain holds too much, so the block starts a new one. With <see cref="Chains"/> heads retained the whole
/// cache holds at most a few times that many entries.</summary>
internal sealed class ConsecutiveBlockOverlays(int maxBlocks = ConsecutiveBlockOverlays.DefaultMaxBlocks, long maxEntries = ConsecutiveBlockOverlays.DefaultMaxEntries)
{
    public const int DefaultMaxBlocks = 128;
    public const long DefaultMaxEntries = 500_000;
    public const int Chains = 4;

    private readonly LruCache<ulong, RangeOverlay> _byLastBlock = new(Chains, nameof(ConsecutiveBlockOverlays));

    public RangeOverlay? EndingAt(ulong block, Hash256 hash) =>
        _byLastBlock.TryGet(block, out RangeOverlay? chain) && chain.LastHash == hash ? chain : null;

    /// <param name="excluded">Addresses the block may have written after its transactions; they never enter the chain.</param>
    public void Publish(BlockChangesets block, RangeOverlay? earlierBlocks, IReadOnlySet<AddressAsKey> excluded)
    {
        bool continues = earlierBlocks is not null && earlierBlocks.Length < maxBlocks && earlierBlocks.Entries < maxEntries;
        _byLastBlock.Set(block.Number, RangeOverlay.Extend(continues ? earlierBlocks : null, block.FoldAll(), block.Hash, excluded));
    }
}
