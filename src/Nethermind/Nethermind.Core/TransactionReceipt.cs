// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;

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
