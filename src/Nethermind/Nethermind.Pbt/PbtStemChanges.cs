// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    /// <summary>Adds or overwrites the writes for a run of consecutive sub-indices starting at <paramref name="startSubIndex"/>.</summary>
    /// <param name="startSubIndex">The sub-index the run starts at.</param>
    /// <param name="values">The run's writes laid out back to back, each <see cref="ValueHash256.MemorySize"/> bytes; the run must fit the stem from <paramref name="startSubIndex"/>.</param>
    /// <returns>The map to keep using — this instance, or a larger variant it promoted to.</returns>
    /// <remarks>
    /// Equivalent to <see cref="Set"/> per leaf, but a run is knowable in a way a leaf is not: its
    /// sub-indices are consecutive and ascending. So a variant promotes once to the tier that holds the
    /// whole result rather than climbing there a leaf at a time, and the large variant — where a
    /// deployment's up-to-a-full-stem of code chunks lands — places the run in a pair of block copies
    /// rather than a search and a shift per leaf.
    /// </remarks>
    IPbtStemChanges SetRange(byte startSubIndex, ReadOnlySpan<byte> values);

    /// <summary>The sub-index of the write at ordinal <paramref name="index"/> (writes are ordered ascending by sub-index).</summary>
    byte SubIndexAt(int index);

    /// <summary>The value of the write at ordinal <paramref name="index"/>, by reference to avoid copying the 32-byte struct.</summary>
    ref readonly ValueHash256 Get(int index);
}

/// <summary>
/// Rents and returns the pooled <see cref="IPbtStemChanges"/> variants. Each variant is pooled in its
/// own <see cref="StaticPool{T}"/>, giving five size-tiered pools: single, up-to-four, up-to-eight,
/// up-to-sixteen, and sorted.
/// </summary>
public static class PbtStemChanges
{
    /// <summary>Rents an empty map. The first <see cref="IPbtStemChanges.Set"/> grows it as needed.</summary>
    public static IPbtStemChanges Rent() => StaticPool<SingleStemChanges>.Rent();

    /// <summary>Rents an empty map of the smallest variant that holds <paramref name="count"/> writes without promoting.</summary>
    /// <remarks>
    /// Only <see cref="IPbtStemChanges.SetRange"/> knows a write count up front. Getting this wrong costs
    /// a promotion, not correctness — every variant still grows itself on overflow.
    /// </remarks>
    internal static IPbtStemChanges RentFor(int count) =>
        count <= 1 ? StaticPool<SingleStemChanges>.Rent() : RentSeedable(count);

    /// <summary>Rents the smallest variant that holds <paramref name="count"/> writes, seeded with an already-ascending run of writes.</summary>
    /// <param name="count">The writes the map is to end up with, at least two — the seed's own writes plus whatever the caller is promoting to make room for.</param>
    internal static IPbtStemChanges RentSeeded(int count, ReadOnlySpan<byte> subIndices, ReadOnlySpan<ValueHash256> values)
    {
        SeedableStemChanges rented = RentSeedable(count);
        rented.Seed(subIndices, values);
        return rented;
    }

    /// <summary>Rents the smallest variant that holds <paramref name="count"/> (at least two) writes.</summary>
    private static SeedableStemChanges RentSeedable(int count) => count switch
    {
        <= Search4.Lanes => StaticPool<Length4StemChanges>.Rent(),
        <= Search8.Lanes => StaticPool<Length8StemChanges>.Rent(),
        <= Search16.Lanes => StaticPool<Length16StemChanges>.Rent(),
        _ => StaticPool<SortedStemChanges>.Rent(),
    };

    /// <summary>Whether a run of <paramref name="count"/> writes from <paramref name="startSubIndex"/> overwrites <paramref name="subIndex"/>.</summary>
    internal static bool RunCovers(byte startSubIndex, int count, byte subIndex) => subIndex >= startSubIndex && subIndex < startSubIndex + count;

    /// <summary>The write at ordinal <paramref name="index"/> of a run, as <see cref="IPbtStemChanges.SetRange"/> lays one out.</summary>
    internal static ValueHash256 RunValue(ReadOnlySpan<byte> values, int index) =>
        new(values.Slice(index * ValueHash256.MemorySize, ValueHash256.MemorySize));

    /// <summary>Returns <paramref name="changes"/> to the pool matching its concrete variant.</summary>
    public static void Return(IPbtStemChanges changes)
    {
        switch (changes)
        {
            case SingleStemChanges single: StaticPool<SingleStemChanges>.Return(single); break;
            case SeedableStemChanges seedable: seedable.ReturnSelf(); break;
        }
    }
}

/// <summary>The variants that hold more than one write, and so can be rented already holding a map's writes to promote it.</summary>
internal abstract class SeedableStemChanges : IPbtStemChanges, IResettable
{
    public abstract int Count { get; }

    public abstract IPbtStemChanges Set(byte subIndex, in ValueHash256 value);

    public abstract IPbtStemChanges SetRange(byte startSubIndex, ReadOnlySpan<byte> values);

    public abstract byte SubIndexAt(int index);

    public abstract ref readonly ValueHash256 Get(int index);

    public abstract void Reset();

    /// <summary>Seeds this (freshly rented, still empty) map from an already-ascending run of writes.</summary>
    /// <remarks><paramref name="subIndices"/> must be ascending; seeding skips the per-entry insertion a sequence of <see cref="Set"/> calls would incur.</remarks>
    internal abstract void Seed(ReadOnlySpan<byte> subIndices, ReadOnlySpan<ValueHash256> values);

    /// <summary>Returns this map to the pool of its own variant.</summary>
    internal abstract void ReturnSelf();
}

/// <summary>The single-write variant: no backing allocation. Promotes to <see cref="Length4StemChanges"/> on a second key.</summary>
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

        Length4StemChanges promoted = StaticPool<Length4StemChanges>.Rent();
        promoted.Set(_subIndex, _value);
        promoted.Set(subIndex, value);
        StaticPool<SingleStemChanges>.Return(this);
        return promoted;
    }

    public IPbtStemChanges SetRange(byte startSubIndex, ReadOnlySpan<byte> values)
    {
        int count = values.Length / ValueHash256.MemorySize;
        if (count == 0) return this;

        // this variant's leaf survives the run only if the run does not overwrite it
        int resulting = count + (_hasValue && !PbtStemChanges.RunCovers(startSubIndex, count, _subIndex) ? 1 : 0);
        if (resulting == 1) return Set(startSubIndex, PbtStemChanges.RunValue(values, 0));

        IPbtStemChanges promoted = PbtStemChanges.RentFor(resulting);
        if (_hasValue) promoted = promoted.Set(_subIndex, _value);
        StaticPool<SingleStemChanges>.Return(this);
        return promoted.SetRange(startSubIndex, values);
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

/// <summary>
/// A fixed tier's sub-index search: one compare of the tier's whole sub-index array against a sub-index.
/// </summary>
/// <remarks>
/// Every tier compares in <see cref="Vector128{T}"/> and narrows only its load, because a narrower vector
/// is not a narrower compare: <see cref="Vector64{T}"/> is hardware-accelerated on Arm64 but emulated on
/// x64, where it compiles to a scalar compare per lane and a hand-assembled mask — an order of magnitude
/// more code than the <c>vpcmpltub</c> the same search in <see cref="Vector128{T}"/> lowers to on both.
/// <para>
/// The search is a type parameter of <see cref="FixedStemChanges{TSearch}"/> rather than a virtual method
/// so that each tier's JIT instantiation inlines its own load, and sizes its array to its own lanes.
/// </para>
/// </remarks>
internal interface ISubIndexSearch
{
    /// <summary>The sub-indices one compare covers, which is the tier's capacity and its array's length.</summary>
    static abstract int Lanes { get; }

    /// <summary>A bit per lane of a <see cref="Vector128{T}"/>, set where the lane's sub-index is strictly below <paramref name="subIndex"/>.</summary>
    /// <param name="subIndices">The tier's whole sub-index array, live entries first. Lanes at or above the map's count hold stale sub-indices, and the lanes past a tier's <see cref="Lanes"/> hold the zeroes its load left, so callers must discard their bits.</param>
    static abstract uint LessThan(byte[] subIndices, byte subIndex);
}

/// <summary>Four lanes, read as one <see cref="uint"/> so the tier's array needs no padding to load.</summary>
internal readonly struct Search4 : ISubIndexSearch
{
    internal const int Lanes = 4;

    static int ISubIndexSearch.Lanes => Lanes;

    static uint ISubIndexSearch.LessThan(byte[] subIndices, byte subIndex) =>
        Vector128.LessThan(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref subIndices[0])).AsByte(), Vector128.Create(subIndex)).ExtractMostSignificantBits();
}

/// <summary>Eight lanes, read as one <see cref="ulong"/> so the tier's array needs no padding to load.</summary>
internal readonly struct Search8 : ISubIndexSearch
{
    internal const int Lanes = 8;

    static int ISubIndexSearch.Lanes => Lanes;

    static uint ISubIndexSearch.LessThan(byte[] subIndices, byte subIndex) =>
        Vector128.LessThan(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref subIndices[0])).AsByte(), Vector128.Create(subIndex)).ExtractMostSignificantBits();
}

/// <summary>Sixteen lanes: the array is a whole vector, so it loads as one.</summary>
internal readonly struct Search16 : ISubIndexSearch
{
    internal const int Lanes = 16;

    static int ISubIndexSearch.Lanes => Lanes;

    static uint ISubIndexSearch.LessThan(byte[] subIndices, byte subIndex) =>
        Vector128.LessThan(Vector128.LoadUnsafe(ref subIndices[0]), Vector128.Create(subIndex)).ExtractMostSignificantBits();
}

/// <summary>The small variants: up to <typeparamref name="TSearch"/>'s lanes of writes in a fixed array kept ascending by insertion sort. Promotes to the next tier up when full.</summary>
/// <typeparam name="TSearch">The tier's sub-index search, which fixes its capacity.</typeparam>
internal abstract class FixedStemChanges<TSearch> : SeedableStemChanges where TSearch : struct, ISubIndexSearch
{
    private readonly byte[] _subIndices = new byte[TSearch.Lanes];
    private readonly ValueHash256[] _values = new ValueHash256[TSearch.Lanes];
    private int _count;

    public override int Count => _count;

    public override IPbtStemChanges Set(byte subIndex, in ValueHash256 value)
    {
        int i = InsertionIndex(subIndex);
        if (i < _count && _subIndices[i] == subIndex)
        {
            _values[i] = value;
            return this;
        }

        if (_count < TSearch.Lanes)
        {
            _subIndices.AsSpan(i, _count - i).CopyTo(_subIndices.AsSpan(i + 1));
            _values.AsSpan(i, _count - i).CopyTo(_values.AsSpan(i + 1));
            _subIndices[i] = subIndex;
            _values[i] = value;
            _count++;
            return this;
        }

        IPbtStemChanges result = Promoted(_count + 1).Set(subIndex, value);
        ReturnSelf();
        return result;
    }

    public override IPbtStemChanges SetRange(byte startSubIndex, ReadOnlySpan<byte> values)
    {
        int count = values.Length / ValueHash256.MemorySize;
        if (count == 0) return this;

        int resulting = count;
        for (int i = 0; i < _count; i++)
        {
            if (!PbtStemChanges.RunCovers(startSubIndex, count, _subIndices[i])) resulting++;
        }

        // these variants address their values by ordinal, not by sub-index, so they have no block-copy to
        // offer a run: placing at most a tier's worth of leaves one by one is the whole of what they can do
        if (resulting <= TSearch.Lanes)
        {
            IPbtStemChanges changes = this;
            for (int i = 0; i < count; i++) changes = changes.Set((byte)(startSubIndex + i), PbtStemChanges.RunValue(values, i));
            return changes;
        }

        IPbtStemChanges result = Promoted(resulting).SetRange(startSubIndex, values);
        ReturnSelf();
        return result;
    }

    /// <summary>The ordinal <paramref name="subIndex"/> belongs at: the number of live entries strictly less than it.</summary>
    /// <remarks>Since the sub-indices are sorted ascending, that is the population count of the search's "&lt; subIndex" lanes masked to the live ones.</remarks>
    private int InsertionIndex(byte subIndex) =>
        BitOperations.PopCount(TSearch.LessThan(_subIndices, subIndex) & (uint)((1 << _count) - 1));

    /// <summary>Rents the tier that holds <paramref name="resulting"/> writes, seeded with this map's writes.</summary>
    /// <remarks>
    /// The caller returns this map to the pool itself, once it has made the write it is promoting for:
    /// that write may be handed in by reference to a value this map still owns, which returning it first
    /// would expose to whoever rents it next.
    /// </remarks>
    private IPbtStemChanges Promoted(int resulting) =>
        PbtStemChanges.RentSeeded(resulting, _subIndices.AsSpan(0, _count), _values.AsSpan(0, _count));

    internal override void Seed(ReadOnlySpan<byte> subIndices, ReadOnlySpan<ValueHash256> values)
    {
        subIndices.CopyTo(_subIndices);
        values.CopyTo(_values);
        _count = subIndices.Length;
    }

    public override byte SubIndexAt(int index) => _subIndices[index];

    public override ref readonly ValueHash256 Get(int index) => ref _values[index];

    public override void Reset() => _count = 0;
}

/// <summary>The up-to-four-write variant: an account's basic-data-and-code-hash header, or a stem holding a handful of storage slots.</summary>
internal sealed class Length4StemChanges : FixedStemChanges<Search4>
{
    internal override void ReturnSelf() => StaticPool<Length4StemChanges>.Return(this);
}

/// <summary>The up-to-eight-write variant.</summary>
internal sealed class Length8StemChanges : FixedStemChanges<Search8>
{
    internal override void ReturnSelf() => StaticPool<Length8StemChanges>.Return(this);
}

/// <summary>The up-to-sixteen-write variant: the last tier before the directly-addressed <see cref="SortedStemChanges"/>.</summary>
internal sealed class Length16StemChanges : FixedStemChanges<Search16>
{
    internal override void ReturnSelf() => StaticPool<Length16StemChanges>.Return(this);
}

/// <summary>The large variant: sub-indices kept ascending in a byte array that indexes into a directly-addressed values array, insertion-sorted on write.</summary>
/// <remarks>
/// <c>_order[0.._count]</c> holds the present sub-indices in ascending order; each is used verbatim as the
/// index into <c>_values</c> (which is addressed by sub-index). A repeated <see cref="Set"/> overwrites the
/// value in place; a new sub-index is insertion-sorted into <c>_order</c>. Sorting happens on write, so
/// <see cref="Get"/> reads directly and <see cref="Reset"/> need only clear the count (stale <c>_values</c>
/// entries are never read, as every new sub-index rewrites its slot).
/// <para>
/// Addressing <c>_values</c> by sub-index is also what lets <see cref="SetRange"/> place a run in block
/// copies: a run of consecutive sub-indices is a contiguous span of <c>_values</c> and a contiguous slice
/// of <c>_order</c>, so neither needs to be walked a leaf at a time.
/// </para>
/// </remarks>
internal sealed class SortedStemChanges : SeedableStemChanges
{
    private const int Capacity = 256;
    private readonly byte[] _order = new byte[Capacity];
    private readonly ValueHash256[] _values = new ValueHash256[Capacity];
    private int _count;

    public override int Count => _count;

    /// <inheritdoc/>
    /// <remarks>Always returns this: the largest variant has nothing to promote to.</remarks>
    public override IPbtStemChanges Set(byte subIndex, in ValueHash256 value)
    {
        int lo = LowerBound(subIndex);
        if (lo < _count && _order[lo] == subIndex)
        {
            _values[subIndex] = value;
            return this;
        }

        _order.AsSpan(lo, _count - lo).CopyTo(_order.AsSpan(lo + 1));
        _order[lo] = subIndex;
        _values[subIndex] = value;
        _count++;
        return this;
    }

    /// <inheritdoc/>
    /// <remarks>Always returns this: the largest variant has nothing to promote to.</remarks>
    public override IPbtStemChanges SetRange(byte startSubIndex, ReadOnlySpan<byte> values)
    {
        int count = values.Length / ValueHash256.MemorySize;
        if (count == 0) return this;

        // _values is addressed by sub-index, so the run's values are already contiguous in it: the whole
        // run lands in one copy, wherever the run falls relative to what is already here.
        values.CopyTo(MemoryMarshal.AsBytes(_values.AsSpan(startSubIndex, count)));

        // _order is ascending, so the sub-indices the run overwrites are exactly the slice [lo, hi) — the
        // run replaces it wholesale, which is one move of the entries above it and one ascending fill.
        int lo = LowerBound(startSubIndex);
        int hi = LowerBound(startSubIndex + count);
        int above = _count - hi;
        _order.AsSpan(hi, above).CopyTo(_order.AsSpan(lo + count));
        for (int i = 0; i < count; i++) _order[lo + i] = (byte)(startSubIndex + i);
        _count = lo + count + above;
        return this;
    }

    internal override void Seed(ReadOnlySpan<byte> subIndices, ReadOnlySpan<ValueHash256> values)
    {
        subIndices.CopyTo(_order);
        for (int i = 0; i < subIndices.Length; i++) _values[subIndices[i]] = values[i];
        _count = subIndices.Length;
    }

    /// <summary>The first ordinal whose sub-index is at or above <paramref name="subIndex"/>, or <see cref="Count"/> if there is none.</summary>
    /// <param name="subIndex">Not a <see cref="byte"/>: a run's exclusive end is 256 when it reaches the end of the stem.</param>
    private int LowerBound(int subIndex)
    {
        int lo = 0, hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_order[mid] < subIndex) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    public override byte SubIndexAt(int index) => _order[index];

    public override ref readonly ValueHash256 Get(int index) => ref _values[_order[index]];

    public override void Reset() => _count = 0;

    internal override void ReturnSelf() => StaticPool<SortedStemChanges>.Return(this);
}
