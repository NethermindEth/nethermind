// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Threading;

namespace Nethermind.Evm.Precompiles;

internal static partial class Eip2537
{
    /// <summary>Decodes and validates one input item into its destination buffer.</summary>
    internal interface IItemDecoder
    {
        Result Decode(int index);
    }

    /// <summary>
    /// Runs <paramref name="decoder"/> for every index in [0, <paramref name="count"/>), stopping early once one fails.
    /// </summary>
    /// <returns>The failure of the lowest index that failed to decode, otherwise <see cref="Result.Success"/>.</returns>
    /// <remarks>
    /// Items are decoded within a worker budget: the caller decodes too and never waits for a helper that has not
    /// started, so a call cannot stall when the thread pool is saturated (for example by block prewarming).
    /// The failure reported is the one decoding the items in order meets first, whatever order they run in.
    /// </remarks>
    internal static Result DecodeAll<TDecoder>(int count, TDecoder decoder) where TDecoder : struct, IItemDecoder
    {
#pragma warning disable CS0162 // Unreachable code detected
        if (DisableConcurrency)
        {
            Result result = Result.Success;
            for (int i = 0; i < count && result; i++)
            {
                result = decoder.Decode(i);
            }

            return result;
        }
        else
        {
            using ParallelUnbalancedWork.WorkerScope workerScope = ParallelUnbalancedWork.BeginWorkerScope(Environment.ProcessorCount);
            DecodeAllState<TDecoder> state = new(decoder);
            ParallelUnbalancedWork.For(0, count, state, static (index, s) =>
            {
                s.Decode(index);
                return s;
            });

            return state.Result;
        }
#pragma warning restore CS0162 // Unreachable code detected
    }

    private sealed class DecodeAllState<TDecoder>(TDecoder decoder) where TDecoder : struct, IItemDecoder
    {
        private readonly Lock _lock = new();
        private int _failedIndex = int.MaxValue;

        public Result Result { get; private set; } = Result.Success;

        public void Decode(int index)
        {
            // an item after a known failure cannot change which failure is reported
            if (index > Volatile.Read(ref _failedIndex)) return;
            Result local = decoder.Decode(index);
            if (local) return;

            lock (_lock)
            {
                if (index < _failedIndex)
                {
                    Result = local;
                    Volatile.Write(ref _failedIndex, index);
                }
            }
        }
    }
}
