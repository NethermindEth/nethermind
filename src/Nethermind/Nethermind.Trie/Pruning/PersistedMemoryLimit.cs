// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie.Pruning;

[DebuggerDisplay("{persistedMemoryLimit/(1024*1024)} MB")]
public class PersistedMemoryLimit(IPruningStrategy baseStrategy, long persistedMemoryLimit) : IPruningStrategy
{
    public bool PruningEnabled => baseStrategy.PruningEnabled;

    public int MaxDepth => baseStrategy.MaxDepth;

    public bool ShouldPruneDirtyNode(in long dirtyNodeMemory) => baseStrategy.ShouldPruneDirtyNode(in dirtyNodeMemory);

    public bool ShouldPrunePersistedNode(in long persistedNodeMemory) => (persistedNodeMemory >= persistedMemoryLimit);

    // Target prune is either 5% of the persisted node memory or at least 50MiB.
    public double PrunePersistedNodePortion => 0.05;
    public long PrunePersistedNodeMinimumTarget => 50.MiB();

    public int TrackedPastKeyCount => baseStrategy.TrackedPastKeyCount;
    public int ShardBit => baseStrategy.ShardBit;
}
