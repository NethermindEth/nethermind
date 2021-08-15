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
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Data
{
    public class UserOperation
    {
        public UserOperation(Address target, UInt256 nonce, byte[] callData, long callGas, UInt256 maxFeePerGas, UInt256 maxPriorityFeePerGas, Address paymaster, Address signer, Signature signature, AccessList accessList)
        {
            Target = target;
            Nonce = nonce;
            CallData = callData;
            CallGas = callGas;
            MaxFeePerGas = maxFeePerGas;
            MaxPriorityFeePerGas = maxPriorityFeePerGas;
            Paymaster = paymaster;
            Signer = signer;
            Signature = signature;
            AccessList = accessList;
            Hash = this.CalculateHash();
        }

        public Address Target { get; }
        public UInt256 Nonce { get; }
        public byte[] CallData { get; }
        public long CallGas { get; }
        public UInt256 MaxFeePerGas { get; }
        public UInt256 MaxPriorityFeePerGas { get; }
        public Address Paymaster { get; }
        public Address Signer { get; }
        public Signature Signature { get; }
        public AccessList AccessList { get; }
        public int ResimulationCounter { get; set; }
        public Keccak? Hash { get; set; }
    }
}
