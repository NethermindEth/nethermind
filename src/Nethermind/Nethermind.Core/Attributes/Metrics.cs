// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

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

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class KeyIsLabel : Attribute
{
    public string LabelName { get; }

    public KeyIsLabel(string labelName)
    {
        LabelName = labelName;
    }
}
