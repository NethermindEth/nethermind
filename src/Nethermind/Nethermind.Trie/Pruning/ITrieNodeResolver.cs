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
        /// Returns a cached and resolved <see cref="TrieNode"/> or a <see cref="TrieNode"/> with Unknown type
        /// but the hash set. The latter case allows to resolve the node later. Resolving the node means loading
        /// its RLP data from the state database.
        /// </summary>
        /// <remarks>
        /// Phase B prefers <see cref="GetOrLoadNode"/>, <see cref="TryGetOrLoadNode"/>, and
        /// <see cref="TryGetCachedNode"/>. <see cref="FindCachedOrUnknown"/> is retained as a
        /// transitional wrapper for callers that intentionally publish an unresolved placeholder
        /// (e.g. child slots, snap stitching, storage-root inline binding) without immediately
        /// loading RLP. It will be removed once those sites are migrated.
        /// </remarks>
        /// <param name="hash">Keccak hash of the RLP of the node.</param>
        TrieNode FindCachedOrUnknown(in TreePath path, in ValueHash256 hash);

        /// <summary>
        /// Returns a fully resolved <see cref="TrieNode"/>: cache hit if the node is cached
        /// with RLP, otherwise loads RLP from storage and decodes it before returning.
        /// </summary>
        /// <remarks>
        /// Replaces the <c>FindCachedOrUnknown</c> + <c>ResolveNode</c> two-step at call sites
        /// that always immediately use the returned node structurally. The default implementation
        /// falls back to the legacy pair so resolvers that have not yet been migrated keep working.
        /// </remarks>
        TrieNode GetOrLoadNode(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None)
        {
            TrieNode node = FindCachedOrUnknown(in path, in hash);
            node.ResolveNode(this, in path, flags);
            return node;
        }

        /// <summary>
        /// Like <see cref="GetOrLoadNode"/> but returns <c>false</c> if RLP cannot be loaded or
        /// decoded, instead of throwing. Used by best-effort traversal paths that previously
        /// constructed an unknown placeholder and immediately called <c>TryResolveNode</c>.
        /// </summary>
        bool TryGetOrLoadNode(in TreePath path, in ValueHash256 hash, [NotNullWhen(true)] out TrieNode? node, ReadFlags flags = ReadFlags.None)
        {
            TrieNode candidate = FindCachedOrUnknown(in path, in hash);
            TreePath pathCopy = path;
            if (!candidate.TryResolveNode(this, ref pathCopy, flags))
            {
                node = null;
                return false;
            }
            node = candidate;
            return true;
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
