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

        public ArrayPoolList<byte> FullMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, out bool success) =>
            Merge(key, enumerator, out success);

        public ArrayPoolList<byte> PartialMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, out bool success) =>
            Merge(key, enumerator, out success);

        public SetReceiptsStats GetAndResetStats()
        {
            return Interlocked.Exchange(ref _stats, new());
        }

        private static int BinarySearchForBlock(ReadOnlySpan<byte> data, int target)
        {
            if (data.Length == 0)
                return 0;

            int count = data.Length / sizeof(int);
            int left = 0, right = count - 1;

            // Short circuits in many cases
            if (ReadValLastBlockNum(data) == target)
                return right * BlockNumSize;

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

        private static void ReverseInt32(Span<byte> data) => MemoryMarshal.Cast<byte, int>(data).Reverse();

        private static bool IsReorgFromOp(ReadOnlySpan<byte> operand, out int fromBlock)
        {
            if (operand.Length == BlockNumSize + 1 && operand[0] == ReorgOperator)
            {
                fromBlock = ReadValLastBlockNum(operand);
                return true;
            }

            fromBlock = 0;
            return false;
        }

        private static void ReorgFrom(ArrayPoolList<byte> data, int fromBlock)
        {
            // TODO: detect if revert block is already compressed
            var revertIndex = BinarySearchForBlock(data.AsSpan(), fromBlock);
            data.Truncate(revertIndex >= 0 ? revertIndex : ~revertIndex);
        }

        private static void MergeOperand(ArrayPoolList<byte> result, Span<byte> value, ref int lastBlockNum)
        {
            // Do revert if requested
            if (IsReorgFromOp(value, out int revertBlock))
            {
                ReorgFrom(result, revertBlock);
                return;
            }

            // Reverse if needed (if data is coming from backward sync)
            if (ReadValBlockNum(value) > ReadValLastBlockNum(value))
            {
                ReverseInt32(value);
            }

            var firstNewBlock = ReadValBlockNum(value);

            // Validate we are merging non-intersecting segments - to prevent data corruption
            if (!IsNextBlockNewer(next: result.Count > 1 ? result[^1] : -1, last: lastBlockNum, false))
            {
                // setting success=false instead of throwing during background merge may simply hide the error
                // TODO: check if this can be handled better, for example via paranoid_checks=true
                throw ValidationException($"Invalid order during merge: {lastBlockNum} -> {firstNewBlock}.");
            }

            result.AddRange(value);
        }

        // TODO: avoid array copying in case of a single value?
        private ArrayPoolList<byte> Merge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, out bool success)
        {
            var lastBlockNum = -1;
            var timestamp = Stopwatch.GetTimestamp();

            try
            {
                success = true;
                var isBackwardSync = UseBackwardSyncFor(key);

                // Calculate total length
                var resultLength = 0;
                for (var i = 0; i < enumerator.Count; i++)
                {
                    ReadOnlySpan<byte> value = enumerator.Get(i);

                    if (!IsReorgFromOp(value, out _))
                        resultLength += value.Length;
                }

                // Merge all operands
                var result = new ArrayPoolList<byte>(resultLength);
                if (!isBackwardSync)
                {
                    for (var i = 0; i < enumerator.Count; i++)
                        MergeOperand(result, enumerator.Get(i), ref lastBlockNum);
                }
                else
                {
                    for (var i = enumerator.Count - 1; i >= 0; i--)
                        MergeOperand(result, enumerator.Get(i), ref lastBlockNum);
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
