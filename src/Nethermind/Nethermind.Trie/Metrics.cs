// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

namespace Nethermind.Trie
{
    // Per-node counters are incremented from the parallel commit/resolve paths (trie commit tasks,
    // ParallelUnbalancedWork encode workers, prewarm scopes) as well as the block-processing thread,
    // so they use the main/other split on cache-line-padded slots: the block thread updates a line
    // no other thread touches, and the counters do not false-share with each other.
    public static class Metrics
    {
        private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

        [CounterMetric]
        [Description("Number of trie node hash calculations.")]
        public static long TreeNodeHashCalculations => _mainTreeNodeHashCalculations.Value + _otherTreeNodeHashCalculations.Value;
        private static CacheLinePaddedLong _mainTreeNodeHashCalculations;
        private static CacheLinePaddedLong _otherTreeNodeHashCalculations;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeHashCalculations() =>
            Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainTreeNodeHashCalculations.Value : ref _otherTreeNodeHashCalculations.Value);

        [CounterMetric]
        [Description("Number of trie node RLP encodings.")]
        public static long TreeNodeRlpEncodings => _mainTreeNodeRlpEncodings.Value + _otherTreeNodeRlpEncodings.Value;
        private static CacheLinePaddedLong _mainTreeNodeRlpEncodings;
        private static CacheLinePaddedLong _otherTreeNodeRlpEncodings;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpEncodings() =>
            Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainTreeNodeRlpEncodings.Value : ref _otherTreeNodeRlpEncodings.Value);

        [CounterMetric]
        [Description("Number of trie node RLP decodings.")]
        public static long TreeNodeRlpDecodings => _mainTreeNodeRlpDecodings.Value + _otherTreeNodeRlpDecodings.Value;
        private static CacheLinePaddedLong _mainTreeNodeRlpDecodings;
        private static CacheLinePaddedLong _otherTreeNodeRlpDecodings;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpDecodings() =>
            Interlocked.Increment(ref IsBlockProcessingThread ? ref _mainTreeNodeRlpDecodings.Value : ref _otherTreeNodeRlpDecodings.Value);
    }
}
