// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>
/// An <see cref="IPbtStore"/> that reads trie nodes and leaf blobs from a persisted
/// <see cref="IPbtPersistence.IReader"/> and writes them straight into an
/// <see cref="IPbtPersistence.IWriteBatch"/>. It lets <see cref="TrieUpdater.UpdateRoot"/> run
/// directly against durable storage — for a bulk rebuild — rather than the in-memory overlays of a
/// processing scope.
/// </summary>
/// <remarks>
/// The reader is a point-in-time snapshot that does not observe writes buffered in the batch. This is
/// safe within a single <see cref="TrieUpdater.UpdateRoot"/> call, which reads each node/blob at most
/// once and before writing it. The arrays returned by the reader are already owned, so they are wrapped
/// without copying; a written value is copied into a fresh array the batch owns, and its lease released.
/// </remarks>
internal sealed class PersistenceBackedPbtStore(IPbtPersistence.IReader reader, IPbtPersistence.IWriteBatch batch) : IPbtStore
{
    public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => reader.GetTrieNode(key);

    public RefCountingMemory? GetLeafBlob(in Stem stem) => reader.GetLeafBlob(stem);

    public void SetTrieNode(in TrieNodeKey key, RefCountingMemory? node) => batch.SetTrieNode(key, node.ToArrayAndRelease());

    public void SetLeafBlob(in Stem stem, RefCountingMemory? blob) => batch.SetLeafBlob(stem, blob.ToArrayAndRelease());
}
