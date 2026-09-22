// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>The transactions-trie root of one payload, computed once by whichever thread reaches it first.</summary>
/// <remarks>
/// Queued on the thread pool so it overlaps the serial work that precedes the block's construction, and claimed
/// by the consumer when the pool has not picked it up yet. The root costs less than a wait for a queued work
/// item does on a pool busy with the previous block, and the thread that waits is the engine API's request
/// thread: whoever arrives first does the work, the other blocks only for as long as it takes.
/// </remarks>
/// <param name="encodedTransactions">The payload's transactions, which must not be mutated once this is started.</param>
internal sealed class TxRootComputation(byte[][] encodedTransactions)
{
    private readonly Lock _lock = new();
    private Hash256? _root;

    /// <summary>Computes the root, or returns at once when another thread has already computed it.</summary>
    /// <remarks>A thread that arrives while another is computing blocks until that one is done.</remarks>
    internal void Run()
    {
        if (Volatile.Read(ref _root) is not null) return;

        lock (_lock)
        {
            _root ??= TxTrie.CalculateRoot(encodedTransactions);
        }
    }

    /// <summary>The root, computed here when no thread has started it.</summary>
    internal Hash256 GetResult()
    {
        Run();
        return _root!;
    }
}
