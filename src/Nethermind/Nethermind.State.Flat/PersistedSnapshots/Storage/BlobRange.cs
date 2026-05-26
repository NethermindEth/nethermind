// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// The contiguous trie-node RLP region a base persisted snapshot occupies inside one blob
/// arena file. A base snapshot writes every RLP through a single <see cref="BlobArenaWriter"/>,
/// so its bytes form one <c>[Offset, Offset + Length)</c> run that can be prefetched in a
/// single <c>posix_fadvise(WILLNEED)</c> call.
/// </summary>
/// <remarks>
/// Only base snapshots carry a non-empty range. Compacted / persistable snapshots reference
/// scattered blob arenas via <c>ref_ids</c> and store <see cref="None"/>.
/// </remarks>
public readonly record struct BlobRange(ushort BlobArenaId, long Offset, long Length)
{
    /// <summary>Sentinel for snapshots with no contiguous blob region.</summary>
    public static readonly BlobRange None = default;

    /// <summary>True when there is no region to prefetch.</summary>
    public bool IsEmpty => Length == 0;
}
