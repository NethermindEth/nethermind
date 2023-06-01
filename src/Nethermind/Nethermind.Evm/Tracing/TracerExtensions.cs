// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Evm.Tracing;

public static class TracerExtensions
{
    public static CancellationTxTracer WithCancellation(this ITxTracer txTracer, CancellationToken cancellationToken, bool setDefaultCancellations = true)
    {
        return !setDefaultCancellations
            ? new(txTracer, cancellationToken)
            : new(txTracer, cancellationToken)
            {
                IsTracingActions = true,
                IsTracingOpLevelStorage = true,
                IsTracingInstructions = true, // a little bit costly but almost all are simple calls
                IsTracingRefunds = true
            };
    }

    public static CancellationBlockTracer WithCancellation(this IBlockTracer blockTracer, CancellationToken cancellationToken) =>
        new(blockTracer, cancellationToken);

    public static T? GetTracer<T>(this ITxTracer txTracer)
        where T : class, ITxTracer
    {
        if (txTracer is null)
            return null;
        if (txTracer is T foundTracer)
        {
            return foundTracer;
        }
        if (txTracer is ITxTracerWrapper txTracerWrapper)
        {
            return GetTracer<T>(txTracerWrapper.InnerTracer);
        }
        if (txTracer is CompositeTxTracer compositeTxTracer)
        {
            foreach (ITxTracer tracer in compositeTxTracer._txTracers)
            {
                if (GetTracer<T>(tracer) is T foundTracer2)
                {
                    return foundTracer2;
                }
            }
        }
        return null;
    }
}
