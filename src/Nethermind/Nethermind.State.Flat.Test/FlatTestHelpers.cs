// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Persistence;
using NSubstitute;

namespace Nethermind.State.Flat.Test;

internal static class FlatTestHelpers
{
    /// <summary>
    /// Builds a single-snapshot <see cref="ReadOnlySnapshotBundle"/> backed by a substitute persistence reader,
    /// optionally pre-populating the snapshot content via <paramref name="populate"/>.
    /// </summary>
    public static ReadOnlySnapshotBundle MakeBundle(ResourcePool pool, Action<SnapshotContent>? populate = null)
    {
        SnapshotContent content = pool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        populate?.Invoke(content);
        Snapshot snap = new(StateId.PreGenesis, StateId.PreGenesis, content, pool, ResourcePool.Usage.MainBlockProcessing);
        SnapshotPooledList list = new(1) { snap };
        return new ReadOnlySnapshotBundle(list, Substitute.For<IPersistence.IPersistenceReader>(), recordDetailedMetrics: false);
    }
}
