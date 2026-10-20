// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

namespace Nethermind.Trie
{
    public static class Metrics
    {
        private static bool IsBlockProcessingThread => ProcessingThread.IsBlockProcessingThread;

        // The block-processing thread keeps its dedicated padded word; every other thread (RPC
        // workers, prewarm workers) previously shared ONE "other" word per counter, making each
        // increment a contended cross-core RMW under concurrent load — striped instead.
        [CounterMetric]
        [Description("Number of trie node hash calculations.")]
        public static long TreeNodeHashCalculations => _mainTreeNodeHashCalculations.Value + _otherTreeNodeHashCalculations.Sum;
        private static CacheLinePaddedLong _mainTreeNodeHashCalculations;
        private static readonly StripedLong _otherTreeNodeHashCalculations = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeHashCalculations()
        {
            if (IsBlockProcessingThread) Interlocked.Increment(ref _mainTreeNodeHashCalculations.Value);
            else _otherTreeNodeHashCalculations.Increment();
        }

        [CounterMetric]
        [Description("Number of trie node RLP encodings.")]
        public static long TreeNodeRlpEncodings => _mainTreeNodeRlpEncodings.Value + _otherTreeNodeRlpEncodings.Sum;
        private static CacheLinePaddedLong _mainTreeNodeRlpEncodings;
        private static readonly StripedLong _otherTreeNodeRlpEncodings = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpEncodings()
        {
            if (IsBlockProcessingThread) Interlocked.Increment(ref _mainTreeNodeRlpEncodings.Value);
            else _otherTreeNodeRlpEncodings.Increment();
        }

        [CounterMetric]
        [Description("Number of trie node RLP decodings.")]
        public static long TreeNodeRlpDecodings => _mainTreeNodeRlpDecodings.Value + _otherTreeNodeRlpDecodings.Sum;
        private static CacheLinePaddedLong _mainTreeNodeRlpDecodings;
        private static readonly StripedLong _otherTreeNodeRlpDecodings = new();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void IncrementTreeNodeRlpDecodings()
        {
            if (IsBlockProcessingThread) Interlocked.Increment(ref _mainTreeNodeRlpDecodings.Value);
            else _otherTreeNodeRlpDecodings.Increment();
        }
    }
}
