// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Persisted-snapshot metadata catalog: the source of truth for which persisted snapshots exist across
/// restarts. <see cref="NullSnapshotCatalog"/> is wired in its place when long finality is disabled.
/// </summary>
public interface ISnapshotCatalog
{
    /// <summary>Persist a catalog entry, keyed by its <c>(To, depth)</c> tuple.</summary>
    void Add(CatalogEntry entry);

    /// <summary>Remove the entry at <c>(to, depth)</c>. Returns <c>true</c> when one was present.</summary>
    bool Remove(in StateId to, long depth);

    /// <summary>Stream all catalog entries (unordered); eagerly version-checks and seeds metadata.</summary>
    IEnumerable<CatalogEntry> Load();
}
