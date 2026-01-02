// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Metric;

/// <summary>
/// Used by MetricController to provide labels. Useful in high performance scenario where you don't want to set a string
/// on the metric dictionary as key and/or you don't want to use tuple.
/// </summary>
public interface IMetricLabels
{
    string[] Labels { get; }
}
