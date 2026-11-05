// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor.Notifiers;

/// <summary>
/// Wraps an <see cref="INotifier"/> with a sliding-window rate limiter.
/// When the limit is first exceeded, a single warning is forwarded to the inner notifier;
/// further messages are silently dropped until the window clears.
/// </summary>
internal sealed class RateLimitedNotifier(INotifier inner, int maxMessages, TimeSpan window) : INotifier
{
    private readonly Queue<DateTime> _timestamps = new(maxMessages);
    private readonly Lock _lock = new();
    private bool _limitActive;

    public async Task NotifyFailureAsync(TestFailure failure, CancellationToken ct)
    {
        if (CheckRateLimit(out bool justHit))
            await inner.NotifyFailureAsync(failure, ct);
        else if (justHit)
            await inner.NotifyErrorAsync(RateLimitMessage);
    }

    public async Task NotifyErrorAsync(string error, Exception? exception = null)
    {
        if (CheckRateLimit(out bool justHit))
            await inner.NotifyErrorAsync(error, exception);
        else if (justHit)
            await inner.NotifyErrorAsync(RateLimitMessage);
    }

    // do not limit, forward directly
    public Task NotifyLiveAsync(string message) => inner.NotifyLiveAsync(message);

    public async Task NotifyStatsAsync(MonitorStats stats, CancellationToken ct)
    {
        if (CheckRateLimit(out bool justHit))
            await inner.NotifyStatsAsync(stats, ct);
        else if (justHit)
            await inner.NotifyErrorAsync(RateLimitMessage);
    }

    private string RateLimitMessage =>
        $"Rate limit reached ({maxMessages} messages per {window:g}) — subsequent notifications suppressed until the window clears.";

    private bool CheckRateLimit(out bool justHit)
    {
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            while (_timestamps.Count > 0 && now - _timestamps.Peek() > window)
                _timestamps.Dequeue();

            if (_timestamps.Count < maxMessages)
            {
                _timestamps.Enqueue(now);
                _limitActive = false;
                justHit = false;
                return true;
            }

            justHit = !_limitActive;
            _limitActive = true;
            return false;
        }
    }

    public void Dispose() => inner.Dispose();
}

internal static class RateLimitedNotifierExtensions
{
    public static INotifier RateLimited(this INotifier notifier, int maxMessages, TimeSpan window) =>
        new RateLimitedNotifier(notifier, maxMessages, window);
}
