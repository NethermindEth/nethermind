// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.FlatPriorityRead.Test;

/// <summary>A storage trie store over a flat persistence reader and write batch that records the path of every node it writes.</summary>
public sealed class StorageTrieWriter(IPersistence.IPersistenceReader? reader, IPersistence.IWriteBatch batch, Hash256 account, ISet<TreePath> written)
    : AbstractMinimalTrieStore
{
    /// <summary>Sets <paramref name="slots"/> in the storage trie of <paramref name="address"/> at <paramref name="root"/> and writes the changed nodes to <paramref name="batch"/>.</summary>
    /// <param name="reader">Reads the existing trie; null when <paramref name="root"/> is empty.</param>
    /// <returns>The new root.</returns>
    public static Hash256 Write(IPersistence.IPersistenceReader? reader, IPersistence.IWriteBatch batch, Address address, Hash256 root,
        IEnumerable<(UInt256 Slot, UInt256 Value)> slots, ISet<TreePath> written)
    {
        StorageTree tree = new(new StorageTrieWriter(reader, batch, address.ToAccountPath.ToCommitment(), written), root, LimboLogs.Instance);
        foreach ((UInt256 slot, UInt256 value) in slots) tree.Set(slot, value.ToBigEndian());
        tree.Commit();
        return tree.RootHash;
    }

    public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        reader?.TryLoadStorageRlp(account, path, flags);

    public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(batch, account, written);

    private sealed class Committer(IPersistence.IWriteBatch batch, Hash256 account, ISet<TreePath> written) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            batch.SetStorageTrieNode(account, path, node.FullRlp.AsSpan());
            written.Add(path);
            return node;
        }

        public void Dispose() { }
    }
}
