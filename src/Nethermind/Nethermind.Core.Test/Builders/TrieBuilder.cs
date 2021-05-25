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

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test.Builders
{
    public class TrieBuilder : BuilderBase<PatriciaTree>
    {
        private readonly AccountDecoder _accountDecoder = new();
        
        public TrieBuilder(IKeyValueStoreWithBatching db)
        {
            TestObjectInternal = new PatriciaTree(db, Keccak.EmptyTreeHash, false, true, LimboLogs.Instance);
        }

        public TrieBuilder WithAccountsByIndex(int start, int count)
        {
            int end = start + count;
            for (int j = start; j < end; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = GenerateIndexedAccountRlp(j);
                TestObjectInternal.Set(key.Bytes, value);
            }

            for (int j = 0; j < end; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = GenerateIndexedAccountRlp(j + 1);
                TestObjectInternal.Set(key.Bytes, value);
            }

            TestObjectInternal.Commit(0);
            TestObjectInternal.UpdateRootHash();

            return this;
        }
        
        private Account GenerateIndexedAccount(int index)
        {
            Account account = new(
                (UInt256) index,
                (UInt256) index,
                Keccak.EmptyTreeHash,
                Keccak.OfAnEmptyString);

            return account;
        }

        private byte[] GenerateIndexedAccountRlp(int index)
        {
            Account account = GenerateIndexedAccount(index);
            byte[] value = _accountDecoder.Encode(account).Bytes;
            return value;
        }
        
        
    }
}
