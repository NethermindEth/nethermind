// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db.LogIndex;

public partial class LogIndexStorage
{
    public class EmptyEnumerator : IEnumerator<int>
    {
        public static EmptyEnumerator Instance { get; } = new();

        public int Current => 0;
        object? IEnumerator.Current => Current;

        private EmptyEnumerator() { }

        public bool MoveNext() => false;
        public void Reset() { }
        public void Dispose() { }
    }

    // TODO: pre-fetch next value?
    // TODO: use ArrayPool for current value
    public sealed class LogIndexEnumerator : IEnumerator<int>
    {
        private readonly LogIndexStorage _storage;
        private readonly byte[] _key;
        private readonly int _from;
        private readonly ISortedView _view;

        private int[]? _value;
        private int _index;

        public LogIndexEnumerator(LogIndexStorage storage, ISortedKeyValueStore db, byte[] key, int to, int from)
        {
            _storage = storage;
            _key = key;
            _from = from;

            if (from < 0) from = 0;
            if (to < from) return;

            ReadOnlySpan<byte> fromKey = CreateDbKey(_key, SpecialPostfix.BackwardMerge, stackalloc byte[MaxDbKeyLength]);
            ReadOnlySpan<byte> toKey = CreateDbKey(_key, SpecialPostfix.UpperBound, stackalloc byte[MaxDbKeyLength]);
            _view = db.GetViewBetween(fromKey, toKey);
        }

        public bool MoveNext()
        {
            // Simple case - just shift the array index
            if (_value is not null && _index < _value.Length - 1)
            {
                _index++;
                return true;
            }

            // Complex case - move view to the first/next value
            bool success;

            if (_value is null)
            {
                ReadOnlySpan<byte> startKey = CreateDbKey(_key, _from, stackalloc byte[MaxDbKeyLength]);
                success = _view.StartBefore(startKey) || _view.MoveNext();
            }
            else
            {
                _index = 0;
                success = _view.MoveNext() && _view.CurrentKey.StartsWith(_key);
            }

            if (success)
            {
                _value = IsCompressed(_view.CurrentValue, out _)
                    ? _storage.DecompressDbValue(_view.CurrentValue)
                    : ReadBlockNums(_view.CurrentValue);
            }

            return success;
        }

        public void Reset() => throw new NotSupportedException($"{nameof(LogIndexEnumerator)} can not be reset.");

        public int Current => _value is not null && _index < _value.Length ? _value[_index] : -1;
        object? IEnumerator.Current => Current;

        public void Dispose() => _view.Dispose();
    }
}
