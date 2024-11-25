// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;

namespace Nethermind.Trie.Pruning
{
    [DebuggerDisplay("{_memoryLimit/(1024*1024)} MB")]
    public class MemoryLimit(long memoryLimit) : IPruningStrategy
    {
        public bool PruningEnabled => true;
        public int MaxDepth => (int)Reorganization.MaxDepth;
        public bool ShouldPrune(in long currentMemory) => PruningEnabled && currentMemory >= memoryLimit;
        public int TrackedPastKeyCount => 0;
    }
}
