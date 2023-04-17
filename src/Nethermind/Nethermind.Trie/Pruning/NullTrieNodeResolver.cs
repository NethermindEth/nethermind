// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public class NullTrieNodeResolver : ITrieNodeResolver
    {
        private NullTrieNodeResolver() { }

        public static readonly NullTrieNodeResolver Instance = new();

        public TrieNode FindCachedOrUnknown(Keccak hash) => new(NodeType.Unknown, hash);
        public byte[]? LoadRlp(Keccak hash, ReadFlags flags = ReadFlags.None) => null;
    }
}
