// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

internal static class PbtConfigExtensions
{
    /// <summary>What the configured tiling and levels amount to, as every producer writes it.</summary>
    public static PbtTrieFormat TrieNodeWriteFormat(this IPbtConfig config) => new(config.TrieNodeTiling, config.TrieNodeLevels);
}
