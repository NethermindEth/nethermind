// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.StateDiffsWriter.Storage;

namespace Nethermind.StateDiffsWriter.Service;

/// <summary>
/// Background pruner: every <see cref="IStateDiffsWriterConfig.PruneIntervalSeconds"/>
/// seconds, delete <see cref="BlockDiffsColumns.Default"/> rows whose key is older
/// than <c>head - KeepLastNBlocks</c>. The slot-counts CF is intentionally never
/// pruned: it represents the current world-state slot map and must outlive any
/// per-block window.
/// </summary>
public sealed class DiffsPruner(
    IBlockTree blockTree,
    BlockDiffsStore store,
    DiffsWriterService writer,
    IStateDiffsWriterConfig config,
    ILogManager logManager) : IDisposable
{
    private readonly IBlockTree _blockTree = blockTree;
    private readonly BlockDiffsStore _store = store;
    private readonly DiffsWriterService _writer = writer;
    private readonly IStateDiffsWriterConfig _config = config;
    private readonly ILogger _logger = logManager.GetClassLogger<DiffsPruner>();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public void Start()
    {
        if (_config.PruneIntervalSeconds <= 0)
        {
            if (_logger.IsInfo)
                _logger.Info("StateDiffsWriter: BlockDiffs pruning disabled (PruneIntervalSeconds <= 0)");
            return;
        }

        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* shutdown best-effort */ }
        _cts.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        TimeSpan period = TimeSpan.FromSeconds(_config.PruneIntervalSeconds);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    PruneOnce();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"StateDiffsWriter: prune sweep failed: {ex.Message}");
                }

                await Task.Delay(period, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal int PruneOnce()
    {
        long head = (long)(_blockTree.Head?.Number ?? 0);
        long anchor = Math.Max(head, _writer.LastWrittenBlock);
        long cutoff = anchor - _config.KeepLastNBlocks;
        if (cutoff <= 0) return 0;

        int removed;
        lock (_writer.WriteLock)
        {
            removed = _store.PruneOlderThan(cutoff);
        }

        if (removed > 0)
        {
            Metrics.StateDiffsWriterPrunerRowsRemovedTotal += removed;
            if (_logger.IsDebug)
                _logger.Debug($"StateDiffsWriter: pruned {removed} BlockDiffs rows older than block {cutoff}");
        }
        return removed;
    }
}
