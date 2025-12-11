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
        public const int Size = ValueSize + 1;

        public static bool Is(MergeOp op, ReadOnlySpan<byte> operand)
        {
            return operand.Length == Size && operand[0] == (byte)op;
        }

        public static bool Is(MergeOp op, ReadOnlySpan<byte> operand, out LogPosition position)
        {
            if (operand.Length == Size && operand[0] == (byte)op)
            {
                position = GetLastLogPosition(operand);
                return true;
            }

            position = 0;
            return false;
        }

        public static bool IsAny(ReadOnlySpan<byte> operand) =>
            Is(MergeOp.Reorg, operand) ||
            Is(MergeOp.Truncate, operand);

        public static Span<byte> Create(MergeOp op, LogPosition position, Span<byte> buffer)
        {
            Span<byte> dbValue = buffer[..Size];
            dbValue[0] = (byte)op;
            SetFirstLogPosition(dbValue[1..], position);
            return dbValue;
        }

        // TODO: use ArrayPool?
        public static Span<byte> Create(MergeOp op, LogPosition position)
        {
            var buffer = new byte[Size];
            return Create(op, position, buffer);
        }

        public static Span<byte> ApplyTo(Span<byte> operand, MergeOp op, LogPosition position, bool isBackward)
        {
            // In most cases the searched block will be near or at the end of the operand, if present there
            var i = LastValueSearch(operand, position, isBackward);

            return op switch
            {
                MergeOp.Reorg when i < 0 => Span<byte>.Empty,
                MergeOp.Reorg when i >= operand.Length => operand,
                MergeOp.Reorg => operand[..i],

                MergeOp.Truncate when i < 0 => operand,
                MergeOp.Truncate when i >= operand.Length => Span<byte>.Empty,
                MergeOp.Truncate => operand[(i + ValueSize)..],

                _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unsupported merge operation.")
            };
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
