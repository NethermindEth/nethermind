// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.Tracing;

/// <summary>A block whose every transaction boundary the index can seed. Workers tracing its transactions each take
/// their own seed source, and the block is published for the next block's trace to chain onto once all of them are
/// done.</summary>
public interface ICoveredBlock : IDisposable
{
    /// <summary>A seed source for one worker: it folds the block's writes up to the transaction last asked for and
    /// extends in place while the worker's targets rise, so a worker taking transactions in ascending order folds each
    /// changeset once.</summary>
    IPrefixStateSeedSource CreateWorkerSeeds();

    /// <summary>Called once every transaction has been traced: the block's writes join the overlay that a trace of
    /// the next block reads through, so consecutive whole-block traces find what earlier blocks wrote in memory.</summary>
    void Complete();
}
