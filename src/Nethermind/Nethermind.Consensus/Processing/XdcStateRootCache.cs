// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
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
///
/// The remoteToLocal mapping allows any code that asks "do we have state for root X?"
/// (where X is geth's root from stored headers) to be redirected to our local root Y.
/// This prevents MissingTrieNodeException when loading state for subsequent blocks.
/// </summary>
public static class XdcStateRootCache
{
    private static readonly ConcurrentDictionary<long, Hash256> _computedStateRoots = new();
    private static readonly ConcurrentDictionary<Hash256, Hash256> _remoteToLocal = new();
    private static readonly ConcurrentDictionary<long, Hash256> _remoteRootsByBlock = new();

    /// <summary>
    /// Store the computed state root for a given block number, with the remote (geth) root for reverse lookup.
    /// </summary>
    public static void SetComputedStateRoot(long blockNumber, Hash256 localStateRoot, Hash256? remoteStateRoot = null)
    {
        _computedStateRoots[blockNumber] = localStateRoot;

        if (remoteStateRoot is not null && remoteStateRoot != localStateRoot)
        {
            _remoteToLocal[remoteStateRoot] = localStateRoot;
            _remoteRootsByBlock[blockNumber] = remoteStateRoot;
        }

        // Evict old entries to prevent unbounded memory growth (keep last 10000 blocks)
        if (blockNumber > 10000)
        {
            long evictBlock = blockNumber - 10000;
            _computedStateRoots.TryRemove(evictBlock, out _);
            if (_remoteRootsByBlock.TryRemove(evictBlock, out var oldRemote))
            {
                _remoteToLocal.TryRemove(oldRemote, out _);
            }
        }
    }

    /// <summary>
    /// Get the computed state root for a given block number.
    /// Returns null if no override exists.
    /// </summary>
    public static Hash256? GetComputedStateRoot(long blockNumber) =>
        _computedStateRoots.TryGetValue(blockNumber, out var root) ? root : null;

    /// <summary>
    /// Given a remote (geth) state root, find the locally-computed state root.
    /// This is the key fix: when Nethermind tries to load state for a stored header's root,
    /// it can be redirected to our local root that actually exists in the trie.
    /// </summary>
    public static Hash256? FindLocalRootForRemote(Hash256 remoteRoot) =>
        _remoteToLocal.TryGetValue(remoteRoot, out var localRoot) ? localRoot : null;

    /// <summary>
    /// Check if a given root is either a known local root or can be mapped from a remote root.
    /// Returns the usable root (local if mapping exists, original otherwise).
    /// </summary>
    public static Hash256 ResolveRoot(Hash256 root)
    {
        // Try remote→local mapping first
        if (_remoteToLocal.TryGetValue(root, out var localRoot))
            return localRoot;

        // Otherwise return as-is (might be local already)
        return root;
    }

    /// <summary>
    /// Get the latest cached block number and its computed root.
    /// Used for recovery after restart — if cache is empty but we need to find
    /// a valid state root to start from.
    /// </summary>
    public static (long blockNumber, Hash256 root)? GetLatestCachedRoot()
    {
        if (_computedStateRoots.IsEmpty) return null;
        long maxBlock = _computedStateRoots.Keys.Max();
        return (maxBlock, _computedStateRoots[maxBlock]);
    }

    /// <summary>
    /// Number of cached entries.
    /// </summary>
    public static int Count => _computedStateRoots.Count;
}
