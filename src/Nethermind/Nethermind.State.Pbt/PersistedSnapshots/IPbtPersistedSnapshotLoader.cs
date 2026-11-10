// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.PersistedSnapshots;

public interface IPbtPersistedSnapshotLoader : IDisposable
{
    void Load();
    bool ConvertAndRegister(PbtSnapshot snapshot);
    bool RegisterCompacted(PbtPersistedSnapshot snapshot, ReadOnlySpan<PbtPersistedSnapshot> sources);
}

public sealed class NullPbtPersistedSnapshotLoader : IPbtPersistedSnapshotLoader
{
    public static readonly NullPbtPersistedSnapshotLoader Instance = new();
    public void Load() { }
    public bool ConvertAndRegister(PbtSnapshot snapshot) => false;
    public bool RegisterCompacted(PbtPersistedSnapshot snapshot, ReadOnlySpan<PbtPersistedSnapshot> sources) => false;
    public void Dispose() { }
}
