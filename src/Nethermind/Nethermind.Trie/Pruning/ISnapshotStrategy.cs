// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public interface IPruningStrategy
    {
        bool PruningEnabled { get; }
        bool ShouldPrune(in long currentMemory);
    }
}
