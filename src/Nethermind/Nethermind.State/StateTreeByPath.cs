// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StateTreeByPath : PatriciaTree, IStateTree
    {
        private readonly AccountDecoder _decoder = new();

        private static readonly UInt256 CacheSize = 1024;

        private static readonly int CacheSizeInt = (int)CacheSize;

        private static readonly Dictionary<UInt256, byte[]> Cache = new(CacheSizeInt);

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        //protected ITrieStore _storageTrieStore;

        static StateTreeByPath()
        {
            Span<byte> buffer = stackalloc byte[32];
            for (int i = 0; i < CacheSizeInt; i++)
            {
                UInt256 index = (UInt256)i;
                index.ToBigEndian(buffer);
                Cache[index] = Keccak.Compute(buffer).BytesToArray();
            }
        }

        [DebuggerStepThrough]
        public StateTreeByPath(ICappedArrayPool? bufferPool = null)
            : base(new MemColumnsDb<StateColumns>(), Keccak.EmptyTreeHash, true, true, NullLogManager.Instance, bufferPool, TrieNodeResolverCapability.Path)
        {
            TrieType = TrieType.State;
        }

        public StateTreeByPath(ITrieStore? store, ILogManager? logManager)
            : base(store, Keccak.EmptyTreeHash, true, true, logManager)
        {
            if (store.Capability == TrieNodeResolverCapability.Hash) throw new ArgumentException("Only accepts by path store");
            TrieType = TrieType.State;
        }

        public Account? Get(Address address, Keccak? rootHash = null)
        {
            byte[] addressKeyBytes = Keccak.Compute(address.Bytes).BytesToArray();
            byte[]? bytes = Get(addressKeyBytes, rootHash);
            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        //[DebuggerStepThrough]
        internal Account? Get(Keccak keccak) // for testing
        {
            byte[]? bytes = Get(keccak.Bytes);
            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        public void Set(Address address, Account? account)
        {
            ValueKeccak keccak = ValueKeccak.Compute(address.Bytes);
            Set(keccak.BytesAsSpan, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }

        [DebuggerStepThrough]
        public Rlp? Set(in ValueKeccak keccak, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }
    }
}
