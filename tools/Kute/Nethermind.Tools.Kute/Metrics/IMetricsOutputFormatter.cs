// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public interface IMetricsReportFormatter
{
    Task WriteAsync(Stream stream, MetricsReport report, CancellationToken token = default);
}
