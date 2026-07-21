// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json;
using Nethermind.Logging;

namespace Nethermind.PortfolioViewer.Plugin;

/// <summary>Tracks (persisted) the IPFS pins this plugin added, so "unpin all" removes only the plugin's own
/// pins and never the user's unrelated pinset.</summary>
public interface IPinnedCidStore
{
    void Add(string cid);
    IReadOnlyCollection<string> Snapshot();
    void Clear();
}

/// <inheritdoc cref="IPinnedCidStore"/>
public sealed class PinnedCidStore : IPinnedCidStore
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly ConcurrentDictionary<string, byte> _cids = new();
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _fileLock = new();

    public PinnedCidStore(string dbPath, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<PinnedCidStore>();
        _path = Path.Combine(dbPath, "portfolio-viewer-pins.json");
        Load();
    }

    public void Add(string cid)
    {
        if (_cids.TryAdd(cid, 0)) Save();
    }

    public IReadOnlyCollection<string> Snapshot() => Materialize();

    private List<string> Materialize()
    {
        List<string> cids = new(_cids.Count);
        foreach (KeyValuePair<string, byte> entry in _cids) cids.Add(entry.Key);
        return cids;
    }

    public void Clear()
    {
        _cids.Clear();
        try
        {
            lock (_fileLock)
            {
                File.Delete(_path);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not delete portfolio-viewer pin list: {e.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            string[]? data = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path), Json);
            if (data is not null)
            {
                foreach (string cid in data) _cids.TryAdd(cid, 0);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not load portfolio-viewer pin list: {e.Message}");
        }
    }

    private void Save()
    {
        // temp-then-move keeps the file intact if the process dies mid-write
        try
        {
            lock (_fileLock)
            {
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Materialize(), Json));
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not persist portfolio-viewer pin list: {e.Message}");
        }
    }
}
