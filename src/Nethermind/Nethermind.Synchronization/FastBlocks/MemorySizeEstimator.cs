// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    internal static class MemorySizeEstimator
    {
        public static long EstimateSize(Block? block)
        {
            if (block is null)
            {
                return 0;
            }

            long estimate = 80L;
            estimate += EstimateSize(block.Header);
            estimate += EstimateSize(block.Body);
            return estimate;
        }

        public static long EstimateSize(TxReceipt? txReceipt)
        {
            if (txReceipt is null)
            {
                return 0;
            }

            long estimate = 320L;
            foreach (LogEntry? logEntry in txReceipt.Logs!)
            {
                estimate += logEntry.Data.Length + logEntry.Topics.Length * 80;
            }

            return estimate;
        }

        public static long EstimateSize(BlockBody? blockBody)
        {
            if (blockBody is null)
            {
                return 0;
            }

            long estimate = 80L;
            estimate += blockBody.Transactions.Length * 8L;
            estimate += blockBody.Uncles.Length * 8L;

            foreach (Transaction transaction in blockBody.Transactions)
            {
                estimate += EstimateSize(transaction);
            }

            foreach (BlockHeader header in blockBody.Uncles)
            {
                estimate += EstimateSize(header);
            }

            return estimate;
        }

        /// <summary>
        /// Rough header memory size estimator
        /// </summary>
        /// <param name="header"></param>
        /// <returns></returns>
        public static long EstimateSize(BlockHeader? header)
        {
            if (header is null)
            {
                return 8;
            }

            return 1212 + (header.ExtraData?.Length ?? 0);
        }

        public static long EstimateSize(Transaction? transaction)
        {
            if (transaction is null)
            {
                return 8;
            }

            return 408 + (transaction.Data?.Length ?? 0);
        }
    }
}
