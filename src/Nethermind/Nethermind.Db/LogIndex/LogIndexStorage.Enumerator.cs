// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Db.LogIndex;

public partial class LogIndexStorage
{
    // TODO: pre-fetch next value?
    /// <summary>
    /// Enumerates block numbers from <see cref="LogIndexStorage"/> for the given <c>key</c>,
    /// within the specified <c>from</c>/<c>to</c> range.
    /// </summary>
    public sealed class LogIndexEnumerator : ILogIndexEnumerator
    {
        private const int CompletedIndex = int.MinValue;

        private readonly CompressionAlgorithm _compressionAlgorithm;
        private readonly byte[] _key;
        private readonly int _from;
        private readonly int _to;
        private readonly ISortedView _view;

        private ArrayPoolList<int>? _value;
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
            while (_index >= _value!.Count && _view.MoveNext())
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

        public bool MoveTo(int target)
        {
            if (_index >= 0 && _value is not null)
            {
                ReadOnlySpan<int> blocks = _value.AsSpan();
                int index = _index;

                if (blocks[index] >= target)
                    return true;

                if (target <= blocks[^1])
                {
                    index++;
                    if (blocks[index] < target)
                        index = LowerBound(blocks, index + 1, target);

                    if (blocks[index] <= _to)
                    {
                        _index = index;
                        return true;
                    }

                    _index = CompletedIndex;
                    return false;
                }
            }

            return MoveToLaterChunk(target);
        }

        private bool MoveToLaterChunk(int target)
        {
            if (_index == CompletedIndex)
                return false;

            if (_value is null)
                throw new InvalidOperationException($"{nameof(MoveNext)} must return true before {nameof(MoveTo)} is called.");

            if (target > _to)
            {
                _index = CompletedIndex;
                return false;
            }

            // The adjacent chunk first keeps dense postings on the sequential path
            if (!_view.MoveNext())
            {
                _index = CompletedIndex;
                return false;
            }

            SetValue();

            if (_value.Count == 0 || _value[^1] < target)
            {
                ReadOnlySpan<byte> skippedKey = _view.CurrentKey;

                if (skippedKey.EndsWith(Postfix.ForwardMerge))
                {
                    _index = CompletedIndex;
                    return false;
                }

                int skippedKeySuffix = ReadKeySuffix(skippedKey);

                // Lands on the last finalized chunk starting at or before target, which is never behind the current one
                if (!_view.StartBefore(CreateDbKey(_key, target, stackalloc byte[MaxDbKeyLength])))
                {
                    _index = CompletedIndex;
                    return false;
                }

                if (ReadKeySuffix(_view.CurrentKey) != skippedKeySuffix)
                    SetValue();
            }

            _index = BinarySearch(_value.AsSpan(), target);

            // The forward-merge key does not encode its first block, so its values can still start below target
            while (_index >= _value.Count && _view.MoveNext())
            {
                SetValue();
                _index = BinarySearch(_value.AsSpan(), target);
            }

            return CheckWithinRange();
        }

        private bool CheckWithinRange()
        {
            if (IsWithinRange())
                return true;

            _index = CompletedIndex;
            return false;
        }

        private static int ReadKeySuffix(ReadOnlySpan<byte> dbKey) => BinaryPrimitives.ReadInt32BigEndian(dbKey[^BlockNumberSize..]);

        /// <summary>
        /// Finds the first index at or after <paramref name="start"/> with a value not less than <paramref name="target"/>,
        /// probing exponentially growing steps so that close targets cost a few comparisons.
        /// The value before <paramref name="start"/> must be less than <paramref name="target"/>.
        /// </summary>
        private static int LowerBound(ReadOnlySpan<int> blocks, int start, int target)
        {
            int low = start - 1;
            int step = 1;
            int high;

            while (true)
            {
                int probe = low + step;
                if (probe >= blocks.Length || blocks[probe] >= target)
                {
                    high = Math.Min(probe, blocks.Length);
                    break;
                }

                low = probe;
                step <<= 1;
            }

            return low + 1 + BinarySearch(blocks[(low + 1)..high], target);
        }

        private bool TryStartView()
        {
            ReadOnlySpan<byte> startKey = CreateDbKey(_key, _from, stackalloc byte[MaxDbKeyLength]);

            // need to start either just before the startKey
            // or at the beginning of a view otherwise
            return _view.StartBefore(startKey) || _view.MoveNext();
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

        private int FindFromIndex()
        {
            int index = BinarySearch(_value!.AsSpan(), _from);
            return index >= 0 ? index : ~index;
        }

        public void Reset() => throw new NotSupportedException($"{nameof(LogIndexEnumerator)} can not be reset.");

        public int Current => _value is not null && _index >= 0 && _index < _value.Count ? _value[_index] : -1;
        object? IEnumerator.Current => Current;

        public void Dispose()
        {
            _view.Dispose();
            _value?.Dispose();
        }
    }
}
