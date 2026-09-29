// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// A single catalog entry describing a persisted snapshot's identity, metadata-arena location and
/// persisted <see cref="SnapshotTier"/>. The contiguous blob-RLP region (base snapshots only) lives in
/// the snapshot's own metadata table under the <c>blob_range</c> key, not here.
/// </summary>
public sealed record CatalogEntry(
    StateId From,
    StateId To,
    SnapshotLocation Location,
    SnapshotTier Tier);
