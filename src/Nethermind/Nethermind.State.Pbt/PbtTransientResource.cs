// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>Per-block scratch state that is never committed into a snapshot: prewarm deduplication and the node groups staged for the shared trie cache.</summary>
public sealed class PbtTransientResource(long prewarmCapacity = 1024, int nodeGroupCapacity = 1024) : IDisposable, IResettable
{
    /// <summary>Capacities the pool remembers so a replacement resource starts where the last one grew to.</summary>
    public sealed record Size(long PrewarmCapacity, int NodeGroupCapacity);

    private BloomFilter _prewarmedAddresses = new(prewarmCapacity, 14);
    private long _leases = RefCountingLease.Single;
    private IPbtResourcePool? _returnPool;
    private PbtResourcePool.Usage _returnUsage;

    /// <summary>Groups folded or warmed during this block, folded into the shared cache after the block commits.</summary>
    public PbtTrieNodeCache.ChildCache NodeGroups { get; } = new(nodeGroupCapacity);

    internal Size GetSize() => new(_prewarmedAddresses.Capacity, NodeGroups.Capacity);

    internal void OnRented(IPbtResourcePool pool, PbtResourcePool.Usage usage)
    {
        _returnPool = pool;
        _returnUsage = usage;
        Volatile.Write(ref _leases, RefCountingLease.Single);
    }

    internal bool TryAcquireLease() => RefCountingLease.TryAcquire(ref _leases);

    /// <summary>Spins until only the owner lease remains, so in-flight warmer reads and writes have drained.</summary>
    internal void WaitForExclusiveLease()
    {
        SpinWait spinWait = default;
        while (Volatile.Read(ref _leases) != RefCountingLease.Single) spinWait.SpinOnce();
    }

    internal void ReleaseLease()
    {
        if (RefCountingLease.ReleaseOnce(ref _leases))
        {
            if (_returnPool is null)
                throw new InvalidOperationException($"{nameof(PbtTransientResource)} final lease released without a registered return pool");
            _returnPool.ReturnCachedResource(_returnUsage, this);
        }
    }

    /// <summary>Returns whether an account or storage slot should be prewarmed, recording the hint.</summary>
    /// <remarks>Bloom false positives and concurrent duplicate admission are allowed. The caller must hold a lease.</remarks>
    public bool ShouldPrewarm(Address address, UInt256? slot = null) => ShouldPrewarm(address.Bytes, slot);

    /// <inheritdoc cref="ShouldPrewarm(Address, UInt256?)"/>
    public bool ShouldPrewarm(in ValueAddress address, UInt256? slot = null) => ShouldPrewarm(address.AsSpan, slot);

    private bool ShouldPrewarm(ReadOnlySpan<byte> addressBytes, UInt256? slot)
    {
        ulong key = PrewarmKey(addressBytes, slot);
        if (_prewarmedAddresses.MightContain(key)) return false;
        _prewarmedAddresses.Add(key);
        return true;
    }

    internal static ulong PrewarmKey(ReadOnlySpan<byte> addressBytes, UInt256? slot)
    {
        ref byte address = ref MemoryMarshal.GetReference(addressBytes);
        if (slot is null) return (ulong)SpanExtensions.FastHash64For20Bytes(ref address);

        UInt256 slotValue = slot.Value;
        return (ulong)SpanExtensions.FastHash64ForAddressAndSlot(ref address, ref Unsafe.As<UInt256, byte>(ref slotValue));
    }

    /// <summary>Clears deduplication state and staged groups, growing either structure if its capacity was exceeded.</summary>
    /// <remarks>Only the exclusive owner may reset the resource after all query leases have drained.</remarks>
    public void Reset()
    {
        NodeGroups.Reset();
        if (_prewarmedAddresses.Count > _prewarmedAddresses.Capacity)
        {
            long capacity = (long)BitOperations.RoundUpToPowerOf2((ulong)_prewarmedAddresses.Count);
            BloomFilter replacement = new(capacity, _prewarmedAddresses.BitsPerKey);
            _prewarmedAddresses.Dispose();
            _prewarmedAddresses = replacement;
        }
        else
        {
            _prewarmedAddresses.Clear();
        }
    }

    /// <summary>Frees the filter and releases staged groups when the exclusively owned resource is discarded, rather than returned to its pool.</summary>
    public void Dispose()
    {
        NodeGroups.Dispose();
        _prewarmedAddresses.Dispose();
    }
}
