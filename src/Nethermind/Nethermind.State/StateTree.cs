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
        private readonly AccountDecoder _decoder = new();

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        [DebuggerStepThrough]
        public StateTree(ICappedArrayPool? bufferPool = null)
            : base(new MemDb(), Keccak.EmptyTreeHash, true, true, NullLogManager.Instance, bufferPool: bufferPool)
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
        public Account? Get(Address address, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> bytes = Get(ValueKeccak.Compute(address.Bytes).BytesAsSpan, rootHash);
            return bytes.IsEmpty ? null : _decoder.Decode(bytes);
        }

        [DebuggerStepThrough]
        public AccountStruct? GetStruct(Address address, Hash256? rootHash = null)
        {
            ReadOnlySpan<byte> bytes = Get(ValueKeccak.Compute(address.Bytes).BytesAsSpan, rootHash);
            Rlp.ValueDecoderContext valueDecoderContext = new Rlp.ValueDecoderContext(bytes);
            return bytes.IsEmpty ? null : _decoder.DecodeStruct(ref valueDecoderContext);
        }

        [DebuggerStepThrough]
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
