// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public sealed class PrettyMetricsReportFormatter : IMetricsReportFormatter
{
    public async Task WriteAsync(Stream stream, MetricsReport report, CancellationToken token = default)
    {
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("=== Report ===".AsMemory(), token);
        await writer.WriteLineAsync($"Total Time: {report.TotalTime.TotalSeconds} s".AsMemory(), token);
        await writer.WriteLineAsync($"Total Messages: {report.TotalMessages}\n".AsMemory(), token);
        await writer.WriteLineAsync($"Succeeded: {report.Succeeded}".AsMemory(), token);
        await writer.WriteLineAsync($"Failed: {report.Failed}".AsMemory(), token);
        await writer.WriteLineAsync($"Ignored: {report.Ignored}".AsMemory(), token);
        await writer.WriteLineAsync($"Responses: {report.Responses}\n".AsMemory(), token);
        await writer.WriteLineAsync("Singles:".AsMemory(), token);
        foreach ((var methodName, var metrics) in report.SinglesMetrics)
        {
            await writer.WriteLineAsync($"  {methodName}:".AsMemory(), token);
            await writer.WriteLineAsync($"    Count: {report.Singles[methodName].Count}".AsMemory(), token);
            await writer.WriteLineAsync($"    Max: {metrics.Max.TotalMilliseconds} ms".AsMemory(), token);
            await writer.WriteLineAsync($"    Average: {metrics.Average.TotalMilliseconds} ms".AsMemory(), token);
            await writer.WriteLineAsync($"    Min: {metrics.Min.TotalMilliseconds} ms".AsMemory(), token);
            await writer.WriteLineAsync($"    Stddev: {metrics.StandardDeviation.TotalMilliseconds} ms".AsMemory(), token);
        }

        await writer.WriteLineAsync("Batches:".AsMemory(), token);
        await writer.WriteLineAsync($"  Count: {report.Batches.Count}".AsMemory(), token);
        await writer.WriteLineAsync($"  Max: {report.BatchesMetrics.Max.TotalMilliseconds} ms".AsMemory(), token);
        await writer.WriteLineAsync($"  Average: {report.BatchesMetrics.Average.TotalMilliseconds} ms".AsMemory(), token);
        await writer.WriteLineAsync($"  Min: {report.BatchesMetrics.Min.TotalMilliseconds} ms".AsMemory(), token);
        await writer.WriteLineAsync($"  Stddev: {report.BatchesMetrics.StandardDeviation.TotalMilliseconds} ms".AsMemory(), token);
    }
}
