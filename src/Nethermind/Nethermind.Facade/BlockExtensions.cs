// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;

namespace Nethermind.Facade
{
    public static class BlockExtensions
    {
        public static bool TryFindLog(
            this Block block,
            TxReceipt[] receipts,
            LogEntry matchEntry,
            out LogEntry foundEntry,
            FindOrder receiptFindOrder = FindOrder.Descending,
            FindOrder logsFindOrder = FindOrder.Descending,
            IEqualityComparer<LogEntry>? comparer = null) =>
            block.Header.TryFindLog(receipts, matchEntry, out foundEntry, receiptFindOrder, logsFindOrder, comparer);

        public static bool TryFindLog(
            this BlockHeader blockHeader,
            TxReceipt[] receipts,
            LogEntry matchEntry,
            out LogEntry foundEntry,
            FindOrder receiptFindOrder = FindOrder.Descending, // iterating backwards, by default we are interested only in the last one
            FindOrder logsFindOrder = FindOrder.Descending, // iterating backwards, by default we are interested only in the last one
            IEqualityComparer<LogEntry>? comparer = null)
        {
            comparer ??= LogEntryAddressAndTopicsMatchTemplateEqualityComparer.Instance;

            if (blockHeader.Bloom.Matches(matchEntry))
            {
                for (int i = 0; i < receipts.Length; i++)
                {
                    TxReceipt receipt = GetItemAt(receipts, i, receiptFindOrder);
                    if (receipt.Bloom.Matches(matchEntry))
                    {
                        for (int j = 0; j < receipt.Logs.Length; j++)
                        {
                            var receiptLog = GetItemAt(receipt.Logs, j, logsFindOrder);
                            if (comparer.Equals(receiptLog, matchEntry))
                            {
                                foundEntry = receiptLog;
                                return true;
                            }
                        }
                    }
                }
            }

            foundEntry = null;
            return false;
        }

        public static IEnumerable<LogEntry> FindLogs(
            this BlockHeader blockHeader,
            TxReceipt[] receipts,
            LogEntry matchEntry,
            FindOrder receiptFindOrder = FindOrder.Ascending, // iterating forwards, by default we are interested in all items in order of appearance 
            FindOrder logsFindOrder = FindOrder.Ascending, // iterating forwards, by default we are interested in all items in order of appearance
            IEqualityComparer<LogEntry>? comparer = null)
        {
            comparer ??= LogEntryAddressAndTopicsMatchTemplateEqualityComparer.Instance;

            if (blockHeader.Bloom.Matches(matchEntry))
            {
                for (int i = 0; i < receipts.Length; i++)
                {
                    TxReceipt receipt = GetItemAt(receipts, i, receiptFindOrder);
                    if (receipt.Bloom.Matches(matchEntry))
                    {
                        for (int j = 0; j < receipt.Logs.Length; j++)
                        {
                            var receiptLog = GetItemAt(receipt.Logs, j, logsFindOrder);
                            if (comparer.Equals(receiptLog, matchEntry))
                            {
                                yield return receiptLog;
                            }
                        }
                    }
                }
            }
        }

        public static IEnumerable<LogEntry> FindLogs(
            this BlockHeader blockHeader,
            TxReceipt[] receipts,
            LogFilter logFilter,
            FindOrder receiptFindOrder = FindOrder.Ascending, // iterating forwards, by default we are interested in all items in order of appearance 
            FindOrder logsFindOrder = FindOrder.Ascending) // iterating forwards, by default we are interested in all items in order of appearance
        {
            if (logFilter.Matches(blockHeader.Bloom))
            {
                for (int i = 0; i < receipts.Length; i++)
                {
                    TxReceipt receipt = GetItemAt(receipts, i, receiptFindOrder);
                    if (logFilter.Matches(receipt.Bloom))
                    {
                        for (int j = 0; j < receipt.Logs.Length; j++)
                        {
                            var receiptLog = GetItemAt(receipt.Logs, j, logsFindOrder);
                            if (logFilter.Accepts(receiptLog))
                            {
                                yield return receiptLog;
                            }
                        }
                    }
                }
            }
        }

        private static T GetItemAt<T>(T[] items, int index, FindOrder findOrder) =>
            items[findOrder == FindOrder.Ascending ? index : items.Length - index - 1];
    }

    public enum FindOrder
    {
        Ascending,
        Descending
    }
}
