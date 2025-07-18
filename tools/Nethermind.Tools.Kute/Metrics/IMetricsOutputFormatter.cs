// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public interface IMetricsReportFormatter
{
    Task WriteAsync(Stream stream, MetricsReport report);
}

public sealed class NullMetricsReportFormatter : IMetricsReportFormatter
{
    public Task WriteAsync(Stream stream, MetricsReport report) => Task.CompletedTask;
}
