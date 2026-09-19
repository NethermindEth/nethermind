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
    // Retain at most one cache per size, including stale key/value references until overwritten.
    // Active overlay leases prevent reuse even if the covered block is disposed early.
    private static readonly BlockReadCache?[] SpareReads = new BlockReadCache?[13];
    private readonly int _cacheBits = CacheBits(rows);
    private BlockReadCache? _reads = RentReads(CacheBits(rows));
    private readonly Lock _readsLock = new();
    private int _activeReads;
    private bool _disposed;

    private static int CacheBits(BlockChangesets rows) => Math.Clamp(BitOperations.Log2((uint)Math.Max(1, rows.Rows.Length)) + 2, 2, 12);

    private static BlockReadCache RentReads(int bits) =>
        Interlocked.Exchange(ref SpareReads[bits], null) ?? new BlockReadCache(bits, bits + 2);

    public IPrefixStateSeedSource CreateWorkerSeeds()
    {
        using Lock.Scope scope = _readsLock.EnterScope();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new WorkerSeeds(rows, earlierBlocks, this);
    }

    /// <summary>Nothing is published for a block the chain cannot describe exactly.</summary>
    public void Complete() => chain?.Publish(rows, earlierBlocks, excluded);

    public void Dispose()
    {
        using Lock.Scope scope = _readsLock.EnterScope();
        if (_disposed) return;
        _disposed = true;
        if (_activeReads == 0) ReturnReads();
    }

    private void ReturnReads()
    {
        BlockReadCache? reads = _reads;
        _reads = null;
        if (reads is not null && reads.Clear())
            Interlocked.CompareExchange(ref SpareReads[_cacheBits], reads, null);
    }

    private ReadLease? TryLeaseReads()
    {
        using Lock.Scope scope = _readsLock.EnterScope();
        if (_disposed) return null;
        _activeReads++;
        return new ReadLease(this, _reads!);
    }

    private void ReleaseReads()
    {
        using Lock.Scope scope = _readsLock.EnterScope();
        if (--_activeReads == 0 && _disposed) ReturnReads();
    }

    private sealed class ReadLease(CoveredBlock owner, BlockReadCache cache) : IDisposable
    {
        private CoveredBlock? _owner = owner;
        public BlockReadCache Cache => cache;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseReads();
    }

    /// <summary>One worker's view of the block: its own overlay, folded as far as the last target and extended in
    /// place while the targets rise. Nothing here is shared, so nothing here is locked.</summary>
    private sealed class WorkerSeeds : IPrefixStateSeedSource
    {
        private readonly BlockChangesets _rows;
        private readonly RangeOverlay? _earlierBlocks;
        private readonly CoveredBlock _owner;
        private readonly MidBlockOverlay _overlay = new();

        public WorkerSeeds(BlockChangesets rows, RangeOverlay? earlierBlocks, CoveredBlock owner)
        {
            _rows = rows;
            _earlierBlocks = earlierBlocks;
            _owner = owner;
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
            ReadLease? lease = _owner.TryLeaseReads();
            if (lease is null) return false;
            try
            {
                slot.Arm(_earlierBlocks is null ? view : new ChainedReadOverlay(view, _earlierBlocks), lease, lease.Cache);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            return true;
        }

        public bool TryOpenBlock(Block block, [NotNullWhen(true)] out ICoveredBlock? covered)
        {
            covered = null;
            return false;
        }
    }

}
