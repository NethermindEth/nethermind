// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public interface IMetricsReporter
{
    Task Message() => Task.CompletedTask;
    Task Response() => Task.CompletedTask;
    Task Succeeded() => Task.CompletedTask;
    Task Failed() => Task.CompletedTask;
    Task Ignored() => Task.CompletedTask;
    Task Batch(int requestId, TimeSpan elapsed) => Task.CompletedTask;
    Task Single(int requestId, TimeSpan elapsed) => Task.CompletedTask;
    Task Total(TimeSpan elapsed) => Task.CompletedTask;
}

public sealed class NullMetricsReporter : IMetricsReporter;
