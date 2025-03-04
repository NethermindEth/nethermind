// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;

namespace Nethermind.Trie.Pruning
{
    [DebuggerDisplay("{dirtyMemoryLimit/(1024*1024)} MB, {persistedMemoryLimit/(1024*1024)} MB")]
    public class MemoryLimit(long dirtyMemoryLimit, long totalMemoryLimit) : IPruningStrategy
    {
        public bool PruningEnabled => true;
        public int MaxDepth => (int)Reorganization.MaxDepth;
        public bool ShouldPruneDirtyNode(in long dirtyNodeMemory) => dirtyNodeMemory >= dirtyMemoryLimit;
        public bool ShouldPrunePersistedNode(in long persistedNodeMemory) => persistedNodeMemory >= (totalMemoryLimit - dirtyMemoryLimit);
        public int TrackedPastKeyCount => 0;
    }
}
