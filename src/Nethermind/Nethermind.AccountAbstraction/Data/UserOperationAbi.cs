// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Data
{
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

    public class UserOperationAbiPacker
    {
        private readonly AbiEncoder _abiEncoder = new AbiEncoder();
        private readonly AbiSignature _opSignature = new AbiSignature("encodeOp", new AbiTuple<UserOperationAbi>());

        public byte[] Pack(UserOperation op)
        {
            UserOperationAbi abi = op.Abi;
            abi.Signature = Bytes.Empty;
            byte[] encodedBytes = _abiEncoder.Encode(AbiEncodingStyle.None, _opSignature, abi);
            byte[] slicedBytes = encodedBytes.Slice(32, encodedBytes.Length - 64);
            return slicedBytes;
        }
    }
}
