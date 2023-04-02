// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                TestObjectInternal.Set(key.Span, value);
            }

            for (int j = 0; j < end; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = GenerateIndexedAccountRlp(j + 1);
                TestObjectInternal.Set(key.Span, value);
            }

            TestObjectInternal.Commit(0);
            TestObjectInternal.UpdateRootHash();

            return this;
        }

        private Account GenerateIndexedAccount(int index)
        {
            Account account = new(
                (UInt256)index,
                (UInt256)index,
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
