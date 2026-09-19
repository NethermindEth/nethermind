// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>A covered block opened for a whole-block trace: rows in memory, the overlay of the blocks traced before
/// it when they are consecutive, and one seed source per worker.</summary>
internal sealed class CoveredBlock(BlockChangesets rows, RangeOverlay? earlierBlocks, ConsecutiveBlockOverlays? chain, IReadOnlySet<AddressAsKey> excluded) : ICoveredBlock
{
    // Retain at most one cache per size. Concurrent blocks always own different caches.
    private static readonly BlockReadCache?[] SpareReads = new BlockReadCache?[13];
    private readonly int _cacheBits = CacheBits(rows);
    private BlockReadCache? _reads = RentReads(CacheBits(rows));

    private static int CacheBits(BlockChangesets rows) => Math.Clamp(BitOperations.Log2((uint)Math.Max(1, rows.Rows.Length)) + 2, 2, 12);

    private static BlockReadCache RentReads(int bits) =>
        Interlocked.Exchange(ref SpareReads[bits], null) ?? new BlockReadCache(bits, bits + 2);

    public IPrefixStateSeedSource CreateWorkerSeeds() => new WorkerSeeds(rows, earlierBlocks,
        _reads ?? throw new ObjectDisposedException(nameof(CoveredBlock)));

    /// <summary>Nothing is published for a block the chain cannot describe exactly.</summary>
    public void Complete() => chain?.Publish(rows, earlierBlocks, excluded);

    public void Dispose()
    {
        BlockReadCache? reads = Interlocked.Exchange(ref _reads, null);
        if (reads is null) return;
        reads.Clear();
        Interlocked.CompareExchange(ref SpareReads[_cacheBits], reads, null);
    }

    /// <summary>One worker's view of the block: its own overlay, folded as far as the last target and extended in
    /// place while the targets rise. Nothing here is shared, so nothing here is locked.</summary>
    private sealed class WorkerSeeds : IPrefixStateSeedSource
    {
        private readonly BlockChangesets _rows;
        private readonly RangeOverlay? _earlierBlocks;
        private readonly BlockReadCache _reads;
        private readonly MidBlockOverlay _overlay = new();

        public WorkerSeeds(BlockChangesets rows, RangeOverlay? earlierBlocks, BlockReadCache reads)
        {
            _rows = rows;
            _earlierBlocks = earlierBlocks;
            _reads = reads;
            _overlay.Reset(rows.Number);
        }

        public bool Enabled => true;

        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot)
        {
            if ((ulong)block.Number != _rows.Number || block.Hash != _rows.Hash) return false;
            if (transactionIndex < 0 || transactionIndex > _rows.Rows.Length) return false;

            // Every row of the block was read when it was opened, so the fold cannot fail here.
            if (_overlay.Folded > transactionIndex) _overlay.Reset(_rows.Number);
            while (_overlay.Folded < transactionIndex) _overlay.Fold(_overlay.Folded, _rows.Rows[_overlay.Folded]);

            IStateReadOverlay view = new MidBlockReadOverlay(_overlay);
            slot.Arm(_earlierBlocks is null ? view : new ChainedReadOverlay(view, _earlierBlocks), NoLease.Instance, _reads);
            return true;
        }

        public bool TryOpenBlock(Block block, [NotNullWhen(true)] out ICoveredBlock? covered)
        {
            covered = null;
            return false;
        }
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }
}
