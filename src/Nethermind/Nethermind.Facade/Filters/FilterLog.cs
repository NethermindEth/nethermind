// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters
{
    public class FilterLog(long logIndex, long blockNumber, ulong blockTimestamp, Hash256 blockHash, int transactionIndex, Hash256 transactionHash, Address address, byte[] data, Hash256[] topics, bool removed = false) : ILogEntry
    {
        public Address Address { get; } = address;
        public Hash256 BlockHash { get; } = blockHash;
        public long BlockNumber { get; } = blockNumber;
        public ulong BlockTimestamp { get; } = blockTimestamp;
        public byte[] Data { get; } = data;
        public long LogIndex { get; } = logIndex;
        public bool Removed { get; } = removed;
        public Hash256[] Topics { get; } = topics;
        public Hash256 TransactionHash { get; } = transactionHash;
        public long TransactionIndex { get; } = transactionIndex;

        public FilterLog(long logIndex, TxReceipt txReceipt, LogEntry logEntry, ulong blockTimestamp, bool removed = false)
            : this(
                logIndex,
                txReceipt.BlockNumber,
                blockTimestamp,
                txReceipt.BlockHash,
                txReceipt.Index,
                txReceipt.TxHash,
                logEntry.Address,
                logEntry.Data,
                logEntry.Topics,
                removed)
        { }
    }
}
