// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

/// <summary>
/// Throttles notifications about a sustained connection outage to avoid flooding the channel during long downtime.
/// </summary>
internal sealed class OutageReporter(ErrorReporter errorReporter, INotifier notifier, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private DateTimeOffset? _outageStart;
    private DateTimeOffset _lastNotified;
    private int _sentCount;

    /// <summary>
    /// Records a connection failure and forwards it only once the next escalation threshold has been reached.
    /// </summary>
    public void OnError(string error, Exception ex)
    {
        DateTimeOffset now = _time.GetUtcNow();
        _outageStart ??= now;

        DateTimeOffset since = _sentCount == 0 ? _outageStart.Value : _lastNotified;
        TimeSpan requiredGap = _sentCount switch
        {
            0 => TimeSpan.FromMinutes(5),
            1 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromHours(1)
        };

        if (now - since < requiredGap)
            return;

        _lastNotified = now;
        _sentCount++;
        errorReporter.Report(error, ex);
    }

    /// <summary>
    /// Marks the connection as healthy, emitting a recovery notification if the outage had been alerted.
    /// </summary>
    public void OnRecovered()
    {
        if (_sentCount > 0 && _outageStart is { } start)
            _ = notifier.NotifyLiveAsync($"Connection restored after {_time.GetUtcNow() - start:g}");

        _outageStart = null;
        _lastNotified = default;
        _sentCount = 0;
    }
}
