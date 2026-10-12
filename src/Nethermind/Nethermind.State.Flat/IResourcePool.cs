// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

public interface IResourcePool
{
    SnapshotContent GetSnapshotContent(ResourcePool.Usage usage);
    void ReturnSnapshotContent(ResourcePool.Usage usage, SnapshotContent snapshotContent);
    SortedSnapshotContent GetSortedSnapshotContent(ResourcePool.Usage usage);
    void ReturnSortedSnapshotContent(ResourcePool.Usage usage, SortedSnapshotContent sortedContent);
    TransientResource GetCachedResource(ResourcePool.Usage usage);
    void ReturnCachedResource(ResourcePool.Usage usage, TransientResource transientResource);
    Snapshot CreateSnapshot(in StateId from, in StateId to, ResourcePool.Usage usage);
}
