// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using App.Metrics.Formatters.Json;

namespace Nethermind.Tools.Kute.MetricsConsumer;

public class JsonMetricsConsumer : IMetricsConsumer
{
    public async Task ConsumeMetrics(Metrics metrics)
    {
        var snapshot = metrics.Snapshot;
        var formatter = new MetricsJsonOutputFormatter();

        using (var stream = Console.OpenStandardOutput())
        {
            await formatter.WriteAsync(stream, snapshot);
        }
    }
}
