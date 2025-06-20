// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

public partial class LogIndexStorage
{
    private enum MergeOp : byte
    {
        /// <summary>
        /// Reorgs from the provided block number,
        /// removing any numbers starting from it.
        /// </summary>
        ReorgOp = 1,

        /// <summary>
        /// Truncates data up to the provided block number,
        /// removing it and anything coming before.
        /// </summary>
        TruncateOp = 2
    }

    private static class MergeOps
    {
        public static bool Is(MergeOp op, ReadOnlySpan<byte> operand)
        {
            return operand.Length == BlockNumSize + 1 && operand[0] == (byte)op;
        }

        public static bool Is(MergeOp op, ReadOnlySpan<byte> operand, out int fromBlock)
        {
            if (operand.Length == BlockNumSize + 1 && operand[0] == (byte)op)
            {
                fromBlock = ReadValLastBlockNum(operand);
                return true;
            }

            fromBlock = 0;
            return false;
        }

        public static bool IsAny(ReadOnlySpan<byte> operand) =>
            Is(MergeOp.ReorgOp, operand, out _) ||
            Is(MergeOp.TruncateOp, operand, out _);

        public static Span<byte> Create(MergeOp op, int fromBlock, Span<byte> buffer)
        {
            Span<byte> dbValue = buffer[..(BlockNumSize + 1)];
            dbValue[0] = (byte)op;
            WriteValBlockNum(dbValue[1..], fromBlock);
            return dbValue;
        }

        // TODO: use ArrayPool?
        public static Span<byte> Create(MergeOp op, int fromBlock)
        {
            var buffer = new byte[BlockNumSize + 1];
            return Create(op, fromBlock, buffer);
        }

        public static Span<byte> ApplyTo(Span<byte> operand, MergeOp op, int block, bool isBackward)
        {
            // In most cases the searched block will be near or at the end of the operand, if present there
            var i = BlockLastSearch(operand, block, isBackward);

            if (op is MergeOp.ReorgOp)
            {
                if (i < 0) return Span<byte>.Empty;
                if (i >= operand.Length) return operand;
                return operand[..i];
            }

            if (op is MergeOp.TruncateOp)
            {
                if (i < 0) return operand;
                if (i >= operand.Length) return Span<byte>.Empty;
                return operand[(i + BlockNumSize)..];
            }

            throw new ArgumentOutOfRangeException(nameof(op), op, "Unsupported merge operation.");
        }

        private static int BlockLastSearch(ReadOnlySpan<byte> operand, int block, bool isBackward)
        {
            if (operand.IsEmpty)
                return 0;

            var i = operand.Length - BlockNumSize;
            for (; i >= 0; i -= BlockNumSize)
            {
                var currentBlock = ReadValBlockNum(operand[i..]);
                if (currentBlock == block)
                    return i;

                if (isBackward)
                {
                    if (currentBlock > block)
                        return i - BlockNumSize;
                }
                else
                {
                    if (currentBlock < block)
                        return i + BlockNumSize;
                }
            }

            return i;
        }

        // TODO: check if MemoryExtensions.BinarySearch<int> can be used and will be faster
        private static int BlockBinarySearch(ReadOnlySpan<byte> data, int target)
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
    }
}
