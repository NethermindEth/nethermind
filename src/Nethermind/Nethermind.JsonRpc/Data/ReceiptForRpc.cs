// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data
{
    public class ReceiptForRpc
    {
        public ReceiptForRpc()
        {
        }

        public ReceiptForRpc(Hash256 txHash, TxReceipt receipt, ulong blockTimestamp, TxGasInfo gasInfo, int logIndexStart = 0)
        {
            TransactionHash = txHash;
            TransactionIndex = receipt.Index;
            BlockHash = receipt.BlockHash;
            BlockNumber = receipt.BlockNumber;
            CumulativeGasUsed = receipt.GasUsedTotal;
            GasUsed = receipt.GasUsed;
            EffectiveGasPrice = gasInfo.EffectiveGasPrice ?? receipt.EffectiveGasPrice;
            BlobGasUsed = gasInfo.BlobGasUsed;
            BlobGasPrice = gasInfo.BlobGasPrice;
            From = receipt.Sender;
            To = receipt.Recipient;
            ContractAddress = receipt.ContractAddress;
            Logs = (receipt.Logs ?? []).Select((l, idx) => new LogEntryForRpc(receipt, l, blockTimestamp, idx + logIndexStart)).ToArray();
            LogsBloom = receipt.Bloom;
            Root = receipt.PostTransactionState;
            Status = receipt.PostTransactionState is null ? receipt.StatusCode : null;
            Type = receipt.TxType;

            // EIP-8141: surface the gas payer and per-frame results for frame transactions.
            if (receipt.TxType == TxType.FrameTx)
            {
                Payer = receipt.Payer;
                FrameReceipts = (receipt.FrameReceipts ?? []).Select(static f => new FrameReceiptForRpc(f)).ToArray();
            }
        }

        public Hash256 TransactionHash { get; set; }
        public long TransactionIndex { get; set; }
        public Hash256? BlockHash { get; set; }
        public ulong BlockNumber { get; set; }
        public ulong CumulativeGasUsed { get; set; }
        public ulong GasUsed { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ulong? BlobGasUsed { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public UInt256? BlobGasPrice { get; set; }

        public UInt256? EffectiveGasPrice { get; set; }
        public Address From { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Address To { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Address? ContractAddress { get; set; }
        /// <summary>Nullable because a caller can send <c>"logs": null</c>, which the deserializer honours.</summary>
        public LogEntryForRpc[]? Logs { get; set; }
        public Bloom? LogsBloom { get; set; }
        public Hash256? Root { get; set; }
        public long? Status { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Address? Payer { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public FrameReceiptForRpc[]? FrameReceipts { get; set; }

        public TxType Type { get; set; }

        public TxReceipt ToReceipt()
        {
            TxReceipt receipt = new()
            {
                Bloom = LogsBloom,
                Index = (int)TransactionIndex,
                Logs = (Logs ?? []).Select(static l => l.ToLogEntry()).ToArray(),
                Recipient = To,
                Sender = From,
                BlockHash = BlockHash,
                BlockNumber = BlockNumber,
                ContractAddress = ContractAddress,
                GasUsed = GasUsed,
                StatusCode = Status is not null ? (byte)Status : byte.MinValue,
                TxHash = TransactionHash,
                GasUsedTotal = CumulativeGasUsed,
                PostTransactionState = Root,
                TxType = Type
            };

            // EIP-8141: mirror the constructor so a frame receipt survives the round trip.
            if (Type == TxType.FrameTx)
            {
                receipt.Payer = Payer;
                TxFrameReceipt[] frameReceipts = (FrameReceipts ?? []).Select(static f => f.ToFrameReceipt()).ToArray();
                receipt.FrameReceipts = frameReceipts;

                // Frames are authoritative, as on the wire path: derive the dependent fields so a payload
                // cannot leave Logs and the bloom contradicting the frame receipts on the same receipt.
                if (frameReceipts.Length > 0)
                {
                    receipt.Logs = TxFrameReceipt.ConcatLogs(frameReceipts);
                    receipt.StatusCode = TxFrameReceipt.AggregateStatus(frameReceipts);
                    // Bloom is absent from the EIP-8141 wire receipt, so DecodeFrameTxReceipt leaves it
                    // for TxReceipt to derive from Logs. Clearing it keeps the pair from contradicting.
                    receipt.Bloom = null;
                }
            }

            return receipt;
        }
    }
}
