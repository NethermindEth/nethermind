// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Hands out and takes back the per-layer collections, pooled per <see cref="PbtResourcePool.Usage"/>.</summary>
public interface IPbtResourcePool
{
    /// <summary>Rents an empty content for a new diff layer.</summary>
    PbtSnapshotContent GetSnapshotContent(PbtResourcePool.Usage usage);

    /// <summary>
    /// Returns <paramref name="content"/>, resetting it. It must be returned to the
    /// <paramref name="usage"/> it was rented from, and the caller must not touch it afterwards.
    /// </summary>
    void ReturnSnapshotContent(PbtResourcePool.Usage usage, PbtSnapshotContent content);

    /// <summary>Rents an empty prewarm resource with its owner lease armed.</summary>
    PbtTransientResource GetCachedResource(PbtResourcePool.Usage usage);

    /// <summary>Returns an exclusively owned prewarm resource after its final lease is released.</summary>
    /// <remarks>Use the original rental usage; the caller must not access the resource after returning it.</remarks>
    void ReturnCachedResource(PbtResourcePool.Usage usage, PbtTransientResource resource);

    /// <summary>Rents an empty canonical leaf accumulator for one key-zone partition.</summary>
    PbtWriteBatchBuilder GetWriteBatch(PbtResourcePool.Usage usage);

    /// <summary>Returns a partition batch to its original rental usage, discarding pending mutations.</summary>
    /// <remarks>The caller must not access the batch after returning it.</remarks>
    void ReturnWriteBatch(PbtResourcePool.Usage usage, PbtWriteBatchBuilder batch);

}
