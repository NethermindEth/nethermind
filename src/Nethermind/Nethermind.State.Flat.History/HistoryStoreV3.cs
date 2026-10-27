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
    public int TryGetValueBeforeNextChange(ulong block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer, out ulong nextChangeBlock,
        ReadScope? scope = null, long generation = 0) => scope is null
        ? Read(block, flatKey, outBuffer, out nextChangeBlock, null, generation)
        : scope.Read(this, block, flatKey, outBuffer, out nextChangeBlock, generation);

    [SkipLocalsInit]
    private int Read(ulong block, scoped ReadOnlySpan<byte> flatKey, Span<byte> outBuffer, out ulong nextChangeBlock,
        ReadScope? scope, long generation)
    {
        nextChangeBlock = 0;

        // Ascending suffix, so the answer is the first entry at or after [key | block + 1].
        if (block == ulong.MaxValue) return -1;

        Span<byte> seekKey = stackalloc byte[flatKey.Length + BlockBytes];
        WriteHistoryKey(seekKey, flatKey, block + 1);

        Span<byte> upperBound = stackalloc byte[flatKey.Length + BlockBytes + 1];
        flatKey.CopyTo(upperBound);
        upperBound[flatKey.Length..].Fill(0xFF);

        if (scope is null && _history is ISnapshotSortedKeyValueStore snapshotHistory)
        {
            Span<byte> found = stackalloc byte[seekKey.Length];
            if (!snapshotHistory.TryGetCeilingInSnapshot(seekKey, upperBound, found, out int keyLength, outBuffer, out int valueLength)
                || keyLength != found.Length || !found[..flatKey.Length].SequenceEqual(flatKey)) return -1;

            nextChangeBlock = BinaryPrimitives.ReadUInt64BigEndian(found[flatKey.Length..]);
            ValidateValueLength(valueLength, outBuffer.Length, nextChangeBlock);
            return valueLength;
        }

        using ISortedView? ownedView = scope is null ? _history.GetViewBetween(seekKey, upperBound) : null;
        ISortedView view = ownedView ?? scope!.GetView(_history, seekKey, upperBound, generation);
        if (!view.MoveNext()) return -1;

        ReadOnlySpan<byte> foundKey = view.CurrentKey;
        if (foundKey.Length != flatKey.Length + BlockBytes || !foundKey[..flatKey.Length].SequenceEqual(flatKey))
            return -1;

        nextChangeBlock = BinaryPrimitives.ReadUInt64BigEndian(foundKey[flatKey.Length..]);
        ReadOnlySpan<byte> value = view.CurrentValue;
        ValidateValueLength(value.Length, outBuffer.Length, nextChangeBlock);

        value.CopyTo(outBuffer);
        return value.Length;
    }

    private static void ValidateValueLength(int valueLength, int bufferLength, ulong nextChangeBlock)
    {
        if (valueLength > bufferLength)
        {
            throw new StateUnavailableException(
                $"History value of {valueLength} bytes at block {nextChangeBlock} exceeds the {bufferLength}-byte encoder maximum - the row is corrupt; resync the flatHistory database.");
        }
    }

    /// <summary>Retains a bounded view until its capture generation changes.</summary>
    /// <remarks>Reuse relies on capture appending rows above the published watermark, then calling
    /// <see cref="HistoryAvailability.MarkCapturePublished"/> before live state changes. Existing rows stay
    /// unchanged while <see cref="HistoryScopeGate"/> protects the reader from pruning. Any future rewrite of
    /// retained rows must also invalidate the capture generation before readers can observe updated live state.</remarks>
    internal sealed class ReadScope : IDisposable
    {
        private readonly Lock _lock = new();
        private ISortedView? _view;
        private long _generation;
        private bool _disposed;

        internal int Read(HistoryStoreV3 store, ulong block, ReadOnlySpan<byte> flatKey, Span<byte> output,
            out ulong nextChangeBlock, long generation)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return store.Read(block, flatKey, output, out nextChangeBlock, this, generation);
            }
        }

        internal ISortedView GetView(ISortedKeyValueStore history, ReadOnlySpan<byte> lower, ReadOnlySpan<byte> upper, long generation)
        {
            if (_generation == generation && _view is IRebindableSortedView reusable && reusable.TryResetBounds(lower, upper)) return _view;

            _view?.Dispose();
            _view = null;
            _view = history.GetViewBetween(lower, upper, ReadFlags.HintRepeatedReads);
            _generation = generation;
            return _view;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _view?.Dispose();
                _view = null;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHistoryKey(Span<byte> destination, scoped ReadOnlySpan<byte> flatKey, ulong block)
    {
        flatKey.CopyTo(destination[..flatKey.Length]);
        BinaryPrimitives.WriteUInt64BigEndian(destination[flatKey.Length..], block);
    }
}
