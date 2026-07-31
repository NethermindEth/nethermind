// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

namespace Nethermind.Trie
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Number of trie node hash calculations.")]
        public static long TreeNodeHashCalculations => _treeNodeHashCalculations.Sum();
        private static readonly PerThreadCounter _treeNodeHashCalculations = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeHashCalculations() => _treeNodeHashCalculations.Increment();

        [CounterMetric]
        [Description("Number of trie node RLP encodings.")]
        public static long TreeNodeRlpEncodings => _treeNodeRlpEncodings.Sum();
        private static readonly PerThreadCounter _treeNodeRlpEncodings = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpEncodings() => _treeNodeRlpEncodings.Increment();

        [CounterMetric]
        [Description("Number of trie node RLP decodings.")]
        public static long TreeNodeRlpDecodings => _treeNodeRlpDecodings.Sum();
        private static readonly PerThreadCounter _treeNodeRlpDecodings = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpDecodings() => _treeNodeRlpDecodings.Increment();
    }
}
