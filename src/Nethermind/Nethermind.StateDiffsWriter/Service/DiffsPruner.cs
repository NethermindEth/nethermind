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
/// Background pruner of <see cref="BlockDiffsColumns.Default"/> rows older than
/// <c>head - KeepLastNBlocks</c>. The slot-counts CF is never pruned; it is the live world-state slot map.
/// </summary>
public sealed class DiffsPruner(
    IBlockTree blockTree,
    BlockDiffsStore store,
    DiffsWriterService writer,
    IStateDiffsWriterConfig config,
    ILogManager logManager) : IAsyncDisposable, IDisposable
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

    // Non-blocking; use DisposeAsync to join the loop.
    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
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
        if (_config.KeepLastNBlocks < 0)
        {
            // Negative window would wrap past the cutoff<=0 guard and prune everything.
            if (_logger.IsWarn)
                _logger.Warn(
                    $"StateDiffsWriter: KeepLastNBlocks is negative ({_config.KeepLastNBlocks}); " +
                    "pruning disabled to avoid deleting the whole BlockDiffs window.");
            return 0;
        }

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
