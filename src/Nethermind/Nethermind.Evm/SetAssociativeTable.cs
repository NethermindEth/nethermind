// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Threading;

namespace Nethermind.Evm;

/// <summary>An immutable entry of a <see cref="SetAssociativeTable{TEntry}"/>, found by its bytes.</summary>
/// <param name="fastHash">The hash of <see cref="Key"/> that picks the entry's set.</param>
internal abstract class SetAssociativeEntry(int fastHash)
{
    public readonly int FastHash = fastHash;

    /// <summary>The bytes the entry is found by. They must not change while the entry is in a table.</summary>
    public abstract ReadOnlySpan<byte> Key { get; }
}

/// <summary>A fixed-size, set-associative table of immutable entries keyed by bytes.</summary>
/// <remarks>
/// <para>
/// The hash of a key picks one set, and the entry may sit in any way of it, so keys whose hashes pick the same set
/// do not evict each other until the set is full. A new entry goes to the front of its set and the oldest one falls
/// off the end. A hit does not reorder the set, so lookups never write.
/// </para>
/// <para>
/// Lookups take no lock. Every hit is checked against the whole key, so a hash collision costs a miss, never a wrong
/// entry. Additions are serialised and look the key up again first, so a key is in the table at most once. An
/// addition moves the entries down one way starting from the end, and a lookup walks from the front, so it never
/// skips an entry that stays in the table.
/// </para>
/// </remarks>
internal sealed class SetAssociativeTable<TEntry> where TEntry : SetAssociativeEntry
{
    private readonly TEntry?[] _entries;
    private readonly int _ways;
    private readonly int _setMask;
    private readonly Lock _addLock = new();

    /// <param name="sets">The number of sets, a power of two.</param>
    /// <param name="ways">The number of entries a set holds.</param>
    public SetAssociativeTable(int sets, int ways)
    {
        if (!BitOperations.IsPow2(sets)) throw new ArgumentOutOfRangeException(nameof(sets), sets, "Must be a power of two.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);

        _entries = new TEntry?[sets * ways];
        _ways = ways;
        _setMask = sets - 1;
    }

    /// <summary>The most entries the table holds.</summary>
    public int Capacity => _entries.Length;

    /// <summary>The number of entries in the table now.</summary>
    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _entries.Length; i++)
            {
                if (Volatile.Read(ref _entries[i]) is not null) count++;
            }

            return count;
        }
    }

    /// <summary>Returns the entry whose key is <paramref name="key"/>, or <see langword="null"/>.</summary>
    public TEntry? Find(ReadOnlySpan<byte> key, int fastHash) => Find(SetOf(fastHash), key, fastHash);

    /// <summary>
    /// Adds <paramref name="entry"/> at the front of its set, dropping the oldest entry of a full set, unless an entry
    /// with the same key is already there.
    /// </summary>
    /// <returns>The entry the table holds for the key: <paramref name="entry"/>, or the one added before it.</returns>
    public TEntry GetOrAdd(TEntry entry)
    {
        Span<TEntry?> set = SetOf(entry.FastHash);
        lock (_addLock)
        {
            TEntry? existing = Find(set, entry.Key, entry.FastHash);
            if (existing is not null) return existing;

            for (int way = set.Length - 1; way > 0; way--)
            {
                Volatile.Write(ref set[way], set[way - 1]);
            }

            Volatile.Write(ref set[0], entry);
            return entry;
        }
    }

    private Span<TEntry?> SetOf(int fastHash) => _entries.AsSpan((fastHash & _setMask) * _ways, _ways);

    private static TEntry? Find(Span<TEntry?> set, ReadOnlySpan<byte> key, int fastHash)
    {
        for (int way = 0; way < set.Length; way++)
        {
            TEntry? entry = Volatile.Read(ref set[way]);
            if (entry is not null && entry.FastHash == fastHash && entry.Key.SequenceEqual(key)) return entry;
        }

        return null;
    }
}
