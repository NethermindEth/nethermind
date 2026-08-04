// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History;

/// <summary>
/// One chunk of a block's changeset, as delivered by any <see cref="IWindowImportSource"/> — the same shape
/// <see cref="ChangesetSidecarStore"/> persists locally, so a fetched chunk and a locally-captured one are
/// interchangeable to whatever consumes them. <see cref="ChunkIndex"/> is 0-based and contiguous within a block;
/// <see cref="IsLastChunkForBlock"/> lets a consumer detect a block's end without needing an upfront chunk count.
/// </summary>
public readonly record struct WindowImportChunk(ulong Block, uint ChunkIndex, bool IsLastChunkForBlock, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Transport-agnostic source of changeset chunks for populating or extending a window. The concurrent backfill
/// importer (39-3) is written against this interface, not against a specific transport, so a local trie-diff
/// walker and a devp2p peer client (39-2) are interchangeable inputs to the same import path — devp2p is not
/// the only feed this is meant to support; era-file bulk artifacts and post-Amsterdam BALs are plausible future
/// implementations behind the same seam.
/// </summary>
public interface IWindowImportSource
{
    /// <summary>Streams changeset chunks for <c>[fromBlockInclusive, toBlockInclusive]</c> in ascending block
    /// order; within a block, chunks are yielded in ascending <see cref="WindowImportChunk.ChunkIndex"/> order.
    /// Cancellation must stop the stream promptly — a backfill importer running alongside live head processing
    /// is expected to cancel this frequently rather than let a single call run unbounded.</summary>
    IAsyncEnumerable<WindowImportChunk> GetChangesetsAsync(ulong fromBlockInclusive, ulong toBlockInclusive, CancellationToken cancellationToken);
}
