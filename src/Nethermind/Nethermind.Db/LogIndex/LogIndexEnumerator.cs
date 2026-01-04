// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db.LogIndex;

public partial class LogIndexStorage
{
    // TODO: pre-fetch next value?
    // TODO: use ArrayPool for current value
    public sealed class LogIndexEnumerator : IEnumerator<int>
    {
        private const int CompletedIndex = int.MinValue;

        private readonly LogIndexStorage _storage;
        private readonly byte[] _key;
        private readonly int _from;
        private readonly int _to;
        private readonly ISortedView _view;

        private int[]? _value;
        private int _index;

        public LogIndexEnumerator(LogIndexStorage storage, ISortedKeyValueStore db, byte[] key, int from, int to)
        {
            if (from < 0) from = 0;
            if (to < from) throw new ArgumentException("To must be greater or equal to from.", nameof(to));

            _storage = storage;
            _key = key;
            (_from, _to) = (from, to);

            ReadOnlySpan<byte> fromKey = CreateDbKey(_key, SpecialPostfix.BackwardMerge, stackalloc byte[MaxDbKeyLength]);
            ReadOnlySpan<byte> toKey = CreateDbKey(_key, SpecialPostfix.UpperBound, stackalloc byte[MaxDbKeyLength]);
            _view = db.GetViewBetween(fromKey, toKey);
        }

        private bool TryStart()
        {
            ReadOnlySpan<byte> startKey = CreateDbKey(_key, _from, stackalloc byte[MaxDbKeyLength]);

            if (!_view.StartBefore(startKey) && !_view.MoveNext())
                return false;

            SetValue();
            _index = FindStartIndex();

            return true;
        }

        public bool MoveNext()
        {
            if (_index == CompletedIndex)
                return false;

            // Try to initialize if needed
            if (_value is null)
            {
                if (!TryStart())
                {
                    _index = CompletedIndex;
                    return false;
                }

                // Shift the view until we can start at `from`
                while (Current < _from && _view.MoveNext())
                {
                    SetValue();
                    _index = FindStartIndex();
                }

                // If failed to find a matching segment
                if (Current < _from || Current > _to)
                {
                    _index = CompletedIndex;
                    return false;
                }

                return true;
            }

            _index++;

            // Shift the view until we can continue
            while (_index >= _value!.Length && _view.MoveNext())
            {
                SetValue();
                _index = 0;
            }

            // If failed to find a matching segment
            if (Current < _from || Current > _to)
            {
                _index = CompletedIndex;
                return false;
            }

            return true;
        }

        // private bool MoveViewWhileValid()
        // {
        //     while (MoveView())
        //     {
        //         if (!_view.CurrentKey.StartsWith(_key))
        //             return false; // key is outside the range
        //
        //         if (_view.CurrentValue.IsEmpty)
        //             continue; // skip empty view
        //
        //         _value = IsCompressed(_view.CurrentValue, out _)
        //             ? _storage.DecompressDbValue(_view.CurrentValue)
        //             : ReadBlockNums(_view.CurrentValue);
        //
        //         if (Math.Min(_value[0], _value[^1]) > _to)
        //             return false; // next range is after from, break
        //     }
        //
        //     return true;
        // }
        //
        // private bool MoveView()
        // {
        //     bool moved;
        //     if (_value is null)
        //     {
        //         ReadOnlySpan<byte> startKey = CreateDbKey(_key, _from, stackalloc byte[MaxDbKeyLength]);
        //         moved = _view.StartBefore(startKey) || _view.MoveNext();
        //     }
        //     else
        //     {
        //         moved = _view.MoveNext();
        //     }
        //
        //     // TODO: remove check?
        //     if (!moved || _view.CurrentKey.StartsWith(_key))
        //         return false;
        //
        //     if (_view.CurrentValue.IsEmpty)
        //         return true;
        //
        //     _value = IsCompressed(_view.CurrentValue, out _)
        //         ? _storage.DecompressDbValue(_view.CurrentValue)
        //         : ReadBlockNums(_view.CurrentValue);
        //
        //     if (Math.Max(_value[0], _value[^1]) < _from)
        //         return false;
        // }

        private void SetValue()
        {
            _value = IsCompressed(_view.CurrentValue, out _)
                ? _storage.DecompressDbValue(_view.CurrentValue)
                : ReadBlockNums(_view.CurrentValue);

            ReverseBlocksIfNeeded(_value);
        }

        private int FindStartIndex()
        {
            var index = BinarySearch(_value, _from);
            return index >= 0 ? index : ~index;
        }

        public void Reset() => throw new NotSupportedException($"{nameof(LogIndexEnumerator)} can not be reset.");

        public int Current => _value is not null && _index >= 0 && _index < _value.Length ? _value[_index] : -1;
        object? IEnumerator.Current => Current;

        public void Dispose() => _view.Dispose();
    }
}
