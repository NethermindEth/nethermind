// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

/// <summary>
/// The tracer capabilities the dispatch specializes on, taken from the tracer running the
/// transaction.
/// </summary>
/// <remarks>
/// Each is read once per transaction, so routing them through this type costs nothing at run time.
/// It exists so the zkEVM build can answer with a constant: an ahead-of-time compiler emits every
/// reachable specialization, and a guest that never traces and never cancels would otherwise carry a
/// second opcode table and a second dispatch loop it can never enter. See
/// <c>DispatchFlags.zkevm.cs</c>.
/// </remarks>
internal static partial class DispatchFlags
{
    /// <summary>Whether the coming transaction reports every opcode to the tracer.</summary>
    public static bool Tracing(bool tracerIsTracingInstructions) => tracerIsTracingInstructions;

    /// <summary>Whether the coming transaction can be cancelled part-way through.</summary>
    public static bool Cancelable(bool tracerIsCancelable) => tracerIsCancelable;

    /// <summary>Rejects a tracer this build cannot serve.</summary>
    /// <remarks>Both capabilities are taken from the tracer here, so every tracer is servable.</remarks>
    public static void Validate(ITxTracer tracer) { }
}
