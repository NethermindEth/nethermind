// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Tools.Kute.Replay;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test.Replay;

public class ReplayReportTests
{
    [Test]
    public void Json_keeps_the_sweep_report_shape()
    {
        // Sweep comparison scripts parse this output, so the field names, order and number format must stay fixed.
        LevelResult result = new()
        {
            Concurrency = 8,
            Succeeded = 7,
            RpcErrors = 1,
            HttpErrors = 2,
            TransportErrors = 3,
            Elapsed = TimeSpan.FromMilliseconds(1_234.5),
            RequestBytes = 3 << 19,
            Latencies = [TimeSpan.FromMilliseconds(1.25), TimeSpan.FromMilliseconds(2.5), TimeSpan.FromMilliseconds(40)],
            Rewritten = 4,
            FeesStripped = 5,
            Untagged = 6,
        };

        object expected = new
        {
            levels = new object[]
            {
                new
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
                },
            },
        };

        Assert.That(ReplayReport.Json([result]), Is.EqualTo(JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true })));
    }
}
