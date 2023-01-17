// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    public class StateTreeByPath : PatriciaTreeByPath, IStateTree
    {
        private readonly AccountDecoder _decoder = new();

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        [DebuggerStepThrough]
        public StateTreeByPath()
            : base(new MemDb(), Keccak.EmptyTreeHash, true, true, NullLogManager.Instance)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public StateTreeByPath(ITrieStore? store, ILogManager? logManager)
            : base(store, Keccak.EmptyTreeHash, true, true, logManager)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public Account? Get(Address address, Keccak? rootHash = null)
        {
            Span<byte> pathNibbles = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(ValueKeccak.Compute(address.Bytes).BytesAsSpan, pathNibbles);
            TrieNode accountNode = TrieStore.FindCachedOrUnknown(pathNibbles);
            if (!accountNode.IsLeaf || accountNode.FullRlp is null)
            {
                return null;
            }

            return _decoder.Decode(accountNode.Value.AsRlpStream());
        }

        [DebuggerStepThrough]
        internal Account? Get(Keccak keccak) // for testing
        {
            Span<byte> pathNibbles = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(keccak.Bytes, pathNibbles);

            byte[]? nodeData = TrieStore[pathNibbles.ToArray()];
            if (nodeData is not null)
            {
                TrieNode node = new(NodeType.Unknown, nodeData);
                node.ResolveNode(TrieStore);
                return _decoder.Decode(node.Value.AsRlpStream());
            }
            byte[]? bytes = Get(keccak.Bytes);
            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        public void Set(Address address, Account? account)
        {
            ValueKeccak keccak = ValueKeccak.Compute(address.Bytes);
            Set(keccak.BytesAsSpan, account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account));
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
