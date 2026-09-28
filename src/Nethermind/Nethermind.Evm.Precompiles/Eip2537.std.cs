// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    /// <returns>A failure if any item failed to decode, otherwise <see cref="Result.Success"/>.</returns>
    /// <remarks>
    /// Items are decoded within a worker budget: the caller decodes too and never waits for a helper that has not
    /// started, so a call cannot stall when the thread pool is saturated (for example by block prewarming).
    /// Which failure is returned when several items fail is unspecified.
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
        public Result Result { get; private set; } = Result.Success;

        public void Decode(int index)
        {
            if (!Result) return;
            Result local = decoder.Decode(index);
            // racy but safe: workers only ever store a failure, so the result after the barrier fails iff any item did
            if (!local) Result = local;
        }
    }
}
