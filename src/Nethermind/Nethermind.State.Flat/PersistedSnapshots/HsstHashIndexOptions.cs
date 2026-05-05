// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Per-snapshot toggles for the BTreeHashIndex HSST format. Selects which large
/// HSSTs in a persisted snapshot get a trailing hash-index section. The same
/// <see cref="TargetUtilization"/> is used wherever the format is enabled.
/// </summary>
public readonly record struct HsstHashIndexOptions(
    bool ForAddressIndex,
    bool ForTriesIndex,
    double TargetUtilization)
{
    public static HsstHashIndexOptions Disabled { get; } = new(false, false, 0.75);
}
