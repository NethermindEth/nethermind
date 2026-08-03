// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Owns the lifecycle of the <see cref="ISnapshotRepository"/>'s persisted tier: loads it from the
/// catalog at startup (<see cref="Load"/>) and tears it down at shutdown (<see cref="IDisposable.Dispose"/>).
/// </summary>
public interface IPersistedSnapshotLoader : IDisposable
{
    /// <summary>Drives the repository's persisted tier from empty to fully populated; called once at startup.</summary>
    void Load();

    /// <summary>Persists an in-memory <see cref="Snapshot"/> as a base entry in the repository's persisted tier.</summary>
    void ConvertAndRegister(Snapshot snapshot);
}
