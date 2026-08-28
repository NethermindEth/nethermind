// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test.Builders
{
    public class TrieBuilder : BuilderBase<PatriciaTree>
    {
        public TrieBuilder(INodeStorage db) => TestObjectInternal = new PatriciaTree(new RawScopedTrieStore(db), Keccak.EmptyTreeHash, true, LimboLogs.Instance);

        public TrieBuilder WithAccountsByIndex(int start, int count)
        {
            int end = start + count;
            for (int j = start; j < end; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = GenerateIndexedAccountRlp(j);
                TestObjectInternal.Set(key.Bytes, value);
            }

            for (int j = 0; j < end; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = GenerateIndexedAccountRlp(j + 1);
                TestObjectInternal.Set(key.Bytes, value);
            }

            TestObjectInternal.Commit();
            TestObjectInternal.UpdateRootHash();

            return this;
        }

        private Account GenerateIndexedAccount(int index)
        {
            Account account = new(
                (ulong)index,
                (ulong)index,
                Keccak.EmptyTreeHash,
                Keccak.OfAnEmptyString);

            return account;
        }

        private byte[] GenerateIndexedAccountRlp(int index)
        {
            Account account = GenerateIndexedAccount(index);
            byte[] value = Rlp.Encode(account).Bytes;
            return value;
        }


    }
}
