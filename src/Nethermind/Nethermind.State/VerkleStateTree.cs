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
using System.Diagnostics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.State
{
    public class VerkleStateTree : VerkleTree
    {
        
        public VerkleStateTree()
            : base(EmptyTreeHash, true, NullLogManager.Instance)
        {
            TrieType = TrieType.State;
        }
        public VerkleStateTree(ILogManager? logManager)
            : base(EmptyTreeHash, true, logManager)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public Account? Get(Address address, Keccak? rootHash = null)
        {
            // byte[]? bytes = GetValue(ValueKeccak.Compute(address.Bytes).BytesAsSpan, rootHash);
            byte[] key = GetTreeKeyPrefixAccount(address);
            
            // byte[]? bytes = GetValue(ValueKeccak.Compute(address.Bytes).BytesAsSpan);
            // if (bytes is null)
            // {
            //     return null;
            // }
            Span<byte> version = GetValueSpan(key , AccountTreeIndexes.Version);
            Span<byte> balance = GetValueSpan(key, AccountTreeIndexes.Balance);
            Span<byte> nonce = GetValueSpan(key, AccountTreeIndexes.Nonce);
            Span<byte> codeKeccak = GetValueSpan(key, AccountTreeIndexes.CodeHash);
            Span<byte> codeSize = GetValueSpan(key, AccountTreeIndexes.CodeSize);
            if (version.IsEmpty || balance.IsEmpty || nonce.IsEmpty || codeKeccak.IsEmpty || codeSize.IsEmpty)
            {
                return null;
            }

            UInt256 balanceU = new (balance, true);
            UInt256 nonceU = new (nonce, true);
            Keccak codeHash = new (codeKeccak.ToArray());
            UInt256 codeSizeU = new (codeSize, true);
            UInt256 versionU = new (version, true);
        
            if (
                versionU.Equals(UInt256.Zero) &&
                balanceU.Equals(UInt256.Zero) &&
                nonceU.Equals(UInt256.Zero) &&
                codeHash.Equals(Keccak.Zero) &&
                codeSizeU.Equals(UInt256.Zero)
            )
            {
                return null;
            }
            Account account = new (
                balanceU,
                nonceU,
                codeHash,
                codeSizeU,
                versionU
                );

            return account;
        }
        

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        public void Set(Address address, Account? account)
        {
            byte[] keyPrefix = GetTreeKeyPrefixAccount(address);
            if (account is null)
            {
                SetValue(keyPrefix, AccountTreeIndexes.Version, UInt256.Zero.ToBigEndian());
                SetValue(keyPrefix, AccountTreeIndexes.Balance, UInt256.Zero.ToBigEndian());
                SetValue(keyPrefix, AccountTreeIndexes.Nonce, UInt256.Zero.ToBigEndian());
                SetValue(keyPrefix, AccountTreeIndexes.CodeHash, Keccak.Zero.Bytes);
                SetValue(keyPrefix, AccountTreeIndexes.CodeSize, UInt256.Zero.ToBigEndian());
            }
            else
            {
                SetValue(keyPrefix,AccountTreeIndexes.Version, account.Version.ToBigEndian());
                SetValue(keyPrefix,AccountTreeIndexes.Balance, account.Balance.ToBigEndian());
                SetValue(keyPrefix,AccountTreeIndexes.Nonce, account.Nonce.ToBigEndian());
                SetValue(keyPrefix,AccountTreeIndexes.CodeHash, account.CodeHash.Bytes);
                SetValue(keyPrefix,AccountTreeIndexes.CodeSize, account.CodeSize.ToBigEndian());
                if (account.Code != null)
                {
                    SetCode(address, account.Code.ToArray());
                }
            }
            
        }
        
    }
}
