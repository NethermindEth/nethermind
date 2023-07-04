// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public static class No
    {
        public static IPersistenceStrategy Persistence => NoPersistence.Instance;
        public static IPruningStrategy Pruning => NoPruning.Instance;
    }
}
