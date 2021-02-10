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
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    public class AccountDecoder : IRlpObjectDecoder<Account?>
    {
        public (Keccak CodeHash, Keccak StorageRoot) DecodeHashesOnly(RlpStream rlpStream)
        {
            rlpStream.SkipLength();
            rlpStream.SkipItem();
            rlpStream.SkipItem();
            Keccak storageRoot = rlpStream.DecodeKeccak();
            Keccak codeHash = rlpStream.DecodeKeccak();
            return (codeHash, storageRoot);
        }
        
        public Keccak DecodeStorageRootOnly(RlpStream rlpStream)
        {
            rlpStream.SkipLength();
            rlpStream.SkipItem();
            rlpStream.SkipItem();
            Keccak storageRoot = rlpStream.DecodeKeccak();
            return storageRoot;
        }
        
        public Account? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int length = rlpStream.ReadSequenceLength();
            if (length == 1)
            {
                return null;
            }
            
            UInt256 nonce = rlpStream.DecodeUInt256();
            UInt256 balance = rlpStream.DecodeUInt256();
            Keccak storageRoot = rlpStream.DecodeKeccak();
            Keccak codeHash = rlpStream.DecodeKeccak();
            Account account = new(nonce, balance, storageRoot, codeHash);
            return account;
        }

        public Rlp Encode(Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }
            
            int contentLength = GetContentLength(item);
            RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(item.Nonce);
            rlpStream.Encode(item.Balance);
            rlpStream.Encode(item.StorageRoot);
            rlpStream.Encode(item.CodeHash);
            return new Rlp(rlpStream.Data);
        }

        public int GetLength(Account? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return 1;
            }
            
            return Rlp.LengthOfSequence(GetContentLength(item));
        }
        
        private int GetContentLength(Account? item)
        {
            if (item is null)
            {
                return 0;
            }
            
            var contentLength = 2 * Rlp.LengthOfKeccakRlp;
            contentLength += Rlp.LengthOf(item.Nonce);
            contentLength += Rlp.LengthOf(item.Balance);
            return contentLength;
        }
    }
}
