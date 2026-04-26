// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning
{
    // ReSharper disable once InconsistentNaming
    public static class ITrieStoreExtensions
    {
        public static IScopedTrieStore GetTrieStore(this ITrieStore trieStore, Address address) =>
            trieStore.GetTrieStore((Hash256)address.ToAccountPath);
    }
}
