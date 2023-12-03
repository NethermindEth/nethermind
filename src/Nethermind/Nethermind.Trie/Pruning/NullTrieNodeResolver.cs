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

        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);
        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => null;
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 storage)
        {
            return this;
        }

        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
    }
}
