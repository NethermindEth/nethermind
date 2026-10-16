// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

/// <summary>Runs a call frame's bytecode on behalf of the dispatch loop, in whole or in part.</summary>
/// <remarks>
/// <para>
/// A machine constructed with a runner offers it every frame it runs without an instruction tracer: on entry and on
/// every resume after a child frame, whenever the program counter is inside the code. A frame resumed at the end of
/// its code stops without an offer, as the dispatch loop stops it. In a cancelable run the machine has polled
/// cancellation immediately before every offer, so <see cref="Run"/> must not poll before its first opcode; it polls
/// with <see cref="PollCancellation"/> at taken jumps, once <see cref="CancellationPollInterval"/> opcodes have run
/// since the offer, as the dispatch loop does.
/// </para>
/// <para>
/// <see cref="Run"/> returns either what the dispatch loop would have returned for the rest of the frame, with the
/// program counter, stack head, gas, memory, return data and <see cref="VirtualMachine{TGasPolicy}.OpCodeCount"/>
/// left exactly as the loop leaves them (a STOP read from the execution padding is not counted), or
/// <see cref="Declined"/> or <see cref="DeclinedReportingOpcodes"/>, with all the same state exactly what the
/// interpreter holds before the instruction at the program counter, from where the loop runs the frame.
/// </para>
/// <para>
/// <see cref="Run"/> may throw only what opcode bodies and <see cref="PollCancellation"/> throw. One instance serves many
/// machines and threads.
/// </para>
/// </remarks>
[Experimental("NMEVM0001")]
public abstract partial class FrameCodeRunner<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    /// <summary>The runner leaves the frame, from the program counter it set, to the dispatch loop.</summary>
    public const EvmExceptionType Declined = (EvmExceptionType)int.MinValue;

    /// <summary>As <see cref="Declined"/>; the machine then reports the opcodes the loop ran through <see cref="OnInterpreted"/>.</summary>
    public const EvmExceptionType DeclinedReportingOpcodes = (EvmExceptionType)(int.MinValue + 1);

    /// <summary>The version of this contract: 1.</summary>
    /// <remarks>
    /// Runners must check it before they run anything, and a runner built against another version must decline every
    /// frame. It is a property rather than a constant so the check reads the version of the core the runner is loaded
    /// into, not the one it was compiled against.
    /// </remarks>
    public static int ContractVersion => 1;

    /// <summary>Runs the frame of <paramref name="codeInfo"/> from <paramref name="programCounter"/>.</summary>
    /// <param name="bodies">What each entry of the untraced dispatch table the frame would run on executes.</param>
    protected internal abstract EvmExceptionType Run(
        VirtualMachine<TGasPolicy> vm,
        CodeInfo codeInfo,
        OpcodeBodySet bodies,
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref nint programCounter,
        FrameRunFlags flags);

    /// <summary>The dispatch loop ran <paramref name="opCodes"/> opcodes of a frame this runner declined with <see cref="DeclinedReportingOpcodes"/>.</summary>
    protected internal virtual void OnInterpreted(CodeInfo codeInfo, int opCodes) { }
}

/// <summary>How a frame is offered to a <see cref="FrameCodeRunner{TGasPolicy}"/>.</summary>
[Experimental("NMEVM0001")]
[Flags]
public enum FrameRunFlags : byte
{
    None = 0,
    /// <summary>The run is cancelable: the runner polls where the dispatch loop would.</summary>
    Cancelable = 1,
}
