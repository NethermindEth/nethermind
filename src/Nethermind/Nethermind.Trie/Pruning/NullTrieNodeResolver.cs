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

        public TrieNode FindCachedOrUnknown(Hash256 hash) => new(NodeType.Unknown, hash);
        public T LoadRlp<T, TDeserializer>(TDeserializer deserializer, Hash256 hash, ReadFlags flags = ReadFlags.None) where TDeserializer : ISpanDeserializer<T>
        {
            return deserializer.Deserialize(null);
        }
    }
}
