// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public sealed class PrettyMetricsReportFormatter : IMetricsReportFormatter
{
    public async Task WriteAsync(Stream stream, MetricsReport report, CancellationToken token = default)
    {
        using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("=== Report ===", token);
        await writer.WriteLineAsync($"Total Time: {report.TotalTime.TotalSeconds} s", token);
        await writer.WriteLineAsync($"Total Messages: {report.TotalMessages}\n", token);
        await writer.WriteLineAsync($"Succeeded: {report.Succeeded}", token);
        await writer.WriteLineAsync($"Failed: {report.Failed}", token);
        await writer.WriteLineAsync($"Ignored: {report.Ignored}", token);
        await writer.WriteLineAsync($"Responses: {report.Responses}\n", token);
        await writer.WriteLineAsync("Singles:", token);
        foreach (var single in report.SinglesMetrics)
        {
            var methodName = single.Key;
            var metrics = single.Value;
            await writer.WriteLineAsync($"  {methodName}:", token);
            await writer.WriteLineAsync($"    Count: {report.Singles[methodName].Count}", token);
            await writer.WriteLineAsync($"    Max: {metrics.Max.TotalMilliseconds} ms", token);
            await writer.WriteLineAsync($"    Average: {metrics.Average.TotalMilliseconds} ms", token);
            await writer.WriteLineAsync($"    Min: {metrics.Min.TotalMilliseconds} ms", token);
            await writer.WriteLineAsync($"    Stddev: {metrics.StandardDeviation.TotalMilliseconds} ms", token);
        }

        await writer.WriteLineAsync("Batches:", token);
        await writer.WriteLineAsync($"  Count: {report.Batches.Count}", token);
        await writer.WriteLineAsync($"  Max: {report.BatchesMetrics.Max.TotalMilliseconds} ms", token);
        await writer.WriteLineAsync($"  Average: {report.BatchesMetrics.Average.TotalMilliseconds} ms", token);
        await writer.WriteLineAsync($"  Min: {report.BatchesMetrics.Min.TotalMilliseconds} ms", token);
        await writer.WriteLineAsync($"  Stddev: {report.BatchesMetrics.StandardDeviation.TotalMilliseconds} ms", token);
    }
}

internal static class StreamWriterExt
{
    public static async Task WriteLineAsync(this StreamWriter writer, string value, CancellationToken token)
    {
        await writer.WriteLineAsync(MemoryExtensions.AsMemory(value), token);
    }
}
