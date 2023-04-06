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
            byte[] addressKeyBytes = Keccak.Compute(address.Bytes).Bytes;
            Keccak expectedRoot = rootHash ?? RootHash;

            byte[]? bytes;
            ///Scenarios to consider:
            ///StateReader:
            /// - RootRef is null and RootHash is hash of empty
            /// - all calls will have rootHash param
            ///StateProvider:
            /// - Uncommitted tree - need to traverse to get the value
            /// - Tree commited, so should have RootRef.IsDirty false
            /// - RootRef can be set to a different hash then the latest one persissted, so need to check cache 1st
            if (RootRef?.IsDirty == true)
            {
                bytes = Get(addressKeyBytes);
            }
            else
            {
                bytes = GetCachedAccount(addressKeyBytes, expectedRoot);
                bytes ??= GetPersistedAccount(addressKeyBytes);
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
