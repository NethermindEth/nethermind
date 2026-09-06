// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

/// <inheritdoc cref="DispatchFlags"/>
internal static partial class DispatchFlags
{
    /// <summary>The guest proves a block; it reports nothing per opcode.</summary>
    public const bool ConstTracing = false;

    /// <summary>The guest runs to completion or fails; there is nothing to cancel it.</summary>
    public const bool ConstCancelable = false;

    public static bool Tracing(bool tracerIsTracingInstructions) => ConstTracing;

    public static bool Cancelable(bool tracerIsCancelable) => ConstCancelable;

    /// <summary>Rejects a tracer whose capabilities this build compiled away.</summary>
    /// <remarks>
    /// Without this a tracing tracer would silently see no opcodes, and a cancelable one would run
    /// past its cancellation, because neither specialization was compiled.
    /// </remarks>
    public static void Validate(ITxTracer tracer)
    {
        if (tracer.IsTracingInstructions != ConstTracing)
            throw new NotSupportedException("The zkEVM guest compiles no instruction-tracing dispatch.");
        if (tracer.IsCancelable != ConstCancelable)
            throw new NotSupportedException("The zkEVM guest compiles no cancelable dispatch.");
    }
}
