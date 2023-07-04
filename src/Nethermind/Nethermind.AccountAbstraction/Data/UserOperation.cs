// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Abi;

namespace Nethermind.AccountAbstraction.Data
{
    public class UserOperation
    {
        private static readonly UserOperationAbiPacker _packer = new();
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
            AddressesToCodeHashes = ImmutableDictionary<Address, Keccak>.Empty;
        }

        private Keccak CalculateHash()
        {
            return Keccak.Compute(_packer.Pack(this));
        }

        private readonly AbiSignature _idSignature = new AbiSignature("RequestId", AbiType.Bytes32, AbiAddress.Instance, AbiType.UInt256);

        public void CalculateRequestId(Address entryPointAddress, ulong chainId)
        {
            RequestId = Keccak.Compute(_abiEncoder.Encode(AbiEncodingStyle.None, _idSignature, CalculateHash(), entryPointAddress, chainId));
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

        public Keccak? RequestId { get; set; }
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
        public IDictionary<Address, Keccak> AddressesToCodeHashes { get; set; }
        public bool AlreadySimulated { get; set; }
        public bool PassedBaseFee { get; set; } // if the MaxFeePerGas has ever exceeded the basefee
    }
}
