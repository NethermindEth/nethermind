// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Timers;
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.Core;

/// <summary>
/// Wraps a <see cref="ProgressLogger"/> with a periodic timer that drives <see cref="ProgressLogger.LogProgress"/>
/// and ends the logger on disposal.
/// </summary>
/// <remarks>
/// All access to the inner <see cref="ProgressLogger"/> is locked: <see cref="Update"/> from the work thread races
/// timer-fired <see cref="ProgressLogger.LogProgress"/>, and <see cref="Timer.Stop"/> doesn't drain in-flight ticks.
/// </remarks>
public sealed class ProgressReporter : IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly ProgressLogger _progressLogger;
    private readonly Timer _timer;
    private int _disposed;

    public ProgressReporter(string prefix, ILogManager logManager, ulong total, TimeSpan? interval = null)
    {
        _progressLogger = new ProgressLogger(prefix, logManager);
        _progressLogger.Reset(0, total);
        _timer = new Timer((interval ?? DefaultInterval).TotalMilliseconds);
        _timer.Elapsed += OnElapsed;
        _timer.Enabled = true;
    }

    public ProgressLogger Logger => _progressLogger;

    public void Update(ulong value)
    {
        lock (_progressLogger) _progressLogger.Update(value);
    }

    private void OnElapsed(object? sender, ElapsedEventArgs e)
    {
        lock (_progressLogger) _progressLogger.LogProgress();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Stop();
        _timer.Dispose();
        lock (_progressLogger)
        {
            _progressLogger.MarkEnd();
            _progressLogger.LogProgress();
        }
    }
}
