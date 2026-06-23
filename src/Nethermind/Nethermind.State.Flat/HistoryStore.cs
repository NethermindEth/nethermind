// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat;

/// <summary>
/// Finalized-only historical state for one flat domain (account or storage), over already-encoded byte keys.
/// History is key-major (<c>[key | block BE] -> value</c>, contiguous per key) and holds the values: an as-of read
/// is a single floor-seek that yields the value directly. ChangeMarkers is block-major
/// (<c>[block BE | key] -> empty</c>, contiguous per block) and holds no values; it drives block-range operations —
/// sliding-window pruning (R3) and segment export (R4) — that the key-major History cannot answer contiguously.
/// </summary>
public sealed class HistoryStore
{
    private const int BlockBytes = sizeof(long);

    private readonly ISortedKeyValueStore _history;
    private readonly IDb _changeMarkers;

    public HistoryStore(IDb history, IDb changeMarkers)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(changeMarkers);
        if (history is not ISortedKeyValueStore sortedHistory)
            throw new ArgumentException($"History column must be a {nameof(ISortedKeyValueStore)}.", nameof(history));

        _history = sortedHistory;
        _changeMarkers = changeMarkers;
    }

    /// <summary>Records the post-change value at <paramref name="block"/>; an empty value is a deletion tombstone.</summary>
    [SkipLocalsInit]
    public void RecordChange(long block, scoped ReadOnlySpan<byte> flatKey, scoped ReadOnlySpan<byte> value, IWriteBatch historyBatch, IWriteBatch changeMarkerBatch)
    {
        Span<byte> historyKey = stackalloc byte[flatKey.Length + BlockBytes];
        WriteHistoryKey(historyKey, flatKey, block);
        if (value.IsEmpty)
            historyBatch.Set(historyKey, Array.Empty<byte>());
        else
            historyBatch.PutSpan(historyKey, value);

        Span<byte> markerKey = stackalloc byte[BlockBytes + flatKey.Length];
        WriteMarkerKey(markerKey, block, flatKey);
        changeMarkerBatch.Set(markerKey, Array.Empty<byte>());
    }

    /// <summary>
    /// Reads the value as of <paramref name="block"/> into <paramref name="outBuffer"/>. Returns -1 when the key
    /// never changed at/before <paramref name="block"/> (caller falls back to the tip), 0 for a deletion tombstone,
    /// otherwise the number of value bytes written.
    /// </summary>
    [SkipLocalsInit]
    public int TryGetAt(long block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer)
    {
        // A change at exactly `block` is the floor; KeyExists disambiguates a present-but-empty tombstone (length 0)
        // from a missing key, which Get's length alone cannot. Otherwise floor-seek strictly below `block`, where the
        // backends' inclusive/exclusive StartBefore agree, and read the value from the same seek (CurrentValue).
        Span<byte> key = stackalloc byte[flatKey.Length + BlockBytes];
        WriteHistoryKey(key, flatKey, block);
        if (_history.KeyExists(key))
            return _history.Get(key, outBuffer);

        Span<byte> lowerBound = stackalloc byte[flatKey.Length + BlockBytes];
        flatKey.CopyTo(lowerBound);
        lowerBound[flatKey.Length..].Clear();

        using ISortedView view = _history.GetViewBetween(lowerBound, key);
        if (!view.StartBefore(key)) return -1;

        ReadOnlySpan<byte> foundKey = view.CurrentKey;
        if (foundKey.Length != flatKey.Length + BlockBytes || !foundKey[..flatKey.Length].SequenceEqual(flatKey))
            return -1;

        ReadOnlySpan<byte> value = view.CurrentValue;
        value.CopyTo(outBuffer);
        return value.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHistoryKey(Span<byte> destination, scoped ReadOnlySpan<byte> flatKey, long block)
    {
        flatKey.CopyTo(destination[..flatKey.Length]);
        BinaryPrimitives.WriteInt64BigEndian(destination[flatKey.Length..], block);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteMarkerKey(Span<byte> destination, long block, scoped ReadOnlySpan<byte> flatKey)
    {
        BinaryPrimitives.WriteInt64BigEndian(destination[..BlockBytes], block);
        flatKey.CopyTo(destination[BlockBytes..]);
    }
}
