// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor.Notifiers;

internal class NullNotifier : INotifier
{
    public Task NotifyFailureAsync(TestFailure failure, CancellationToken ct) => Task.CompletedTask;
    public Task NotifyErrorAsync(string message) => Task.CompletedTask;
    public void Dispose() { }
}
