// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Static cache mapping block number → our computed state root for XDC chains.
/// XDC state roots diverge from geth at checkpoint blocks (every 900 blocks starting at 1800).
/// Since all subsequent blocks inherit the diverged state, every block from 1800 onwards
/// has a different state root than what geth computes.
///
/// Headers stored in the DB have geth's state root (from download).
/// This cache stores our computed state root so BeginScope can use it
/// instead of the stored header's root.
/// </summary>
public static class XdcStateRootCache
{
    private static readonly ConcurrentDictionary<long, Hash256> _computedStateRoots = new();

    /// <summary>
    /// Store the computed state root for a given block number.
    /// </summary>
    public static void SetComputedStateRoot(long blockNumber, Hash256 stateRoot)
    {
        _computedStateRoots[blockNumber] = stateRoot;

        // Evict old entries to prevent unbounded memory growth (keep last 2000 blocks)
        if (blockNumber > 2000)
        {
            _computedStateRoots.TryRemove(blockNumber - 2000, out _);
        }
    }

    /// <summary>
    /// Get the computed state root for a given block number.
    /// Returns null if no override exists.
    /// </summary>
    public static Hash256? GetComputedStateRoot(long blockNumber) =>
        _computedStateRoots.TryGetValue(blockNumber, out var root) ? root : null;
}
