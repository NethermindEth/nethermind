// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.Metrics;

public sealed class JsonMetricsReportFormatter : IMetricsReportFormatter
{
    public async Task WriteAsync(Stream stream, MetricsReport report, CancellationToken token = default)
    {
        await JsonSerializer.SerializeAsync(stream, report, cancellationToken: token);
    }
}
