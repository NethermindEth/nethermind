// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Metric;

public class NoopMetricObserver : IMetricObserver
{
    public static NoopMetricObserver Instance = new();

    public void Observe(double value, IMetricLabels? labels = null)
    {
    }
}
