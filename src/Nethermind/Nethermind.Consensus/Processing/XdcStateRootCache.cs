// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Persistent cache mapping block number → our computed state root for XDC chains.
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
///
/// PERSISTENCE: The latest mapping (last block's local+remote roots) is saved to disk
/// every 100 blocks. On restart, it loads the saved mapping so the cache isn't empty.
/// </summary>
public static class XdcStateRootCache
{
    private static readonly ConcurrentDictionary<long, Hash256> _computedStateRoots = new();
    private static readonly ConcurrentDictionary<Hash256, Hash256> _remoteToLocal = new();
    private static readonly ConcurrentDictionary<long, Hash256> _remoteRootsByBlock = new();

    private static string? _persistPath;
    private static long _lastPersistedBlock;
    private const int PersistEveryNBlocks = 100;
    private static readonly object _persistLock = new();
    private static bool _loaded;

    /// <summary>
    /// Set the path for persistence file. Call once during startup.
    /// </summary>
    public static void SetPersistPath(string dataDir)
    {
        _persistPath = Path.Combine(dataDir, "xdc-state-root-cache.json");
        LoadFromDisk();
    }

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

        // Persist to disk periodically
        if (blockNumber - _lastPersistedBlock >= PersistEveryNBlocks)
        {
            PersistToDisk(blockNumber, localStateRoot, remoteStateRoot);
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
    /// </summary>
    public static Hash256? FindLocalRootForRemote(Hash256 remoteRoot) =>
        _remoteToLocal.TryGetValue(remoteRoot, out var localRoot) ? localRoot : null;

    /// <summary>
    /// Check if a given root is either a known local root or can be mapped from a remote root.
    /// Returns the usable root (local if mapping exists, original otherwise).
    /// </summary>
    public static Hash256 ResolveRoot(Hash256 root)
    {
        if (_remoteToLocal.TryGetValue(root, out var localRoot))
            return localRoot;
        return root;
    }

    /// <summary>
    /// Get the latest cached block number and its computed root.
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

    private static void PersistToDisk(long blockNumber, Hash256 localRoot, Hash256? remoteRoot)
    {
        if (_persistPath is null) return;

        lock (_persistLock)
        {
            try
            {
                // Persist the FULL remote→local mapping, not just the latest entry
                var data = new FullCacheEntry
                {
                    LastBlockNumber = blockNumber,
                    RemoteToLocalMappings = _remoteToLocal.ToDictionary(
                        kvp => kvp.Key.ToString(),
                        kvp => kvp.Value.ToString()
                    )
                };
                var json = JsonSerializer.Serialize(data);
                File.WriteAllText(_persistPath, json);
                _lastPersistedBlock = blockNumber;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"XdcStateRootCache: Persist failed: {ex.Message}");
            }
        }
    }

    private static void LoadFromDisk()
    {
        if (_loaded || _persistPath is null || !File.Exists(_persistPath)) return;
        _loaded = true;

        try
        {
            var json = File.ReadAllText(_persistPath);
            
            // Try new format first
            try
            {
                var data = JsonSerializer.Deserialize<FullCacheEntry>(json);
                if (data?.RemoteToLocalMappings is not null)
                {
                    foreach (var kvp in data.RemoteToLocalMappings)
                    {
                        var remote = new Hash256(kvp.Key);
                        var local = new Hash256(kvp.Value);
                        _remoteToLocal[remote] = local;
                    }
                    _lastPersistedBlock = data.LastBlockNumber;
                    Console.WriteLine($"XdcStateRootCache: Loaded {_remoteToLocal.Count} mappings from disk (up to block {data.LastBlockNumber})");
                    return;
                }
            }
            catch { /* Fall through to old format */ }
            
            // Fallback to old single-entry format for backwards compatibility
            var oldData = JsonSerializer.Deserialize<CacheEntry>(json);
            if (oldData?.LocalRoot is not null)
            {
                var localRoot = new Hash256(oldData.LocalRoot);
                _computedStateRoots[oldData.BlockNumber] = localRoot;

                if (oldData.RemoteRoot is not null)
                {
                    var remoteRoot = new Hash256(oldData.RemoteRoot);
                    _remoteToLocal[remoteRoot] = localRoot;
                    _remoteRootsByBlock[oldData.BlockNumber] = remoteRoot;
                }

                _lastPersistedBlock = oldData.BlockNumber;
                Console.WriteLine($"XdcStateRootCache: Loaded legacy format — block {oldData.BlockNumber}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XdcStateRootCache: Failed to load from disk: {ex.Message}");
        }
    }

    private class CacheEntry
    {
        public long BlockNumber { get; set; }
        public string? LocalRoot { get; set; }
        public string? RemoteRoot { get; set; }
    }
    
    private class FullCacheEntry
    {
        public long LastBlockNumber { get; set; }
        public Dictionary<string, string>? RemoteToLocalMappings { get; set; }
    }
}
