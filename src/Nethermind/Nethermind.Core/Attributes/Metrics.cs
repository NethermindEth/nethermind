// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Metric;

namespace Nethermind.Core.Attributes;

/// <summary>
/// Represents a metric with up/down semantics, something that is incremented or decremented over time.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class CounterMetricAttribute : Attribute { }

/// <summary>
/// Represents a metric with an assignment semantics, something that is assigned to a new value.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class GaugeMetricAttribute : Attribute { }

/// <summary>
/// Mark that the attribute is a dictionary whose key is used as a label of name LabelName.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class KeyIsLabelAttribute(params string[] labelNames) : Attribute
{
    public string[] LabelNames { get; } = labelNames;
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SummaryMetricAttribute : Attribute
{
    public string[] LabelNames { get; set; } = [];

    // Summary objective in quantile-epsilon pair
    public double[] ObjectiveQuantile { get; set; } = [];
    public double[] ObjectiveEpsilon { get; set; } = [];
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class HistogramMetricAttribute : Attribute
{
    public string[] LabelNames { get; init; } = [];
    public double[] Buckets { get; init; } = [];
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ExponentialPowerHistogramMetric : Attribute
{
    public string[] LabelNames { get; init; } = [];
    public double Start { get; set; }
    public double Factor { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Mark a metric as detailed
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class DetailedMetricAttribute : Attribute
{
}

/// <summary>
/// Mark a property that is used to enable detailed metrics (not a metric itself)
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class DetailedMetricOnFlagAttribute : Attribute
{
}

public sealed class StringLabel(string label) : IMetricLabels, IEquatable<StringLabel>
{
    private readonly string _label = label;

    public string[] Labels { get; } = [label];

    public bool Equals(StringLabel? other) => other is not null && _label == other._label;

    public override bool Equals(object? obj) => Equals(obj as StringLabel);

    public override int GetHashCode() => _label.GetHashCode();
}
