// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    public interface ITrieNodeResolver
    {
        /// <summary>
        /// Returns a cached and resolved <see cref="TrieNode"/> or a <see cref="TrieNode"/> with Unknown type
        /// but the hash set. The latter case allows to resolve the node later. Resolving the node means loading
        /// its RLP data from the state database.
        /// </summary>
        /// <param name="hash">Keccak hash of the RLP of the node.</param>
        /// <returns></returns>
        TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash);

        /// <summary>
        /// Loads RLP of the node.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);

        /// <summary>
        /// Loads RLP of the node, but return null instead of throwing if does not exist.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);

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
