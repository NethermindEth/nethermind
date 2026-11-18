// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

/// <summary>Resolves trie nodes and stages commits in the supplied batch when present.</summary>
/// <param name="nodeStorage">Node storage for reads and ordinary commits.</param>
/// <param name="address">Storage trie address, or null for the state trie.</param>
/// <param name="writeBatch">An externally owned batch; committers do not dispose it.</param>
public class RawScopedTrieStore(INodeStorage nodeStorage, Hash256? address, INodeStorage.IWriteBatch? writeBatch) : IScopedTrieStore
{
    /// <summary>Creates a trie store whose committers own their write batches.</summary>
    /// <param name="nodeStorage">Node storage for reads and commits.</param>
    /// <param name="address">Storage trie address, or null for the state trie.</param>
    public RawScopedTrieStore(INodeStorage nodeStorage, Hash256? address = null) : this(nodeStorage, address, null) { }

    public RawScopedTrieStore(IKeyValueStoreWithBatching kv, Hash256? address = null) : this(new NodeStorage(kv), address)
    {
    }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        nodeStorage.Get(address, path, hash.ValueHash256, flags) ?? ThrowMissingNode(path, hash);

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        nodeStorage.Get(address, path, hash.ValueHash256, flags);

    // Out of line, so the message's interpolation handler does not widen the frame of every load.
    [DoesNotReturn, StackTraceHidden]
    private byte[] ThrowMissingNode(in TreePath path, Hash256 hash) =>
        throw new MissingTrieNodeException($"Node missing hash={hash} addr={address} path={path}", address, path, hash);

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => new RawScopedTrieStore(nodeStorage, address);

    public INodeStorage.KeyScheme Scheme => nodeStorage.Scheme;

    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(nodeStorage, address, writeFlags, writeBatch);

    public class Committer(INodeStorage nodeStorage, Hash256? address, WriteFlags writeFlags, INodeStorage.IWriteBatch? writeBatch) : ICommitter
    {
        public Committer(INodeStorage nodeStorage, Hash256? address, WriteFlags writeFlags) : this(nodeStorage, address, writeFlags, null) { }

        INodeStorage.IWriteBatch _writeBatch = writeBatch ?? nodeStorage.StartWriteBatch();

        public void Dispose()
        {
            if (writeBatch is null) _writeBatch.Dispose();
        }

        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            if (!node.IsBoundaryProofNode)
            {
                if (node.Keccak is null)
                {
                    ThrowUnknownHash(node);
                }

                node.IsPersisted = true;
                _writeBatch.Set(address, path, node.Keccak, node.FullRlp.AsSpan(), writeFlags);
            }

            return node;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");
    }
}
