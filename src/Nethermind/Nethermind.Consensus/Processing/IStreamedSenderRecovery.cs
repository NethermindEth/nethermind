// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Sender recovery that runs concurrently with a block's journey through the processing
/// pipeline, so execution never waits for the whole block's senders up front.
/// </summary>
public interface IStreamedSenderRecovery
{
    /// <summary>
    /// Starts recovering the block's senders off the caller's thread. The recovery must stay
    /// pure computation: an executor waiting on it deadlocks against anything it locks on,
    /// so work like transaction-pool lookups belongs to the caller.
    /// </summary>
    void Begin(Block block);

    /// <summary>
    /// Blocks until every sender of the block is recovered; a no-op without recovery in flight.
    /// </summary>
    void EnsureSendersRecovered(Block block, CancellationToken token);

    /// <summary>
    /// Blocks until the transaction is fully recovered or the block's recovery completes;
    /// a no-op when already recovered or without recovery in flight.
    /// </summary>
    void EnsureSenderRecovered(Block block, Transaction transaction);
}
