// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.PersistedSnapshots;

internal interface IPbtRetainedSnapshotCompactor : IAsyncDisposable
{
    // Ownership transfers even when enqueue fails or cancellation is already requested.
    ValueTask EnqueueAsync(ArrayPoolList<StateId> batch, ulong persistedBlockNumber, CancellationToken cancellationToken);
}

internal sealed class NullPbtRetainedSnapshotCompactor : IPbtRetainedSnapshotCompactor
{
    internal static readonly NullPbtRetainedSnapshotCompactor Instance = new();
    public ValueTask EnqueueAsync(ArrayPoolList<StateId> batch, ulong persistedBlockNumber, CancellationToken cancellationToken)
    {
        batch.Dispose();
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
