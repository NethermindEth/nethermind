// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

/// <summary>
/// Selects tracing and cancellation capabilities supported by the current build.
/// </summary>
/// <remarks>
/// The standard build forwards tracer capabilities through identity methods that inline away.
/// The zkEVM build returns constants so ahead-of-time compilation can remove unsupported dispatch
/// specializations and per-site tracing branches. See <c>DispatchFlags.zkevm.cs</c>.
/// </remarks>
internal static partial class DispatchFlags
{
    /// <summary>Whether this build supports EVM tracing.</summary>
    public const bool ConstTracing = true;

    /// <summary>Whether the requested EVM tracing capability is enabled.</summary>
    public static bool Tracing(bool isTracing) => isTracing;

    /// <summary>Whether dispatch counts the opcodes it runs, for the opcode metric and the cancellation poll.</summary>
    public static bool CountOpcodes => true;

    /// <summary>Whether every executed code is followed in memory by zero bytes that dispatch may read.</summary>
    /// <remarks>
    /// When set, dispatch reads the next opcode and PUSH immediates without testing the program counter against
    /// the code length: running off the end reads STOP from the padding. See <c>CodeInfo.PadForDispatch</c>.
    /// </remarks>
    public static bool PaddedCode => false;

    /// <summary>Whether the coming transaction can be cancelled part-way through.</summary>
    public static bool Cancelable(bool tracerIsCancelable) => tracerIsCancelable;

    /// <summary>Rejects a tracer this build cannot serve.</summary>
    /// <remarks>Capabilities are taken from the tracer here, so every tracer is servable.</remarks>
    public static void Validate(ITxTracer tracer) { }
}
