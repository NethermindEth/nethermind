// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Erigon-style pre-value store: <c>[key | block] -&gt; value BEFORE the change at block</c>, key-major with an
/// ASCENDING block suffix (the opposite of <see cref="HistoryStore"/>'s descending suffix), so an as-of read is a
/// forward seek for the smallest recorded block strictly greater than the query block.
/// </summary>
/// <remarks>
/// The subtask's decision record calls for "if no later change is found, the current live flat value is
/// correct." That rule is sound under one condition: the fallback must read the <em>persisted</em> flat column
/// (<c>FlatDbColumns.Account</c>/<c>Storage</c>), never a tip- or snapshot-stack-including view. The invariant
/// chain that makes this hold: <see cref="HistoryWriter"/>'s capture always runs strictly before the flat persist
/// it captured from commits (its own contract — "the flat persist commits only after; must never get ahead of
/// durable history"), so at any moment the persisted flat columns hold exactly the state as of the current
/// watermark, never ahead of it and never behind it once a round completes. A key changed in
/// <c>(watermark, tip]</c> lives only in the in-memory snapshot stack, not yet in the persisted columns — the
/// persisted columns still hold its value as of the watermark. So for a query at <c>B &lt;= watermark</c> whose
/// forward-seek finds no captured change in <c>(B, watermark]</c>, the key's value provably did not change from B
/// through the watermark, and the persisted column's value (== the watermark's value) equals the value at B.
/// <see cref="HistoryReader"/> implements the fallback this way; do not introduce a fallback that reads through a
/// bundle/scope that could observe the tip instead. Prune correctness (a plain range-delete below the floor, no
/// per-key retention logic) is independent of this and unaffected either way.
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
