// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters
{
    public class FilterLog
    {
        public Address Address { get; }
        public Keccak? BlockHash { get; }
        public long BlockNumber { get; }
        public byte[] Data { get; }
        public long LogIndex { get; }
        public bool Removed { get; }
        public Keccak[] Topics { get; }
        public Keccak? TransactionHash { get; }
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

        public FilterLog(long logIndex, long transactionLogIndex, long blockNumber, Keccak? blockHash, int transactionIndex, Keccak? transactionHash, Address address, byte[] data, Keccak[] topics, bool removed = false)
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
