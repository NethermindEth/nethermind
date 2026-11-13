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
/// with <see cref="PollCancellation"/> at the first taken jump once <see cref="CancellationPollInterval"/> opcodes have
/// run since the offer or its last poll, as the dispatch loop does. On a hand-back it reports the opcodes it ran since
/// then through <c>opCodesSincePoll</c>, which the loop counts on from, so the frame polls exactly where and as often
/// as the interpreter alone would.
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
/// <para>
/// Register a runner only through an <c>IReadOnlyQueryModule</c>, which scopes it to the read-only query environments.
/// The machine takes a runner wherever the container can resolve one, so a runner registered in any other module is
/// attached to block processing, prewarming and block validation as well. Only <see cref="VirtualMachine{TGasPolicy}"/>,
/// <see cref="EthereumVirtualMachine"/> and <see cref="VirtualMachine"/> accept a runner; a subclass that does not pass
/// one to the base constructor, such as Taiko's <c>TaikoEthereumVirtualMachine</c>, runs every frame on the dispatch
/// loop, so a query module on such a chain gets no runner.
/// </para>
/// </remarks>
[Experimental("NMEVM0001")]
public abstract partial class FrameCodeRunner<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    /// <summary>The runner leaves the frame, from the program counter it set, to the dispatch loop.</summary>
    public const EvmExceptionType Declined = (EvmExceptionType)int.MinValue;

    /// <summary>As <see cref="Declined"/>; the machine then reports the opcodes the loop ran through <see cref="OnInterpreted"/>.</summary>
    public const EvmExceptionType DeclinedReportingOpcodes = (EvmExceptionType)(int.MinValue + 1);

    /// <summary>The version of this contract: 2, which adds <see cref="RequestId"/>.</summary>
    /// <remarks>
    /// Runners must check it before they run anything, and a runner built against another version must decline every
    /// frame. It is a property rather than a constant so the check reads the version of the core the runner is loaded
    /// into, not the one it was compiled against.
    /// </remarks>
    public static int ContractVersion => 2;

    /// <summary>Runs the frame of <paramref name="codeInfo"/> from <paramref name="programCounter"/>.</summary>
    /// <param name="bodies">What each entry of the untraced dispatch table the frame would run on executes.</param>
    /// <param name="opCodesSincePoll">
    /// Zero on entry. On <see cref="Declined"/> or <see cref="DeclinedReportingOpcodes"/> in a cancelable run, the
    /// opcodes the runner counted since the offer or its last <see cref="PollCancellation"/>, whichever came later, as
    /// the dispatch loop counts them; the loop then polls at its first taken jump once the interval is spent. Read only
    /// on a hand-back in a cancelable run.
    /// </param>
    protected internal abstract EvmExceptionType Run(
        VirtualMachine<TGasPolicy> vm,
        CodeInfo codeInfo,
        OpcodeBodySet bodies,
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref nint programCounter,
        ref int opCodesSincePoll,
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
