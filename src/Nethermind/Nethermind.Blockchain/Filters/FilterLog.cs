//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters
{
    public class FilterLog
    {
        public Address Address { get; }
        public Keccak BlockHash { get; }
        public long BlockNumber { get; }
        public byte[] Data { get; }
        public long LogIndex { get; }
        public bool Removed { get; }
        public Keccak[] Topics { get; }
        public Keccak TransactionHash { get; }
        public long TransactionIndex { get; }
        public long TransactionLogIndex { get; }
        
        public FilterLog(long logIndex, long transactionLogIndex, TxReceipt txReceipt, LogEntry logEntry)
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
                txReceipt.Removed) { }

        public FilterLog(long logIndex, long transactionLogIndex, long blockNumber, Keccak blockHash, int transactionIndex, Keccak transactionHash, Address address, byte[] data, Keccak[] topics, bool removed = false)
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
