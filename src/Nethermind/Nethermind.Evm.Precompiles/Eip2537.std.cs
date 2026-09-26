// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Threading;

namespace Nethermind.Evm.Precompiles;

internal static partial class Eip2537
{
    /// <summary>
    /// Runs <paramref name="decode"/> for every index in [0, <paramref name="count"/>), stopping early once one fails.
    /// </summary>
    /// <returns>A failure if any item failed to decode, otherwise <see cref="Result.Success"/>.</returns>
    /// <remarks>
    /// Items are decoded within a worker budget: the caller decodes too and never waits for a helper that has not
    /// started, so a call cannot stall when the thread pool is saturated (for example by block prewarming).
    /// Which failure is returned when several items fail is unspecified.
    /// </remarks>
    internal static Result DecodeAll(int count, Func<int, Result> decode)
    {
        Result result = Result.Success;

#pragma warning disable CS0162 // Unreachable code detected
        if (DisableConcurrency)
        {
            for (int i = 0; i < count && result; i++)
            {
                result = decode(i);
            }

            return result;
        }
#pragma warning restore CS0162 // Unreachable code detected

        using ParallelUnbalancedWork.WorkerScope workerScope = ParallelUnbalancedWork.BeginWorkerScope(Environment.ProcessorCount);
        ParallelUnbalancedWork.For(0, count, index =>
        {
            if (!result) return;
            Result local = decode(index);
            // racy but safe: workers only ever store a failure, so the result after the barrier fails iff any item did
            if (!local) result = local;
        });

        return result;
    }
}
