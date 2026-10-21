// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Db;

namespace Nethermind.State.Flat;

internal static class FlatDbConfigExtensions
{
    /// <summary>
    /// Validates that <see cref="IFlatDbConfig.CompactSize"/> fits in <see cref="int"/> with the small
    /// headroom (+8) used for pool sizing, so the later narrowing cannot overflow, and that
    /// <see cref="IFlatDbConfig.PersistedSnapshotMaxCompactSize"/> is a power of 2 no smaller than
    /// <see cref="IFlatDbConfig.CompactSize"/> within the same <see cref="int"/> range.
    /// </summary>
    /// <remarks>
    /// A max below <c>CompactSize</c> would cap <c>GetPersistedSnapshotCompactSize</c> under the
    /// <c>== CompactSize</c> / <c>&gt; CompactSize</c> boundary predicates, so no persisted-snapshot
    /// boundary would ever be detected; a non-power-of-2 max misclassifies boundaries the same way.
    /// </remarks>
    public static void ValidateCompactSize(this IFlatDbConfig config)
    {
        if (config.CompactSize > int.MaxValue - 8)
            throw new ArgumentOutOfRangeException(nameof(config.CompactSize), "Compact size must not exceed int.MaxValue - 8");
        if (config.PersistedSnapshotMaxCompactSize > int.MaxValue - 8)
            throw new ArgumentOutOfRangeException(nameof(config.PersistedSnapshotMaxCompactSize), "Persisted snapshot max compact size must not exceed int.MaxValue - 8");
        if (config.PersistedSnapshotMaxCompactSize < config.CompactSize)
            throw new ArgumentOutOfRangeException(nameof(config.PersistedSnapshotMaxCompactSize), "Persisted snapshot max compact size must not be smaller than CompactSize");
        if (!BitOperations.IsPow2(config.PersistedSnapshotMaxCompactSize))
            throw new ArgumentException("Persisted snapshot max compact size must be a power of 2", nameof(config.PersistedSnapshotMaxCompactSize));
    }
}
