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
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Data
{
    public partial class UserOperation
    {
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
            
            Hash = CalculateHash(this);
        }
        
        public UserOperationAbi Abi => new UserOperationAbi
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

        public Keccak Hash { get; set; }
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
