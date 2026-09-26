// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Tracing;

/// <summary>A block whose access list seeds every one of its transactions. The list already holds the state before
/// each of them, so workers take transactions in any order without folding anything, and nothing needs publishing
/// for the next block: its parent state is the state this block committed.</summary>
internal sealed class BlockAccessListCoveredBlock(BlockAccessListPrefix prefix) : ICoveredBlock
{
    public IPrefixStateSeedSource CreateWorkerSeeds() => new WorkerSeeds(prefix);

    public void Complete()
    {
    }

    public void Dispose()
    {
    }

    /// <summary>One worker's seeds: a single overlay re-pointed at each transaction the worker takes, so tracing a
    /// block allocates one overlay per worker rather than one per transaction.</summary>
    private sealed class WorkerSeeds(BlockAccessListPrefix prefix) : IPrefixStateSeedSource
    {
        private readonly BlockAccessListReadOverlay _overlay = new(prefix);

        public bool Enabled => true;

        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot)
        {
            if (block.Hash != prefix.BlockHash || (uint)transactionIndex > (uint)prefix.TransactionCount) return false;

            slot.Disarm();
            _overlay.TransactionIndex = (uint)transactionIndex;
            slot.Arm(_overlay, _overlay);
            return true;
        }

        public bool TryOpenBlock(Block block, [NotNullWhen(true)] out ICoveredBlock? covered)
        {
            covered = null;
            return false;
        }
    }
}
