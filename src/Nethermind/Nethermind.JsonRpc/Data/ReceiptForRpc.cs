// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Data
{
    public class ReceiptForRpc
    {
        public ReceiptForRpc()
        {
        }

        public ReceiptForRpc(Keccak txHash, TxReceipt receipt, TxGasInfo gasInfo, int logIndexStart = 0)
        {
            TransactionHash = txHash;
            TransactionIndex = receipt.Index;
            BlockHash = receipt.BlockHash;
            BlockNumber = receipt.BlockNumber;
            CumulativeGasUsed = receipt.GasUsedTotal;
            GasUsed = receipt.GasUsed;
            EffectiveGasPrice = gasInfo.EffectiveGasPrice;
            DataGasUsed = gasInfo.DataGasUsed;
            DataGasPrice = gasInfo.DataGasPrice;
            From = receipt.Sender;
            To = receipt.Recipient;
            ContractAddress = receipt.ContractAddress;
            Logs = receipt.Logs.Select((l, idx) => new LogEntryForRpc(receipt, l, idx + logIndexStart)).ToArray();
            LogsBloom = receipt.Bloom;
            Root = receipt.PostTransactionState;
            Status = receipt.StatusCode;
            Error = string.IsNullOrEmpty(receipt.Error) ? null : receipt.Error;
            Type = receipt.TxType;
        }

        public Keccak TransactionHash { get; set; }
        public long TransactionIndex { get; set; }
        public Keccak BlockHash { get; set; }
        public long BlockNumber { get; set; }
        public long CumulativeGasUsed { get; set; }
        public long GasUsed { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ulong? DataGasUsed { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public UInt256? DataGasPrice { get; set; }

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
        public string? Error { get; set; }
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
