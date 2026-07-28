// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization;

/// <summary>
/// Attributes wall-clock time to each active <see cref="SyncMode"/> and publishes the running totals
/// into <see cref="Metrics.SyncTimeInModeSeconds"/> (Prometheus label <c>sync_mode</c>).
/// </summary>
/// <remarks>
/// The elapsed interval is flushed both when <see cref="ISyncModeSelector.Changed"/> fires (precise
/// attribution at transitions) and on every metrics-update tick via <see cref="UpdateMetrics"/> (so a
/// long-running mode keeps updating between transitions rather than only when it ends). Because
/// <see cref="SyncMode"/> is a flag set and several stages can run in parallel, an interval is added to
/// every active reportable flag, so the per-mode totals can sum to more than the wall-clock sync time.
/// </remarks>
public sealed class SyncTimeInModeTracker
{
    private static readonly SyncMode[] ReportableModes =
    [
        SyncMode.WaitingForBlock,
        SyncMode.Disconnected,
        SyncMode.FastHeaders,
        SyncMode.FastBodies,
        SyncMode.FastReceipts,
        SyncMode.FastBlockAccessLists,
        SyncMode.FastSync,
        SyncMode.StateNodes,
        SyncMode.Full,
        SyncMode.DbLoad,
        SyncMode.BeaconHeaders,
        SyncMode.UpdatingPivot,
    ];

    private readonly Func<long> _getTimestamp;
    private readonly Lock _lock = new();
    private readonly Dictionary<SyncMode, double> _secondsByMode = [];

    private SyncMode _currentMode;
    private long _lastTimestamp;

    public SyncTimeInModeTracker(ISyncModeSelector syncModeSelector, Func<long>? getTimestamp = null)
    {
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        _currentMode = syncModeSelector.Current;
        _lastTimestamp = _getTimestamp();

        foreach (SyncMode mode in ReportableModes)
        {
            _secondsByMode[mode] = 0;
            Metrics.SyncTimeInModeSeconds[mode] = 0;
        }

        syncModeSelector.Changed += OnSyncModeChanged;
    }

    /// <summary>
    /// Flushes the time elapsed since the last flush into the current mode's buckets. Intended to be
    /// registered as a periodic metrics-update action so in-progress modes update on every scrape.
    /// </summary>
    public void UpdateMetrics()
    {
        lock (_lock)
        {
            Flush();
        }
    }

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
    {
        lock (_lock)
        {
            Flush();
            _currentMode = e.Current;
        }
    }

    private void Flush()
    {
        long now = _getTimestamp();
        double elapsedSeconds = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        _lastTimestamp = now;

        if (elapsedSeconds <= 0) return;

        foreach (SyncMode mode in ReportableModes)
        {
            if ((_currentMode & mode) == mode)
            {
                double total = _secondsByMode[mode] + elapsedSeconds;
                _secondsByMode[mode] = total;
                Metrics.SyncTimeInModeSeconds[mode] = (long)total;
            }
        }
    }
}
