// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.MetricsConsumer;

class PrettyReportMetricsConsumer : IMetricsConsumer
{
    public void ConsumeMetrics(Metrics metrics)
    {
        var responses = metrics.Responses;
        var requests = metrics.Requests.Values.Select(mm => mm.Count).Sum();

        Console.WriteLine($"""
        Results
          Messages .. {metrics.Messages}
          Failed .... {metrics.Failed}
          Ignored ... {metrics.Ignored}
          Methods ... {responses + requests}
            Responses .. {responses}
            Requests ... {requests}
        """);

        var longestMethod = metrics.Requests.Keys
            .MaxBy(method => method.Length)?
            .Length ?? 0;
        foreach (var (method, mm) in metrics.Requests)
        {
            var dots = new string('.', (2 + longestMethod - method.Length));
            Console.WriteLine($"""
              {method} {dots} {mm.Count} in {mm.RunningTime.TotalMilliseconds} ms
        """);
        }

        Console.WriteLine($"""

        Total Running Time .. {metrics.TotalRunningTime.TotalMilliseconds} ms
        """);
    }
}
