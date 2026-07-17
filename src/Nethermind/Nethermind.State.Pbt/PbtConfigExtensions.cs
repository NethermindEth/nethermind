// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

internal static class PbtConfigExtensions
{
    /// <summary>The group encoding <paramref name="config"/> selects for the trie nodes this node writes.</summary>
    public static PbtGroupFormat TrieNodeWriteFormat(this IPbtConfig config) =>
        config.InterleaveTrieNodeLevels ? PbtGroupFormat.Interleaved : PbtGroupFormat.EveryLevel;
}
