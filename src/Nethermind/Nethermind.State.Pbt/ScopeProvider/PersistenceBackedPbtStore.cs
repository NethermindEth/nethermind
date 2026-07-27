// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides <see cref="TrieUpdater.UpdateRoot"/> direct access to durable storage during bulk rebuilds.</summary>
/// <remarks>
/// The reader is a point-in-time snapshot and does not observe buffered writes. This is safe because an
/// update reads each node and blob before writing it. Writes are synchronized because the RocksDB batch
/// is shared by fold workers.
/// </remarks>
internal sealed class PersistenceBackedPbtStore(IPbtPersistence.IReader reader, IPbtPersistence.IWriteBatch batch) : IPbtStore
{
    private readonly Lock _writeLock = new();

    public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => reader.GetTrieNode(key);

    public RefCountingMemory? GetLeafBlob(in Stem stem) => reader.GetLeafBlob(stem);

    public void SetTrieNode(in TrieNodeKey key, RefCountingMemory? node)
    {
        using (node)
        {
            lock (_writeLock) batch.SetTrieNode(key, node is null ? default : node.GetSpan());
        }
    }

    public void SetLeafBlob(in Stem stem, RefCountingMemory? blob)
    {
        using (blob)
        {
            lock (_writeLock) batch.SetLeafBlob(stem, blob is null ? default : blob.GetSpan());
        }
    }
}
