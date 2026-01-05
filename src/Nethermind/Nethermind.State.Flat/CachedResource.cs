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

public record CachedResource(CachedResource.Size size): IDisposable, IResettable
{
    public record Size(long PrewarmedAddressSize, int NodesCacheSize)
    {
    }

    public BloomFilter PrewarmedAddresses = new BloomFilter(size.PrewarmedAddressSize, 14); // 14 is exactly 8 probe, which the SIMD instruction do.
    public TrieNodeCache.ChildCache Nodes = new TrieNodeCache.ChildCache(size.NodesCacheSize);

    public Size GetSize()
    {
        return new Size(PrewarmedAddresses.Capacity, Nodes.Capacity);
    }

    public int CachedNodes => Nodes.Count;

    public void Reset()
    {
        Nodes.Reset();

        if (PrewarmedAddresses.Count > PrewarmedAddresses.Capacity)
        {
            long newCapacity = (long)BitOperations.RoundUpToPowerOf2((ulong)PrewarmedAddresses.Count);
            PrewarmedAddresses.Dispose();
            PrewarmedAddresses = new BloomFilter(newCapacity, PrewarmedAddresses.BitsPerKey);
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

    public void Dispose()
    {
        PrewarmedAddresses.Dispose();
    }

    public bool TryGetStateNode(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        return Nodes.TryGet(null, path, hash, out node);
    }

    public TrieNode GetOrAddStateNode(in TreePath path, TrieNode trieNode)
    {
        return Nodes.GetOrAdd(null, path, trieNode);
    }

    public void UpdateStateNode(in TreePath path, TrieNode node)
    {
        Nodes.Set(null, path, node);
    }

    public bool TryGetStorageNode(Hash256AsKey address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        return Nodes.TryGet(address, path, hash, out node);
    }

    public TrieNode GetOrAddStorageNode(Hash256AsKey address, in TreePath path, TrieNode trieNode)
    {
        return Nodes.GetOrAdd(address, path, trieNode);
    }

    public void UpdateStorageNode(Hash256AsKey address, in TreePath path, TrieNode node)
    {
        Nodes.Set(address, path, node);
    }
}
