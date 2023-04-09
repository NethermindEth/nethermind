// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

        private static readonly Rlp EmptyAccountRlp = Rlp.Encode(Account.TotallyEmpty);

        [DebuggerStepThrough]
        public StateTreeByPath()
            : base(new MemDb(), Keccak.EmptyTreeHash, true, true, NullLogManager.Instance, TrieNodeResolverCapability.Path)
        {
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public StateTreeByPath(ITrieStore? store, ILogManager? logManager)
            : base(store, Keccak.EmptyTreeHash, true, true, logManager)
        {
            if (store.Capability == TrieNodeResolverCapability.Hash) throw new ArgumentException("Only accepts by path store");
            TrieType = TrieType.State;
        }

        [DebuggerStepThrough]
        public Account? Get(Address address, Keccak? rootHash = null)
        {
            byte[] addressKeyBytes = Keccak.Compute(address.Bytes).Bytes;
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
        public Rlp? Set(Keccak keccak, Account? account)
        {
            Rlp rlp = account is null ? null : account.IsTotallyEmpty ? EmptyAccountRlp : Rlp.Encode(account);

            Set(keccak.Bytes, rlp);
            return rlp;
        }

        private byte[] GetCachedAccount(byte[] addressBytes, Keccak stateRoot)
        {
            Span<byte> nibbleBytes = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(addressBytes, nibbleBytes);
            TrieNode node = TrieStore.FindCachedOrUnknown(nibbleBytes, stateRoot);
            return node?.NodeType == NodeType.Leaf ? node.Value : null;
        }

        private byte[] GetPersistedAccount(byte[] addressBytes)
        {
            byte[]? nodeData = TrieStore[addressBytes];
            if (nodeData is not null)
            {
                TrieNode node = new(NodeType.Unknown, nodeData);
                node.ResolveNode(TrieStore);
                return node.Value;
            }
            return null;
        }

        private TrieNode? GetPersistedRoot()
        {
            byte[]? nodeData = TrieStore[Nibbles.ToEncodedStorageBytes(Array.Empty<byte>())];
            if (nodeData is not null)
            {
                TrieNode root = new(NodeType.Unknown, nodeData);
                root.ResolveKey(TrieStore, true);
                return root;
            }
            return null;
        }

    }
}
