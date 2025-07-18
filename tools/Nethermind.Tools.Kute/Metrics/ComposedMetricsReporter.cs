// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public sealed class ComposedMetricsReporter : IMetricsReporter
{
    private readonly IMetricsReporter[] _reporters;


    public ComposedMetricsReporter(params IMetricsReporter[] reporters)
    {
        _reporters = reporters;
    }

    public async Task Message()
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Message();
        }
    }

    public async Task Response()
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Response();
        }
    }

    public async Task Succeeded()
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Succeeded();
        }
    }

    public async Task Failed()
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Failed();
        }
    }

    public async Task Ignored()
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Ignored();
        }
    }

    public async Task Batch(int requestId, TimeSpan elapsed)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Batch(requestId, elapsed);
        }
    }

    public async Task Single(int requestId, TimeSpan elapsed)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Single(requestId, elapsed);
        }
    }

    public async Task Total(TimeSpan elapsed)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Total(elapsed);
        }
    }
}
