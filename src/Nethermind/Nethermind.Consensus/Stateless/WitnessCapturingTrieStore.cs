// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// WitnessCapturingTrieStore works by reading and writing to the passed in keyValueStore first as if it is an archive node.
/// If a node is missing, then it will try to find from the base store.
/// On reset the base db provider is expected to clear any diff which causes this overlay trie store to no longer
/// see overlayed keys.
/// </summary>
public class WitnessCapturingTrieStore(IKeyValueStoreWithBatching keyValueStore, IReadOnlyTrieStore baseStore) : ITrieStore
{
    private readonly INodeStorage _nodeStorage = new NodeStorage(keyValueStore);

    private readonly ConcurrentDictionary<Hash256, byte[]> _rlpCollector = new();

    public byte[][] TouchedNodesRlp => _rlpCollector.Values.ToArray();

    public void Dispose()
    {
        baseStore.Dispose();
    }

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        TrieNode node = baseStore.FindCachedOrUnknown(address, in path, hash);
        if (node.NodeType != NodeType.Unknown)
            _rlpCollector.TryAdd(node.Keccak, node.FullRlp.Span.ToArray());
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
        byte[]? code = _nodeStorage.Get(address, in path, hash, flags) ?? baseStore.TryLoadRlp(address, in path, hash, flags);
        if (code is not null) _rlpCollector.TryAdd(hash, code);
        return code;
    }

    public bool IsPersisted(Hash256? address, in TreePath path, in ValueHash256 keccak) => _nodeStorage.Get(address, in path, in keccak) is not null || baseStore.IsPersisted(address, in path, in keccak);

    public bool HasRoot(Hash256 stateRoot) => _nodeStorage.Get(null, TreePath.Empty, stateRoot) is not null || baseStore.HasRoot(stateRoot);

    public IDisposable BeginScope(BlockHeader? baseBlock) => baseStore.BeginScope(baseBlock);

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public INodeStorage.KeyScheme Scheme => baseStore.Scheme;

    public IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;

    // Write directly to _nodeStorage, which goes to db provider.
    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => new RawScopedTrieStore.Committer(_nodeStorage, address, writeFlags);
}
