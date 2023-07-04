// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Trie.Pruning
{
    // ReSharper disable once InconsistentNaming
    public static class ITrieStoreExtensions
    {
        public static IReadOnlyTrieStore AsReadOnly(this ITrieStore trieStore, IKeyValueStore? readOnlyStore = null) =>
            trieStore.AsReadOnly(readOnlyStore);
    }
}
