// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.BalanceViewer.Plugin;

/// <summary>Result and progress of one account's node-side token-detection scan on one chain.</summary>
/// <param name="Contracts">ERC-20 contract addresses the account has transferred to/from (candidates
/// the client confirms with a live balance check). Discovered in-process via the node's log index.</param>
/// <param name="NftContracts">ERC-721 / ERC-1155 contract addresses the account has transferred/received
/// (candidates the client confirms via interface + ownership checks).</param>
/// <param name="ScannedFrom">Lowest block scanned so far (inclusive) — a resumable cursor.</param>
/// <param name="Head">Head block when the scan started, so the covered range is known.</param>
/// <param name="Complete">True once the sweep reached the bottom of the node's retained history.</param>
/// <param name="UpdatedMs">Unix-milliseconds timestamp of the last update.</param>
public sealed record DetectionEntry(
    IReadOnlyList<string> Contracts, IReadOnlyList<string> NftContracts, long ScannedFrom, long Head, bool Complete, long UpdatedMs);

/// <summary>
/// Node-side cache of per-account token-detection results and scan progress.
/// </summary>
/// <remarks>
/// Shared across every client of this node and persisted to the data directory, so a completed
/// (or partially completed) scan is never repeated on reload or from another device. Keyed by
/// chain id and lower-cased address. Only data derived from the node itself is stored.
/// </remarks>
public interface IDetectionCache
{
    /// <summary>Returns the cached entry for the account on the chain, or null if never scanned.</summary>
    DetectionEntry? Get(long chainId, string address);

    /// <summary>Stores (overwrites) the entry for the account on the chain and persists it.</summary>
    void Put(long chainId, string address, DetectionEntry entry);

    /// <summary>Drops every cached entry and the backing file. Intended for developer/diagnostic use.</summary>
    void Clear();
}

/// <inheritdoc cref="IDetectionCache"/>
public sealed class DetectionCache : IDetectionCache
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Bounds the on-disk cache. Each entry is small; eviction is LRU by last update so the actively
    // viewed working set stays cached and does not thrash (an evicted entry only re-scans if a new
    // client session revisits that address). The contract list is capped so a pathologically active
    // address can't bloat one entry.
    private const int DefaultMaxEntries = 10_000;
    private const int DefaultMaxContractsPerEntry = 2_000;

    private readonly ConcurrentDictionary<string, DetectionEntry> _entries = new();
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _fileLock = new();
    private readonly int _maxEntries;
    private readonly int _maxContractsPerEntry;

    public DetectionCache(string dbPath, ILogManager logManager, int maxEntries = DefaultMaxEntries, int maxContractsPerEntry = DefaultMaxContractsPerEntry)
    {
        _logger = logManager.GetClassLogger<DetectionCache>();
        _path = Path.Combine(dbPath, "balance-viewer-detection.json");
        _maxEntries = maxEntries;
        _maxContractsPerEntry = maxContractsPerEntry;
        Load();
    }

    private static string Key(long chainId, string address) => chainId + ":" + address.ToLowerInvariant();

    public DetectionEntry? Get(long chainId, string address) =>
        _entries.TryGetValue(Key(chainId, address), out DetectionEntry? entry) ? entry : null;

    public void Put(long chainId, string address, DetectionEntry entry)
    {
        if (entry.Contracts.Count > _maxContractsPerEntry)
        {
            entry = entry with { Contracts = entry.Contracts.Take(_maxContractsPerEntry).ToArray() };
        }
        if (entry.NftContracts.Count > _maxContractsPerEntry)
        {
            entry = entry with { NftContracts = entry.NftContracts.Take(_maxContractsPerEntry).ToArray() };
        }
        _entries[Key(chainId, address)] = entry;
        EvictIfNeeded();
        Save();
    }

    public void Clear()
    {
        _entries.Clear();
        try
        {
            lock (_fileLock)
            {
                File.Delete(_path);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not delete balance-viewer detection cache: {e.Message}");
        }
    }

    // LRU eviction: while over the entry cap, drop the least-recently-updated entry
    private void EvictIfNeeded()
    {
        while (_entries.Count > _maxEntries)
        {
            string? oldest = null;
            long oldestMs = long.MaxValue;
            foreach ((string key, DetectionEntry value) in _entries)
            {
                if (value.UpdatedMs < oldestMs) { oldestMs = value.UpdatedMs; oldest = key; }
            }
            if (oldest is null || !_entries.TryRemove(oldest, out _)) break;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            Dictionary<string, DetectionEntry>? data =
                JsonSerializer.Deserialize<Dictionary<string, DetectionEntry>>(File.ReadAllText(_path), Json);
            if (data is not null)
            {
                foreach ((string key, DetectionEntry value) in data)
                {
                    // tolerate entries written by an older schema (missing lists deserialize to null)
                    _entries[key] = value with { Contracts = value.Contracts ?? [], NftContracts = value.NftContracts ?? [] };
                }
            }
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not load balance-viewer detection cache: {e.Message}");
        }
    }

    private void Save()
    {
        // full-file write behind a lock: writes are infrequent (a scan posts progress every few
        // seconds), and a temp-then-move keeps the file intact if the process dies mid-write
        try
        {
            lock (_fileLock)
            {
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not persist balance-viewer detection cache: {e.Message}");
        }
    }
}
