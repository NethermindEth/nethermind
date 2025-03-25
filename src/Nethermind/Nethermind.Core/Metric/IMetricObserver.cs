// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Metric;

public interface IMetricObserver
{
    public void Observe(double value, IMetricLabels? labels = null);
}
