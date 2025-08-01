// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public interface IMetricsReporter
{
    Task Message(CancellationToken token = default) => Task.CompletedTask;
    Task Response(CancellationToken token = default) => Task.CompletedTask;
    Task Succeeded(CancellationToken token = default) => Task.CompletedTask;
    Task Failed(CancellationToken token = default) => Task.CompletedTask;
    Task Ignored(CancellationToken token = default) => Task.CompletedTask;
    Task Batch(JsonRpc.Request.Batch batch, TimeSpan elapsed, CancellationToken token = default) => Task.CompletedTask;
    Task Single(JsonRpc.Request.Single single, TimeSpan elapsed, CancellationToken token = default) => Task.CompletedTask;
    Task Total(TimeSpan elapsed, CancellationToken token = default) => Task.CompletedTask;
}

public sealed class NullMetricsReporter : IMetricsReporter;
