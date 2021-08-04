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

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Data
{
    public class ReceiptForRpc
    {
        public ReceiptForRpc()
        {
        }
       
        public ReceiptForRpc(Keccak txHash, TxReceipt receipt, UInt256? effectiveGasPrice)
        {
            TransactionHash = txHash;
            TransactionIndex = receipt.Index;
            BlockHash = receipt.BlockHash;
            BlockNumber = receipt.BlockNumber;
            CumulativeGasUsed = receipt.GasUsedTotal;
            GasUsed = receipt.GasUsed;
            EffectiveGasPrice = effectiveGasPrice;
            From = receipt.Sender;
            To = receipt.Recipient;
            ContractAddress = receipt.ContractAddress;
            Logs = receipt.Logs.Select((l, idx) => new LogEntryForRpc(receipt, l, idx)).ToArray();
            LogsBloom = receipt.Bloom;
            Root = receipt.PostTransactionState;
            Status = receipt.StatusCode;
            Error = receipt.Error;
            Type = receipt.TxType;
        }
        
        public Keccak TransactionHash { get; set; }
        public long TransactionIndex { get; set; }
        public Keccak BlockHash { get; set; }
        public long BlockNumber { get; set; }
        public long CumulativeGasUsed { get; set; }
        public long GasUsed { get; set; }
        
        public UInt256? EffectiveGasPrice { get; set; }
        public Address From { get; set; }
        
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Address To { get; set; }
        
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Address ContractAddress { get; set; }
        public LogEntryForRpc[] Logs { get; set; }
        public Bloom? LogsBloom { get; set; }
        public Keccak Root { get; set; }
        public long Status { get; set; }
        public string Error { get; set; }
        public TxType Type { get; set; }

        public TxReceipt ToReceipt()
        {
            TxReceipt receipt = new();
            receipt.Bloom = LogsBloom;
            receipt.Error = Error;
            receipt.Index = (int)TransactionIndex;
            receipt.Logs = Logs.Select(l => l.ToLogEntry()).ToArray();
            receipt.Recipient = To;
            receipt.Sender = From;
            receipt.BlockHash = BlockHash;
            receipt.BlockNumber = BlockNumber;
            receipt.ContractAddress = ContractAddress;
            receipt.GasUsed = GasUsed;
            receipt.StatusCode = (byte)Status;
            receipt.TxHash = TransactionHash;
            receipt.GasUsedTotal = CumulativeGasUsed;
            receipt.PostTransactionState = Root;
            receipt.TxType = Type;
            return receipt;
        }
    }
}
