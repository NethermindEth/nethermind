// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>Output shapes for a sweep report.</summary>
public enum ReplayReportFormat
{
    /// <summary>A fixed-width table for reading in a terminal.</summary>
    Pretty,

    /// <summary>One JSON object per level, for programmatic comparison.</summary>
    Json,

    /// <summary>One CSV row per level, for spreadsheets and plotting.</summary>
    Csv,
}

/// <summary>Renders the results of a concurrency sweep.</summary>
public static class ReplayReport
{
    /// <summary>Formats a sweep as a fixed-width table, one row per concurrency level.</summary>
    public static string Pretty(IReadOnlyList<LevelResult> results)
    {
        StringBuilder builder = new();
        builder.AppendLine("conc |    sent | failed |     rps |    MiB/s |    mean |     p50 |     p90 |     p99 |     max");
        builder.AppendLine("-----+---------+--------+---------+----------+---------+---------+---------+---------+--------");

        foreach (LevelResult result in results)
        {
            double throughput = result.Elapsed > TimeSpan.Zero
                ? result.RequestBytes / (double)(1 << 20) / result.Elapsed.TotalSeconds
                : 0d;

            builder.Append(CultureInfo.InvariantCulture, $"{result.Concurrency,4} |");
            builder.Append(CultureInfo.InvariantCulture, $"{result.Total,8} |");
            builder.Append(CultureInfo.InvariantCulture, $"{result.Failed,7} |");
            builder.Append(CultureInfo.InvariantCulture, $"{result.RequestsPerSecond,8:F1} |");
            builder.Append(CultureInfo.InvariantCulture, $"{throughput,9:F1} |");
            builder.Append(CultureInfo.InvariantCulture, $"{result.Mean.TotalMilliseconds,8:F2} |");
            builder.Append(CultureInfo.InvariantCulture, $"{result.P50.TotalMilliseconds,8:F2} |");
            builder.Append(CultureInfo.InvariantCulture, $"{result.P90.TotalMilliseconds,8:F2} |");
            builder.Append(CultureInfo.InvariantCulture, $"{result.P99.TotalMilliseconds,8:F2} |");
            builder.AppendLine(CultureInfo.InvariantCulture, $"{result.Max.TotalMilliseconds,8:F2}");
        }

        AppendFailureBreakdown(builder, results);

        return builder.ToString();
    }

    /// <summary>Formats a sweep as one CSV row per concurrency level, with a header.</summary>
    public static string Csv(IReadOnlyList<LevelResult> results)
    {
        StringBuilder builder = new();
        builder.AppendLine("concurrency,requests,succeeded,rpc_errors,http_errors,transport_errors,elapsed_s,rps,request_mib,rewritten,fees_stripped,untagged,mean_ms,p50_ms,p90_ms,p99_ms,min_ms,max_ms");

        foreach (LevelResult result in results)
        {
            builder.AppendLine(string.Join(',',
                result.Concurrency.ToString(CultureInfo.InvariantCulture),
                result.Total.ToString(CultureInfo.InvariantCulture),
                result.Succeeded.ToString(CultureInfo.InvariantCulture),
                result.RpcErrors.ToString(CultureInfo.InvariantCulture),
                result.HttpErrors.ToString(CultureInfo.InvariantCulture),
                result.TransportErrors.ToString(CultureInfo.InvariantCulture),
                result.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
                result.RequestsPerSecond.ToString("F3", CultureInfo.InvariantCulture),
                (result.RequestBytes / (double)(1 << 20)).ToString("F3", CultureInfo.InvariantCulture),
                result.Rewritten.ToString(CultureInfo.InvariantCulture),
                result.FeesStripped.ToString(CultureInfo.InvariantCulture),
                result.Untagged.ToString(CultureInfo.InvariantCulture),
                result.Mean.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                result.P50.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                result.P90.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                result.P99.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                result.Min.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                result.Max.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)));
        }

        return builder.ToString();
    }

    /// <summary>Formats a sweep as JSON, one object per concurrency level.</summary>
    public static string Json(IReadOnlyList<LevelResult> results)
    {
        object[] levels = new object[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            LevelResult result = results[i];
            levels[i] = new
            {
                concurrency = result.Concurrency,
                requests = result.Total,
                succeeded = result.Succeeded,
                rpcErrors = result.RpcErrors,
                httpErrors = result.HttpErrors,
                transportErrors = result.TransportErrors,
                failureRate = result.FailureRate,
                elapsedSeconds = result.Elapsed.TotalSeconds,
                requestsPerSecond = result.RequestsPerSecond,
                requestMib = result.RequestBytes / (double)(1 << 20),
                rewritten = result.Rewritten,
                feesStripped = result.FeesStripped,
                untagged = result.Untagged,
                latencyMs = new
                {
                    mean = result.Mean.TotalMilliseconds,
                    min = result.Min.TotalMilliseconds,
                    p50 = result.P50.TotalMilliseconds,
                    p90 = result.P90.TotalMilliseconds,
                    p99 = result.P99.TotalMilliseconds,
                    max = result.Max.TotalMilliseconds,
                },
            };
        }

        return JsonSerializer.Serialize(new { levels }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AppendFailureBreakdown(StringBuilder builder, IReadOnlyList<LevelResult> results)
    {
        foreach (LevelResult result in results)
        {
            if (result.Failed == 0)
            {
                continue;
            }

            builder.AppendLine(CultureInfo.InvariantCulture,
                $"concurrency {result.Concurrency}: {result.FailureRate:P2} failed "
                + $"({result.RpcErrors} rpc, {result.HttpErrors} http, {result.TransportErrors} transport)");
        }
    }
}
