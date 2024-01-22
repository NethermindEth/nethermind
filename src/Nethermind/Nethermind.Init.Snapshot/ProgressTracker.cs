// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using ITimer = Nethermind.Core.Timers.ITimer;

namespace Nethermind.Init.Snapshot;

public class ProgressTracker : IDisposable
{
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private readonly long? _total;
    private long _current;

    public ProgressTracker(ILogManager logManager, ITimerFactory timerFactory, TimeSpan interval, long current = 0,
        long? total = null)
    {
        _total = total;
        _current = current;

        _logger = logManager.GetClassLogger();

        _timer = timerFactory.CreateTimer(interval);
        _timer.Elapsed += ReportProgress;
        _timer.AutoReset = false;
    }

    public void AddProgress(long count)
    {
        _current += count;
        _timer.Enabled = true;
    }

    private static string HumanReadableSize(long byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "Cannot be negative");

        if (byteCount < 1.KB())
            return $"{byteCount:0.##}B";
        if (byteCount < 1.MB())
            return $"{(float)byteCount / 1.KB():0.##}KB";
        if (byteCount < 1.GB())
            return $"{(float)byteCount / 1.MB():0.##}MB";

        return $"{(float)byteCount / 1.GB():0.##}GB";
    }

    private void ReportProgress(object? sender, EventArgs e)
    {
        if (_logger.IsInfo)
        {
            _logger.Info(_total is null
                ? $"Snapshot download progress {HumanReadableSize(_current)}"
                : $"Snapshot download progress {HumanReadableSize(_current)} out of {HumanReadableSize(_total.Value)}");
        }

        _timer.Enabled = true;
    }

    void IDisposable.Dispose() => _timer.Dispose();
}
