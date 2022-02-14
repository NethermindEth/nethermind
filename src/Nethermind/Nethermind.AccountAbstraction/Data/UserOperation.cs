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
// 

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Abi;
using Nethermind.Core.Extensions;

namespace Nethermind.AccountAbstraction.Data
{
    public class UserOperation
    {
        private static readonly UserOperationDecoder _decoder = new();
        private static readonly AbiEncoder _abiEncoder = new();
        public UserOperation(UserOperationRpc userOperationRpc)
        {
            Sender = userOperationRpc.Sender;
            Nonce = userOperationRpc.Nonce;
            InitCode = userOperationRpc.InitCode;
            CallData = userOperationRpc.CallData;
            CallGas = userOperationRpc.CallGas;
            VerificationGas = userOperationRpc.VerificationGas;
            PreVerificationGas = userOperationRpc.PreVerificationGas;
            MaxFeePerGas = userOperationRpc.MaxFeePerGas;
            MaxPriorityFeePerGas = userOperationRpc.MaxPriorityFeePerGas;
            Paymaster = userOperationRpc.Paymaster;
            PaymasterData = userOperationRpc.PaymasterData;
            Signature = userOperationRpc.Signature;

            AccessList = UserOperationAccessList.Empty;
            RequestId = CalculateRequestId(entryPointAddress, chainId);
        }

        private Keccak CalculateHash()
        {
            return Keccak.Compute(_decoder.Encode(this).Bytes);
        }

        public Keccak CalculateRequestId(Address entryPointAddress, int chainId)
        {
            private AbiSignature _idSignature = new AbiSignature("RequestId", new AbiArray(
                new AbiTuple(
                    AbiType.Bytes32,
                    AbiAddress.Instance,
                    AbiType.UInt256
                )
            ));
            Keccac hash = userOperation.CalculateHash();
            return Keccak.Compute(_abiEncoder.Encode(AbiEncodingStyle.None, _idSignature, [hash, entryPointAddress, chainId]));
        }

        public UserOperationAbi Abi => new()
        {
            Sender = Sender!,
            Nonce = Nonce,
            InitCode = InitCode,
            CallData = CallData,
            CallGas = CallGas,
            VerificationGas = VerificationGas,
            PreVerificationGas = PreVerificationGas,
            MaxFeePerGas = MaxFeePerGas,
            MaxPriorityFeePerGas = MaxPriorityFeePerGas,
            Paymaster = Paymaster!,
            PaymasterData = PaymasterData,
            Signature = Signature!
        };

        public Keccak RequestId { get; set; }
        public Address Sender { get; set; }
        public UInt256 Nonce { get; set; }
        public byte[] InitCode { get; set; }
        public byte[] CallData { get; set; }
        public UInt256 CallGas { get; set; }
        public UInt256 VerificationGas { get; set; }
        public UInt256 PreVerificationGas { get; set; }
        public UInt256 MaxFeePerGas { get; set; }
        public UInt256 MaxPriorityFeePerGas { get; set; }
        public Address Paymaster { get; set; }
        public byte[] Signature { get; set; }
        public byte[] PaymasterData { get; set; }
        public UserOperationAccessList AccessList { get; set; }
        public bool AlreadySimulated { get; set; }
    }

    public struct UserOperationAbi
    {
        public Address Sender { get; set; }
        public UInt256 Nonce { get; set; }
        public byte[] InitCode { get; set; }
        public byte[] CallData { get; set; }
        public UInt256 CallGas { get; set; }
        public UInt256 VerificationGas { get; set; }
        public UInt256 PreVerificationGas { get; set; }
        public UInt256 MaxFeePerGas { get; set; }
        public UInt256 MaxPriorityFeePerGas { get; set; }
        public Address Paymaster { get; set; }
        public byte[] PaymasterData { get; set; }
        public byte[] Signature { get; set; }
    }
}
