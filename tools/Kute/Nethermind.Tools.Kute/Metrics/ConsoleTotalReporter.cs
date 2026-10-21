// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public sealed class ConsoleTotalReporter(IMetricsReportProvider provider, IMetricsReportFormatter formatter) : IMetricsReporter
{
    private readonly IMetricsReportProvider _provider = provider;
    private readonly IMetricsReportFormatter _formatter = formatter;

    public async Task Total(TimeSpan elapsed, CancellationToken token = default)
    {
        MetricsReport report = _provider.Report();
        await using Stream stream = Console.OpenStandardOutput();
        await _formatter.WriteAsync(stream, report, token);
    }
}
