// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>The node records of one trie that a <see cref="BufferBulkSet"/> reads and writes: a trie node buffer and the records it does not hold.</summary>
internal interface INodeRecordStore
{
    /// <inheritdoc cref="INodeRecordBuffer.TryLocate"/>
    bool TryLocate(ulong pointer, out NodeRecord record);

    /// <inheritdoc cref="INodeRecordBuffer.StartWriteBatch"/>
    INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength);

    /// <summary>Loads a node <see cref="TryLocate"/> does not resolve, by its position and hash.</summary>
    /// <returns>False when the store has no node of <paramref name="hash"/> at <paramref name="path"/>.</returns>
    bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecord record);
}
