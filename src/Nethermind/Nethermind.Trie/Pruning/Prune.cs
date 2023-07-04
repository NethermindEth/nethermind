// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public static class Prune
    {
        public static IPruningStrategy WhenCacheReaches(long sizeInBytes)
            => new MemoryLimit(sizeInBytes);
    }
}
