// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.HealthChecks;

public class ClHealthRequestsTracker(ITimestamper timestamper, int maxIntervalClRequestTime, ILogger logger)
    : IEngineRequestsTracker, IClHealthTracker, IAsyncDisposable
{
    private const int ClUnavailableReportMessageDelay = 5;

    private DateTime _latestForkchoiceUpdated = timestamper.UtcNow;
    private DateTime _latestNewPayload = timestamper.UtcNow;

    private Timer _timer;

    public Task StartAsync()
    {
        _timer = new Timer(ReportClStatus, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(ClUnavailableReportMessageDelay));

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _timer?.Change(Timeout.Infinite, 0);
        if (_timer is not null) await _timer.DisposeAsync();
    }

    private void ReportClStatus(object _)
    {
        if (!CheckClAlive())
        {
            if (logger.IsWarn)
                logger.Warn("Not receiving ForkChoices from the consensus client that are required to sync.");
        }
    }

    private bool IsRequestTooOld(DateTime now, DateTime requestTime)
    {
        TimeSpan diff = (now - requestTime).Duration();
        return diff > TimeSpan.FromSeconds(maxIntervalClRequestTime);
    }

    public bool CheckClAlive()
    {
        var now = timestamper.UtcNow;
        return !IsRequestTooOld(now, _latestForkchoiceUpdated) && !IsRequestTooOld(now, _latestNewPayload);
    }

    public void OnForkchoiceUpdatedCalled()
    {
        _latestForkchoiceUpdated = timestamper.UtcNow;
    }

    public void OnNewPayloadCalled()
    {
        _latestNewPayload = timestamper.UtcNow;
    }
}
