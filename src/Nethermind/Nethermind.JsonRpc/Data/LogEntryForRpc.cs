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

namespace Nethermind.JsonRpc.Data
{
    public class LogEntryForRpc
    {
        public LogEntryForRpc()
        {
        }

        public LogEntryForRpc(TxReceipt receipt, LogEntry logEntry, int index)
        {
            Removed = false;
            LogIndex = index;
            TransactionIndex = receipt.Index;
            TransactionHash = receipt.TxHash;
            BlockHash = receipt.BlockHash;
            BlockNumber = receipt.BlockNumber;
            Address = logEntry.LoggersAddress;
            Data = logEntry.Data;
            Topics = logEntry.Topics;
        }
        
        public bool? Removed { get; set; }
        public long? LogIndex { get; set; }
        public long? TransactionIndex { get; set; }
        public Keccak TransactionHash { get; set; }
        public Keccak BlockHash { get; set; }
        public long? BlockNumber { get; set; }
        public Address Address { get; set; }
        public byte[] Data { get; set; }
        public Keccak[] Topics { get; set; }

        public LogEntry ToLogEntry()
        {
            return new(Address, Data, Topics);
        }
    }
}
