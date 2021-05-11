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
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class TxReceipt
    {
        /// <summary>
        /// EIP-2718 transaction type
        /// </summary>
        public TxType TxType { get; set; }
        
        /// <summary>
        ///     EIP-658
        /// </summary>
        public byte StatusCode { get; set; }
        public long BlockNumber { get; set; }
        public Keccak? BlockHash { get; set; }
        public Keccak? TxHash { get; set; }
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
        public Keccak? PostTransactionState { get; set; }
        public Bloom? Bloom { get; set; }
        public LogEntry[]? Logs { get; set; }
        public string? Error { get; set; }
        public bool Removed { get; set; }

        /// <summary>
        /// Ignores receipt output on RLP serialization.
        /// Output is either StateRoot or StatusCode depending on eip configuration.
        /// </summary>
        public bool SkipStateAndStatusInRlp { get; set; }
    }

    public ref struct TxReceiptStructRef
    {
        /// <summary>
        /// EIP-2718 transaction type
        /// </summary>
        public TxType TxType { get; set; }
        
        /// <summary>
        ///     EIP-658
        /// </summary>
        public byte StatusCode { get; set; }
        public long BlockNumber { get; set; }
        public KeccakStructRef BlockHash;
        public KeccakStructRef TxHash;
        public int Index { get; set; }
        public long GasUsed { get; set; }
        public long GasUsedTotal { get; set; }
        public AddressStructRef Sender;
        public AddressStructRef ContractAddress;
        public AddressStructRef Recipient;

        [Todo(Improve.Refactor, "Receipt tracer?")]
        public Span<byte> ReturnValue;

        /// <summary>
        ///     Removed in EIP-658
        /// </summary>
        public KeccakStructRef PostTransactionState;

        public BloomStructRef Bloom;
        
        /// <summary>
        /// Rlp encoded logs
        /// </summary>
        public Span<byte> LogsRlp { get; set; }
        
        public LogEntry[]? Logs { get; }
        
        public string? Error { get; set; }

        public TxReceiptStructRef(TxReceipt receipt)
        {
            TxType = receipt.TxType;
            StatusCode = receipt.StatusCode;
            BlockNumber = receipt.BlockNumber;
            BlockHash = (receipt.BlockHash ?? Keccak.Zero).ToStructRef();
            TxHash = (receipt.TxHash ?? Keccak.Zero).ToStructRef();
            Index = receipt.Index;
            GasUsed = receipt.GasUsed;
            GasUsedTotal = receipt.GasUsedTotal;
            Sender = (receipt.Sender ?? Address.Zero).ToStructRef();
            ContractAddress = (receipt.ContractAddress ?? Address.Zero).ToStructRef();
            Recipient = (receipt.Recipient ?? Address.Zero).ToStructRef();
            ReturnValue = receipt.ReturnValue;
            PostTransactionState = (receipt.PostTransactionState ?? Keccak.Zero).ToStructRef();
            Bloom = (receipt.Bloom ?? Core.Bloom.Empty).ToStructRef();
            Logs = receipt.Logs;
            LogsRlp = Span<byte>.Empty;
            Error = receipt.Error;
        }
    }
}
