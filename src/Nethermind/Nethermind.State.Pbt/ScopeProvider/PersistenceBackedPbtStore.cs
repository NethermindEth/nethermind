// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>
/// An <see cref="IPbtStore"/> that lets <see cref="TrieUpdater.UpdateRoot"/> run directly against
/// durable storage — for a bulk rebuild — rather than the in-memory overlays of a processing scope.
/// </summary>
/// <remarks>
/// The reader is a point-in-time snapshot that does not observe writes buffered in the batch. This is
/// safe within a single <see cref="TrieUpdater.UpdateRoot"/> call, which reads each node/blob at most
/// once and before writing it — including the two reads the descent makes off its own recursion: a
/// chain's target lies below the frame reading it, and a group collapsing onto an untouched child reads
/// one nothing descended into.
/// <para>
/// Reads are a snapshot's and need nothing of this; the writes take a lock, the batch underneath being
/// one RocksDB structure that several threads of a fold would otherwise write at once.
/// </para>
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
