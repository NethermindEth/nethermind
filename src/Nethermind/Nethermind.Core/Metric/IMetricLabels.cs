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

/// <summary>
/// Labels held for the life of the process, one instance per set of values whose <see cref="IMetricLabels.Labels"/>
/// never change, so an observer may resolve the labelled child once per instance rather than on every observation.
/// </summary>
public interface IStableMetricLabels : IMetricLabels;
