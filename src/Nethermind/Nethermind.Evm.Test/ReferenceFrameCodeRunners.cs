// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
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
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, FrameRunFlags flags)
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
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, FrameRunFlags flags)
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
public sealed class CheckedSteppingRunner(int steps) : FrameCodeRunner<EthereumGasPolicy>
{
    private delegate EvmExceptionType Execute(ref EvmStack stack, ref EthereumGasPolicy gas, VirtualMachine<EthereumGasPolicy> vm, ref nint programCounter);

    private static readonly ConcurrentDictionary<Type, Execute> _bodies = new();
    private long _stepped;

    /// <summary>The opcodes this runner ran itself.</summary>
    public long Stepped => Interlocked.Read(ref _stepped);

    protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, FrameRunFlags flags)
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
            _bodies.GetOrAdd(body.BodyType!, static type => type
                .GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!
                .CreateDelegate<Execute>())(ref stack, ref gas, vm, ref next);
            programCounter = next;
            vm.OpCodeCount++;
            Interlocked.Increment(ref _stepped);
        }

        return Declined;
    }
}

/// <summary>Forwards offers to a runner chosen per test; declines while none is set.</summary>
public sealed class SwitchingRunner : FrameCodeRunner<EthereumGasPolicy>
{
    public FrameCodeRunner<EthereumGasPolicy>? Inner { get; set; }

    protected internal override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, FrameRunFlags flags) =>
        Inner is { } inner ? inner.Run(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, flags) : Declined;

    protected internal override void OnInterpreted(CodeInfo codeInfo, int opCodes) => Inner?.OnInterpreted(codeInfo, opCodes);
}
