// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <remarks>
/// Delegates all logic to base store, only adds logic for capturing trie nodes
/// accessed during execution as well as during state root recomputation.
/// </remarks>
public class WitnessCapturingTrieStore(IReadOnlyTrieStore baseStore) : ITrieStore
{
    private readonly ConcurrentDictionary<Hash256AsKey, byte[]> _rlpCollector = new();

    public IEnumerable<byte[]> TouchedNodesRlp => _rlpCollector.Values;

    public void Dispose() => baseStore.Dispose();

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        TrieNode node = baseStore.FindCachedOrUnknown(address, in path, hash);
        if (node.NodeType != NodeType.Unknown) _rlpCollector.TryAdd(node.Keccak, node.FullRlp.ToArray());
        return node;
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = TryLoadRlp(address, in path, hash, flags);
        if (rlp is null) throw new MissingTrieNodeException("Missing RLP node", address, path, hash);
        return rlp;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = baseStore.TryLoadRlp(address, in path, hash, flags);
        if (rlp is not null) _rlpCollector.TryAdd(hash, rlp);
        return rlp;
    }

    public bool HasRoot(Hash256 stateRoot) => baseStore.HasRoot(stateRoot);

    public IDisposable BeginScope(BlockHeader? baseBlock) => baseStore.BeginScope(baseBlock);

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public INodeStorage.KeyScheme Scheme => baseStore.Scheme;

    public IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;

    // Write delegates to base store, be careful to use no-op base store committer
    // if we don't want to persist any trie nodes to the database.
    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => baseStore.BeginCommit(address, root, writeFlags);
}
