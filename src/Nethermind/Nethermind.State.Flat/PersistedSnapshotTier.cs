// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Metric;

namespace Nethermind.State.Flat;

/// <summary>
/// Label for the persisted-snapshot pool. The pool is a single instance wired by
/// <c>FlatWorldStateModule</c>; this type survives as the key of the per-pool metric
/// dictionaries. Use the static <see cref="Persisted"/> singleton; equality is
/// reference-based.
///
/// <para>
/// Implements <see cref="IMetricLabels"/> so the type can be used directly as the key of
/// per-pool metric dictionaries. <see cref="MetricsController"/>'s
/// <c>KeyIsLabelGaugeMetricUpdater</c> dispatches on <see cref="IMetricLabels"/> and
/// reads <see cref="Labels"/> for the Prometheus label values — wire format is
/// <c>"persisted"</c>.
/// </para>
/// </summary>
public sealed class PersistedSnapshotTier : IMetricLabels
{
    public static readonly PersistedSnapshotTier Persisted = new("persisted");

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
