// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Lets a consumer that tolerates a not-yet-recovered sender find the background recovery still running for a block's
/// transactions, so it can wait on that instead of polling the transactions.
/// </summary>
public interface ISenderRecoveryTracker
{
    /// <summary>
    /// The recovery still running for <paramref name="txs"/>, or <c>null</c> when no sender will land later: every
    /// transaction's sender is then final, a null one being an invalid signature.
    /// </summary>
    ISenderRecoveryProgress? GetInFlight(Transaction[] txs);
}

/// <summary>Progress of one background sender recovery.</summary>
public interface ISenderRecoveryProgress
{
    /// <summary>
    /// Senders recovered so far. Published in batches while the recovery runs, so a change means senders have landed
    /// since the last read but the value itself trails the transactions; it is exact once <see cref="IsCompleted"/>.
    /// </summary>
    int Recovered { get; }

    bool IsCompleted { get; }

    /// <summary>Blocks until the recovery completes or the timeout elapses; <c>true</c> once it has completed.</summary>
    bool WaitForCompletion(int millisecondsTimeout);
}
