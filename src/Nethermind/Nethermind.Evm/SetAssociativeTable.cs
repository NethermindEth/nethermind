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

    /// <summary>
    /// Whether the entry was added or found since its set last aged. Only a table that keeps used entries reads it.
    /// </summary>
    internal bool Used;

    /// <summary>The bytes the entry is found by. They must not change while the entry is in a table.</summary>
    public abstract ReadOnlySpan<byte> Key { get; }
}

/// <summary>A fixed-size, set-associative table of immutable entries keyed by bytes.</summary>
/// <remarks>
/// <para>
/// The hash of a key picks one set, and the entry may sit in any way of it, so keys whose hashes pick the same set
/// do not evict each other until the set is full. A new entry goes to the front of its set and the oldest one falls
/// off the end. A hit does not reorder the set.
/// </para>
/// <para>
/// Lookups take no lock. Every hit is checked against the whole key, so a hash collision costs a miss, never a wrong
/// entry. Additions are serialised and look the key up again first, so a key is in the table at most once. An
/// addition moves the entries down one way, starting from the one it drops, and a lookup walks from the front, so it
/// never skips an entry that stays in the table.
/// </para>
/// <para>
/// A table that keeps used entries marks an entry when it is added or found, which a lookup writes once per entry
/// until the set ages. A full set then drops its oldest unmarked entry rather than its oldest, and it has no room
/// while all its entries are marked. Asking <see cref="HasRoomFor"/> about a set without room counts as a refusal,
/// and after <see cref="RefusalsBeforeAging"/> of them the set ages: its marks are cleared, so entries that stopped
/// being used make way again. A caller whose additions are costly asks first, so a working set larger than the table
/// keeps the entries in use instead of replacing them on every miss, and then adds with <see cref="TryGetOrAdd"/>,
/// which asks again under the lock that serialises additions, so an addition that raced another one never drops a
/// marked entry.
/// </para>
/// </remarks>
internal sealed class SetAssociativeTable<TEntry> where TEntry : SetAssociativeEntry
{
    private readonly TEntry?[] _entries;
    private readonly int _ways;
    private readonly int _setMask;
    private readonly Lock _addLock = new();

    // Per set, the refusals since it last aged; null unless the table keeps used entries.
    private readonly int[]? _refusals;

    /// <param name="sets">The number of sets, a power of two.</param>
    /// <param name="ways">The number of entries a set holds.</param>
    /// <param name="keepsUsedEntries">Whether entries added or found since their set last aged are kept over new ones.</param>
    public SetAssociativeTable(int sets, int ways, bool keepsUsedEntries = false)
    {
        if (!BitOperations.IsPow2(sets)) throw new ArgumentOutOfRangeException(nameof(sets), sets, "Must be a power of two.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ways);

        _entries = new TEntry?[sets * ways];
        _ways = ways;
        _setMask = sets - 1;
        if (keepsUsedEntries) _refusals = new int[sets];
    }

    /// <summary>The most entries the table holds.</summary>
    public int Capacity => _entries.Length;

    /// <summary>How many refusals age a set of a table that keeps used entries.</summary>
    public int RefusalsBeforeAging => 4 * _ways;

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
    public TEntry? Find(ReadOnlySpan<byte> key, int fastHash)
    {
        TEntry? entry = Find(SetOf(fastHash), key, fastHash);
        if (_refusals is not null && entry is { Used: false }) entry.Used = true;
        return entry;
    }

    /// <summary>
    /// Whether an entry with this hash would find room in its set: always, unless the table keeps used entries and
    /// every entry of the set was added or found since it last aged.
    /// </summary>
    /// <remarks>A <see langword="false"/> answer counts as a refusal of the set.</remarks>
    public bool HasRoomFor(int fastHash)
    {
        if (_refusals is null) return true;

        int setIndex = fastHash & _setMask;
        lock (_addLock)
        {
            return HasRoom(setIndex);
        }
    }

    /// <summary>
    /// Adds <paramref name="entry"/> at the front of its set, dropping the oldest entry of a full set (in a table that
    /// keeps used entries, the oldest unmarked one if there is one), unless an entry with the same key is already there.
    /// </summary>
    /// <returns>The entry the table holds for the key: <paramref name="entry"/>, or the one added before it.</returns>
    public TEntry GetOrAdd(TEntry entry)
    {
        Span<TEntry?> set = SetOf(entry.FastHash);
        lock (_addLock)
        {
            TEntry? existing = Find(set, entry.Key, entry.FastHash);
            if (existing is not null) return existing;

            return Add(set, entry);
        }
    }

    /// <summary>
    /// Like <see cref="GetOrAdd"/>, except that a table that keeps used entries adds nothing while the set has no room,
    /// which counts as a refusal of the set, as with <see cref="HasRoomFor"/>.
    /// </summary>
    /// <returns>
    /// The entry the table holds for the key: <paramref name="entry"/> or the one added before it; or
    /// <see langword="null"/> if the set had no room.
    /// </returns>
    public TEntry? TryGetOrAdd(TEntry entry)
    {
        int setIndex = entry.FastHash & _setMask;
        Span<TEntry?> set = SetAt(setIndex);
        lock (_addLock)
        {
            TEntry? existing = Find(set, entry.Key, entry.FastHash);
            if (existing is not null) return existing;

            return _refusals is null || HasRoom(setIndex) ? Add(set, entry) : null;
        }
    }

    // Under _addLock, in a table that keeps used entries: whether the set has room, counting a refusal if not.
    private bool HasRoom(int setIndex)
    {
        Span<TEntry?> set = SetAt(setIndex);
        if (WayToDrop(set) >= 0) return true;
        if (++_refusals![setIndex] < RefusalsBeforeAging) return false;

        _refusals[setIndex] = 0;
        foreach (TEntry? entry in set) entry!.Used = false;
        return false;
    }

    // Under _addLock: puts the entry at the front of the set, dropping the way WayToDrop picks or else the last one.
    private TEntry Add(Span<TEntry?> set, TEntry entry)
    {
        int drop = _refusals is null ? -1 : WayToDrop(set);
        if (drop < 0) drop = set.Length - 1;

        for (int way = drop; way > 0; way--)
        {
            Volatile.Write(ref set[way], set[way - 1]);
        }

        entry.Used = true;
        Volatile.Write(ref set[0], entry);
        return entry;
    }

    private Span<TEntry?> SetOf(int fastHash) => SetAt(fastHash & _setMask);

    private Span<TEntry?> SetAt(int setIndex) => _entries.AsSpan(setIndex * _ways, _ways);

    // The last way that is free or holds an unmarked entry, or -1.
    private static int WayToDrop(Span<TEntry?> set)
    {
        for (int way = set.Length - 1; way >= 0; way--)
        {
            TEntry? entry = set[way];
            if (entry is null || !entry.Used) return way;
        }

        return -1;
    }

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
