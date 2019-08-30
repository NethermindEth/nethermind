/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class FilterLog
    {
        public bool Removed { get; }
        public UInt256 LogIndex { get; }
        public long BlockNumber { get; }
        public Keccak BlockHash { get; }
        public Keccak TransactionHash { get; }
        public UInt256 TransactionIndex { get; }
        public Address Address { get; }
        public byte[] Data { get; }
        public Keccak[] Topics { get; }

        public FilterLog(long logIndex, TxReceipt txReceipt, LogEntry logEntry) 
            : this((UInt256) logIndex, txReceipt, logEntry) { }
        
        public FilterLog(UInt256 logIndex, TxReceipt txReceipt, LogEntry logEntry)
            : this(
                logIndex,
                txReceipt.BlockNumber,
                txReceipt.BlockHash,
                (UInt256) txReceipt.Index,
                txReceipt.TxHash,
                logEntry.LoggersAddress,
                logEntry.Data,
                logEntry.Topics) { }

        public FilterLog(UInt256 logIndex, long blockNumber, Keccak blockHash, UInt256 transactionIndex, Keccak transactionHash, Address address, byte[] data, Keccak[] topics, bool removed = false)
        {
            Removed = removed;
            LogIndex = logIndex;
            BlockNumber = blockNumber;
            BlockHash = blockHash;
            TransactionIndex = transactionIndex;
            TransactionHash = transactionHash;
            Address = address;
            Data = data;
            Topics = topics;
        }
    }
}