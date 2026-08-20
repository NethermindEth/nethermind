// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
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

    // Invariant: the pool return runs exactly once, at refcount zero, so an in-flight trie-warmer
    // lookup can never overlap Reset/re-rent of this resource.
    private long _leases = RefCountingLease.Single;
    private IResourcePool? _returnPool;
    private ResourcePool.Usage _returnUsage;

    public BloomFilter PrewarmedAddresses = new(size.PrewarmedAddressSize, 14); // 14 is exactly 8 probes, which the SIMD instruction does.
    public TrieNodeCache.ChildCache Nodes = new(size.NodesCacheSize);

    /// <summary>
    /// The trie warmer's negative cache: paths whose persistence lookup missed, keyed like <see cref="Nodes"/> but
    /// holding only unresolved placeholders. It is read and written exclusively by the warmer and is never consulted
    /// by a live read nor promoted into the shared <see cref="TrieNodeCache"/>, so a stale miss can never reach the
    /// state-root computation.
    /// </summary>
    public TrieNodeCache.ChildCache MissNodes = new(size.NodesCacheSize);

    internal void OnRented(IResourcePool pool, ResourcePool.Usage usage)
    {
        _returnPool = pool;
        _returnUsage = usage;
        Volatile.Write(ref _leases, RefCountingLease.Single);
    }

    internal bool TryAcquireLease() => RefCountingLease.TryAcquire(ref _leases);

    /// <summary>
    /// Releases one lease; the final release returns the resource to the pool it was checked out from.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Dispose"/> (the <c>RefCountingDisposable</c> convention): the pool's
    /// <c>IDisposable</c> contract reserves <see cref="Dispose"/> for destroying an over-capacity resource
    /// (freeing the <see cref="BloomFilter"/>), so it cannot double as the return-to-pool path.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The final release found no return pool registered,
    /// i.e. the resource was not checked out through <see cref="ResourcePool.GetCachedResource"/>.</exception>
    internal void ReleaseLease()
    {
        if (RefCountingLease.ReleaseOnce(ref _leases))
        {
            if (_returnPool is null)
            {
                throw new InvalidOperationException($"{nameof(TransientResource)} final lease released without a registered return pool");
            }

            _returnPool.ReturnCachedResource(_returnUsage, this);
        }
    }

    public Size GetSize() => new(PrewarmedAddresses.Capacity, Nodes.Capacity);

    public int CachedNodes => Nodes.Count;

    public void Reset()
    {
        Nodes.Reset();
        MissNodes.Reset();

        if (PrewarmedAddresses.Count > PrewarmedAddresses.Capacity)
        {
            long newCapacity = (long)BitOperations.RoundUpToPowerOf2((ulong)PrewarmedAddresses.Count);
            double bitsPerKey = PrewarmedAddresses.BitsPerKey;
            // Create new filter before disposing old one to avoid null ref race condition
            BloomFilter newFilter = new(newCapacity, bitsPerKey);
            BloomFilter oldFilter = Interlocked.Exchange(ref PrewarmedAddresses, newFilter);
            oldFilter.Dispose();
        }
        else
        {
            PrewarmedAddresses.Clear();
        }

    }

    public bool ShouldPrewarm(Address address, UInt256? slot) => ShouldPrewarm(address.Bytes, slot);

    public bool ShouldPrewarm(in ValueAddress address, UInt256? slot) => ShouldPrewarm(address.AsSpan, slot);

    private bool ShouldPrewarm(ReadOnlySpan<byte> addressBytes, UInt256? slot)
    {
        long hash = SpanExtensions.FastHash64For20Bytes(ref MemoryMarshal.GetReference(addressBytes));
        if (slot is not null)
        {
            UInt256 slotValue = slot.Value;
            hash ^= SpanExtensions.FastHash64For32Bytes(ref Unsafe.As<UInt256, byte>(ref slotValue));
        }

        ulong bloomKey = (ulong)hash;
        if (PrewarmedAddresses.MightContain(bloomKey)) return false;
        PrewarmedAddresses.Add(bloomKey);
        return true;
    }

    public void Dispose() => PrewarmedAddresses.Dispose();

    public bool TryGetStateNode(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) => Nodes.TryGet(null, path, hash, out node);

    public TrieNode GetOrAddStateNode(in TreePath path, TrieNode trieNode) => Nodes.GetOrAdd(null, path, trieNode);

    public void UpdateStateNode(in TreePath path, TrieNode node) => Nodes.Set(null, path, node);

    public bool TryGetMissStateNode(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) => MissNodes.TryGet(null, path, hash, out node);

    public TrieNode GetOrAddMissStateNode(in TreePath path, TrieNode trieNode) => MissNodes.GetOrAdd(null, path, trieNode);

    public bool TryGetStorageNode(Hash256AsKey address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) => Nodes.TryGet(address, path, hash, out node);

    public TrieNode GetOrAddStorageNode(Hash256AsKey address, in TreePath path, TrieNode trieNode) => Nodes.GetOrAdd(address, path, trieNode);

    public void UpdateStorageNode(Hash256AsKey address, in TreePath path, TrieNode node) => Nodes.Set(address, path, node);

    public bool TryGetMissStorageNode(Hash256AsKey address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) => MissNodes.TryGet(address, path, hash, out node);

    public TrieNode GetOrAddMissStorageNode(Hash256AsKey address, in TreePath path, TrieNode trieNode) => MissNodes.GetOrAdd(address, path, trieNode);
}
