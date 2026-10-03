// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections;

/// <summary>
/// The chained hash table behind the guest <see cref="OptimizedDictionary{TKey,TValue}"/> and <see cref="OptimizedHashSet{T}"/>.
/// </summary>
/// <remarks>
/// Laid out like the BCL dictionary (1-based bucket heads, an entry array in insertion order with a LIFO free list), so
/// enumeration order matches it, but with every field and link a full word: the guest pays a narrow load or store
/// several times an aligned one. Bucket counts are powers of two indexed by a Fibonacci multiply, so a probe needs
/// neither a modulo nor a bounds check, and growth is out of line so the probe paths keep a small frame.
/// Not thread-safe; the guest is single-threaded. Enumerators carry no version check: removing entries or overwriting
/// values while enumerating is supported, but adding or clearing during enumeration is undefined, where the BCL throws.
/// </remarks>
internal struct OptimizedHashTable<TKey, TValue>
{
    private const ulong Fibonacci = 0x9E3779B97F4A7C15;
    private const nint StartOfFreeList = -3;
    private static readonly nint[] EmptyBuckets = new nint[1];

    private nint[] _buckets;
    private Entry[] _entries;
    private nint _mask;
    private nint _count;
    private nint _freeList;
    private nint _freeCount;
    private readonly IEqualityComparer<TKey>? _comparer;

    public OptimizedHashTable(int capacity, IEqualityComparer<TKey>? comparer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _comparer = typeof(TKey).IsValueType
            ? GenericEqualityComparer.GetOptimized(comparer) is { } c && !ReferenceEquals(c, EqualityComparer<TKey>.Default) ? c : null
            : comparer ?? EqualityComparer<TKey>.Default;
        _buckets = EmptyBuckets;
        _entries = [];
        _freeList = -1;
        if (capacity > 0) Initialize(capacity);
    }

    public struct Entry
    {
        /// <summary>Index of the next entry in the chain, -1 at its end, or <see cref="StartOfFreeList"/> minus the next free index.</summary>
        public nint Next;
        public nint HashCode;
        public TKey Key;
        public TValue Value;
    }

    public readonly int Count => (int)(_count - _freeCount);

    public readonly int Capacity => _entries.Length;

    /// <summary>The slots used so far, free ones included; entries at or past it are untouched.</summary>
    public readonly nint Used => _count;

    public readonly Entry[] Entries => _entries;

    public readonly IEqualityComparer<TKey> Comparer => _comparer ?? EqualityComparer<TKey>.Default;

    public static bool IsLive(in Entry entry) => entry.Next >= -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ref nint Bucket(nint hash) =>
        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buckets), (nint)(((ulong)(uint)hash * Fibonacci) >> 32) & _mask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref Entry FindEntry(scoped in TKey key)
    {
        if (!typeof(TKey).IsValueType) return ref FindEntry(in key, new ComparerOps(_comparer!));
        if (_comparer is null) return ref FindEntry(in key, default(DefaultOps));
        return ref FindEntryWithComparer(in key);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly ref Entry FindEntryWithComparer(scoped in TKey key) => ref FindEntry(in key, new ComparerOps(_comparer!));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ref Entry FindEntry<TOps>(scoped in TKey key, TOps ops) where TOps : struct, IKeyOps
    {
        nint hash = ops.Hash(in key);
        nint i = Bucket(hash) - 1;
        // Only a non-empty bucket needs the entries; reaching them costs a null-check probe of the array.
        if (i >= 0)
        {
            ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);
            do
            {
                ref Entry entry = ref Unsafe.Add(ref entries, i);
                if (entry.HashCode == hash && ops.AreEqual(in entry.Key, in key)) return ref entry;
                i = entry.Next;
            } while (i >= 0);
        }

        return ref Unsafe.NullRef<Entry>();
    }

    /// <summary>Finds the entry for <paramref name="key"/>, adding one with a default value if there is none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Entry FindOrAdd(scoped in TKey key, out bool exists)
    {
        if (!typeof(TKey).IsValueType) return ref FindOrAdd(in key, new ComparerOps(_comparer!), out exists);
        if (_comparer is null) return ref FindOrAdd(in key, default(DefaultOps), out exists);

        // A flag of its own: passing the caller's to the out-of-line call would keep it in memory on the fast path too.
        ref Entry entry = ref FindOrAddWithComparer(in key, out bool found);
        exists = found;
        return ref entry;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref Entry FindOrAddWithComparer(scoped in TKey key, out bool exists) => ref FindOrAdd(in key, new ComparerOps(_comparer!), out exists);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Entry FindOrAdd<TOps>(scoped in TKey key, TOps ops, out bool exists) where TOps : struct, IKeyOps
    {
        nint hash = ops.Hash(in key);
        ref nint bucket = ref Bucket(hash);
        nint i = bucket - 1;
        if (i >= 0)
        {
            ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);
            do
            {
                ref Entry entry = ref Unsafe.Add(ref entries, i);
                if (entry.HashCode == hash && ops.AreEqual(in entry.Key, in key))
                {
                    exists = true;
                    return ref entry;
                }

                i = entry.Next;
            } while (i >= 0);
        }

        exists = false;
        return ref Add(in key, hash, ref bucket);
    }

    private ref Entry Add(scoped in TKey key, nint hash, ref nint bucket)
    {
        nint index;
        if (_freeCount > 0)
        {
            index = _freeList;
            _freeList = StartOfFreeList - Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), index).Next;
            _freeCount--;
        }
        else
        {
            index = _count;
            if (index == _entries.Length)
            {
                Grow();
                bucket = ref Bucket(hash);
            }

            _count = index + 1;
        }

        ref Entry entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), index);
        entry.HashCode = hash;
        entry.Next = bucket - 1;
        entry.Key = key;
        entry.Value = default!;
        bucket = index + 1;
        return ref entry;
    }

    public bool Remove(in TKey key, [MaybeNullWhen(false)] out TValue value) =>
        typeof(TKey).IsValueType && _comparer is null
            ? Remove(in key, default(DefaultOps), out value)
            : Remove(in key, new ComparerOps(_comparer!), out value);

    private bool Remove<TOps>(in TKey key, TOps ops, [MaybeNullWhen(false)] out TValue value) where TOps : struct, IKeyOps
    {
        nint hash = ops.Hash(in key);
        ref nint bucket = ref Bucket(hash);
        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);
        nint last = -1;
        nint i = bucket - 1;
        while (i >= 0)
        {
            ref Entry entry = ref Unsafe.Add(ref entries, i);
            if (entry.HashCode == hash && ops.AreEqual(in entry.Key, in key))
            {
                if (last < 0)
                {
                    bucket = entry.Next + 1;
                }
                else
                {
                    Unsafe.Add(ref entries, last).Next = entry.Next;
                }

                value = entry.Value;
                entry.Next = StartOfFreeList - _freeList;
                if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>()) entry.Key = default!;
                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>()) entry.Value = default!;
                _freeList = i;
                _freeCount++;
                return true;
            }

            last = i;
            i = entry.Next;
        }

        value = default;
        return false;
    }

    public void Clear()
    {
        if (_count == 0) return;
        Array.Clear(_buckets);
        Array.Clear(_entries, 0, (int)_count);
        _count = 0;
        _freeList = -1;
        _freeCount = 0;
    }

    /// <summary>Ensures room for <paramref name="capacity"/> entries without growing; returns the resulting capacity.</summary>
    public int EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (_entries.Length >= capacity) return _entries.Length;
        if (_entries.Length == 0) Initialize(capacity);
        else Resize(SizeFor(capacity));
        return _entries.Length;
    }

    /// <summary>Shrinks the storage to the smallest size holding <paramref name="capacity"/> entries, compacting out free slots.</summary>
    public void TrimExcess(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, Count);
        int size = SizeFor(capacity);
        if (size >= _entries.Length) return;

        Entry[] old = _entries;
        nint used = _count;
        Initialize(capacity);
        nint count = 0;
        ref Entry entries = ref MemoryMarshal.GetArrayDataReference(_entries);
        for (nint i = 0; i < used; i++)
        {
            ref Entry source = ref old[i];
            if (!IsLive(in source)) continue;
            ref Entry target = ref Unsafe.Add(ref entries, count);
            target = source;
            ref nint bucket = ref Bucket(source.HashCode);
            target.Next = bucket - 1;
            bucket = ++count;
        }

        _count = count;
    }

    private static int SizeFor(int capacity) => capacity <= 4 ? 4 : (int)BitOperations.RoundUpToPowerOf2((uint)capacity);

    private void Initialize(int capacity)
    {
        int size = SizeFor(capacity);
        _buckets = new nint[size];
        _entries = new Entry[size];
        _mask = size - 1;
        _count = 0;
        _freeList = -1;
        _freeCount = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        if (_entries.Length == 0) Initialize(0);
        else Resize(_entries.Length * 2);
    }

    private void Resize(int size)
    {
        Entry[] entries = new Entry[size];
        Array.Copy(_entries, entries, (int)_count);
        _buckets = new nint[size];
        _entries = entries;
        _mask = size - 1;
        ref Entry first = ref MemoryMarshal.GetArrayDataReference(entries);
        for (nint i = 0; i < _count; i++)
        {
            ref Entry entry = ref Unsafe.Add(ref first, i);
            if (!IsLive(in entry)) continue;
            ref nint bucket = ref Bucket(entry.HashCode);
            entry.Next = bucket - 1;
            bucket = i + 1;
        }
    }

    /// <remarks>Keys travel by reference: a storage cell is a 40-byte struct, and every copy is a block move in the guest.</remarks>
    private interface IKeyOps
    {
        nint Hash(in TKey key);
        bool AreEqual(in TKey stored, in TKey key);
    }

    /// <summary>A value-type key's own hash and equality, called directly.</summary>
    private readonly struct DefaultOps : IKeyOps
    {
        // Through a mutable reference, or the compiler copies the key to call a member of a struct it cannot see is readonly.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint Hash(in TKey key) => Unsafe.AsRef(in key)!.GetHashCode();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AreEqual(in TKey stored, in TKey key) => EqualityComparer<TKey>.Default.Equals(stored, key);
    }

    private readonly struct ComparerOps(IEqualityComparer<TKey> comparer) : IKeyOps
    {
        public nint Hash(in TKey key) => comparer.GetHashCode(key!);

        public bool AreEqual(in TKey stored, in TKey key) => comparer.Equals(stored, key);
    }
}
