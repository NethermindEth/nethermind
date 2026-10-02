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
/// Labels held for the lifetime of the process as one instance per immutable set of values, allowing an observer to
/// resolve their labelled child once per instance rather than on every observation.
/// </summary>
/// <remarks>
/// Implementations must not be mutated after the first observation, and callers should reuse the same instance for
/// the life of the process. The observer uses object identity as the cache key and retains marked instances.
/// </remarks>
public interface IStableMetricLabels : IMetricLabels;
