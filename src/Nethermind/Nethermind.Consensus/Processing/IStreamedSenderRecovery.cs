// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Sender recovery that runs concurrently with a block's journey through the processing
/// pipeline, so execution never waits for the whole block's senders up front.
/// </summary>
public interface IStreamedSenderRecovery
{
    /// <summary>
    /// Starts recovering the block's senders off the caller's thread. At most one call per
    /// <see cref="BlockBody"/> instance — a second would replace the recovery a join may
    /// already be reading. The recovery must stay pure computation: an executor waiting on it
    /// deadlocks against anything it locks on, so work like transaction-pool lookups belongs
    /// to the caller.
    /// </summary>
    void Begin(Block block);

    /// <summary>
    /// Blocks until the block's in-flight recovery has published the transaction at
    /// <paramref name="index"/>; a no-op for blocks without recovery in flight.
    /// </summary>
    void EnsureSenderRecovered(Block block, Transaction transaction, int index);
}
