// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// No-op <see cref="IPersistedSnapshotCompactor"/> wired alongside
/// <see cref="NullPersistedSnapshotRepository"/> when the long-finality feature is
/// disabled, so the rest of the persistence pipeline can resolve a compactor
/// without spinning up real arena-backed compaction work.
/// </summary>
public sealed class NullPersistedSnapshotCompactor : IPersistedSnapshotCompactor
{
    public static readonly NullPersistedSnapshotCompactor Instance = new();

    private NullPersistedSnapshotCompactor() { }

    // Dispose immediately — no compaction work, but ownership still transfers so callers don't leak.
    public ValueTask EnqueueAsync(ArrayPoolList<StateId> batch, long persistedBlockNumber, CancellationToken cancellationToken)
    {
        batch.Dispose();
        return ValueTask.CompletedTask;
    }

    // Shared singleton: disposal must be a safe no-op so a container or forwarding caller
    // can dispose it without breaking the shared instance.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
