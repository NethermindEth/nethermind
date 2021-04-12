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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Evm
{
    public static class ContractAddress
    {
        public static Address From(Address? deployingAddress, UInt256 nonce)
        {
            ValueKeccak contractAddressKeccak =
                ValueKeccak.Compute(
                    Rlp.Encode(
                        Rlp.Encode(deployingAddress),
                        Rlp.Encode(nonce)).Bytes);

            return new Address(in contractAddressKeccak);
        }
        
        public static Address From(Address deployingAddress, Span<byte> salt, Span<byte> initCode)
        {
            // sha3(0xff ++ msg.sender ++ salt ++ sha3(init_code)))
            Span<byte> bytes = new byte[1 + Address.ByteLength + 32 + salt.Length];
            bytes[0] = 0xff;
            deployingAddress.Bytes.CopyTo(bytes.Slice(1, 20));
            salt.CopyTo(bytes.Slice(21, salt.Length));
            ValueKeccak.Compute(initCode).BytesAsSpan.CopyTo(bytes.Slice(21 + salt.Length, 32));
                
            ValueKeccak contractAddressKeccak = ValueKeccak.Compute(bytes);
            return new Address(in contractAddressKeccak);
        }
    }
}
