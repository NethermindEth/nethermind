// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute;

class ConsoleReportMetricsConsumer : IMetricsConsumer
{
    public void ConsumeMetrics(Metrics metrics)
    {
        Console.WriteLine($"""
        Total Running Time:  {metrics.TotalRunningTime.TotalMilliseconds} ms
        Results:
            Total:     {metrics.Total}
            Failures:  {metrics.Failed}
            Methods:
                Responses: {metrics.Responses}
                Requests:  {metrics.Requests.Values.Sum()}
        """);
        foreach (var (method, count) in metrics.Requests)
        {
            Console.WriteLine($"""
                    {method}:  {count}
        """);
        }
    }
}
