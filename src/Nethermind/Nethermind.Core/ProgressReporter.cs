// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.Core;

/// <summary>
/// Wraps a <see cref="ProgressLogger"/> with a periodic timer that drives <see cref="ProgressLogger.LogProgress"/>
/// on a wall-clock interval and guarantees <see cref="ProgressLogger.MarkEnd"/> on disposal.
/// </summary>
/// <remarks>
/// Replaces the recurring <c>Reset</c> → <c>new Timer(...)</c> → <c>try/finally MarkEnd</c> pattern at long-running
/// operation callsites. Update <see cref="ProgressLogger.CurrentValue"/> via <see cref="Update"/> from the work loop;
/// the timer reads it on each tick. Access the underlying <see cref="ProgressLogger"/> via <see cref="Logger"/> for
/// custom formatting (<see cref="ProgressLogger.SetFormat"/>) or skipped/queued counters.
/// </remarks>
public sealed class ProgressReporter : IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly ProgressLogger _progressLogger;
    private readonly Timer _timer;
    private int _disposed;

    public ProgressReporter(string prefix, ILogManager logManager, long total, TimeSpan? interval = null)
    {
        _progressLogger = new ProgressLogger(prefix, logManager);
        _progressLogger.Reset(0, total);
        _timer = new Timer((interval ?? DefaultInterval).TotalMilliseconds);
        _timer.Elapsed += (_, _) => _progressLogger.LogProgress();
        _timer.Enabled = true;
    }

    public ProgressLogger Logger => _progressLogger;

    public void Update(long value) => _progressLogger.Update(value);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Stop();
        _timer.Dispose();
        _progressLogger.MarkEnd();
        _progressLogger.LogProgress();
    }
}
