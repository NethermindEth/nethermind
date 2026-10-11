// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.PersistedSnapshots;

public interface IPbtRetainedSnapshotLoader : IDisposable
{
    void Load();
    bool ConvertAndRegister(PbtSnapshot snapshot);
    bool RegisterCompacted(PbtRetainedSnapshot snapshot, ReadOnlySpan<PbtRetainedSnapshot> sources);
}

public sealed class NullPbtRetainedSnapshotLoader : IPbtRetainedSnapshotLoader
{
    public static readonly NullPbtRetainedSnapshotLoader Instance = new();
    public void Load() { }
    public bool ConvertAndRegister(PbtSnapshot snapshot) => false;
    public bool RegisterCompacted(PbtRetainedSnapshot snapshot, ReadOnlySpan<PbtRetainedSnapshot> sources) => false;
    public void Dispose() { }
}
