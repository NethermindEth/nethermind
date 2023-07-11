// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using App.Metrics.Formatters;
using App.Metrics.Formatters.Ascii;

namespace Nethermind.Tools.Kute.MetricsConsumer;

class PrettyReportMetricsConsumer : IMetricsConsumer
{
    private readonly IMetricsOutputFormatter _formatter = new MetricsTextOutputFormatter();

    public async Task ConsumeMetrics(Metrics metrics)
    {
        var snapshot = metrics.Snapshot;
        using (var stream = Console.OpenStandardOutput())
        {
            await _formatter.WriteAsync(stream, snapshot);
        }
    }
}
