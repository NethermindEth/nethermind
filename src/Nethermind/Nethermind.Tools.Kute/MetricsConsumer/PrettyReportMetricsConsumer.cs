// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.MetricsConsumer;

class PrettyReportMetricsConsumer : IMetricsConsumer
{
    public void ConsumeMetrics(Metrics metrics)
    {
        Console.WriteLine($"""
        Running Time .. {metrics.TotalRunningTime.TotalMilliseconds} ms
        Results
          Messages .. {metrics.Messages}
          Failed .... {metrics.Failed}
          Ignored ... {metrics.Ignored}
          Methods
            Responses .. {metrics.Responses}
            Requests ... {metrics.Requests.Values.Sum()}
        """);
        var longestMethod = metrics.Requests.Keys
            .MaxBy(method => method.Length)?
            .Length ?? 0;
        foreach (var (method, count) in metrics.Requests)
        {
            var dots = new string('.', (2 + longestMethod - method.Length));
            Console.WriteLine($"""
              {method} {dots} {count}
        """);
        }
    }
}
