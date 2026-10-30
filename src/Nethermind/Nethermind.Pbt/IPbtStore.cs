// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

/// <summary>The node groups of one tree that <see cref="TrieUpdater"/> reads and writes: a group record buffer and the groups it does not hold.</summary>
public interface IPbtStore
{
    /// <summary>Finds a borrowed record, or misses when its pointer is absent or no longer available.</summary>
    bool TryLocate(ulong pointer, out PbtGroupRecord record);

    /// <summary>Reserves contiguous, zero-filled bytes for group records, registered with the store when the batch is disposed.</summary>
    /// <remarks>
    /// Any number of batches may be written concurrently, each started and disposed on any thread. The caller fills the batch
    /// with complete records before publishing their pointers to a parent. The store keeps the records alive through fold
    /// completion and publication. A removed group writes no record: it is simply no longer linked.
    /// </remarks>
    /// <param name="alignedTotalLength">The total length of the records, a multiple of <see cref="PbtGroupRecordCodec.RecordAlignment"/>.</param>
    IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength);

    /// <summary>Loads a group <see cref="TryLocate"/> does not resolve, by its path, handing its bytes to <paramref name="action"/>.</summary>
    /// <remarks>
    /// The bytes are the group's payload, or its record when the store holds one, which <see cref="PbtGroupRecordCodec.IsRecord"/>
    /// tells apart. They are readable only during the call, and the path is borrowed only for it.
    /// </remarks>
    /// <returns>False, without calling <paramref name="action"/>, when the store has no group at <paramref name="path"/>.</returns>
    bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct;
}
