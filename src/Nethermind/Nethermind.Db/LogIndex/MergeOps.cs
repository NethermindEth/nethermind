// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

public partial class LogIndexStorage
{
    private static class MergeOps
    {
        /// <summary>
        /// Reorgs from the provided block number,
        /// removing any numbers starting from it.
        /// </summary>
        private const byte ReorgOp = (byte)'<';

        /// <summary>
        /// Truncates data up to the provided block number,
        /// removing it and anything coming before.
        /// </summary>
        private const byte TruncateOp = (byte)'|';

        public static bool IsReorg(ReadOnlySpan<byte> operand, out int fromBlock)
        {
            if (operand.Length == BlockNumSize + 1 && operand[0] == ReorgOp)
            {
                fromBlock = ReadValLastBlockNum(operand);
                return true;
            }

            fromBlock = 0;
            return false;
        }

        public static bool IsTruncate(ReadOnlySpan<byte> operand, out int fromBlock)
        {
            if (operand.Length == BlockNumSize + 1 && operand[0] == TruncateOp)
            {
                fromBlock = ReadValLastBlockNum(operand);
                return true;
            }

            fromBlock = 0;
            return false;
        }

        public static bool IsOp(ReadOnlySpan<byte> operand) =>
            IsReorg(operand, out _) ||
            IsTruncate(operand, out _);

        public static Span<byte> Reorg(int fromBlock, Span<byte> buffer)
        {
            Span<byte> dbValue = buffer[..(BlockNumSize + 1)];
            dbValue[0] = ReorgOp;
            WriteValBlockNum(dbValue[1..], fromBlock);
            return dbValue;
        }

        public static Span<byte> Truncate(int fromBlock, Span<byte> buffer)
        {
            Span<byte> dbValue = buffer[..(BlockNumSize + 1)];
            dbValue[0] = TruncateOp;
            WriteValBlockNum(dbValue[1..], fromBlock);
            return dbValue;
        }

        // TODO: use ArrayPool?
        public static Span<byte> Truncate(int fromBlock)
        {
            var buffer = new byte[BlockNumSize + 1];
            return Truncate(fromBlock, buffer);
        }
    }
}
