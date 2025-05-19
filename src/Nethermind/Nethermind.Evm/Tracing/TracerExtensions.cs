// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Word = System.Runtime.Intrinsics.Vector256<byte>;

namespace Nethermind.Evm.Tracing;

public static class TracerExtensions
{
    public static CancellationTxTracer WithCancellation(this ITxTracer txTracer, CancellationToken cancellationToken)
    {
        return new(txTracer, cancellationToken);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TraceWord(this ITxTracer tracer, in Word value) => tracer.ReportStackPush(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TraceBytes(this ITxTracer tracer, in byte value, int length) => tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(in value, length));
}
