// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.MetricsConsumer;

class PrettyReportMetricsConsumer : IMetricsConsumer
{
    public void ConsumeMetrics(Metrics metrics)
    {
        Console.WriteLine($"""
        Total Running Time: {metrics.TotalRunningTime.TotalMilliseconds} ms
        Results:
          Messages .. {metrics.Messages}
            Failed .. {metrics.Failed}
            Successes
              Responses .. {metrics.Responses}
              Requests
                Ignored .. {metrics.IgnoredRequests}
                Processed
        """);

        var longestMethod = metrics.ProcessedRequests.Keys
            .MaxBy(method => method.Length)?
            .Length ?? 0;
        foreach (var (method, mm) in metrics.ProcessedRequests)
        {
            var dots = new string('.', (2 + longestMethod - method.Length));
            Console.WriteLine($"""
                  {method} {dots} {mm.Count} in {mm.RunningTime.TotalMilliseconds} ms
        """);
        }
    }
}
