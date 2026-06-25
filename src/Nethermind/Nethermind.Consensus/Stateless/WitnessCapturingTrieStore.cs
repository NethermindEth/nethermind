// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <remarks>
/// Delegates all logic to base store except for writing trie nodes (readonly!)
/// Adds logic for capturing trie nodes accessed during execution and state root recomputation.
/// </remarks>
public class WitnessCapturingTrieStore(IReadOnlyTrieStore baseStore) : ITrieStore
{
    // Plain Dictionary, not ConcurrentDictionary: a rented entry is exclusive to a single synchronous
    // caller, so the collector only ever sees one writer per rent.
    private readonly Dictionary<Hash256AsKey, byte[]> _rlpCollector = [];

    public IEnumerable<byte[]> TouchedNodesRlp => _rlpCollector.Values;

    /// <summary>Clears the captured-node set so the wrapper can be reused across pooled rents.</summary>
    public void Reset() => _rlpCollector.Clear();

    public void Dispose() => baseStore.Dispose();

    public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash)
    {
        TrieNode node = baseStore.FindCachedOrUnknown(address, in path, hash);
        if (node.NodeType != NodeType.Unknown)
        {
            // Materialise the RLP only on first capture: TryAdd would allocate node.FullRlp.ToArray()
            // on every cache hit (hot in SLOAD loops touching the same branch) just to discard it.
            ref byte[]? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_rlpCollector, node.Keccak, out bool exists);
            if (!exists) slot = node.FullRlp.ToArray();
        }
        return node;
    }

    public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
        TryLoadRlp(address, in path, hash, flags)
        ?? throw new MissingTrieNodeException("Missing RLP node", address, path, hash);

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

    public IBlockCommitter BeginBlockCommit(ulong blockNumber) => NullCommitter.Instance;

    // WitnessCapturingTrieStore is read-only, so we return a no-op committer that doesn't persist any trie nodes
    public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => NullCommitter.Instance;
}
