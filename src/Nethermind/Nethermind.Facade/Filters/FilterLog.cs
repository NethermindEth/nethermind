// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters
{
    public class FilterLog
    {
        public Address Address { get; }
        public Commitment BlockHash { get; }
        public long BlockNumber { get; }
        public byte[] Data { get; }
        public long LogIndex { get; }
        public bool Removed { get; }
        public Commitment[] Topics { get; }
        public Commitment TransactionHash { get; }
        public long TransactionIndex { get; }
        public long TransactionLogIndex { get; }

        public FilterLog(long logIndex, long transactionLogIndex, TxReceipt txReceipt, LogEntry logEntry, bool removed = false)
            : this(
                logIndex,
                transactionLogIndex,
                txReceipt.BlockNumber,
                txReceipt.BlockHash,
                txReceipt.Index,
                txReceipt.TxHash,
                logEntry.LoggersAddress,
                logEntry.Data,
                logEntry.Topics,
                removed)
        { }

        public FilterLog(long logIndex, long transactionLogIndex, long blockNumber, Commitment blockHash, int transactionIndex, Commitment transactionHash, Address address, byte[] data, Commitment[] topics, bool removed = false)
        {
            Removed = removed;
            LogIndex = logIndex;
            BlockNumber = blockNumber;
            BlockHash = blockHash;
            TransactionIndex = transactionIndex;
            TransactionLogIndex = transactionLogIndex;
            TransactionHash = transactionHash;
            Address = address;
            Data = data;
            Topics = topics;
        }
    }
}
