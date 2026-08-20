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
/// <remarks>
/// A read finding no captured change above its query block falls back to the live flat value - sound only against
/// the PERSISTED flat column, never a tip/snapshot-stacked view: capture runs strictly before the flat persist it
/// captured from commits, so the persisted columns always hold exactly the state as of the watermark, and a key
/// with no captured change in <c>(B, watermark]</c> provably still holds its block-B value there.
/// </remarks>
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

    /// <summary>Records the value the key held immediately BEFORE the change at <paramref name="block"/>; an
    /// empty value means the key did not exist before this block (this is its first-ever change).</summary>
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

    /// <summary>
    /// Looks up the recorded change nearest above <paramref name="block"/> (the smallest recorded block strictly
    /// greater than it). Returns -1 when no such change is recorded — the caller decides what "no later captured
    /// change" means for its read (see the safety remark on this type); this store makes no claim about the
    /// live/current value. Returns 0 for an empty pre-value (the key did not exist before that change), otherwise
    /// the number of bytes written to <paramref name="outBuffer"/>.
    /// </summary>
    [SkipLocalsInit]
    public int TryGetValueBeforeNextChange(ulong block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer, out ulong nextChangeBlock)
    {
        nextChangeBlock = 0;

        // Ascending suffix: the smallest recorded block strictly greater than `block` is the first entry at or
        // after [key | block + 1]. block == ulong.MaxValue can have no "next change" by construction.
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
