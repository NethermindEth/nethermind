// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
            Span<byte> bytes = Get(ValueKeccak.Compute(address.Bytes).BytesAsSpan, rootHash);
            return bytes.IsEmpty ? null : _decoder.Decode(bytes.AsRlpValueContext());
        }

        [DebuggerStepThrough]
        internal Account? Get(Keccak keccak) // for testing
        {
            Span<byte> bytes = Get(keccak.Bytes);
            return bytes.IsEmpty ? null : _decoder.Decode(bytes.AsRlpValueContext());
        }

        public void Set(Address address, Account? account)
        {
            ValueKeccak keccak = ValueKeccak.Compute(address.Bytes);
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);
            Set(keccak.BytesAsSpan, rlp);
        }

        [DebuggerStepThrough]
        public Rlp? Set(Keccak keccak, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }
    }
}
