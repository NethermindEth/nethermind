// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor.Notifiers;

internal interface INotifier : IDisposable
{
    Task NotifyFailureAsync(TestFailure failure, CancellationToken ct);
    Task NotifyErrorAsync(string error);
    Task NotifyStatsAsync(MonitorStats stats);
}

internal class NullNotifier : INotifier
{
    public static readonly NullNotifier Instance = new();

    public Task NotifyFailureAsync(TestFailure failure, CancellationToken ct) => Task.CompletedTask;
    public Task NotifyErrorAsync(string error) => Task.CompletedTask;
    public Task NotifyStatsAsync(MonitorStats stats) => Task.CompletedTask;
    public void Dispose() { }
}
