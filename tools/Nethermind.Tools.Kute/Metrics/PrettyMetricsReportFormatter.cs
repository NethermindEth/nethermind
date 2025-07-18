// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public sealed class PrettyMetricsReportFormatter : IMetricsReportFormatter
{
    public async Task WriteAsync(Stream stream, MetricsReport report)
    {
        using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("=== Report ===");
        await writer.WriteLineAsync($"Total Messages: {report.TotalMessages}");
        await writer.WriteLineAsync($"Succeeded: {report.Succeeded}");
        await writer.WriteLineAsync($"Failed: {report.Failed}");
        await writer.WriteLineAsync($"Ignored: {report.Ignored}");
        await writer.WriteLineAsync($"Responses: {report.Responses}\n");
        await writer.WriteLineAsync($"Total Time: {report.TotalTime}");
        await writer.WriteLineAsync("Singles:");
        foreach (var single in report.Singles)
        {
            await writer.WriteLineAsync($"  {single.Key}: {single.Value}");
        }
        await writer.WriteLineAsync("Batches:");
        foreach (var batch in report.Batches)
        {
            await writer.WriteLineAsync($"  {batch.Key}: {batch.Value}");
        }
    }
}
