// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public sealed class ConsoleTotalReporter : IMetricsReporter
{
    private readonly IMetricsReportProvider _provider;
    private readonly IMetricsReportFormatter _formatter;

    public ConsoleTotalReporter(IMetricsReportProvider provider, IMetricsReportFormatter formatter)
    {
        _provider = provider;
        _formatter = formatter;
    }

    public async Task Total(TimeSpan elapsed, CancellationToken token = default)
    {
        var report = _provider.Report();
        using (var stream = Console.OpenStandardOutput())
        {
            await _formatter.WriteAsync(stream, report, token);
        }
    }
}
