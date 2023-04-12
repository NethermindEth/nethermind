// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class NullTrieNodeResolver : ITrieNodeResolver
    {
        private NullTrieNodeResolver() { }

        public static readonly NullTrieNodeResolver Instance = new();

        public TrieNodeResolverCapability Capability => TrieNodeResolverCapability.Hash;

        public TrieNode FindCachedOrUnknown(Keccak hash) => new(NodeType.Unknown, hash);
        public TrieNode FindCachedOrUnknown(Keccak hash, Span<byte> nodePath, Span<byte> storagePrefix) => new(NodeType.Unknown, nodePath, hash){StoreNibblePathPrefix = storagePrefix.ToArray()};

        public byte[]? LoadRlp(Keccak hash) => null;

        public byte[]? LoadRlp(Span<byte> nodePath, Keccak rootHash)
        {
            return null;
        }

        public bool ExistsInDB(Keccak hash, byte[] nodePathNibbles)
        {
            return false;
        }
    }
}
