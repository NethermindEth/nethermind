// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public enum MetricsReportFormat
{
    Report, Json,
}

public sealed record MetricsReport
{
    public required long TotalMessages { get; init; }
    public required long Failed { get; init; }
    public required long Succeeded { get; init; }
    public required long Ignored { get; init; }
    public required long Responses { get; init; }
    public required TimeSpan TotalTime { get; init; }
    public required IReadOnlyDictionary<int, TimeSpan> Singles { get; init; }
    public required IReadOnlyDictionary<int, TimeSpan> Batches { get; init; }
}

public interface IMetricsReportProvider
{
    MetricsReport Report();
}
