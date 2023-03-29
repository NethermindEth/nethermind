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

        //[DebuggerStepThrough]
        public Account? Get(Address address, Keccak? rootHash = null)
        {
            byte[]? bytes = null;
            byte[] addressKeyBytes = Keccak.Compute(address.Bytes).Bytes;
            if (rootHash is not null && RootHash != rootHash)
            {
                Span<byte> nibbleBytes = stackalloc byte[64];
                Nibbles.BytesToNibbleBytes(addressKeyBytes, nibbleBytes);
                TrieNode node = TrieStore.FindCachedOrUnknown(nibbleBytes, rootHash);
                if (node?.NodeType == NodeType.Leaf)
                    bytes = node.Value;
            }

            if (bytes is null && (rootHash is null || RootHash == rootHash))
            {
                if (RootRef?.IsPersisted == true)
                {
                    byte[]? nodeData = TrieStore[addressKeyBytes];
                    if (nodeData is not null)
                    {
                        TrieNode node = new(NodeType.Unknown, nodeData);
                        node.ResolveNode(TrieStore);
                        bytes = node.Value;
                    }
                }
                else
                {
                    bytes = Get(addressKeyBytes);
                }
            }

            return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
        }

        //[DebuggerStepThrough]
        internal Account? Get(Keccak keccak) // for testing
        {
            byte[] addressKeyBytes = keccak.Bytes;
            if (RootRef?.IsPersisted == true)
            {
                byte[]? nodeData = TrieStore[addressKeyBytes];
                if (nodeData is not null)
                {
                    TrieNode node = new(NodeType.Unknown, nodeData);
                    node.ResolveNode(TrieStore);
                    return _decoder.Decode(node.Value.AsRlpStream());
                }
            }
            byte[]? bytes = Get(addressKeyBytes);
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
