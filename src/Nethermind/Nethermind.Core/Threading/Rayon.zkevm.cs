// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Threading;

public static partial class Rayon
{
    public static partial int WorkerCount => 1;

    public static partial bool IsWorkerThread => false;

    private static partial (TA, TB) JoinCore<TSa, TSb, TA, TB>(in TSa aState, Func<TSa, bool, TA> a, in TSb bState, Func<TSb, bool, TB> b)
    {
        TA resultA;
        try
        {
            resultA = a(aState, false);
        }
        catch
        {
            // Same contract as the pool: b completes before a's exception surfaces, its own is discarded.
            try
            {
                b(bState, false);
            }
            catch
            {
            }

            throw;
        }

        return (resultA, b(bState, false));
    }

    private static partial void ForCore<TState>(int fromInclusive, int toExclusive, in TState state, Action<TState, int> body)
    {
        for (int i = fromInclusive; i < toExclusive; i++)
        {
            body(state, i);
        }
    }
}
