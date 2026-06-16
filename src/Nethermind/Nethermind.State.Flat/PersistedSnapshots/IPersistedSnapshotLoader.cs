// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Owns the lifecycle of the <see cref="ISnapshotRepository"/>'s persisted tier: loads it from the
/// catalog at startup (<see cref="Load"/>) and tears it down at shutdown (<see cref="IDisposable.Dispose"/>).
/// </summary>
public interface IPersistedSnapshotLoader : IDisposable
{
    /// <summary>Rehydrate the arena/blob stores, construct every persisted snapshot from the catalog
    /// into the repository's tier buckets, and rebuild their blooms. Drives the repository's persisted
    /// tier from empty to fully populated; called once at startup.</summary>
    void Load();

    /// <summary>
    /// Persist an in-memory <see cref="Snapshot"/> as a base entry in the persisted tier: build its
    /// HSST metadata + contiguous trie-RLP region into the shared arena/blob pools, fsync for
    /// durability, then register it in the repository's base bucket (which takes its own lease).
    /// </summary>
    void ConvertAndRegister(Snapshot snapshot);
}
