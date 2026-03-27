// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Trie;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Flat;

/// <summary>
/// Contains some large variables used by <see cref="SnapshotBundle"/> but not committed into <see cref="IFlatDbManager"/>
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

    public PreallocatedCappedArrayPool BufferPool = new();
    public RefCountingNodeLeasePool LeasePool = new();

    public void Reset()
    {
        LeasePool.Reset();
        BufferPool.Reset();
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

    public void Dispose()
    {
        Reset();
        PrewarmedAddresses.Dispose();
    }

    public RefCountingTrieNode? TryGetStateNode(in TreePath path, in ValueHash256 hash) =>
        Nodes.TryGet(null, path, hash);

    public void UpdateStateRlp(in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp) =>
        Nodes.SetAndLease(null, path, hash, rlp).Dispose();

    public RefCountingTrieNode? TryGetStorageNode(Hash256AsKey address, in TreePath path, in ValueHash256 hash) =>
        Nodes.TryGet(address, path, hash);

    public void UpdateStorageRlp(Hash256AsKey address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp) =>
        Nodes.SetAndLease(address, path, hash, rlp).Dispose();

    public RefCountingTrieNode SetAndLeaseStateNode(in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp) =>
        Nodes.SetAndLease(null, path, hash, rlp);

    public RefCountingTrieNode SetAndLeaseStorageNode(Hash256AsKey address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp) =>
        Nodes.SetAndLease(address, path, hash, rlp);
}
