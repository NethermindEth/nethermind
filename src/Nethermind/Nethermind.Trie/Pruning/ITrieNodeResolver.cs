// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieNodeResolver
    {
        /// <summary>
        /// Returns a fully resolved <see cref="TrieNode"/>: cache hit if the node is cached
        /// with RLP, otherwise loads RLP from storage and decodes it before returning.
        /// Throws <see cref="MissingTrieNodeException"/> when the node is absent; use
        /// <see cref="TryGetOrLoadNode"/> for best-effort lookups.
        /// </summary>
        TrieNode GetOrLoadNode(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None)
        {
            if (TryGetCachedNode(in path, in hash, out TrieNode? cached))
            {
                return cached;
            }

            byte[]? rlp = LoadRlp(in path, in hash, flags)
                ?? throw new MissingTrieNodeException("Node missing", null, path, new Hash256(in hash));

            return TrieNode.DecodeNode(in path, in hash, rlp);
        }

        /// <summary>
        /// Like <see cref="GetOrLoadNode"/> but returns <c>false</c> if RLP cannot be loaded or
        /// decoded, instead of throwing. Used by best-effort traversal paths.
        /// </summary>
        bool TryGetOrLoadNode(in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None)
        {
            if (TryGetCachedNode(in path, in hash, out node))
            {
                return true;
            }

            byte[]? rlp = TryLoadRlp(in path, in hash, flags);
            if (rlp is null)
            {
                node = null;
                return false;
            }

            try
            {
                node = TrieNode.DecodeNode(in path, in hash, rlp);
                return true;
            }
            catch (TrieException)
            {
                node = null;
                return false;
            }
        }

        /// <summary>
        /// Cache-only lookup. Returns <c>true</c> with the cached typed (resolved) node when one
        /// exists. Never allocates a placeholder, never loads RLP. Default implementation always
        /// returns <c>false</c>; resolvers that maintain a node cache override.
        /// </summary>
        bool TryGetCachedNode(in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            node = null;
            return false;
        }

        /// <summary>
        /// Loads RLP of the node.
        /// </summary>
        byte[]? LoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None);

        /// <summary>
        /// Loads RLP of the node, but return null instead of throwing if does not exist.
        /// </summary>
        byte[]? TryLoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None);

        /// <summary>
        /// Got another node resolver for another trie. Used for tree traversal. For simplicity, if address is null,
        /// return state trie.
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        [Todo("Find a way to not have this. PatriciaTrie on its own does not need the concept of storage.")]
        ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address);

        INodeStorage.KeyScheme Scheme { get; }
    }
}
