// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;

namespace Nethermind.Pbt;

/// <summary>
/// One stem's sub-index → 32-byte value writes, exposed in ascending sub-index order. The common
/// case is a single write (a storage-zone slot); only full-code header stems grow large.
/// </summary>
/// <remarks>
/// A zero value is a leaf clear and is retained (never dropped), so <see cref="TrieUpdater"/> sees it.
/// Instances are pooled and size-tiered (see <see cref="PbtStemChanges"/>); <see cref="Set"/> may
/// promote to a larger variant and return it, so callers must use the returned reference. The writes
/// are read back by ordinal via <see cref="SubIndexAt"/> / <see cref="Get"/> rather than materialized
/// into a buffer.
/// </remarks>
public interface IPbtStemChanges
{
    /// <summary>The number of leaves written.</summary>
    int Count { get; }

    /// <summary>Adds or overwrites the write for <paramref name="subIndex"/>.</summary>
    /// <returns>The map to keep using — this instance, or a larger variant it promoted to.</returns>
    IPbtStemChanges Set(byte subIndex, in ValueHash256 value);

    /// <summary>The sub-index of the write at ordinal <paramref name="index"/> (writes are ordered ascending by sub-index).</summary>
    byte SubIndexAt(int index);

    /// <summary>The value of the write at ordinal <paramref name="index"/>, by reference to avoid copying the 32-byte struct.</summary>
    ref readonly ValueHash256 Get(int index);
}

/// <summary>
/// Rents and returns the pooled <see cref="IPbtStemChanges"/> variants. Each variant is pooled in its
/// own <see cref="StaticPool{T}"/>, giving three size-tiered pools: single, up-to-eight, and sorted.
/// </summary>
public static class PbtStemChanges
{
    /// <summary>Rents an empty map. The first <see cref="IPbtStemChanges.Set"/> grows it as needed.</summary>
    public static IPbtStemChanges Rent() => StaticPool<SingleStemChanges>.Rent();

    /// <summary>Returns <paramref name="changes"/> to the pool matching its concrete variant.</summary>
    public static void Return(IPbtStemChanges changes)
    {
        switch (changes)
        {
            case SingleStemChanges single: StaticPool<SingleStemChanges>.Return(single); break;
            case Length8StemChanges length8: StaticPool<Length8StemChanges>.Return(length8); break;
            case SortedStemChanges sorted: StaticPool<SortedStemChanges>.Return(sorted); break;
        }
    }
}

/// <summary>The single-write variant: no backing allocation. Promotes to <see cref="Length8StemChanges"/> on a second key.</summary>
internal sealed class SingleStemChanges : IPbtStemChanges, IResettable
{
    private byte _subIndex;
    private ValueHash256 _value;
    private bool _hasValue;

    public int Count => _hasValue ? 1 : 0;

    public IPbtStemChanges Set(byte subIndex, in ValueHash256 value)
    {
        if (!_hasValue || _subIndex == subIndex)
        {
            _subIndex = subIndex;
            _value = value;
            _hasValue = true;
            return this;
        }

        Length8StemChanges promoted = StaticPool<Length8StemChanges>.Rent();
        promoted.Set(_subIndex, _value);
        promoted.Set(subIndex, value);
        StaticPool<SingleStemChanges>.Return(this);
        return promoted;
    }

    public byte SubIndexAt(int index) => _subIndex;

    public ref readonly ValueHash256 Get(int index) => ref _value;

    public void Reset()
    {
        _hasValue = false;
        _value = default;
        _subIndex = 0;
    }
}

/// <summary>The up-to-eight-write variant: a fixed array kept ascending by insertion sort. Promotes to <see cref="SortedStemChanges"/> when full.</summary>
internal sealed class Length8StemChanges : IPbtStemChanges, IResettable
{
    private const int Capacity = 8;
    private readonly byte[] _subIndices = new byte[Capacity];
    private readonly ValueHash256[] _values = new ValueHash256[Capacity];
    private int _count;

    public int Count => _count;

    public IPbtStemChanges Set(byte subIndex, in ValueHash256 value)
    {
        // The insertion index is the number of live entries strictly less than subIndex; since the
        // eight sub-indices are sorted ascending, that is the population count of the "< subIndex"
        // lanes masked to the live ones.
        uint less = Vector64.LessThan(Vector64.LoadUnsafe(ref _subIndices[0]), Vector64.Create(subIndex)).ExtractMostSignificantBits();
        int i = BitOperations.PopCount(less & (uint)((1 << _count) - 1));

        if (i < _count && _subIndices[i] == subIndex)
        {
            _values[i] = value;
            return this;
        }

        if (_count < Capacity)
        {
            _subIndices.AsSpan(i, _count - i).CopyTo(_subIndices.AsSpan(i + 1));
            _values.AsSpan(i, _count - i).CopyTo(_values.AsSpan(i + 1));
            _subIndices[i] = subIndex;
            _values[i] = value;
            _count++;
            return this;
        }

        SortedStemChanges promoted = StaticPool<SortedStemChanges>.Rent();
        promoted.PromoteFrom(_subIndices, _values, subIndex, value);
        StaticPool<Length8StemChanges>.Return(this);
        return promoted;
    }

    public byte SubIndexAt(int index) => _subIndices[index];

    public ref readonly ValueHash256 Get(int index) => ref _values[index];

    public void Reset() => _count = 0;
}

/// <summary>The large variant: sub-indices kept ascending in a byte array that indexes into a directly-addressed values array, insertion-sorted on write.</summary>
/// <remarks>
/// <c>_order[0.._count]</c> holds the present sub-indices in ascending order; each is used verbatim as the
/// index into <c>_values</c> (which is addressed by sub-index). A repeated <see cref="Set"/> overwrites the
/// value in place; a new sub-index is insertion-sorted into <c>_order</c>. Sorting happens on write, so
/// <see cref="Get"/> reads directly and <see cref="Reset"/> need only clear the count (stale <c>_values</c>
/// entries are never read, as every new sub-index rewrites its slot).
/// </remarks>
internal sealed class SortedStemChanges : IPbtStemChanges, IResettable
{
    private const int Capacity = 256;
    private readonly byte[] _order = new byte[Capacity];
    private readonly ValueHash256[] _values = new ValueHash256[Capacity];
    private int _count;

    public int Count => _count;

    public IPbtStemChanges Set(byte subIndex, in ValueHash256 value)
    {
        int lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            byte m = _order[mid];
            if (m < subIndex) lo = mid + 1;
            else if (m > subIndex) hi = mid - 1;
            else
            {
                _values[subIndex] = value;
                return this;
            }
        }

        _order.AsSpan(lo, _count - lo).CopyTo(_order.AsSpan(lo + 1));
        _order[lo] = subIndex;
        _values[subIndex] = value;
        _count++;
        return this;
    }

    /// <summary>
    /// Seeds this (freshly rented) map from <see cref="Length8StemChanges"/>'s already-sorted run plus one
    /// more write, avoiding the per-entry insertion sort a sequence of <see cref="Set"/> calls would incur.
    /// </summary>
    /// <remarks><paramref name="subIndices"/> is ascending and must not contain <paramref name="subIndex"/>.</remarks>
    internal void PromoteFrom(ReadOnlySpan<byte> subIndices, ReadOnlySpan<ValueHash256> values, byte subIndex, in ValueHash256 value)
    {
        int insert = 0;
        while (insert < subIndices.Length && subIndices[insert] < subIndex) insert++;

        subIndices[..insert].CopyTo(_order);
        _order[insert] = subIndex;
        subIndices[insert..].CopyTo(_order.AsSpan(insert + 1));

        for (int k = 0; k < subIndices.Length; k++) _values[subIndices[k]] = values[k];
        _values[subIndex] = value;
        _count = subIndices.Length + 1;
    }

    public byte SubIndexAt(int index) => _order[index];

    public ref readonly ValueHash256 Get(int index) => ref _values[_order[index]];

    public void Reset() => _count = 0;
}
