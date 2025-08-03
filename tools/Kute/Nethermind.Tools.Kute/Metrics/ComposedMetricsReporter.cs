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

    public async Task Message(CancellationToken token = default)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Message(token);
        }
    }

    public async Task Response(CancellationToken token = default)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Response(token);
        }
    }

    public async Task Succeeded(CancellationToken token = default)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Succeeded(token);
        }
    }

    public async Task Failed(CancellationToken token = default)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Failed(token);
        }
    }

    public async Task Ignored(CancellationToken token = default)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Ignored(token);
        }
    }

    public async Task Batch(JsonRpc.Request.Batch batch, TimeSpan elapsed, CancellationToken token = default)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Batch(batch, elapsed, token);
        }
    }

    public async Task Single(JsonRpc.Request.Single single, TimeSpan elapsed, CancellationToken token = default)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Single(single, elapsed, token);
        }
    }

    public async Task Total(TimeSpan elapsed, CancellationToken token = default)
    {
        foreach (IMetricsReporter reporter in _reporters)
        {
            await reporter.Total(elapsed, token);
        }
    }
}
