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
            : base(new TrieStoreByPath(new MemColumnsDb<StateColumns>(), NullLogManager.Instance), Keccak.EmptyTreeHash, true, true, NullLogManager.Instance, bufferPool)
        {
            TrieType = TrieType.State;
        }

        public StateTreeByPath(ITrieStore? store, ILogManager? logManager)
            : base(store, Keccak.EmptyTreeHash, true, true, logManager)
        {
            if (store.Capability == TrieNodeResolverCapability.Hash) throw new ArgumentException("Only accepts by path store");
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public AccountStruct? GetStruct(Address address, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> bytes = Get(ValueKeccak.Compute(address.Bytes).BytesAsSpan, rootHash);
            Rlp.ValueDecoderContext valueDecoderContext = new Rlp.ValueDecoderContext(bytes);
            return bytes.IsEmpty ? null : _decoder.DecodeStruct(ref valueDecoderContext);
        }

        public Account? Get(Address address, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> bytes = Get(ValueKeccak.Compute(address.Bytes).BytesAsSpan, rootHash);
            return bytes.IsEmpty ? null : _decoder.Decode(bytes);
        }

        //[DebuggerStepThrough]
        internal Account? Get(Hash256 keccak) // for testing
        {
            ReadOnlySpan<byte> bytes = Get(keccak.Bytes);
            return bytes.IsEmpty ? null : _decoder.Decode(bytes);
        }

        public void Set(Address address, Account? account)
        {
            ValueHash256 keccak = ValueKeccak.Compute(address.Bytes);
            Set(keccak.BytesAsSpan, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }

        [DebuggerStepThrough]
        public Rlp? Set(in ValueHash256 keccak, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }
    }
}
