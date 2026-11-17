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
    /// <summary>
    /// Enumerates block numbers from <see cref="LogIndexStorage"/> for the given <c>key</c>,
    /// within the specified <c>from</c>/<c>to</c> range.
    /// </summary>
    public sealed class LogIndexEnumerator : IEnumerator<int>
    {
        private const int CompletedIndex = int.MinValue;

        private readonly CompressionAlgorithm _compressionAlgorithm;
        private readonly byte[] _key;
        private readonly int _from;
        private readonly int _to;
        private readonly ISortedView _view;

        private int[]? _value;
        private int _count;
        private int _index;

        public LogIndexEnumerator(ISortedKeyValueStore db, CompressionAlgorithm compressionAlgorithm, byte[] key, int from, int to)
        {
            if (from < 0) from = 0;
            if (to < from) throw new ArgumentException("To must be greater or equal to from.", nameof(to));

            _key = key;
            (_from, _to) = (from, to);
            _compressionAlgorithm = compressionAlgorithm;

            ReadOnlySpan<byte> fromKey = CreateDbKey(_key, Postfix.BackwardMerge, stackalloc byte[MaxDbKeyLength]);
            ReadOnlySpan<byte> toKey = CreateDbKey(_key, Postfix.UpperBound, stackalloc byte[MaxDbKeyLength]);
            _view = db.GetViewBetween(fromKey, toKey);
        }

        private bool IsWithinRange()
        {
            int current = Current;
            return current >= _from && current <= _to;
        }

        public bool MoveNext() => _index != CompletedIndex && (_value is null ? TryStart() : TryMove());

        private bool TryStart()
        {
            if (TryStartView())
            {
                SetValue();
                _index = FindFromIndex();
            }
            else // End immediately
            {
                _index = CompletedIndex;
                return false;
            }

            // Shift the view until we can start at `from`
            while (Current < _from && _view.MoveNext())
            {
                SetValue();
                _index = FindFromIndex();
            }

            // Check if the end of the range is reached
            if (!IsWithinRange())
            {
                _index = CompletedIndex;
                return false;
            }

            return true;
        }

        private bool TryMove()
        {
            _index++;

            // Shift the view until we can continue
            while (_index >= _count && _view.MoveNext())
            {
                SetValue();
                _index = 0;
            }

            // Check if the end of the range is reached
            if (!IsWithinRange())
            {
                _index = CompletedIndex;
                return false;
            }

            return true;
        }

        private bool TryStartView()
        {
            ReadOnlySpan<byte> startKey = CreateDbKey(_key, _from, stackalloc byte[MaxDbKeyLength]);

            // need to start either just before the startKey
            // or at the beginning of a view otherwise
            return _view.StartBefore(startKey) || _view.MoveNext();
        }

        private void SetValue() => _count = ReadDbValue(_compressionAlgorithm, _view.CurrentValue, ref _value);

        private int FindFromIndex()
        {
            int index = BinarySearch(_value.AsSpan(0, _count), _from);
            return index >= 0 ? index : ~index;
        }

        public void Reset() => throw new NotSupportedException($"{nameof(LogIndexEnumerator)} can not be reset.");

        public int Current => _value is not null && _index >= 0 && _index < _count ? _value[_index] : -1;
        object? IEnumerator.Current => Current;

        public void Dispose()
        {
            _view.Dispose();
            _index = CompletedIndex;
            ReturnDbValueBuffer(_value);
            _value = null;
        }
    }
}
