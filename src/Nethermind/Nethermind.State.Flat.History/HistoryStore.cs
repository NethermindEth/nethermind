// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Finalized-only historical state for one flat domain (account or storage), over already-encoded byte keys.
/// History is key-major with a descending block suffix (<c>[key | ~block BE] -> value</c>), so each key's versions
/// sit contiguously newest-first: an as-of read is a single forward seek that yields the value directly.
/// </summary>
/// <remarks>
/// The descending suffix also lets a future sliding-window prune run as a purely sequential compaction filter
/// ("first version at/below the cutoff → keep, every later entry of the key → drop" — no lookahead). There is no
/// block-major index; block-range operations derive their view from this column or from the capture stream.
/// </remarks>
internal sealed class HistoryStore
{
    private const int BlockBytes = sizeof(ulong);

    private readonly ISortedKeyValueStore _history;

    public HistoryStore(IDb history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history is not ISortedKeyValueStore sortedHistory)
            throw new ArgumentException($"History column must be a {nameof(ISortedKeyValueStore)}.", nameof(history));

        _history = sortedHistory;
    }

    /// <summary>Records the post-change value at <paramref name="block"/>; an empty value is a deletion tombstone.</summary>
    [SkipLocalsInit]
    public void RecordChange(ulong block, scoped ReadOnlySpan<byte> flatKey, scoped ReadOnlySpan<byte> value, IWriteBatch historyBatch)
    {
        Span<byte> historyKey = stackalloc byte[flatKey.Length + BlockBytes];
        WriteHistoryKey(historyKey, flatKey, block);
        if (value.IsEmpty)
            historyBatch.Set(historyKey, Array.Empty<byte>());
        else
            historyBatch.PutSpan(historyKey, value);
    }

    /// <summary>
    /// Reads the value as of <paramref name="block"/> into <paramref name="outBuffer"/>. Returns -1 when the key never
    /// changed at/before <paramref name="block"/> — with contiguous capture that means it did not exist at that height,
    /// so the caller reports absent — 0 for a deletion tombstone, otherwise the number of value bytes written.
    /// </summary>
    public int TryGetAt(ulong block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer) =>
        TryGetAt(block, flatKey, outBuffer, out _);

    /// <summary>
    /// Same as <see cref="TryGetAt(ulong, ReadOnlySpan{byte}, Span{byte})"/>, additionally reporting the block of
    /// the resolved change in <paramref name="foundAtBlock"/> (0 when nothing was found).
    /// </summary>
    [SkipLocalsInit]
    public int TryGetAt(ulong block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer, out ulong foundAtBlock)
    {
        foundAtBlock = 0;

        // With the descending suffix, the newest version at/below block is the first entry at/after [key | ~block].
        Span<byte> seekKey = stackalloc byte[flatKey.Length + BlockBytes];
        WriteHistoryKey(seekKey, flatKey, block);

        // One byte past [key | 0xFF..FF] so the exclusive upper bound cannot cut off the block-0 version.
        Span<byte> upperBound = stackalloc byte[flatKey.Length + BlockBytes + 1];
        flatKey.CopyTo(upperBound);
        upperBound[flatKey.Length..].Fill(0xFF);
        upperBound[^1] = 0x00;

        using ISortedView view = _history.GetViewBetween(seekKey, upperBound);
        if (!view.MoveNext()) return -1; // first call positions at the first entry of the bounded view

        ReadOnlySpan<byte> foundKey = view.CurrentKey;
        if (foundKey.Length != flatKey.Length + BlockBytes || !foundKey[..flatKey.Length].SequenceEqual(flatKey))
            return -1;

        foundAtBlock = ~BinaryPrimitives.ReadUInt64BigEndian(foundKey[flatKey.Length..]);
        ReadOnlySpan<byte> value = view.CurrentValue;
        Debug.Assert(value.Length <= outBuffer.Length, "history value exceeds caller buffer; a value encoder outgrew its buffer size constant");
        value.CopyTo(outBuffer);
        return value.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHistoryKey(Span<byte> destination, scoped ReadOnlySpan<byte> flatKey, ulong block)
    {
        flatKey.CopyTo(destination[..flatKey.Length]);
        BinaryPrimitives.WriteUInt64BigEndian(destination[flatKey.Length..], ~block);
    }
}
