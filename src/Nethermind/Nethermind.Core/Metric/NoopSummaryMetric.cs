// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Metric;

public class NoopSummaryMetric: ISummaryMetricObserver
{
    public static NoopSummaryMetric Instance = new();

    public void Observe(IMetricLabels labels, double value)
    {
    }
}
