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

        // It's safe to use MemoryMarshal regardless of endianness here
        private static void ReverseBlocks(Span<byte> data) => MemoryMarshal.Cast<byte, int>(data).Reverse();

        private static ArrayPoolList<byte> MergeOperand(ArrayPoolList<byte> result, Span<byte> value, bool isBackwardSync)
        {
            if (MergeOps.IsReorg(value, out int reorgBlock))
                return ReorgFrom(result, reorgBlock);

            if (MergeOps.IsTruncate(value, out int truncateBlock))
                return TruncateTo(result, truncateBlock, isBackwardSync);

            result.AddRange(value);
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

        private static ArrayPoolList<byte> TruncateTo(ArrayPoolList<byte> data, int block, bool isBackwardSync)
        {
            ReadOnlySpan<byte> dataSpan = data.AsSpan();
            var index = BinarySearchForBlock(dataSpan, block);
            if (index < 0) index = ~index;

            index += BlockNumSize;
            using (data) return new(dataSpan[index..]);
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

        // TODO: avoid array copying in case of a single value?
        private ArrayPoolList<byte>? Merge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, bool isPartial)
        {
            var timestamp = Stopwatch.GetTimestamp();

            try
            {
                var isBackwardSync = UseBackwardSyncFor(key);

                // Calculate total length and do some validations
                var resultLength = 0;
                var prevBlock = -1;
                for (var i = 0; i < enumerator.Count; i++)
                {
                    ReadOnlySpan<byte> operand = enumerator.Get(i);

                    if (MergeOps.IsOp(operand))
                    {
                        if (isPartial) return null; // Partially merging custom ops is not supported
                        continue;
                    }

                    var nextBlock = ReadValBlockNum(operand);
                    if (!IsNextBlockNewer(nextBlock, prevBlock, isBackwardSync))
                    {
                        throw ValidationException($"Invalid order during merge: {prevBlock} -> {nextBlock}.");
                    }

                    resultLength += operand.Length;
                    prevBlock = ReadValLastBlockNum(operand);
                }

                // Merge all operands
                var result = new ArrayPoolList<byte>(resultLength);
                for (var i = 0; i < enumerator.Count; i++)
                    result = MergeOperand(result, enumerator.Get(i), isBackwardSync);

                // Revert if backward sync
                if (isBackwardSync)
                {
                    var revertIndex = enumerator.HasExistingValue ? enumerator.Get(0).Length : 0;
                    ReverseBlocks(result.AsSpan()[revertIndex..]);
                }

                if (result.Count > MaxUncompressedLength)
                    storage.EnqueueCompress(key.ToArray());

                return result;
            }
            finally
            {
                _stats.InMemoryMerging.Include(Stopwatch.GetElapsedTime(timestamp));
            }
        }
    }
}
