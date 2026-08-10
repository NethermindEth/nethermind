// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Init.FlatHistory;

/// <summary>
/// Owns the one-shot, node-lifetime backfill attempt for a windowed (<c>HistoryRetentionBlocks &gt; 0</c>) node:
/// once <see cref="PeerFedWindowImporter.ImportRangeAsync"/> successfully connects <c>[floor, anchor]</c>, the
/// persisted connected-range + lowered floor make every later read self-sufficient (live forward capture already
/// covers everything above <c>anchor</c>), so this loop is not a perpetual resync - it retries only until it
/// succeeds once, then stops. Started from its own constructor, exactly like
/// <see cref="Nethermind.State.Flat.History.HistoryWindowPruner"/>'s own background loop, so DI construction alone
/// (forced by <c>StartHistoryWindowBackfill</c>) is what kicks this off - nothing else needs to call a method.
/// </summary>
public sealed class WindowBackfillCoordinator : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly IFlatDbConfig _config;
    private readonly NHistPeerSelector _selector;
    private readonly NHistImportPeerSink _peerSink;
    private readonly IColumnsDb<FlatDbColumns> _db;
    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly HistoryWindowPruner _pruner;
    private readonly HistoryAvailability _availability;
    private readonly HistoryRowFormat _rowFormat;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public WindowBackfillCoordinator(
        IFlatDbConfig config,
        NHistPeerSelector selector,
        NHistImportPeerSink peerSink,
        IColumnsDb<FlatDbColumns> db,
        IColumnsDb<FlatHistoryColumns> history,
        HistoryWindowPruner pruner,
        HistoryAvailability availability,
        HistoryRowFormat rowFormat,
        ILogManager logManager)
    {
        _config = config;
        _selector = selector;
        _peerSink = peerSink;
        _db = db;
        _history = history;
        _pruner = pruner;
        _availability = availability;
        _rowFormat = rowFormat;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<WindowBackfillCoordinator>();

        Started = rowFormat.IsV3 && config.HistoryRetentionBlocks > 0;
        _loop = Started ? RunLoopAsync() : Task.CompletedTask;
    }

    /// <summary>Whether this instance actually started its background backfill attempt - false means the
    /// database is unwindowed and this coordinator is a permanent no-op for the life of the process. Public
    /// because it is the one externally-observable signal that the config gate took effect, used by
    /// <c>FlatHistoryModuleTests</c> to verify the flag wiring without reaching into the loop's internals.</summary>
    public bool Started { get; }

    private async Task RunLoopAsync()
    {
        CancellationToken token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (TryComputeTarget(out ulong floor, out ulong anchor) &&
                    _selector.TryGetEligibleImportPeer(NHistPeerSelector.NoExclusions, out PeerInfo peer, out INHistSyncPeer syncPeer))
                {
                    NHistWindowImportSource source = new(peer, syncPeer);
                    PeerFedWindowImporter importer = new(
                        source, _db, _history, _config, _pruner, _availability, _rowFormat, _logManager, hashSource: null, peerSink: _peerSink);
                    await importer.ImportRangeAsync(floor, anchor, token);
                    if (_logger.IsInfo) _logger.Info($"Windowed history backfill connected [{floor}, {anchor}] from peer {peer}.");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Windowed history backfill attempt failed; retrying in {RetryDelay}. {e}");
            }

            try
            {
                await Task.Delay(RetryDelay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Mirrors <see cref="HistoryWindowPruner"/>'s own read-path-window floor formula
    /// (<c>newFloor = watermark - retention</c>) so the backfill target always agrees with what the pruner is
    /// trying to retain, rather than inventing an independent range-selection policy. No watermark yet, or the
    /// whole chain-so-far already fits inside the retention window, means there is nothing below local live
    /// capture to backfill.</summary>
    private bool TryComputeTarget(out ulong floor, out ulong anchor)
    {
        if (!_availability.TryGetWatermark(out ulong watermark) || watermark <= _config.HistoryRetentionBlocks)
        {
            floor = 0;
            anchor = 0;
            return false;
        }

        anchor = watermark - 1;
        floor = watermark - _config.HistoryRetentionBlocks;
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
