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

namespace Nethermind.State
{
    public static class StateReaderExtensions
    {
        public static UInt256 GetNonce(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetAccount(stateRoot, address)?.Nonce ?? UInt256.Zero;
        }
        
        public static UInt256 GetBalance(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetAccount(stateRoot, address)?.Balance ?? UInt256.Zero;
        }
        
        public static Keccak GetStorageRoot(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetAccount(stateRoot, address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        }

        public static byte[] GetCode(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetCode(GetCodeHash(stateReader, stateRoot, address)) ?? Array.Empty<byte>();
        }
        
        public static Keccak GetCodeHash(this IStateReader stateReader, Keccak stateRoot, Address address)
        {
            return stateReader.GetAccount(stateRoot, address)?.CodeHash ?? Keccak.OfAnEmptyString;
        }
    }
}
