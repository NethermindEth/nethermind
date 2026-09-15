// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>A covered block opened for a whole-block trace: rows in memory, the overlay of the blocks traced before
/// it when they are consecutive, and one seed source per worker.</summary>
internal sealed class CoveredBlock(BlockChangesets rows, RangeOverlay? earlierBlocks, ConsecutiveBlockOverlays chain) : ICoveredBlock
{
    public IPrefixStateSeedSource CreateWorkerSeeds() => new WorkerSeeds(rows, earlierBlocks);

    public void Complete() => chain.Publish(rows, earlierBlocks);

    public void Dispose()
    {
    }

    /// <summary>One worker's view of the block: its own overlay, folded as far as the last target and extended in
    /// place while the targets rise. Nothing here is shared, so nothing here is locked.</summary>
    private sealed class WorkerSeeds : IPrefixStateSeedSource
    {
        private readonly BlockChangesets _rows;
        private readonly RangeOverlay? _earlierBlocks;
        private readonly MidBlockOverlay _overlay = new();

        public WorkerSeeds(BlockChangesets rows, RangeOverlay? earlierBlocks)
        {
            _rows = rows;
            _earlierBlocks = earlierBlocks;
            _overlay.Reset(rows.Number);
        }

        public bool Enabled => true;

        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot)
        {
            if ((ulong)block.Number != _rows.Number || block.Hash != _rows.Hash) return false;
            if (transactionIndex < 0 || transactionIndex > _rows.Rows.Length) return false;

            if (_overlay.Folded > transactionIndex) _overlay.Reset(_rows.Number);
            while (_overlay.Folded < transactionIndex) _overlay.Fold(_overlay.Folded, _rows.Rows[_overlay.Folded]);

            IStateReadOverlay view = new MidBlockReadOverlay(_overlay);
            slot.Arm(_earlierBlocks is null ? view : new ChainedReadOverlay(view, _earlierBlocks), NoLease.Instance);
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
