// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    /// <summary>Rents an empty builder for a scope's uncommitted per-block state.</summary>
    /// <remarks>Disposing the builder returns it, so callers need not hold on to the pool.</remarks>
    PbtWriteBatchBuilder GetWriteBatchBuilder(PbtResourcePool.Usage usage);

    /// <inheritdoc cref="ReturnSnapshotContent"/>
    /// <remarks>Called by <see cref="PbtWriteBatchBuilder.Dispose"/> rather than by whoever rented it.</remarks>
    void ReturnWriteBatchBuilder(PbtResourcePool.Usage usage, PbtWriteBatchBuilder builder);
}
