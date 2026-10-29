// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>Assigns dense ordinals to a block's declared storage reads and owns its worker coverage.</summary>
/// <remarks>
/// Bounded probes and sorted fallbacks allow cheap hashes without unbounded collision chains.
/// Dispose only after all execution workers have stopped using the plan.
/// </remarks>
public sealed partial class BalReadStoragePlan : IDisposable
{
    private const int MaxProbes = 4;
    private readonly ReadOnlyAccountChangesView _accounts;
    private readonly (int Start, int TableStart, int Mask)[] _readOffsets;
    private readonly int[] _addressIndex;
    private int[] _slotIndex = [];
    private bool _disposed;

    /// <summary>The number of declared storage reads.</summary>
    public int TotalReads { get; }

    /// <summary>Builds an index over a validated, address-sorted BAL with sorted storage reads.</summary>
    public BalReadStoragePlan(ReadOnlyBlockAccessList bal)
    {
        _accounts = bal.AccountChanges;
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = _accounts.AsSpan();
        _readOffsets = accounts.IsEmpty ? [] : new (int, int, int)[accounts.Length];
        uint addressCapacity = BitOperations.RoundUpToPowerOf2((uint)accounts.Length * 2);
        _addressIndex = addressCapacity == 0 || addressCapacity > Array.MaxLength ? [] : new int[addressCapacity];
        int start = 0;
        long indexLength = 0;
        for (int ordinal = 0; ordinal < accounts.Length; ordinal++)
        {
            ReadOnlyAccountChanges account = accounts[ordinal];
            Debug.Assert(ordinal == 0 || accounts[ordinal - 1].Address.CompareTo(account.Address) < 0,
                "The binary-search fallback requires strictly increasing BAL addresses.");
            int size = account.StorageReads.Length < 8 ? 0 : (int)BitOperations.RoundUpToPowerOf2((uint)account.StorageReads.Length * 2);
            if (size < 0 || indexLength + size > Array.MaxLength) size = 0;
            _readOffsets[ordinal] = (start, (int)indexLength, size - 1);
            if (_addressIndex.Length > 0)
            {
                CacheOrdinal(_addressIndex, 0, _addressIndex.Length - 1, GetAddressHash(account.Address), ordinal);
            }
            indexLength += size;
            start = checked(start + account.StorageReads.Length);
        }
        TotalReads = start;
        if (indexLength > 0)
        {
            _slotIndex = ArrayPool<int>.Shared.Rent((int)indexLength);
            try
            {
                _slotIndex.AsSpan(0, (int)indexLength).Clear();
                for (int i = 0; i < accounts.Length; i++)
                {
                    ReadOnlyAccountChanges account = accounts[i];
                    (_, int tableStart, int mask) = _readOffsets[i];
                    if (mask < 0) continue;
                    for (int local = 0; local < account.StorageReads.Length; local++)
                    {
                        CacheOrdinal(_slotIndex, tableStart, mask, GetSlotHash(account.StorageReads[local]), local);
                    }
                }
            }
            catch
            {
                ArrayPool<int>.Shared.Return(_slotIndex);
                _slotIndex = [];
                throw;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CacheOrdinal(int[] index, int start, int mask, int hash, int ordinal)
    {
        for (int probe = 0; probe < MaxProbes; probe++)
        {
            ref int entry = ref index[start + ((hash + probe) & mask)];
            if (entry != 0) continue;
            entry = ordinal + 1;
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetAddressHash(Address address)
    {
        ReadOnlySpan<byte> bytes = address.Bytes;
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..])
            ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        hash ^= hash >> 32;
        hash ^= hash >> 16;
        return (int)(hash ^ (hash >> 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindAccount(Address address)
    {
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = _accounts.AsSpan();
        if (_addressIndex.Length > 0)
        {
            int hash = GetAddressHash(address);
            AddressAsKey addressKey = address;
            for (int probe = 0; probe < MaxProbes; probe++)
            {
                int candidate = _addressIndex[(hash + probe) & (_addressIndex.Length - 1)] - 1;
                if (candidate < 0) return -1;
                AddressAsKey candidateAddress = accounts[candidate].Address;
                // Vector128<byte> equality becomes byte loops when intrinsics are disabled.
                if (Vector128.IsHardwareAccelerated
                    ? candidateAddress.Equals(in addressKey)
                    : AddressesEqualScalar(candidateAddress.Value, address)) return candidate;
            }
        }
        return FindAccountSlow(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AddressesEqualScalar(Address left, Address right)
    {
        ReadOnlySpan<byte> a = left.Bytes;
        ReadOnlySpan<byte> b = right.Bytes;
        return BinaryPrimitives.ReadUInt64LittleEndian(a) == BinaryPrimitives.ReadUInt64LittleEndian(b)
            && BinaryPrimitives.ReadUInt64LittleEndian(a[8..]) == BinaryPrimitives.ReadUInt64LittleEndian(b[8..])
            && BinaryPrimitives.ReadUInt32LittleEndian(a[16..]) == BinaryPrimitives.ReadUInt32LittleEndian(b[16..]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int FindAccountSlow(Address address)
    {
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = _accounts.AsSpan();
        int left = 0;
        int right = accounts.Length - 1;
        while (left <= right)
        {
            int middle = left + ((right - left) >> 1);
            int comparison = accounts[middle].Address.CompareTo(address);
            if (comparison == 0) return middle;
            if (comparison < 0) left = middle + 1;
            else right = middle - 1;
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSlotHash(in UInt256 slot)
    {
        ulong hash = slot.u0 ^ slot.u1 ^ slot.u2 ^ slot.u3;
        return (int)(hash ^ (hash >> 32));
    }

    /// <summary>Finds a declared read's ordinal; write slots are not in this index.</summary>
    /// <remarks>Each account or slot lookup checks at most four indexed candidates before falling back to binary search.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOrdinal(in StorageCell cell, out int ordinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int accountOrdinal = FindAccount(cell.Address);
        if (accountOrdinal >= 0)
        {
            (int Start, int TableStart, int Mask) entry = _readOffsets[accountOrdinal];
            ReadOnlySpan<UInt256> reads = _accounts.AsSpan()[accountOrdinal].StorageReads;
            UInt256 slot = cell.Index;
            if (entry.Mask >= 0)
            {
                int hash = GetSlotHash(slot);
                for (int probe = 0; probe < MaxProbes; probe++)
                {
                    int candidate = _slotIndex[entry.TableStart + ((hash + probe) & entry.Mask)] - 1;
                    if (candidate < 0)
                    {
                        // Omitted keys passed four occupied buckets, and construction never removes entries.
                        ordinal = -1;
                        return false;
                    }
                    if (reads[candidate] == slot)
                    {
                        ordinal = entry.Start + candidate;
                        return true;
                    }
                }
            }
            int local = reads.BinarySearch(slot);
            if (local >= 0)
            {
                ordinal = entry.Start + local;
                return true;
            }
        }
        ordinal = -1;
        return false;
    }

    /// <summary>Creates coverage owned by this plan for one execution worker.</summary>
    public BalReadCoverage CreateCoverage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BalReadCoverage coverage = new(this);
        _workers.Enqueue(coverage);
        return coverage;
    }

    /// <summary>Finds the first declared read no worker executed.</summary>
    /// <remarks>Call only after all workers have completed; merges coverage into the first worker.</remarks>
    public bool TryFindUncovered(out Address? address)
    {
        int missing = Combine()?.FirstUncovered() ?? (TotalReads == 0 ? -1 : 0);
        int end = 0;
        foreach (ReadOnlyAccountChanges account in _accounts)
        {
            end += account.StorageReads.Length;
            if (missing >= 0 && missing < end)
            {
                address = account.Address;
                return true;
            }
        }
        address = null;
        return false;
    }

    /// <summary>Records the declared reads some worker executed as storage reads of <paramref name="recorder"/>.</summary>
    /// <remarks>Call only after all workers have completed; merges coverage into the first worker.</remarks>
    public void RecordCoveredReads(BlockAccessIndexRecorder recorder)
    {
        BalReadCoverage? combined = Combine();
        if (combined is null) return;
        int ordinal = 0;
        foreach (ReadOnlyAccountChanges account in _accounts)
        {
            foreach (UInt256 slot in account.StorageReads)
            {
                if (combined.IsCovered(ordinal++)) recorder.AddStorageRead(new StorageCell(account.Address, slot));
            }
        }
    }

    private BalReadCoverage? Combine()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BalReadCoverage? combined = null;
        foreach (BalReadCoverage worker in _workers)
        {
            if (combined is null) combined = worker;
            else combined.Absorb(worker);
        }
        return combined;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        while (_workers.TryDequeue(out BalReadCoverage? worker)) worker.Release();
        if (_slotIndex.Length != 0) ArrayPool<int>.Shared.Return(_slotIndex);
        _slotIndex = [];
    }
}
