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
        public static long TreeNodeHashCalculations => StripedCounter.Sum(_treeNodeHashCalculations);
        private static readonly CacheLinePaddedLong[] _treeNodeHashCalculations = StripedCounter.Create();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeHashCalculations() => StripedCounter.Increment(_treeNodeHashCalculations);

        [CounterMetric]
        [Description("Number of trie node RLP encodings.")]
        public static long TreeNodeRlpEncodings => StripedCounter.Sum(_treeNodeRlpEncodings);
        private static readonly CacheLinePaddedLong[] _treeNodeRlpEncodings = StripedCounter.Create();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpEncodings() => StripedCounter.Increment(_treeNodeRlpEncodings);

        [CounterMetric]
        [Description("Number of trie node RLP decodings.")]
        public static long TreeNodeRlpDecodings => StripedCounter.Sum(_treeNodeRlpDecodings);
        private static readonly CacheLinePaddedLong[] _treeNodeRlpDecodings = StripedCounter.Create();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpDecodings() => StripedCounter.Increment(_treeNodeRlpDecodings);
    }
}
