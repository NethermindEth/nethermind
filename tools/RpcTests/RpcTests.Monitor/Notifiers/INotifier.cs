// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor.Notifiers;

internal interface INotifier : IDisposable
{
    Task NotifyFailureAsync(TestFailure failure, CancellationToken ct);
    Task NotifyErrorAsync(string error, Exception? ex = null);
    Task NotifyLiveAsync(string message);
    Task NotifyStatsAsync(MonitorStats stats, CancellationToken ct);
}

internal class NullNotifier : INotifier
{
    public static readonly NullNotifier Instance = new();

    public Task NotifyFailureAsync(TestFailure failure, CancellationToken ct) => Task.CompletedTask;
    public Task NotifyErrorAsync(string error, Exception? ex = null) => Task.CompletedTask;
    public Task NotifyLiveAsync(string message) => Task.CompletedTask;
    public Task NotifyStatsAsync(MonitorStats stats, CancellationToken ct) => Task.CompletedTask;
    public void Dispose() { }
}
