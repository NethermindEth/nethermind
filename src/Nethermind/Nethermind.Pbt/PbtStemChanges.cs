// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;

namespace Nethermind.Pbt;

/// <param name="SubIndex">The sub-index (0..255) of the leaf within its stem's 256-leaf subtree.</param>
/// <param name="Value">The 32-byte leaf value; a zero value clears the leaf.</param>
public readonly record struct StemLeafWrite(byte SubIndex, ValueHash256 Value);

/// <summary>
/// One stem's sub-index → 32-byte value writes, exposed in ascending sub-index order. The common
/// case is a single write (a storage-zone slot); only full-code header stems grow large.
/// </summary>
/// <remarks>
/// A zero value is a leaf clear and is retained (never dropped), so <see cref="TrieUpdater"/> sees it.
/// Instances are pooled and size-tiered (see <see cref="PbtStemChanges"/>); <see cref="Set"/> may
/// promote to a larger variant and return it, so callers must use the returned reference.
/// </remarks>
public interface IPbtStemChanges
{
    /// <summary>The number of leaves written.</summary>
    int Count { get; }

    /// <summary>Adds or overwrites the write for <paramref name="subIndex"/>.</summary>
    /// <returns>The map to keep using — this instance, or a larger variant it promoted to.</returns>
    IPbtStemChanges Set(byte subIndex, in ValueHash256 value);

    /// <summary>Writes the entries ascending by sub-index into <paramref name="destination"/> (whose length is <see cref="Count"/>).</summary>
    void WriteSorted(Span<StemLeafWrite> destination);
}

/// <summary>
/// Rents and returns the pooled <see cref="IPbtStemChanges"/> variants. Each variant is pooled in its
/// own <see cref="StaticPool{T}"/>, giving three size-tiered pools: single, up-to-eight, and dictionary.
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
            case DictionaryStemChanges dictionary: StaticPool<DictionaryStemChanges>.Return(dictionary); break;
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

    public void WriteSorted(Span<StemLeafWrite> destination)
    {
        if (_hasValue) destination[0] = new StemLeafWrite(_subIndex, _value);
    }

    public void Reset()
    {
        _hasValue = false;
        _value = default;
        _subIndex = 0;
    }
}

/// <summary>The up-to-eight-write variant: a fixed array kept ascending by insertion sort. Promotes to <see cref="DictionaryStemChanges"/> when full.</summary>
internal sealed class Length8StemChanges : IPbtStemChanges, IResettable
{
    private const int Capacity = 8;
    private readonly StemLeafWrite[] _entries = new StemLeafWrite[Capacity];
    private int _count;

    public int Count => _count;

    public IPbtStemChanges Set(byte subIndex, in ValueHash256 value)
    {
        int i = 0;
        while (i < _count && _entries[i].SubIndex < subIndex) i++;

        if (i < _count && _entries[i].SubIndex == subIndex)
        {
            _entries[i] = new StemLeafWrite(subIndex, value);
            return this;
        }

        if (_count < Capacity)
        {
            for (int j = _count; j > i; j--) _entries[j] = _entries[j - 1];
            _entries[i] = new StemLeafWrite(subIndex, value);
            _count++;
            return this;
        }

        DictionaryStemChanges promoted = StaticPool<DictionaryStemChanges>.Rent();
        for (int k = 0; k < _count; k++) promoted.Set(_entries[k].SubIndex, _entries[k].Value);
        promoted.Set(subIndex, value);
        StaticPool<Length8StemChanges>.Return(this);
        return promoted;
    }

    public void WriteSorted(Span<StemLeafWrite> destination) => _entries.AsSpan(0, _count).CopyTo(destination);

    public void Reset() => _count = 0;
}

/// <summary>The large variant: a dictionary that sorts its entries on read.</summary>
internal sealed class DictionaryStemChanges : IPbtStemChanges, IResettable
{
    private readonly Dictionary<byte, ValueHash256> _entries = new(16);

    public int Count => _entries.Count;

    public IPbtStemChanges Set(byte subIndex, in ValueHash256 value)
    {
        _entries[subIndex] = value;
        return this;
    }

    public void WriteSorted(Span<StemLeafWrite> destination)
    {
        int i = 0;
        foreach ((byte subIndex, ValueHash256 value) in _entries) destination[i++] = new StemLeafWrite(subIndex, value);
        destination.Sort(static (left, right) => left.SubIndex.CompareTo(right.SubIndex));
    }

    public void Reset() => _entries.Clear();
}
