// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Pre-value store: <c>[key | block] -&gt; value BEFORE the change at block</c>, key-major with an ASCENDING block
/// suffix (the opposite of <see cref="HistoryStore"/>'s descending suffix), so an as-of read is a forward seek for
/// the smallest recorded block strictly greater than the query block.
/// </summary>
/// <remarks>A read finding no captured change above its query block falls back to the live flat value. Sound only
/// against the PERSISTED column, never a tip-stacked view: capture runs before the persist it captured from, so a
/// key with no change in <c>(B, watermark]</c> still holds its block-B value there.</remarks>
internal sealed class HistoryStoreV3
{
    private const int BlockBytes = sizeof(ulong);

    private readonly ISortedKeyValueStore _history;

    public HistoryStoreV3(IDb history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history is not ISortedKeyValueStore sortedHistory)
            throw new ArgumentException($"History column must be a {nameof(ISortedKeyValueStore)}.", nameof(history));

        _history = sortedHistory;
    }

    /// <summary>The value held before the change at <paramref name="block"/>; empty means it did not exist.</summary>
    [SkipLocalsInit]
    public void RecordPreValue(ulong block, scoped ReadOnlySpan<byte> flatKey, scoped ReadOnlySpan<byte> valueBeforeChange, IWriteBatch batch)
    {
        Span<byte> historyKey = stackalloc byte[flatKey.Length + BlockBytes];
        WriteHistoryKey(historyKey, flatKey, block);
        if (valueBeforeChange.IsEmpty)
            batch.Set(historyKey, Array.Empty<byte>());
        else
            batch.PutSpan(historyKey, valueBeforeChange);
    }

    /// <summary>The recorded change nearest above <paramref name="block"/>. -1 when none is recorded - this store
    /// makes no claim about the live value. 0 for an empty pre-value, otherwise the bytes written.</summary>
    [SkipLocalsInit]
    public int TryGetValueBeforeNextChange(ulong block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer, out ulong nextChangeBlock)
    {
        nextChangeBlock = 0;

        // Ascending suffix, so the answer is the first entry at or after [key | block + 1].
        if (block == ulong.MaxValue) return -1;

        Span<byte> seekKey = stackalloc byte[flatKey.Length + BlockBytes];
        WriteHistoryKey(seekKey, flatKey, block + 1);

        Span<byte> upperBound = stackalloc byte[flatKey.Length + BlockBytes + 1];
        flatKey.CopyTo(upperBound);
        upperBound[flatKey.Length..].Fill(0xFF);

        using ISortedView view = _history.GetViewBetween(seekKey, upperBound);
        if (!view.MoveNext()) return -1;

        ReadOnlySpan<byte> foundKey = view.CurrentKey;
        if (foundKey.Length != flatKey.Length + BlockBytes || !foundKey[..flatKey.Length].SequenceEqual(flatKey))
            return -1;

        nextChangeBlock = BinaryPrimitives.ReadUInt64BigEndian(foundKey[flatKey.Length..]);
        ReadOnlySpan<byte> value = view.CurrentValue;
        if (value.Length > outBuffer.Length)
        {
            throw new StateUnavailableException(
                $"History value of {value.Length} bytes at block {nextChangeBlock} exceeds the {outBuffer.Length}-byte encoder maximum - the row is corrupt; resync the flatHistory database.");
        }

        value.CopyTo(outBuffer);
        return value.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHistoryKey(Span<byte> destination, scoped ReadOnlySpan<byte> flatKey, ulong block)
    {
        flatKey.CopyTo(destination[..flatKey.Length]);
        BinaryPrimitives.WriteUInt64BigEndian(destination[flatKey.Length..], block);
    }
}
