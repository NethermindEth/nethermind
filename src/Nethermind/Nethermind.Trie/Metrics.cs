// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nethermind.Core.Attributes;

namespace Nethermind.Trie
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Number of trie node hash calculations.")]
        public static long TreeNodeHashCalculations => TrieNodeCounters.TotalHashCalculations;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeHashCalculations() => TrieNodeCounters.IncrementHashCalculations();

        [CounterMetric]
        [Description("Number of trie node RLP encodings.")]
        public static long TreeNodeRlpEncodings => TrieNodeCounters.TotalRlpEncodings;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpEncodings() => TrieNodeCounters.IncrementRlpEncodings();

        [CounterMetric]
        [Description("Number of trie node RLP decodings.")]
        public static long TreeNodeRlpDecodings => TrieNodeCounters.TotalRlpDecodings;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpDecodings() => TrieNodeCounters.IncrementRlpDecodings();
    }
}
