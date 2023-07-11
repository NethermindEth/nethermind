// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using App.Metrics.Formatters;
using App.Metrics.Formatters.Json;

namespace Nethermind.Tools.Kute.MetricsConsumer;

public class JsonMetricsConsumer : IMetricsConsumer
{
    private readonly IMetricsOutputFormatter _formatter = new MetricsJsonOutputFormatter();

    public async Task ConsumeMetrics(Metrics metrics)
    {
        var snapshot = metrics.Snapshot;
        using (var stream = Console.OpenStandardOutput())
        {
            await _formatter.WriteAsync(stream, snapshot);
        }
    }
}
