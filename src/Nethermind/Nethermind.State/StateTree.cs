// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
        private static readonly AccountDecoder _decoder = AccountDecoder.Instance;
        private static readonly AccountStructDecoder _structDecoder = AccountStructDecoder.Instance;

        public static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        [DebuggerStepThrough]
        public StateTree(ICappedArrayPool? bufferPool = null)
            : base(new MemDb(), Keccak.EmptyTreeHash, true, NullLogManager.Instance, bufferPool: bufferPool)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public StateTree(IScopedTrieStore? store, ILogManager? logManager)
            : base(store, Keccak.EmptyTreeHash, true, logManager)
        {
            TrieType = TrieType.State;
        }

        public StateTree(ITrieStore? store, ILogManager? logManager)
            : base(store.GetTrieStore(null), logManager)
        {
        }

        [DebuggerStepThrough]
        public Account? Get(Address address, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> bytes = Get(KeccakCache.Compute(address.Bytes).BytesAsSpan, rootHash);
            return bytes.IsEmpty ? null : _decoder.Decode(bytes);
        }

        [DebuggerStepThrough]
        public AccountStruct? GetStruct(Address address, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> bytes = Get(KeccakCache.Compute(address.Bytes).BytesAsSpan, rootHash);
            return bytes.IsEmpty ? null : _structDecoder.Decode(bytes);
        }

        [DebuggerStepThrough]
        public bool TryGetStruct(Address address, out AccountStruct account, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> bytes = Get(KeccakCache.Compute(address.Bytes).BytesAsSpan, rootHash);
            Rlp.ValueDecoderContext valueDecoderContext = new Rlp.ValueDecoderContext(bytes);
            if (bytes.IsEmpty)
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            AccountStruct? nullableStruct = _structDecoder.Decode(ref valueDecoderContext);
            if (nullableStruct is null)
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            account = nullableStruct.Value;
            return true;
        }

        [DebuggerStepThrough]
        internal Account? Get(Hash256 keccak) // for testing
        {
            ReadOnlySpan<byte> bytes = Get(keccak.Bytes);
            return bytes.IsEmpty ? null : _decoder.Decode(bytes);
        }

        public void SetStruct(Address address, AccountStruct? account)
        {
            KeccakCache.ComputeTo(address.Bytes, out ValueHash256 keccak);
            Set(keccak.BytesAsSpan, account is null ? null : account.Value.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }

        public void Set(Address address, Account? account)
        {
            KeccakCache.ComputeTo(address.Bytes, out ValueHash256 keccak);
            Set(keccak.BytesAsSpan, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
        }

        [DebuggerStepThrough]
        public Rlp? Set(Hash256 keccak, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }

        public Rlp? Set(in ValueHash256 keccak, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }
    }
}
