// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;

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
        public const int Size = BlockNumSize + 1;
        public const int Size2 = ValSize + 1;

        public static bool Is(MergeOp op, ReadOnlySpan<byte> operand)
        {
            return operand.Length == Size && operand[0] == (byte)op;
        }

        public static bool Is(MergeOp op, ReadOnlySpan<byte> operand, out int fromBlock)
        {
            if (operand.Length == Size && operand[0] == (byte)op)
            {
                fromBlock = GetValLastBlockNum(operand);
                return true;
            }

            fromBlock = 0;
            return false;
        }

        public static bool Is2(MergeOp op, ReadOnlySpan<byte> operand)
        {
            return operand.Length == Size2 && operand[0] == (byte)op;
        }

        public static bool Is2(MergeOp op, ReadOnlySpan<byte> operand, out long fromBlock)
        {
            if (operand.Length == Size2 && operand[0] == (byte)op)
            {
                fromBlock = GetValLastLogPos(operand);
                return true;
            }

            fromBlock = 0;
            return false;
        }

        public static bool IsAny(ReadOnlySpan<byte> operand) =>
            Is(MergeOp.Reorg, operand) ||
            Is(MergeOp.Truncate, operand);

        public static bool IsAny2(ReadOnlySpan<byte> operand) =>
            Is2(MergeOp.Reorg, operand) ||
            Is2(MergeOp.Truncate, operand);

        public static Span<byte> Create(MergeOp op, int fromBlock, Span<byte> buffer)
        {
            Span<byte> dbValue = buffer[..Size];
            dbValue[0] = (byte)op;
            SetValBlockNum(dbValue[1..], fromBlock);
            return dbValue;
        }

        // TODO: use ArrayPool?
        public static Span<byte> Create(MergeOp op, int fromBlock)
        {
            var buffer = new byte[Size];
            return Create(op, fromBlock, buffer);
        }

        public static Span<byte> Create(MergeOp op, long fromPosition, Span<byte> buffer)
        {
            Span<byte> dbValue = buffer[..Size2];
            dbValue[0] = (byte)op;
            SetValBlockNum(dbValue[1..], fromPosition);
            return dbValue;
        }

        // TODO: use ArrayPool?
        public static Span<byte> Create(MergeOp op, long fromPosition)
        {
            var buffer = new byte[Size2];
            return Create(op, fromPosition, buffer);
        }

        // public static Span<byte> ApplyTo(Span<byte> operand, MergeOp op, int block, bool isBackward)
        // {
        //     // In most cases the searched block will be near or at the end of the operand, if present there
        //     var i = LastBlockSearch(operand, block, isBackward);
        //
        //     if (op is MergeOp.Reorg)
        //     {
        //         if (i < 0) return Span<byte>.Empty;
        //         if (i >= operand.Length) return operand;
        //         return operand[..i];
        //     }
        //
        //     if (op is MergeOp.Truncate)
        //     {
        //         if (i < 0) return operand;
        //         if (i >= operand.Length) return Span<byte>.Empty;
        //         return operand[(i + BlockNumSize)..];
        //     }
        //
        //     throw new ArgumentOutOfRangeException(nameof(op), op, "Unsupported merge operation.");
        // }

        public static Span<byte> ApplyTo(Span<byte> operand, MergeOp op, long pos, bool isBackward)
        {
            // In most cases the searched block will be near or at the end of the operand, if present there
            var i = LastValueSearch(operand, pos, isBackward);

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
                return operand[(i + ValSize)..];
            }

            throw new ArgumentOutOfRangeException(nameof(op), op, "Unsupported merge operation.");
        }

        public static bool TryParse(string input, out Span<byte> mergeOp)
        {
            mergeOp = default;

            var parts = input.Split(':');

            if (parts[0].Any(char.IsDigit))
            {
                return false; // enum value should have string representation
            }

            if (parts.Length == 2)
            {
                if (!Enum.TryParse(parts[0], out MergeOp op)) return false;
                if (!long.TryParse(parts[1], out var pos)) return false;

                mergeOp = Create(op, pos);
                return true;
            }

            if (parts.Length == 3)
            {
                if (!Enum.TryParse(parts[0], out MergeOp op)) return false;
                if (!int.TryParse(parts[1], out var posBlock)) return false;
                if (!int.TryParse(parts[2], out var posIndex)) return false;

                mergeOp = Create(op, new LogPosition(posBlock, posIndex));
                return true;
            }

            return false;
        }

        public static Span<byte> Parse(string input) => TryParse(input, out Span<byte> op)
            ? op
            : throw new FormatException($"Invalid {nameof(MergeOps)} string: \"{input}\"");
    }
}
