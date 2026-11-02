// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm.Test;

/// <summary>Declines every frame, counting the offers.</summary>
public sealed class DecliningRunner : FrameCodeRunner<EthereumGasPolicy>
{
    private int _offers;

    public int Offers => _offers;

    protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        Interlocked.Increment(ref _offers);
        return Declined;
    }
}

/// <summary>Declines every frame reporting opcodes, recording each offer and the opcodes the dispatch loop then ran.</summary>
public sealed class CountingRunner : FrameCodeRunner<EthereumGasPolicy>
{
    private readonly ConcurrentQueue<(nint ProgramCounter, FrameRunFlags Flags)> _offers = new();
    private long _interpreted;

    public IReadOnlyCollection<(nint ProgramCounter, FrameRunFlags Flags)> Offers => _offers;
    public long Interpreted => Interlocked.Read(ref _interpreted);

    protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        _offers.Enqueue((programCounter, flags));
        return DeclinedReportingOpcodes;
    }

    protected internal override void OnInterpreted(CodeInfo codeInfo, int opCodes) => Interlocked.Add(ref _interpreted, opCodes);
}

/// <summary>
/// Runs up to <c>steps</c> straight-line checked bodies itself on each offer, from what <see cref="OpcodeBodySet"/>
/// declares about them, then hands the frame back to the dispatch loop.
/// </summary>
/// <remarks>
/// It stops before anything it would have to model further: an entry that is not a checked, continuing body, a
/// check the interpreter would fail, or the end of the code. A hand-back must leave exactly the interpreter's state.
/// </remarks>
public sealed unsafe class CheckedSteppingRunner(int steps) : FrameCodeRunner<EthereumGasPolicy>
{
    private long _stepped;

    /// <summary>The opcodes this runner ran itself.</summary>
    public long Stepped => Interlocked.Read(ref _stepped);

    protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        ReadOnlySpan<byte> code = codeInfo.CodeSpan;
        for (int step = 0; step < steps && (nuint)programCounter < (nuint)code.Length; step++)
        {
            OpcodeBody body = bodies.Bodies[code[(int)programCounter]];
            if (!body.HasCheckedBody || body.Flow != OpcodeFlow.Continues || body.MayJump || body.FixedGas == ulong.MaxValue ||
                gas.Value < body.FixedGas || stack.Head < body.StackInputs ||
                (body.StackGrowth > 0 && stack.Head + body.StackGrowth >= EvmStack.MaxStackSize))
            {
                break;
            }

            gas.Value -= body.FixedGas;
            nint next = programCounter + 1;
            Execute(body)(ref stack, ref gas, vm, ref next);
            programCounter = next;
            vm.OpCodeCount++;
            opCodesSincePoll++;
            Interlocked.Increment(ref _stepped);
        }

        return Declined;
    }

    private static delegate*<ref EvmStack, ref EthereumGasPolicy, VirtualMachine<EthereumGasPolicy>, ref nint, EvmExceptionType> Execute(OpcodeBody body) =>
        (delegate*<ref EvmStack, ref EthereumGasPolicy, VirtualMachine<EthereumGasPolicy>, ref nint, EvmExceptionType>)body.Execute;
}

/// <summary>
/// Runs the frame itself from each offer while it meets only checked straight-line bodies and the unchecked bodies that
/// end the frame, returning the frame's own result when it reaches one of those; declines at anything else.
/// </summary>
public sealed unsafe class FinishingRunner : FrameCodeRunner<EthereumGasPolicy>
{
    private int _finished;

    /// <summary>The frames this runner ran to their end.</summary>
    public int Finished => _finished;

    protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        ReadOnlySpan<byte> code = codeInfo.CodeSpan;
        while ((nuint)programCounter < (nuint)code.Length)
        {
            OpcodeBody body = bodies.Bodies[code[(int)programCounter]];
            nint next = programCounter + 1;
            if (body.Flow == OpcodeFlow.Terminates && !body.HasCheckedBody && body.Execute != 0)
            {
                EvmExceptionType result = Execute(body)(ref stack, ref gas, vm, ref next);
                programCounter = next;
                vm.AddOpCodes(1);
                Interlocked.Increment(ref _finished);
                return result;
            }

            if (!body.HasCheckedBody || body.Flow != OpcodeFlow.Continues || body.MayJump || body.FixedGas == ulong.MaxValue ||
                gas.Value < body.FixedGas || stack.Head < body.StackInputs ||
                (body.StackGrowth > 0 && stack.Head + body.StackGrowth >= EvmStack.MaxStackSize))
            {
                break;
            }

            gas.Value -= body.FixedGas;
            Execute(body)(ref stack, ref gas, vm, ref next);
            programCounter = next;
            vm.AddOpCodes(1);
            opCodesSincePoll++;
        }

        return Declined;
    }

    private static delegate*<ref EvmStack, ref EthereumGasPolicy, VirtualMachine<EthereumGasPolicy>, ref nint, EvmExceptionType> Execute(OpcodeBody body) =>
        (delegate*<ref EvmStack, ref EthereumGasPolicy, VirtualMachine<EthereumGasPolicy>, ref nint, EvmExceptionType>)body.Execute;
}

/// <summary>Polls cancellation through the runner API on every cancelable offer, then declines.</summary>
public sealed class PollingRunner : FrameCodeRunner<EthereumGasPolicy>
{
    protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        if ((flags & FrameRunFlags.Cancelable) != 0)
        {
            PollCancellation(vm);
            opCodesSincePoll = 0;
        }

        return Declined;
    }
}

/// <summary>Keeps one marker object per code in the runner's per-code slot, recording what each offer found there.</summary>
public sealed class CodeStateRunner : FrameCodeRunner<EthereumGasPolicy>
{
    public readonly List<(CodeInfo Code, object? Found, object Kept)> Seen = [];

    protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        ref object? slot = ref CodeState(codeInfo);
        object? found = Volatile.Read(ref slot);
        object kept = found ?? Interlocked.CompareExchange(ref slot, new object(), null) ?? Volatile.Read(ref slot)!;
        Seen.Add((codeInfo, found, kept));
        return Declined;
    }
}

/// <summary>Forwards offers to a runner chosen per test; declines while none is set.</summary>
public sealed class SwitchingRunner : FrameCodeRunner<EthereumGasPolicy>
{
    public FrameCodeRunner<EthereumGasPolicy>? Inner { get; set; }

    protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags) =>
        Inner is { } inner ? inner.Run(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags) : Declined;

    protected internal override void OnInterpreted(CodeInfo codeInfo, int opCodes) => Inner?.OnInterpreted(codeInfo, opCodes);
}
