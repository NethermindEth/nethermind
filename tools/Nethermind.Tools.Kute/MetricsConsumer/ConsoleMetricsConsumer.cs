// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using App.Metrics.Formatters;

namespace Nethermind.Tools.Kute.MetricsConsumer;

public class ConsoleMetricsConsumer : IMetricsConsumer
{

    private readonly IMetricsOutputFormatter _formatter;

    public ConsoleMetricsConsumer(IMetricsOutputFormatter formatter)
    {
        _formatter = formatter;
    }

    public async Task ConsumeMetrics(Metrics metrics)
    {
        var snapshot = metrics.Snapshot;
        using (var stream = Console.OpenStandardOutput())
        {
            await _formatter.WriteAsync(stream, snapshot);
        }
    }
}
