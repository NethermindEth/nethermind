// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Db.LogIndex;

public partial class LogIndexStorage
{
    internal sealed class ReverseLogIndexEnumerator : IEnumerator<int>
    {
        private const int CompletedIndex = int.MinValue;
        private readonly CompressionAlgorithm _compressionAlgorithm;
        private readonly byte[] _key;
        private readonly int _from;
        private readonly int _to;
        private readonly IReverseSortedView _view;
        private ArrayPoolList<int>? _value;
        private int _index;

        private ReverseLogIndexEnumerator(IReverseSortedView view, CompressionAlgorithm compressionAlgorithm, byte[] key, int from, int to)
        {
            _view = view;
            _compressionAlgorithm = compressionAlgorithm;
            _key = key;
            (_from, _to) = (from, to);
        }

        internal static ReverseLogIndexEnumerator? TryCreate(ISortedKeyValueStore db, CompressionAlgorithm compressionAlgorithm, byte[] key, int from, int to)
        {
            if (from < 0) from = 0;
            if (to < from) throw new ArgumentException("To must be greater or equal to from.", nameof(to));

            ReadOnlySpan<byte> fromKey = CreateDbKey(key, Postfix.BackwardMerge, stackalloc byte[MaxDbKeyLength]);
            ReadOnlySpan<byte> toKey = CreateDbKey(key, Postfix.UpperBound, stackalloc byte[MaxDbKeyLength]);
            ISortedView view = db.GetViewBetween(fromKey, toKey);
            if (view is IReverseSortedView reverse) return new(reverse, compressionAlgorithm, key, from, to);
            view.Dispose();
            return null;
        }

        public bool MoveNext()
        {
            if (_index == CompletedIndex) return false;
            if (_value is null)
            {
                ReadOnlySpan<byte> endKey = CreateDbKey(_key, Postfix.UpperBound, stackalloc byte[MaxDbKeyLength]);
                if (!_view.StartBefore(endKey)) return Complete();
            }
            else
            {
                if (--_index >= 0) return Current >= _from || Complete();
                if (!_view.MovePrevious()) return Complete();
            }

            ReadOnlySpan<byte> cursorKey = CreateDbKey(_key, _to, stackalloc byte[MaxDbKeyLength]);
            do
            {
                // The forward transient key sorts last regardless of the block numbers it contains.
                if (_view.CurrentKey.SequenceCompareTo(cursorKey) > 0 && !_view.CurrentKey.EndsWith(Postfix.ForwardMerge)) continue;
                SetValue();
                int index = _value!.AsSpan().BinarySearch(_to);
                _index = index >= 0 ? index : ~index - 1;
                if (_index >= 0) return Current >= _from || Complete();
            }
            while (_view.MovePrevious());

            return Complete();
        }

        private bool Complete()
        {
            _index = CompletedIndex;
            return false;
        }

        private void SetValue()
        {
            _value?.Dispose();
            ReadOnlySpan<byte> viewValue = _view.CurrentValue;
            if (IsCompressed(viewValue, out int length))
            {
                // +1 fixes TurboPFor reading outside of array bounds
                _value = new(capacity: length + 1, count: length);
                DecompressDbValue(_compressionAlgorithm, viewValue, _value.AsSpan());
            }
            else
            {
                length = viewValue.Length / BlockNumberSize;
                _value = new(capacity: length, count: length);
                ReadBlockNumbers(viewValue, _value.AsSpan());
            }
            ReverseBlocksIfNeeded(_value.AsSpan());
        }

        public void Reset() => throw new NotSupportedException($"{nameof(ReverseLogIndexEnumerator)} can not be reset.");
        public int Current => _value is not null && _index >= 0 && _index < _value.Count ? _value[_index] : -1;
        object? IEnumerator.Current => Current;

        public void Dispose()
        {
            _view.Dispose();
            _value?.Dispose();
        }
    }
}
