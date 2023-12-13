// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public class NoPruning : IPruningStrategy
    {
        private NoPruning() { }

        public static NoPruning Instance { get; } = new();

        public bool PruningEnabled => false;

        public bool ShouldPrune(in long currentMemory)
        {
            return false;
        }
    }
}
