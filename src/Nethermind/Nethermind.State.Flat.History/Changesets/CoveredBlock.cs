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
    private readonly List<MidBlockOverlay> _overlays = [];
    private int _activeReads;
    private bool _disposed;

    private static int CacheBits(BlockChangesets rows) => Math.Clamp(BitOperations.Log2((uint)Math.Max(1, rows.Rows.Length)) + 2, 2, 12);

    private static BlockReadCache RentReads(int bits) =>
        Interlocked.Exchange(ref SpareReads[bits], null) ?? new BlockReadCache(bits, bits + 2);

    public IPrefixStateSeedSource CreateWorkerSeeds()
    {
        using Lock.Scope scope = _readsLock.EnterScope();
        ObjectDisposedException.ThrowIf(_disposed, this);
        MidBlockOverlay overlay = MidBlockOverlay.Rent(rows.Number);
        _overlays.Add(overlay);
        return new WorkerSeeds(rows, earlierBlocks, this, overlay);
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

    /// <summary>Runs once the block is disposed and no lease is active. A worker folds and reads its overlay only
    /// under a lease, so no overlay given back here can still be in use.</summary>
    private void ReturnReads()
    {
        BlockReadCache? reads = _reads;
        _reads = null;
        if (reads is not null && reads.Clear())
            Interlocked.CompareExchange(ref SpareReads[_cacheBits], reads, null);

        foreach (MidBlockOverlay overlay in _overlays) MidBlockOverlay.Return(overlay);
        _overlays.Clear();
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
    private sealed class WorkerSeeds(BlockChangesets rows, RangeOverlay? earlierBlocks, CoveredBlock owner, MidBlockOverlay overlay) : IPrefixStateSeedSource
    {
        public bool Enabled => true;

        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot)
        {
            if ((ulong)block.Number != rows.Number || block.Hash != rows.Hash) return false;
            if (transactionIndex < 0 || transactionIndex > rows.Rows.Length) return false;

            // The lease comes first: the overlay goes back to the spares once the block is disposed and no lease is
            // active, so it is folded only while one is held.
            ReadLease? lease = owner.TryLeaseReads();
            if (lease is null) return false;
            try
            {
                // Every row of the block was read when it was opened, so the fold cannot fail here.
                if (overlay.Folded > transactionIndex) overlay.Reset(rows.Number);
                while (overlay.Folded < transactionIndex) overlay.Fold(overlay.Folded, rows.Rows[overlay.Folded]);

                slot.Arm(new MidBlockReadOverlay(overlay), lease, lease.Cache, earlierBlocks);
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
