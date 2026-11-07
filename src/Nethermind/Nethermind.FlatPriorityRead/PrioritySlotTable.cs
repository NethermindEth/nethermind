// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.FlatPriorityRead;

/// <summary>An open-addressing table of one contract's slots, written by one thread while others read it.</summary>
/// <remarks>
/// Linear probing over an array of plain structs, which the GC never scans. A slot's state is written last with a
/// volatile write, so a reader that sees a slot in use also sees its key and value. A removed slot keeps its key as a
/// tombstone, so no entry ever moves under a reader; growth rehashes into new arrays and publishes them with one
/// reference swap. A reader racing a write may still read the old or the new value of the key being written, so the
/// writer must only change keys no reader can be looking up; <see cref="MemoryPrioritySlotStore"/> guarantees that.
/// </remarks>
public sealed class PrioritySlotTable
{
    private const byte Empty = 0;
    private const byte Occupied = 1;
    private const byte Removed = 2;
    private const int MinCapacity = 16;
    private const double MaxLoad = 0.8;
    private const double LoadAfterGrowth = 0.6;

    private Storage _storage = new(MinCapacity);
    private int _used;
    private int _count;

    public int Count => _count;

    public bool TryGet(in ValueHash256 key, out UInt256 value)
    {
        Storage storage = Volatile.Read(ref _storage);
        int capacity = storage.States.Length;
        int index = Home(key, capacity);
        // Always ends: growth keeps a published table below MaxLoad, so an empty slot follows every probe chain.
        while (true)
        {
            byte state = Volatile.Read(ref storage.States[index]);
            if (state == Empty) break;

            ref Entry entry = ref storage.Entries[index];
            if (entry.Key == key)
            {
                if (state != Occupied) break;
                value = entry.Value;
                return true;
            }

            if (++index == capacity) index = 0;
        }

        value = default;
        return false;
    }

    public void Set(in ValueHash256 key, in UInt256 value)
    {
        int index = Find(_storage, key);
        if (_storage.States[index] == Empty && _used + 1 > _storage.States.Length * MaxLoad)
        {
            Resize((int)(_count / LoadAfterGrowth) + 1);
            index = Find(_storage, key);
        }

        Storage storage = _storage;
        byte state = storage.States[index];
        storage.Entries[index] = new Entry(key, value);
        if (state == Occupied) return;

        if (state == Empty) _used++;
        _count++;
        Volatile.Write(ref storage.States[index], Occupied);
    }

    public void Remove(in ValueHash256 key)
    {
        int index = Find(_storage, key);
        if (_storage.States[index] != Occupied) return;

        _count--;
        Volatile.Write(ref _storage.States[index], Removed);
    }

    /// <returns>The slot holding <paramref name="key"/>, or the empty slot that ends its probe chain.</returns>
    private static int Find(Storage storage, in ValueHash256 key)
    {
        int capacity = storage.States.Length;
        int index = Home(key, capacity);
        while (storage.States[index] != Empty && storage.Entries[index].Key != key)
        {
            if (++index == capacity) index = 0;
        }

        return index;
    }

    private void Resize(int capacity)
    {
        Storage resized = new(Math.Max(MinCapacity, capacity));
        Storage current = _storage;
        for (int i = 0; i < current.States.Length; i++)
        {
            if (current.States[i] != Occupied) continue;

            int index = Find(resized, current.Entries[i].Key);
            resized.Entries[index] = current.Entries[i];
            resized.States[index] = Occupied;
        }

        _used = _count;
        Volatile.Write(ref _storage, resized);
    }

    // Maps the hash onto [0, capacity) with a multiply-shift, so the capacity need not be a power of two.
    private static int Home(in ValueHash256 key, int capacity) => (int)Math.BigMul((ulong)key.GetHashCode64(), (ulong)capacity, out _);

    private readonly record struct Entry(ValueHash256 Key, UInt256 Value);

    private sealed class Storage(int capacity)
    {
        public readonly Entry[] Entries = new Entry[capacity];
        public readonly byte[] States = new byte[capacity];
    }
}
