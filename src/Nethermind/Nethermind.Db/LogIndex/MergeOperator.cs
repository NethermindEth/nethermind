// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Db;

public partial class LogIndexStorage
{
    // TODO: check if success=false + paranoid_checks=true is better than throwing exception
    private class MergeOperator(LogIndexStorage storage) : IMergeOperator
    {
        private SetReceiptsStats _stats = new();

        public string Name => $"{nameof(LogIndexStorage)}.{nameof(MergeOperator)}";

        public ArrayPoolList<byte>? FullMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator) =>
            Merge(key, enumerator, isPartial: false);

        public ArrayPoolList<byte>? PartialMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator) =>
            Merge(key, enumerator, isPartial: true);

        public SetReceiptsStats GetAndResetStats()
        {
            return Interlocked.Exchange(ref _stats, new());
        }

        private static void ReverseInt32(Span<byte> data) => MemoryMarshal.Cast<byte, int>(data).Reverse();

        private static ArrayPoolList<byte> MergeOperand(ArrayPoolList<byte> result, Span<byte> operand)
        {
            if (MergeOps.IsReorg(operand, out int reorgBlock))
                return ReorgFrom(result, reorgBlock);

            if (MergeOps.IsTruncate(operand))
                return result; // Handled before

            // Reverse if needed (if data is coming from backward sync)
            if (ReadValBlockNum(operand) > ReadValLastBlockNum(operand))
                ReverseInt32(operand);

            AddEnsureSorted(result, operand);
            return result;
        }

        private static ArrayPoolList<byte> ReorgFrom(ArrayPoolList<byte> data, int block)
        {
            // TODO: detect if revert block is already compressed
            ReadOnlySpan<byte> span = data.AsSpan();
            var index = BinarySearchForBlock(span, block);
            if (index < 0) index = ~index;

            data.Truncate(index);
            return data;
        }

        private static Span<byte> TruncateTo(Span<byte> existingValue, int block, bool isBackwardSync)
        {
            // TODO: figure out how can this happen
            if (existingValue.Length == 0)
                return existingValue;

            var index = BinarySearchForBlock(existingValue, block);
            if (index < 0) index = ~index;

            return isBackwardSync
                ? existingValue[..index]
                : existingValue[(index + BlockNumSize)..];
        }

        private static int BinarySearchForBlock(ReadOnlySpan<byte> data, int target)
        {
            if (data.Length == 0)
                return 0;

            int count = data.Length / sizeof(int);
            int left = 0, right = count - 1;

            // Short circuits in some cases
            if (ReadValLastBlockNum(data) == target)
                return right * BlockNumSize;
            if (ReadValBlockNum(data) == target)
                return left * BlockNumSize;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                int offset = mid * 4;

                int value = ReadValBlockNum(data[offset..]);

                if (value == target)
                    return offset;
                if (value < target)
                    left = mid + 1;
                else
                    right = mid - 1;
            }

            return ~(left * BlockNumSize);
        }

        // Validate we are merging non-intersecting segments - to prevent data corruption
        // TODO: remove as it's just a time-consuming validation?
        private static void AddEnsureSorted(ArrayPoolList<byte> result, ReadOnlySpan<byte> value)
        {
            if (value.Length == 0)
                return;

            var nextBlock = value.Length > 0 ? ReadValBlockNum(value) : -1;
            var lastBlock = result.Count > 0 ? ReadValLastBlockNum(result.AsSpan()) : -1;

            if (!IsNextBlockNewer(next: nextBlock, last: lastBlock , false))
                throw ValidationException($"Invalid order during merge: {lastBlock} -> {nextBlock}.");

            result.AddRange(value);
        }

        // TODO: avoid array copying in case of a single value?
        private ArrayPoolList<byte>? Merge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, bool isPartial)
        {
            var timestamp = Stopwatch.GetTimestamp();

            try
            {
                bool isBackwardSync = UseBackwardSyncFor(key);
                Span<byte> existingValue = enumerator.GetExistingValue();

                // Calculate total length, do some validations and apply truncate
                var resultLength = existingValue.Length;
                for (var i = 0; i < enumerator.OperandsCount; i++)
                {
                    ReadOnlySpan<byte> operand = enumerator.GetOperand(i);

                    if (MergeOps.IsOp(operand))
                    {
                        if (isPartial)
                            return null; // Notify RocksDB that we can't partially merge custom ops

                        if (isBackwardSync && MergeOps.IsReorg(operand))
                            throw ValidationException("Encountered reorg during backwards sync.");

                        if (MergeOps.IsTruncate(operand, out int truncateBlock))
                            existingValue = TruncateTo(existingValue, truncateBlock, isBackwardSync);

                        continue;
                    }

                    resultLength += operand.Length;
                }

                // Merge all operands
                var result = new ArrayPoolList<byte>(resultLength);
                if (isBackwardSync)
                {
                    for (var i = enumerator.OperandsCount - 1; i >= 0; i--)
                        result = MergeOperand(result, enumerator.GetOperand(i));

                    AddEnsureSorted(result, existingValue);
                }
                else
                {
                    AddEnsureSorted(result, existingValue);

                    for (var i = 0; i < enumerator.OperandsCount; i++)
                        result = MergeOperand(result, enumerator.GetOperand(i));
                }

                if (result.Count > MaxUncompressedLength)
                    storage.EnqueueCompress(key.ToArray());

                if (result.Count % BlockNumSize != 0)
                    throw ValidationException("Invalid data length post-merge.");

                return result;
            }
            finally
            {
                _stats.InMemoryMerging.Include(Stopwatch.GetElapsedTime(timestamp));
            }
        }
    }
}
