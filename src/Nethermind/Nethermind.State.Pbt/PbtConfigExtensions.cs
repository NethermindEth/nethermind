// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

public static class PbtConfigExtensions
{
    /// <summary>The settings <paramref name="config"/> selects for a root computation.</summary>
    public static PbtUpdateOptions UpdateOptions(this IPbtConfig config) =>
        new(config.InterleaveTrieNodeLevels ? PbtGroupFormat.Interleaved : PbtGroupFormat.EveryLevel,
            config.TrieUpdateParallelism > 0 ? config.TrieUpdateParallelism : Environment.ProcessorCount);
}
