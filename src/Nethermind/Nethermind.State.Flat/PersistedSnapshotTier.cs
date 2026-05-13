// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Metric;

namespace Nethermind.State.Flat;

/// <summary>
/// Tier of a persisted-snapshot pool. The pool is split into two sibling instances —
/// short-range (<see cref="Small"/>) and long-range (<see cref="Large"/>) — wired by
/// <c>FlatWorldStateModule</c>. Use the static singletons; equality is reference-based.
///
/// <para>
/// Implements <see cref="IMetricLabels"/> so the type can be used directly as the key of
/// per-tier metric dictionaries. <see cref="MetricsController"/>'s
/// <c>KeyIsLabelGaugeMetricUpdater</c> dispatches on <see cref="IMetricLabels"/> and
/// reads <see cref="Labels"/> for the Prometheus label values — wire format stays
/// <c>"small"</c> / <c>"large"</c>.
/// </para>
/// </summary>
public sealed class PersistedSnapshotTier : IMetricLabels
{
    public static readonly PersistedSnapshotTier Small = new("small");
    public static readonly PersistedSnapshotTier Large = new("large");

    public string Name { get; }
    private readonly string[] _labels;

    private PersistedSnapshotTier(string name)
    {
        Name = name;
        _labels = [name];
    }

    public string[] Labels => _labels;

    public override string ToString() => Name;
}
