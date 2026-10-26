// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>The node records of one trie that a <see cref="NodeRecordBulkSet"/> reads and writes: a trie node buffer and the records it does not hold.</summary>
public interface INodeRecordStore
{
    /// <summary>Finds a borrowed record, or misses when its pointer is absent or no longer available.</summary>
    bool TryLocate(ulong pointer, out NodeRecord record);

    /// <summary>Reserves contiguous, zero-filled bytes for node records, each locatable by its pointer once written.</summary>
    /// <remarks>
    /// The reservation cannot cross a pointer-address boundary. The writer holds a pin no newer than the batch until all written
    /// records and their roots are published.
    /// </remarks>
    /// <param name="alignedTotalLength">The total length of the records, a multiple of the record alignment.</param>
    INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength);

    /// <summary>Loads a node <see cref="TryLocate"/> does not resolve, by its position and hash, handing its bytes to <paramref name="action"/>.</summary>
    /// <remarks>
    /// The bytes are the node's RLP, or its record when the store holds one, which <see cref="NodeRecordCodec.IsRecord"/> tells apart.
    /// They are readable only during the call.
    /// </remarks>
    /// <returns>False, without calling <paramref name="action"/>, when the store has no node of <paramref name="hash"/> at <paramref name="path"/>.</returns>
    bool TryLoad<TArg>(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct;
}
