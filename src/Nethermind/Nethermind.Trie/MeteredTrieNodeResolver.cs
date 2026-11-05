// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie
{
    internal sealed class MeteredTrieNodeResolver(ITrieNodeResolver inner, VisitingStats diagnostics) : ITrieNodeResolver
    {
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            diagnostics.RecordLookup();
            diagnostics.ObserveDepth(path.Length);
            return inner.FindCachedOrUnknown(path, hash);
        }

        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            diagnostics.RecordCacheMiss();
            return inner.LoadRlp(path, hash, flags);
        }

        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            diagnostics.RecordCacheMiss();
            return inner.TryLoadRlp(path, hash, flags);
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) =>
            new MeteredTrieNodeResolver(inner.GetStorageTrieNodeResolver(address), diagnostics);

        public INodeStorage.KeyScheme Scheme => inner.Scheme;
    }
}
