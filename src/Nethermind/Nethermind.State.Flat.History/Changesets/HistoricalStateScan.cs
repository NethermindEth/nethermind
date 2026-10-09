// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Selects v2 account/storage versions and clear markers for an isolated historical-state import.</summary>
/// <remarks>
/// Callbacks must stage into a discardable page batch; commit that batch and the returned cursor atomically.
/// Storage rows retain their write height: the importer must apply clear markers before using the state.
/// The caller owns source availability, retention protection and verification of the imported state.
/// </remarks>
internal sealed class HistoricalStateScan
{
    private readonly ISortedKeyValueStore _source;
    private readonly FlatHistoryColumns _column;
    private readonly ulong _anchor;
    private readonly int _keyLength;
    private readonly int _maxValueLength;
    private readonly bool _isStorageClears;

    public HistoricalStateScan(ISortedKeyValueStore source, HistoryRowFormat format, FlatHistoryColumns column, ulong anchor)
    {
        if (format.IsV3) throw new NotSupportedException("Historical state scans require unwindowed v2 history.");

        (_keyLength, _maxValueLength) = column switch
        {
            FlatHistoryColumns.AccountHistory => (HistoryKeyLayout.AccountKeyLength, 256),
            FlatHistoryColumns.StorageHistory => (BaseFlatPersistence.StorageKeyLength, BaseFlatPersistence.RlpSlotValueBufferSize),
            FlatHistoryColumns.StorageClears => (Hash256.Size, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        };
        _source = source;
        _column = column;
        _anchor = anchor;
        _isStorageClears = column == FlatHistoryColumns.StorageClears;
    }

    /// <summary>Scans at most <paramref name="maxRows"/> raw rows without mutating the input cursor.</summary>
    /// <remarks>
    /// Account/storage callbacks select only the first version at or below the anchor, including tombstones.
    /// Clear callbacks are ascending within each address; the sink overwrites earlier markers with later ones.
    /// An exception leaves no publishable page result; the caller must discard every staged callback.
    /// </remarks>
    public Page ReadPage(Cursor? after, int maxRows, RowHandler stage, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        token.ThrowIfCancellationRequested();
        int rowKeyLength = _keyLength + sizeof(ulong);
        if (after is not null && (after.Anchor != _anchor || after.Column != _column || after.Key.Length != rowKeyLength))
            throw new ArgumentException("The scan cursor belongs to a different historical-state import.", nameof(after));

        Span<byte> previous = stackalloc byte[rowKeyLength];
        Span<byte> lower = stackalloc byte[rowKeyLength + 1];
        Span<byte> upper = stackalloc byte[rowKeyLength + 1];
        upper.Fill(0xFF);
        bool havePrevious = after is not null;
        bool selected = false;
        if (after is not null)
        {
            after.Key.CopyTo(previous);
            after.Key.CopyTo(lower);
            lower[^1] = 0;
            selected = !_isStorageClears && BlockOf(previous) <= _anchor;
        }

        using ISortedView view = _source.GetViewBetween(havePrevious ? lower : [], upper,
            ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead);
        int scanned = 0;
        while (scanned < maxRows)
        {
            token.ThrowIfCancellationRequested();
            if (!view.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                return new Page(havePrevious ? new Cursor(_column, _anchor, previous) : null, scanned, Complete: true);
            }

            ReadOnlySpan<byte> key = view.CurrentKey;
            ReadOnlySpan<byte> value = view.CurrentValue;
            if (key.Length != rowKeyLength || value.Length > _maxValueLength)
                throw new InvalidDataException($"Invalid {_column} row at anchor {_anchor}: key length {key.Length} (expected {rowKeyLength}), value length {value.Length} (maximum {_maxValueLength}).");
            if (havePrevious && key.SequenceCompareTo(previous) <= 0)
                throw new InvalidDataException($"Historical-state rows in {_column} at anchor {_anchor} are not strictly ordered: {Convert.ToHexString(key)} follows {Convert.ToHexString(previous)}.");

            if (!havePrevious || !key[.._keyLength].SequenceEqual(previous[.._keyLength])) selected = false;

            ulong block = BlockOf(key);
            if (block <= _anchor && (_isStorageClears || !selected))
            {
                stage(key[.._keyLength], block, value);
                selected = true;
            }

            key.CopyTo(previous);
            havePrevious = true;
            scanned++;
        }

        token.ThrowIfCancellationRequested();
        return new Page(new Cursor(_column, _anchor, previous), scanned, Complete: false);
    }

    private ulong BlockOf(ReadOnlySpan<byte> key)
    {
        ulong suffix = BinaryPrimitives.ReadUInt64BigEndian(key[_keyLength..]);
        return _isStorageClears ? suffix : ~suffix;
    }

    internal delegate void RowHandler(ReadOnlySpan<byte> key, ulong writtenAt, ReadOnlySpan<byte> value);

    internal readonly record struct Page(Cursor? Position, int Scanned, bool Complete);

    internal sealed class Cursor(FlatHistoryColumns column, ulong anchor, ReadOnlySpan<byte> key)
    {
        private readonly byte[] _key = key.ToArray();

        public FlatHistoryColumns Column { get; } = column;
        public ulong Anchor { get; } = anchor;
        public ReadOnlySpan<byte> Key => _key;
    }
}
