// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// No-op <see cref="IPersistedSnapshotLoader"/> wired when long finality is disabled: it neither loads an
/// existing persisted tier at startup nor converts in-memory snapshots into it, so the tier stays empty.
/// </summary>
public sealed class NullPersistedSnapshotLoader : IPersistedSnapshotLoader
{
    public static readonly NullPersistedSnapshotLoader Instance = new();

    private NullPersistedSnapshotLoader() { }

    public void Load() { }

    public void ConvertAndRegister(Snapshot snapshot) { }

    // Shared singleton: disposal must be a safe no-op.
    public void Dispose() { }
}
