// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Data
{
    public struct UserOperationRpc
    {
        public UserOperationRpc(UserOperation userOperation)
        {
            Sender = userOperation.Sender;
            Nonce = userOperation.Nonce;
            CallData = userOperation.CallData;
            InitCode = userOperation.InitCode;
            CallGas = userOperation.CallGas;
            VerificationGas = userOperation.VerificationGas;
            PreVerificationGas = userOperation.PreVerificationGas;
            MaxFeePerGas = userOperation.MaxFeePerGas;
            MaxPriorityFeePerGas = userOperation.MaxPriorityFeePerGas;
            Paymaster = userOperation.Paymaster;
            Signature = userOperation.Signature;
            PaymasterData = userOperation.PaymasterData;
        }

        public Address Sender { get; set; }
        public UInt256 Nonce { get; set; }
        public byte[] CallData { get; set; }
        public byte[] InitCode { get; set; }
        public UInt256 CallGas { get; set; }
        public UInt256 VerificationGas { get; set; }
        public UInt256 PreVerificationGas { get; set; }
        public UInt256 MaxFeePerGas { get; set; }
        public UInt256 MaxPriorityFeePerGas { get; set; }
        public Address Paymaster { get; set; }
        public byte[] Signature { get; set; }
        public byte[] PaymasterData { get; set; }
    }
}
