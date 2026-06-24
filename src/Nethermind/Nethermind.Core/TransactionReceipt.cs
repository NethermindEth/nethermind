// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class TxReceipt
    {
        private Bloom? _bloom;

        public TxReceipt()
        {
        }

        public TxReceipt(TxReceipt other)
        {
            TxType = other.TxType;
            StatusCode = other.StatusCode;
            BlockNumber = other.BlockNumber;
            BlockHash = other.BlockHash;
            TxHash = other.TxHash;
            Index = other.Index;
            GasUsed = other.GasUsed;
            GasUsedTotal = other.GasUsedTotal;
            ExecutionGasUsed = other.ExecutionGasUsed;
            BlockGasUsed = other.BlockGasUsed;
            StorageGasUsed = other.StorageGasUsed;
            EffectiveGasPrice = other.EffectiveGasPrice;
            Sender = other.Sender;
            ContractAddress = other.ContractAddress;
            Recipient = other.Recipient;
            ReturnValue = other.ReturnValue;
            PostTransactionState = other.PostTransactionState;
            Bloom = other.Bloom;
            Logs = other.Logs;
            Error = other.Error;
        }

        /// <summary>
        /// EIP-2718 transaction type
        /// </summary>
        public TxType TxType { get; set; }

        /// <summary>
        ///     EIP-658
        /// </summary>
        public byte StatusCode { get; set; }
        public long BlockNumber { get; set; }
        public Hash256? BlockHash { get; set; }
        public Hash256? TxHash { get; set; }
        public int Index { get; set; }
        public long GasUsed { get; set; }
        public long GasUsedTotal { get; set; }

        // Diagnostic-only fields. NOT part of the consensus receipt RLP - the receipt
        // encoders in Nethermind.Serialization.Rlp write only StatusCode, GasUsed,
        // GasUsedTotal, Bloom, Logs explicitly, so these new properties do not affect
        // receipts root or block hashes. They are surfaced solely in the diagnostic
        // JSON dump (BlockTraceDumper) to aid investigation of EIP-7778/EIP-8037
        // gas-accounting issues.
        /// <summary>EIP-7778 pre-refund gas used by block-level gas accounting (regular dim).</summary>
        public long BlockGasUsed { get; set; }
        /// <summary>EIP-8037 state-dim gas (storage / state-mutating ops) used by block accounting.</summary>
        public long StorageGasUsed { get; set; }
        /// <summary>Post-refund execution gas without EIP-7976 floor adjustment (OperationGas).</summary>
        public long ExecutionGasUsed { get; set; }
        /// <summary>Effective gas price after EIP-1559 baseFee adjustment - computed at receipt-build time.</summary>
        public UInt256 EffectiveGasPrice { get; set; }

        public Address? Sender { get; set; }
        public Address? ContractAddress { get; set; }
        public Address? Recipient { get; set; }

        [Todo(Improve.Refactor, "Receipt tracer?")]
        public byte[]? ReturnValue { get; set; }

        /// <summary>
        ///     Removed in EIP-658
        /// </summary>
        public Hash256? PostTransactionState { get; set; }
        public Bloom? Bloom { get => _bloom ?? CalculateBloom(); set => _bloom = value; }
        public LogEntry[]? Logs { get; set; }
        public string? Error { get; set; }


        public Bloom CalculateBloom()
            => _bloom = Logs?.Length == 0 ? Bloom.Empty : new Bloom(Logs);

        /// <summary>Whether the bloom is already set; unlike <see cref="Bloom"/>, reading this does not compute it.</summary>
        internal bool IsBloomCalculated => _bloom is not null;
    }

    public ref struct TxReceiptStructRef(TxReceipt receipt)
    {
        /// <summary>
        /// EIP-2718 transaction type
        /// </summary>
        public TxType TxType { get; set; } = receipt.TxType;

        /// <summary>
        ///     EIP-658
        /// </summary>
        public byte StatusCode { get; set; } = receipt.StatusCode;
        public long BlockNumber { get; set; } = receipt.BlockNumber;
        public Hash256StructRef BlockHash = (receipt.BlockHash ?? Keccak.Zero).ToStructRef();
        public Hash256StructRef TxHash = (receipt.TxHash ?? Keccak.Zero).ToStructRef();
        public int Index { get; set; } = receipt.Index;
        public long GasUsed { get; set; } = receipt.GasUsed;
        public long GasUsedTotal { get; set; } = receipt.GasUsedTotal;
        public AddressStructRef Sender = (receipt.Sender ?? Address.Zero).ToStructRef();
        public AddressStructRef ContractAddress = (receipt.ContractAddress ?? Address.Zero).ToStructRef();
        public AddressStructRef Recipient = (receipt.Recipient ?? Address.Zero).ToStructRef();

        [Todo(Improve.Refactor, "Receipt tracer?")]
        public Span<byte> ReturnValue = receipt.ReturnValue;

        /// <summary>
        ///     Removed in EIP-658
        /// </summary>
        public Hash256StructRef PostTransactionState = (receipt.PostTransactionState ?? Keccak.Zero).ToStructRef();

        public BloomStructRef Bloom = (receipt.Bloom ?? Core.Bloom.Empty).ToStructRef();

        /// <summary>
        /// Rlp encoded logs
        /// </summary>
        public ReadOnlySpan<byte> LogsRlp { get; set; } = default;

        public LogEntry[]? Logs { get; set; } = receipt.Logs;

        public string? Error { get; set; } = receipt.Error;
    }
}
