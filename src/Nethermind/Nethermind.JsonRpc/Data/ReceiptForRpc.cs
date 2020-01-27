//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Data
{
    public class ReceiptForRpc
    {
        public ReceiptForRpc()
        {
        }
       
        public ReceiptForRpc(Keccak txHash, TxReceipt receipt)
        {
            TransactionHash = txHash;
            TransactionIndex = receipt.Index;
            BlockHash = receipt.BlockHash;
            BlockNumber = receipt.BlockNumber;
            CumulativeGasUsed = receipt.GasUsedTotal;
            GasUsed = receipt.GasUsed;
            From = receipt.Sender;
            To = receipt.Recipient;
            ContractAddress = receipt.ContractAddress;
            Logs = receipt.Logs.Select((l, idx) => new LogEntryForRpc(receipt, l, idx)).ToArray();
            LogsBloom = receipt.Bloom;
            Root = receipt.PostTransactionState;
            Status = receipt.StatusCode;
            Error = receipt.Error;
        }
        
        public Keccak TransactionHash { get; set; }
        public long TransactionIndex { get; set; }
        public Keccak BlockHash { get; set; }
        public long BlockNumber { get; set; }
        public long CumulativeGasUsed { get; set; }
        public long GasUsed { get; set; }
        public Address From { get; set; }
        
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Address To { get; set; }
        
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Address ContractAddress { get; set; }
        public LogEntryForRpc[] Logs { get; set; }
        public Bloom LogsBloom { get; set; }
        public Keccak Root { get; set; }
        public long Status { get; set; }
        public string Error { get; set; }
    }
}