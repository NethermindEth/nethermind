// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Which in-memory bucket a catalog entry belongs to. Persisted in the catalog so a reload
/// routes each snapshot correctly — a base and a sub-<c>CompactSize</c> compacted snapshot
/// both have a block range below <c>CompactSize</c> and cannot be told apart by range alone.
/// </summary>
public enum SnapshotKind : byte
{
    /// <summary>An in-memory snapshot persisted directly — owns a contiguous blob region.</summary>
    Base = 0,

    /// <summary>A compacted (merged) snapshot — references base blob arenas, no blob region.</summary>
    Compacted = 1,

    /// <summary>The <c>CompactSize</c>-wide snapshot that gets written to RocksDB.</summary>
    Persistable = 2,
}
