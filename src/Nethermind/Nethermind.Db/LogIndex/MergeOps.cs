// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage
{
    public enum MergeOp : byte
    {
        /// <summary>
        /// Reorgs from the provided block number,
        /// removing any numbers starting from it.
        /// </summary>
        Reorg = 1,

        /// <summary>
        /// Truncates data up to the provided block number,
        /// removing it and anything coming before.
        /// </summary>
        Truncate = 2
    }

    public static class MergeOps
    {
        public const int Size = BlockNumberSize + 1;

        public static bool Is(MergeOp op, ReadOnlySpan<byte> operand)
        {
            return operand.Length == Size && operand[0] == (byte)op;
        }

        public static bool Is(MergeOp op, ReadOnlySpan<byte> operand, out int fromBlock)
        {
            if (operand.Length == Size && operand[0] == (byte)op)
            {
                fromBlock = ReadLastBlockNumber(operand);
                return true;
            }

            fromBlock = 0;
            return false;
        }

        public static bool IsAny(ReadOnlySpan<byte> operand) =>
            Is(MergeOp.Reorg, operand, out _) ||
            Is(MergeOp.Truncate, operand, out _);

        public static Span<byte> Create(MergeOp op, int fromBlock, Span<byte> buffer)
        {
            Span<byte> dbValue = buffer[..Size];
            dbValue[0] = (byte)op;
            WriteBlockNumber(dbValue[1..], fromBlock);
            return dbValue;
        }

        public static Span<byte> ApplyTo(Span<byte> operand, MergeOp op, int block, bool isBackward)
        {
            // In most cases the searched block will be near or at the end of the operand, if present there
            var i = LastBlockSearch(operand, block, isBackward);

            if (op is MergeOp.Reorg)
            {
                if (i < 0) return Span<byte>.Empty;
                if (i >= operand.Length) return operand;
                return operand[..i];
            }

            if (op is MergeOp.Truncate)
            {
                if (i < 0) return operand;
                if (i >= operand.Length) return Span<byte>.Empty;
                return operand[(i + BlockNumberSize)..];
            }

            throw new ArgumentOutOfRangeException(nameof(op), op, "Unsupported merge operation.");
        }
    }
}
