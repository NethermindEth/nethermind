// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Monitoring.Metrics;

/// <summary>
/// A static label informer for labeling monitoring gages
/// Note: Assumes that static values are set by the time metrics are registered
/// </summary>
[AttributeUsage(
    AttributeTargets.Field | AttributeTargets.Property,
    AllowMultiple = true)]
public class MetricsStaticDescriptionTagAttribute : Attribute
{
    public string Label { get; }

    public Type Informer { get; }

    /// <summary>
    /// Collects type and its static field name for future metrics registration
    /// </summary>
    /// <param name="metricsStaticLabel">static field name</param>
    /// <param name="informer">type</param>
    public MetricsStaticDescriptionTagAttribute(string metricsStaticLabel, Type informer)
    {
        Label = metricsStaticLabel;
        Informer = informer;
    }
}
