// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public sealed class PrettyMetricsReportFormatter : IMetricsReportFormatter
{
    public async Task WriteAsync(Stream stream, MetricsReport report, CancellationToken token = default)
    {
        using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("=== Report ===", token);
        await writer.WriteLineAsync($"Total Messages: {report.TotalMessages}", token);
        await writer.WriteLineAsync($"Succeeded: {report.Succeeded}", token);
        await writer.WriteLineAsync($"Failed: {report.Failed}", token);
        await writer.WriteLineAsync($"Ignored: {report.Ignored}", token);
        await writer.WriteLineAsync($"Responses: {report.Responses}\n", token);
        await writer.WriteLineAsync($"Total Time: {report.TotalTime}", token);
        await writer.WriteLineAsync("Singles:", token);
        foreach (var single in report.Singles)
        {
            await writer.WriteLineAsync($"  {single.Key}: {single.Value}", token);
        }
        await writer.WriteLineAsync("Batches:", token);
        foreach (var batch in report.Batches)
        {
            await writer.WriteLineAsync($"  {batch.Key}: {batch.Value}", token);
        }
    }
}

internal static class StreamWriterExt
{
    public static async Task WriteLineAsync(this StreamWriter writer, string value, CancellationToken token) =>
        await writer.WriteAsync(MemoryExtensions.AsMemory(value), token);
}
