// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.State.Flat;

/// <summary>
/// Stores a snapshot's changed storage slots in address-owned dictionaries.
/// </summary>
/// <remarks>
/// A slot write exclusively owns one address: transaction commits write from the processing thread, and the
/// storage-root workers each write a distinct address, so the inner dictionaries never take a lock per slot the
/// way one shared concurrent map did. The outer map is concurrent only for the first slot of an address.
/// The per-address dictionaries are not thread-safe: each address must have at most one writer, and reads must not
/// overlap writes to that address. Enumeration and <see cref="Count"/> require all addresses to be quiescent, which
/// holds once the snapshot is sealed.
/// </remarks>
public sealed class AddressSlotDictionary : IReadOnlyCollection<KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?>>
{
    private const int PooledSlotCapacity = 1_024;

    private readonly ConcurrentDictionary<AddressAsKey, AddressSlots> _byAddress = new();

    public int Count
    {
        get
        {
            int count = 0;
            foreach (KeyValuePair<AddressAsKey, AddressSlots> address in _byAddress) count += address.Value.Slots.Count;
            return count;
        }
    }

    public UInt256? this[HashedKey<(Address, UInt256)> key]
    {
        set
        {
            (Address address, UInt256 index) = key.Key;
            Set(address, in index, value);
        }
    }

    public void Set(Address address, in UInt256 index, UInt256? value) => GetOrAddAddress(address).Slots[index] = value;

    public bool TryGetValue(HashedKey<(Address, UInt256)> key, out UInt256? value)
    {
        (Address address, UInt256 index) = key.Key;
        return TryGetValue(address, in index, out value);
    }

    public bool TryGetValue(Address address, in UInt256 index, out UInt256? value)
    {
        if (_byAddress.TryGetValue(address, out AddressSlots? slots))
        {
            return slots.Slots.TryGetValue(index, out value);
        }

        value = null;
        return false;
    }

    /// <summary>Drops every changed slot of <paramref name="address"/>.</summary>
    public bool RemoveAddress(Address address)
    {
        if (!_byAddress.TryRemove(address, out AddressSlots? slots)) return false;

        AddressSlotsPool.Return(slots);
        return true;
    }

    /// <summary>Empties the map without taking the outer stripe locks; requires complete quiescence.</summary>
    internal void NoLockClear()
    {
        foreach (KeyValuePair<AddressAsKey, AddressSlots> address in _byAddress) AddressSlotsPool.Return(address.Value);
        _byAddress.NoLockClear();
    }

    public IEnumerator<KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?>> GetEnumerator()
    {
        foreach (KeyValuePair<AddressAsKey, AddressSlots> address in _byAddress)
        {
            foreach (KeyValuePair<UInt256, UInt256?> slot in address.Value.Slots)
            {
                yield return new KeyValuePair<HashedKey<(Address, UInt256)>, UInt256?>(new((address.Key.Value, slot.Key)), slot.Value);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private AddressSlots GetOrAddAddress(Address address)
    {
        if (_byAddress.TryGetValue(address, out AddressSlots? slots)) return slots;

        AddressSlots candidate = AddressSlotsPool.Rent();
        slots = _byAddress.GetOrAdd(address, candidate);
        if (!ReferenceEquals(slots, candidate)) AddressSlotsPool.Return(candidate);
        return slots;
    }

    private sealed class AddressSlots
    {
        internal Dictionary<UInt256, UInt256?> Slots { get; } = [];

        internal void ResetForPooling() => Slots.ClearAndTrim(PooledSlotCapacity, PooledSlotCapacity);
    }

    private static class AddressSlotsPool
    {
        private const int MaxPooled = 4_096;
        private static readonly ConcurrentQueue<AddressSlots> Pool = [];
        private static int _count;

        public static AddressSlots Rent()
        {
            if (Volatile.Read(ref _count) > 0 && Pool.TryDequeue(out AddressSlots? slots))
            {
                Interlocked.Decrement(ref _count);
                return slots;
            }

            return new AddressSlots();
        }

        public static void Return(AddressSlots slots)
        {
            slots.ResetForPooling();
            if (Interlocked.Increment(ref _count) <= MaxPooled)
            {
                Pool.Enqueue(slots);
            }
            else
            {
                Interlocked.Decrement(ref _count);
            }
        }
    }
}
