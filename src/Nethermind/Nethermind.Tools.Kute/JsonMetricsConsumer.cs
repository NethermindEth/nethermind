// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute;

public class JsonMetricsConsumer : IMetricsConsumer
{
    public void ConsumeMetrics(Metrics metrics)
    {
        var metricsObject = new
        {
            TotalRunningTime = $"{metrics.TotalRunningTime.Milliseconds} ms",
            metrics.Total,
            metrics.Failed,
            Methods = new { metrics.Responses, metrics.Requests }
        };

        string json = JsonSerializer.Serialize(
            metricsObject,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );
        Console.WriteLine(json);
    }
}
