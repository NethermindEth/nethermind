// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Coordinates sender recovery that runs concurrently with a block's journey through the
/// processing pipeline (the engine <c>newPayload</c> path), so that execution never waits for
/// the whole block's senders up front.
/// </summary>
public interface IStreamedSenderRecovery
{
    /// <summary>
    /// Starts recovering the senders of the block's transactions off the caller's thread.
    /// The recovery is pure computation by contract: work that can block on a lock (for example
    /// transaction-pool lookups) must be done by the caller beforehand, because an executor
    /// waiting on this recovery deadlocks against anything the recovery itself waits on.
    /// Senders already present on the transactions are kept.
    /// </summary>
    void Begin(Block block);

    /// <summary>
    /// Blocks until every sender of the block's transactions is recovered. A no-op for blocks
    /// without recovery in flight. For executors that need all senders before dispatch.
    /// </summary>
    void EnsureSendersRecovered(Block block, CancellationToken token);

    /// <summary>
    /// Blocks until the transaction's sender is recovered or the block's recovery completes.
    /// A no-op when the sender is already present or the block has no recovery in flight.
    /// A sender still missing afterwards means recovery genuinely failed for that transaction,
    /// and execution rejects it the same way as on the non-streamed path.
    /// </summary>
    void EnsureSenderRecovered(Block block, Transaction transaction);
}
