// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Api;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.StateComposition;

/// <summary>
/// Disk-only multi-block scan cache.
/// Each scan is stored as <c>{DataDir}/{CachePath}/scan-{blockNumber}.json</c>.
/// No in-memory state — every read goes to disk.
/// </summary>
public sealed class StateCompositionStateHolder : IStateCompositionStateHolder
{
    private readonly IJsonSerializer _serializer;
    private readonly ILogger _logger;
    private readonly string _cacheDir;

    public StateCompositionStateHolder(
        IInitConfig initConfig,
        IStateCompositionConfig config,
        IJsonSerializer serializer,
        ILogManager logManager)
    {
        _serializer = serializer;
        _logger = logManager.GetClassLogger();

        string dataDir = initConfig.DataDir ?? Directory.GetCurrentDirectory();
        _cacheDir = Path.Combine(dataDir, config.CachePath);
        Directory.CreateDirectory(_cacheDir);
    }

    public ScanCacheEntry? GetScan(long? blockNumber)
    {
        if (blockNumber is null)
        {
            // Find the highest block number on disk.
            long? latest = FindLatestBlockNumber();
            if (latest is null) return null;
            blockNumber = latest;
        }

        string path = ScanPath(blockNumber.Value);
        return LoadFromDisk(path);
    }

    public bool HasAnyScan => Directory.GetFiles(_cacheDir, "scan-*.json").Length > 0;

    public bool HasScan(long blockNumber) => File.Exists(ScanPath(blockNumber));

    public IReadOnlyList<ScanMetadata> ListScans()
    {
        List<(long block, string path)> parsed = new();
        foreach (string file in Directory.GetFiles(_cacheDir, "scan-*.json"))
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("scan-") && long.TryParse(fileName.AsSpan(5), out long num))
                parsed.Add((num, file));
        }

        parsed.Sort((a, b) => a.block.CompareTo(b.block));

        List<ScanMetadata> result = new(parsed.Count);
        foreach ((_, string path) in parsed)
        {
            ScanCacheEntry? entry = LoadFromDisk(path);
            if (entry is not null)
                result.Add(entry.Value.Metadata);
        }

        return result;
    }

    public void StoreScan(long blockNumber, Hash256 stateRoot, TimeSpan duration,
                          StateCompositionStats stats, TrieDepthDistribution dist)
    {
        ScanCacheEntry entry = new()
        {
            Stats = stats,
            Distribution = dist,
            Metadata = new ScanMetadata
            {
                BlockNumber = blockNumber,
                StateRoot = stateRoot,
                CompletedAt = DateTimeOffset.UtcNow,
                Duration = duration,
                IsComplete = true,
            }
        };

        try
        {
            string filePath = ScanPath(blockNumber);
            string json = _serializer.Serialize(entry, indented: true);
            File.WriteAllText(filePath, json);

            if (_logger.IsInfo)
                _logger.Info($"StateComposition: persisted scan for block {blockNumber} to {filePath}");
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
                _logger.Error($"StateComposition: failed to persist scan for block {blockNumber}", ex);
        }
    }

    private string ScanPath(long blockNumber) => Path.Combine(_cacheDir, $"scan-{blockNumber}.json");

    private long? FindLatestBlockNumber()
    {
        long? latest = null;
        foreach (string file in Directory.GetFiles(_cacheDir, "scan-*.json"))
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.StartsWith("scan-") && long.TryParse(fileName.AsSpan(5), out long num))
            {
                if (latest is null || num > latest)
                    latest = num;
            }
        }
        return latest;
    }

    private ScanCacheEntry? LoadFromDisk(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            string json = File.ReadAllText(filePath);
            return _serializer.Deserialize<ScanCacheEntry>(json);
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn)
                _logger.Warn($"StateComposition: failed to load {Path.GetFileName(filePath)}: {ex.Message}");
            return null;
        }
    }
}
