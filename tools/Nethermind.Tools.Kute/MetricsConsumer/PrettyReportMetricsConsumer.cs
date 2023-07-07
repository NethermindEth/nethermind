// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using App.Metrics.Formatters.Ascii;

namespace Nethermind.Tools.Kute.MetricsConsumer;

class PrettyReportMetricsConsumer : IMetricsConsumer
{
    public async Task ConsumeMetrics(Metrics metrics)
    {
        var snapshot = metrics.Snapshot;
        var formatter = new MetricsTextOutputFormatter();

        using (var stream = Console.OpenStandardOutput())
        {
            await formatter.WriteAsync(stream, snapshot);
        }
    }
}
