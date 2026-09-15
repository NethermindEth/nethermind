// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>The chains of consecutive blocks traced whole, kept by the block each ends at, so that a trace of the
/// next block chains onto the right one. A chain is cut at <see cref="MaxBlocks"/>: beyond that a lookup walks too
/// many nodes and the chain holds too much, so the block starts a new one.</summary>
internal sealed class ConsecutiveBlockOverlays(int maxBlocks = ConsecutiveBlockOverlays.DefaultMaxBlocks)
{
    public const int DefaultMaxBlocks = 128;
    private const int Chains = 8;

    private readonly LruCache<ulong, RangeOverlay> _byLastBlock = new(Chains, nameof(ConsecutiveBlockOverlays));
    private readonly Lock _lock = new();

    public RangeOverlay? EndingAt(ulong block, Hash256 hash)
    {
        lock (_lock)
        {
            return _byLastBlock.TryGet(block, out RangeOverlay? chain) && chain.LastHash == hash ? chain : null;
        }
    }

    public void Publish(BlockChangesets block, RangeOverlay? earlierBlocks)
    {
        RangeOverlay chain = RangeOverlay.Extend(earlierBlocks is { } e && e.Length < maxBlocks ? e : null, block.FoldAll(), block.Hash);
        lock (_lock)
        {
            _byLastBlock.Set(block.Number, chain);
        }
    }
}
