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
            byte[][] TreeKeys = GetTreeKeysForAccount(address);
            
            // byte[]? bytes = GetValue(ValueKeccak.Compute(address.Bytes).BytesAsSpan);
            // if (bytes is null)
            // {
            //     return null;
            // }
            byte[]? version = GetValue(TreeKeys[AccountTreeIndexes.Version]);
            byte[]? balance = GetValue(TreeKeys[AccountTreeIndexes.Balance]);
            byte[]? nonce = GetValue(TreeKeys[AccountTreeIndexes.Nonce]);
            byte[]? codeKeccak = GetValue(TreeKeys[AccountTreeIndexes.CodeHash]);
            byte[]? codeSize = GetValue(TreeKeys[AccountTreeIndexes.CodeSize]);
            if (version is null || balance is null || nonce is null || codeKeccak is null || codeSize is null)
            {
                return null;
            }

            UInt256 balanceU = new UInt256(balance.AsSpan(), true);
            UInt256 nonceU = new UInt256(nonce.AsSpan(), true);
            Keccak codeHash = new Keccak(codeKeccak);
            UInt256 codeSizeU = new UInt256(codeSize.AsSpan(), true);
            UInt256 versionU = new UInt256(version.AsSpan(), true);
        
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
            byte[][] TreeKeys = GetTreeKeysForAccount(address);
            if (account is null)
            {
                SetValue(TreeKeys[AccountTreeIndexes.Version], UInt256.Zero.ToBigEndian());
                SetValue(TreeKeys[AccountTreeIndexes.Balance], UInt256.Zero.ToBigEndian());
                SetValue(TreeKeys[AccountTreeIndexes.Nonce], UInt256.Zero.ToBigEndian());
                SetValue(TreeKeys[AccountTreeIndexes.CodeHash], Keccak.Zero.Bytes);
                SetValue(TreeKeys[AccountTreeIndexes.CodeSize], UInt256.Zero.ToBigEndian());
            }
            else
            {
                SetValue(TreeKeys[AccountTreeIndexes.Version], account.Version.ToBigEndian());
                SetValue(TreeKeys[AccountTreeIndexes.Balance], account.Balance.ToBigEndian());
                SetValue(TreeKeys[AccountTreeIndexes.Nonce], account.Nonce.ToBigEndian());
                SetValue(TreeKeys[AccountTreeIndexes.CodeHash], account.CodeHash.Bytes);
                SetValue(TreeKeys[AccountTreeIndexes.CodeSize], account.CodeSize.ToBigEndian());
            }
            
        }
        
    }
}
