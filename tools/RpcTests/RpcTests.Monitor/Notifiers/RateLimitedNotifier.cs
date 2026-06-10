// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor.Notifiers;

/// <summary>
/// Wraps an <see cref="INotifier"/> with one or more sliding-window rate limiters.
/// A notification is forwarded only when every configured window allows it.
/// When the limit is first exceeded, a single warning is forwarded to the inner notifier;
/// further messages are silently dropped until all windows clear.
/// </summary>
internal sealed class RateLimitedNotifier(INotifier inner, params (int MaxMessages, TimeSpan Window)[] limits) : INotifier
{
    private readonly (int MaxMessages, TimeSpan Window, Queue<DateTime> Timestamps)[] _windows =
        limits.Select(static l => (l.MaxMessages, l.Window, new Queue<DateTime>(l.MaxMessages))).ToArray();
    private readonly Lock _lock = new();
    private bool _limitActive;

    public async Task NotifyFailureAsync(TestFailure failure, CancellationToken ct)
    {
        if (CheckRateLimit(out bool justHit))
            await inner.NotifyFailureAsync(failure, ct);
        else if (justHit)
            await inner.NotifyErrorAsync(RateLimitMessage);
    }

    public async Task NotifyErrorAsync(string error)
    {
        if (CheckRateLimit(out bool justHit))
            await inner.NotifyErrorAsync(error);
        else if (justHit)
            await inner.NotifyErrorAsync(RateLimitMessage);
    }

    public async Task NotifyStatsAsync(MonitorStats stats)
    {
        if (CheckRateLimit(out bool justHit))
            await inner.NotifyStatsAsync(stats);
        else if (justHit)
            await inner.NotifyErrorAsync(RateLimitMessage);
    }

    private string RateLimitMessage =>
        $"Rate limit reached ({string.Join(", ", _windows.Select(static w => $"{w.MaxMessages} per {w.Window:g}"))}) — subsequent notifications suppressed until the window clears.";

    private bool CheckRateLimit(out bool justHit)
    {
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            bool exceeded = false;
            foreach (var (max, window, ts) in _windows)
            {
                while (ts.Count > 0 && now - ts.Peek() > window)
                    ts.Dequeue();
                if (ts.Count >= max)
                    exceeded = true;
            }

            if (!exceeded)
            {
                foreach (var (_, _, ts) in _windows)
                    ts.Enqueue(now);
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
    public static INotifier RateLimited(this INotifier notifier, params (int MaxMessages, TimeSpan Window)[] limits) =>
        new RateLimitedNotifier(notifier, limits);
}
