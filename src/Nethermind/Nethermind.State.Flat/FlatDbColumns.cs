// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

public enum FlatDbColumns
{
    Metadata,
    Account,
    Storage,
    StateNodes,
    StateTopNodes,
    StorageNodes,
    FallbackNodes,
    SmallPersistedSnapshotCatalog,
    LargePersistedSnapshotCatalog,
    // Retained to preserve enum ordinals for existing RocksDB column families.
    // BlobArenaId is now the underlying ArenaFile.Id (per-file, not per-slice),
    // so no per-tier slice catalog exists. After a wipe-and-resync these columns
    // are empty; for older directories the SnapshotCatalog v2→v3 mismatch trips
    // the "wipe and resync" error before anything touches these columns.
    SmallBlobArenaCatalog,
    LargeBlobArenaCatalog,
}
