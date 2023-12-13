// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Trie.Pruning
{
    [DebuggerDisplay("{_memoryLimit/(1024*1024)} MB")]
    public class MemoryLimit : IPruningStrategy
    {
        private readonly long _memoryLimit;

        public MemoryLimit(long memoryLimit)
        {
            _memoryLimit = memoryLimit;
        }

        public bool PruningEnabled => true;

        public bool ShouldPrune(in long currentMemory)
        {
            return PruningEnabled && currentMemory >= _memoryLimit;
        }
    }
}
