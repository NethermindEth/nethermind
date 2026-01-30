// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Trie;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Flat;

/// <summary>
/// Contains some large variable used by <see cref="SnapshotBundle"/> but not committed into <see cref="IFlatDbManager"/>
/// as part of a <see cref="Snapshot"/>. Pooling this is largely for performance reason.
/// </summary>
/// <param name="size"></param>
public record TransientResource(TransientResource.Size size) : IDisposable, IResettable
{
    public record Size(long PrewarmedAddressSize, int NodesCacheSize);

    public BloomFilter PrewarmedAddresses = new(size.PrewarmedAddressSize, 14); // 14 is exactly 8 probes, which the SIMD instruction does.
    public TrieNodeCache.ChildCache Nodes = new(size.NodesCacheSize);

    public Size GetSize() => new(PrewarmedAddresses.Capacity, Nodes.Capacity);

    public int CachedNodes => Nodes.Count;

    public void Reset()
    {
        Nodes.Reset();

        if (PrewarmedAddresses.Count > PrewarmedAddresses.Capacity)
        {
            long newCapacity = (long)BitOperations.RoundUpToPowerOf2((ulong)PrewarmedAddresses.Count);
            double bitsPerKey = PrewarmedAddresses.BitsPerKey;
            // Create new filter before disposing old one to avoid null ref race condition
            BloomFilter newFilter = new BloomFilter(newCapacity, bitsPerKey);
            BloomFilter oldFilter = Interlocked.Exchange(ref PrewarmedAddresses, newFilter);
            oldFilter.Dispose();
        }
        else
        {
            PrewarmedAddresses.Clear();
        }
    }

    public bool ShouldPrewarm(Address address, UInt256? slot)
    {
        ulong hash;
        if (slot is null)
        {
            hash = XxHash64.HashToUInt64(address.Bytes);
        }
        else
        {
            Span<byte> buffer = stackalloc byte[20 + 32];
            address.Bytes.CopyTo(buffer);
            slot.Value.ToBigEndian(buffer[20..]);
            hash = XxHash64.HashToUInt64(buffer);
        }

        if (PrewarmedAddresses.MightContain(hash)) return false;
        PrewarmedAddresses.Add(hash);
        return true;
    }

    public void Dispose() => PrewarmedAddresses.Dispose();

    public bool TryGetStateNode(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (Nodes.TryGet(null, path, hash, out RefCounterTrieNodeRlp? rlp))
        {
            try
            {
                node = new TrieNode(NodeType.Unknown, hash, rlp.ToArray());
                return true;
            }
            finally
            {
                rlp.Dispose();
            }
        }

        node = null;
        return false;
    }

    public TrieNode GetOrAddStateNode(in TreePath path, TrieNode trieNode)
    {
        // Only cache nodes with RLP data
        if (trieNode.FullRlp.IsNull) return trieNode;

        RefCounterTrieNodeRlp rlp = RefCounterTrieNodeRlp.CreateFromRlp(trieNode.FullRlp.Span);
        RefCounterTrieNodeRlp result = Nodes.GetOrAdd(null, path, rlp);

        if (ReferenceEquals(result, rlp))
        {
            // We added our RLP, return the original trieNode
            return trieNode;
        }
        else
        {
            // An existing RLP was found, dispose our new one and return node from existing RLP
            rlp.Dispose();
            try
            {
                return new TrieNode(NodeType.Unknown, trieNode.Keccak!, result.ToArray());
            }
            finally
            {
                result.Dispose();
            }
        }
    }

    public void UpdateStateNode(in TreePath path, TrieNode node)
    {
        // Only cache nodes with RLP data
        if (node.FullRlp.IsNull) return;

        RefCounterTrieNodeRlp rlp = RefCounterTrieNodeRlp.CreateFromRlp(node.FullRlp.Span);
        Nodes.Set(null, path, rlp);
    }

    public bool TryGetStorageNode(Hash256AsKey address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        if (Nodes.TryGet(address, path, hash, out RefCounterTrieNodeRlp? rlp))
        {
            try
            {
                node = new TrieNode(NodeType.Unknown, hash, rlp.ToArray());
                return true;
            }
            finally
            {
                rlp.Dispose();
            }
        }

        node = null;
        return false;
    }

    public TrieNode GetOrAddStorageNode(Hash256AsKey address, in TreePath path, TrieNode trieNode)
    {
        // Only cache nodes with RLP data
        if (trieNode.FullRlp.IsNull) return trieNode;

        RefCounterTrieNodeRlp rlp = RefCounterTrieNodeRlp.CreateFromRlp(trieNode.FullRlp.Span);
        RefCounterTrieNodeRlp result = Nodes.GetOrAdd(address, path, rlp);

        if (ReferenceEquals(result, rlp))
        {
            // We added our RLP, return the original trieNode
            return trieNode;
        }
        else
        {
            // An existing RLP was found, dispose our new one and return node from existing RLP
            rlp.Dispose();
            try
            {
                return new TrieNode(NodeType.Unknown, trieNode.Keccak!, result.ToArray());
            }
            finally
            {
                result.Dispose();
            }
        }
    }

    public void UpdateStorageNode(Hash256AsKey address, in TreePath path, TrieNode node)
    {
        // Only cache nodes with RLP data
        if (node.FullRlp.IsNull) return;

        RefCounterTrieNodeRlp rlp = RefCounterTrieNodeRlp.CreateFromRlp(node.FullRlp.Span);
        Nodes.Set(address, path, rlp);
    }
}
