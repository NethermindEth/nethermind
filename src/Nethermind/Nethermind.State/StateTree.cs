// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StateTree : PatriciaTree
    {
        private readonly LruCache<ValueKeccak, Account> _cache = new(maxCapacity: 4096, "Account cache");
        private readonly AccountDecoder _decoder = new();

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        [DebuggerStepThrough]
        public StateTree()
            : base(new MemDb(), Keccak.EmptyTreeHash, true, true, NullLogManager.Instance)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public StateTree(ITrieStore? store, ILogManager? logManager)
            : base(store, Keccak.EmptyTreeHash, true, true, logManager)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public Account? Get(Address address, Keccak? rootHash = null)
        {
            ValueKeccak keccak = ValueKeccak.Compute(address.Bytes);
            return Get(keccak, rootHash);
        }

        [DebuggerStepThrough]
        internal Account? Get(Keccak keccak) // for testing
            => Get(keccak.ValueKeccak);

        [DebuggerStepThrough]
        private Account? Get(in ValueKeccak keccak, Keccak? rootHash = null)
        {
            if (rootHash is null && _cache.TryGet(keccak, out Account? account))
            {
                return account;
            }

            byte[]? bytes = Get(keccak.BytesAsSpan, rootHash);
            account = bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());

            if (rootHash is null && account is not null)
            {
                _cache.Set(keccak, account);
            }

            return account;
        }

        [DebuggerStepThrough]
        public void Set(Address address, Account? account)
        {
            ValueKeccak keccak = ValueKeccak.Compute(address.Bytes);
            Set(in keccak, account);
        }

        [DebuggerStepThrough]
        public Rlp? Set(Keccak keccak, Account? account)
            => Set(in keccak.ValueKeccak, account);

        [DebuggerStepThrough]
        public Rlp? Set(in ValueKeccak keccak, Account? account)
        {
            if (account is not null)
            {
                _cache.Set(keccak, account);
            }
            else
            {
                _cache.Delete(keccak);
            }

            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }
    }
}
