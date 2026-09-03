// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.State.Flat.History.Proofs;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class SeriesReader(IColumnsDb<FlatHistoryColumns> history)
{
    public const ulong Window = 16_384;

    private readonly ISortedKeyValueStore _accountColumn = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.AccountCommitments);
    private readonly ISortedKeyValueStore _storageColumn = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageCommitments);
    private readonly CommitmentStore _accountStore = new(history.GetColumnDb(FlatHistoryColumns.AccountCommitments));
    private readonly CommitmentStore _storageStore = new(history.GetColumnDb(FlatHistoryColumns.StorageCommitments));

    public NodeSeriesState ReadStart(in SeriesKey key, ulong from)
    {
        NodeSeriesState state = new();
        Span<byte> prefix = stackalloc byte[SeriesKey.MaxKeyLength];
        int prefixLength = key.WritePrefix(prefix);
        CommitmentStore store = key.Column == FlatHistoryColumns.StorageCommitments ? _storageStore : _accountStore;
        using CommitmentStore.RowChain chain = store.OpenAtOrBelow(prefix[..prefixLength], from);
        if (chain.MoveNext()) state.MaterializeStart(chain);
        return state;
    }

    public SeriesCursor Open(in SeriesKey key, ulong fromExclusive, ulong toInclusive, int maxRowsBuffered, CancellationToken token)
    {
        ISortedKeyValueStore column = key.Column == FlatHistoryColumns.StorageCommitments ? _storageColumn : _accountColumn;
        byte[] prefix = new byte[SeriesKey.MaxPrefixLength];
        int prefixLength = key.WritePrefix(prefix);
        return new SeriesCursor(column, prefix[..prefixLength], fromExclusive, toInclusive, maxRowsBuffered, token);
    }

    public sealed class SeriesCursor(ISortedKeyValueStore column, byte[] prefix, ulong fromExclusive, ulong toInclusive, int maxRowsBuffered, CancellationToken token) : IDisposable
    {
        public const int MinRowsBuffered = 64;
        private const ulong MinWindow = 64;

        private readonly RowArena _arena = new();
        private readonly int _maxRows = Math.Max(MinRowsBuffered, maxRowsBuffered);
        private readonly ArrayPoolList<(ulong Block, int Offset, int Length)> _window = new(Math.Min(Math.Max(MinRowsBuffered, maxRowsBuffered), 4096));
        private ulong _nextLow = fromExclusive + 1;
        private ulong _windowSize = Window;
        private int _position = -1;
        private bool _exhausted = fromExclusive >= toInclusive;

        public ulong Block => _window[_position].Block;

        public ReadOnlySpan<byte> Row
        {
            get
            {
                (ulong _, int offset, int length) = _window[_position];
                return _arena.Slice(offset, length);
            }
        }

        public bool MoveNext()
        {
            if (_position + 1 < _window.Count)
            {
                _position++;
                return true;
            }

            while (!_exhausted)
            {
                token.ThrowIfCancellationRequested();
                if (_nextLow > toInclusive)
                {
                    _exhausted = true;
                    break;
                }

                ulong hi = toInclusive - _nextLow < _windowSize - 1 ? toInclusive : _nextLow + _windowSize - 1;
                if (!ReadDescending(_nextLow, hi))
                {
                    if (_windowSize > MinWindow)
                    {
                        _windowSize = Math.Max(MinWindow, _windowSize / 2);
                        continue;
                    }

                    throw new InvalidDataException("A commitment series holds more rows per block window than the walk may buffer; the column is corrupt.");
                }

                if (hi == toInclusive) _exhausted = true;
                else _nextLow = hi + 1;

                if (_window.Count > 0)
                {
                    for (int left = 0, right = _window.Count - 1; left < right; left++, right--)
                    {
                        (ulong Block, int Offset, int Length) swap = _window[left];
                        _window[left] = _window[right];
                        _window[right] = swap;
                    }

                    _position = 0;
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            _window.Dispose();
            _arena.Dispose();
        }

        private bool ReadDescending(ulong lo, ulong hi)
        {
            _window.Clear();
            _arena.Clear();
            Span<byte> lower = stackalloc byte[SeriesKey.MaxKeyLength];
            int lowerLength = CommitmentKeyLayout.WriteSeekKey(lower, prefix, hi);
            Span<byte> upper = stackalloc byte[SeriesKey.MaxKeyLength];
            int upperLength = CommitmentKeyLayout.WriteSeekKey(upper, prefix, lo - 1);

            using ISortedView view = column.GetViewBetween(lower[..lowerLength], upper[..upperLength], ReadFlags.HintCacheMiss);
            while (view.MoveNext())
            {
                if (view.CurrentKey.Length != lowerLength) continue;

                ulong block = CommitmentKeyLayout.ReadSuffix(view.CurrentKey);
                if (block < lo || block > hi) continue;

                if (_window.Count >= _maxRows) return false;

                ReadOnlySpan<byte> row = view.CurrentValue;
                _window.Add((block, _arena.Append(row), row.Length));
            }

            return true;
        }
    }
}
