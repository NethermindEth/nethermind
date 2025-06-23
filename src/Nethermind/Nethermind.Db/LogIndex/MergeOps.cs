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
            var i = LastBlockSearch(operand, block, isBackward);

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
    }
}
