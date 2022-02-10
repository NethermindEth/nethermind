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
using Nethermind.Abi;
using Nethermind.Serialization.Rlp;

namespace Nethermind.AccountAbstraction.Data
{
    public class UserOperationDecoder
    {
        private readonly AbiEncoder _abiEncoder = new AbiEncoder();
        private AbiSignature _opSignature = new AbiSignature("arrayOfOps", new AbiArray(
            new AbiTuple(
                AbiAddress.Instance,
                AbiType.UInt256,
                AbiType.DynamicBytes,
                AbiType.DynamicBytes,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiAddress.Instance,
                AbiType.DynamicBytes,
                AbiType.DynamicBytes
            )
        ));

        public byte[] Encode(UserOperation[] ops)
        {
            return _abiEncoder.Encode(AbiEncodingStyle.None, _opSignature, ops);
        }

        public UserOperation[]? Decode(byte[] byteArray)
        {
            // TODO: should this throw?
            return _abiEncoder.Decode(AbiEncodingStyle.None, _opSignature, byteArray) as UserOperation[];
        }
    }
}
