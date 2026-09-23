// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

/// <inheritdoc cref="DispatchFlags"/>
internal static partial class DispatchFlags
{
    /// <summary>Disables EVM tracing in the guest; receipt collection remains supported.</summary>
    public const bool ConstTracing = false;

    /// <summary>The guest runs to completion or fails; there is nothing to cancel it.</summary>
    public const bool ConstCancelable = false;

    public static bool Tracing(bool isTracing) => ConstTracing;

    public static bool Cancelable(bool tracerIsCancelable) => ConstCancelable;

    /// <summary>Rejects a tracer whose capabilities this build compiled away.</summary>
    /// <remarks>
    /// Unsupported tracers would lose reports or access-list gas simulation, and cancelable tracers
    /// would run past cancellation. Receipt collection remains supported.
    /// </remarks>
    public static void Validate(ITxTracer tracer)
    {
        if (tracer.IsTracingInstructions != ConstTracing)
            throw new NotSupportedException("The zkEVM guest compiles no instruction-tracing dispatch.");
        if (tracer.IsCancelable != ConstCancelable)
            throw new NotSupportedException("The zkEVM guest compiles no cancelable dispatch.");
        if (tracer.IsTracingActions || tracer.IsTracingRefunds || tracer.IsTracingAccess
            || tracer.IsTracingOpLevelStorage || tracer.IsTracingLogs || tracer.IsTracingBlockHash)
            throw new NotSupportedException("The zkEVM guest compiles no EVM tracing.");
    }
}
