// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <remarks>
/// Delegates all logic to base store except for writing trie nodes (readonly!)
/// Adds logic for capturing trie nodes accessed during execution and state root recomputation.
/// </remarks>
public class WitnessCapturingTrieStore(IReadOnlyTrieStore baseStore) : ITrieStore, IScopedReadOnlyTraversalProvider
{
    private readonly IReadOnlyTrieStore _baseStore = baseStore;
    private readonly ConcurrentDictionary<Hash256AsKey, byte[]> _rlpCollector = new();

    public IEnumerable<byte[]> TouchedNodesRlp => _rlpCollector.Select(static kvp => kvp.Value);

    public void Dispose() => _baseStore.Dispose();

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        TrieNode node = _baseStore.FindCachedOrUnknown(address, in path, hash);
        CaptureNode(node);
        return node;
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        TryLoadRlp(address, in path, hash, flags)
        ?? throw new MissingTrieNodeException("Missing RLP node", address, path, hash);

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? rlp = _baseStore.TryLoadRlp(address, in path, hash, flags);
        CaptureRlp(hash, rlp);
        return rlp;
    }

    public bool HasRoot(Hash256 stateRoot) => _baseStore.HasRoot(stateRoot);

    public IDisposable BeginScope(BlockHeader? baseBlock) => _baseStore.BeginScope(baseBlock);

    public IScopedTrieStore GetTrieStore(Hash256? address) => new ScopedTrieStore(this, address);

    public INodeStorage.KeyScheme Scheme => _baseStore.Scheme;

    public IBlockCommitter BeginBlockCommit(long blockNumber) => NullCommitter.Instance;

    // WitnessCapturingTrieStore is read-only, so we return a no-op committer that doesn't persist any trie nodes
    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => NullCommitter.Instance;

    private void CaptureNode(TrieNode node)
    {
        if (node.NodeType != NodeType.Unknown)
        {
            _rlpCollector.TryAdd(node.Keccak, node.FullRlp.ToArray());
        }
    }

    private void CaptureRlp(Hash256 hash, byte[]? rlp)
    {
        if (rlp is not null)
        {
            _rlpCollector.TryAdd(hash, rlp);
        }
    }

    public ITrieNodeResolver? GetReadOnlyTraversalResolver(Hash256? address) =>
        _baseStore.GetTrieStore(address) is ITrieNodeResolverSource source
            && source.GetReadOnlyTraversalResolver() is { } readOnlyResolver
                ? new WitnessCapturingReadOnlyTraversalResolver(this, address, readOnlyResolver)
                : null;

    private sealed class WitnessCapturingReadOnlyTraversalResolver(
        WitnessCapturingTrieStore fullTrieStore,
        Hash256? address,
        ITrieNodeResolver inner) : ReadOnlyTraversalResolverBase(fullTrieStore, address)
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            TrieNode node = inner.FindCachedOrUnknown(path, hash);
            fullTrieStore.CaptureNode(node);
            return node;
        }

        protected override ITrieNodeResolver WithAddress(Hash256? address1) =>
            new WitnessCapturingReadOnlyTraversalResolver(fullTrieStore, address1, inner.GetStorageTrieNodeResolver(address1));
    }
}
