// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using InlineIL;
using Nethermind.Core;
using Nethermind.Core.Specs;
#if DEBUG
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.Debugger;
#endif

namespace Nethermind.Evm;

using static Nethermind.Evm.VirtualMachineStatics;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    // Poll cancellation every 1024 opcodes (low bits of the per-frame op counter).
    private const int CancellationCheckMask = 1023;

    internal struct DispatchState
    {
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, EvmExceptionType>* OpcodeHandlers;
        public VirtualMachine<TGasPolicy> Vm;
        public nint FinalProgramCounter;
        public int OpCodeCount;
        public int CallDepth;
#if DEBUG
        public DebugTracer<TGasPolicy>? Debugger;
        public bool SkipDebuggerWait;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, EvmExceptionType>[]
        GetOpcodeHandlers<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        GetOpcodeTable().GetHandlers<TTracingInst, TCancelable>(Spec);

    private sealed unsafe class OpcodeTable
    {
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, EvmExceptionType>[]? NoTrace;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, EvmExceptionType>[]? NoTraceCancelable;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, EvmExceptionType>[]? Traced;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, EvmExceptionType>[]? TracedCancelable;

        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, EvmExceptionType>[]
            GetHandlers<TTracingInst, TCancelable>(IReleaseSpec spec)
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
        {
            if (TTracingInst.IsActive)
            {
                return TCancelable.IsActive
                    ? TracedCancelable ??= GenerateOpcodeHandlers<TTracingInst, TCancelable>(spec)
                    : Traced ??= GenerateOpcodeHandlers<TTracingInst, TCancelable>(spec);
            }

            return TCancelable.IsActive
                ? NoTraceCancelable ??= GenerateOpcodeHandlers<TTracingInst, TCancelable>(spec)
                : NoTrace ??= GenerateOpcodeHandlers<TTracingInst, TCancelable>(spec);
        }
    }

    /// <summary>Runs the current frame's bytecode until it halts, faults, or yields a child frame.</summary>
    /// <param name="programCounter">On entry the offset to resume from; on exit the offset reached.</param>
    /// <returns>
    /// The halting reason; <c>None</c>, <c>Stop</c> and <c>Revert</c> are normal halts, while <c>Suspend</c>
    /// indicates a yielded child frame.
    /// </returns>
    [SkipLocalsInit]
    private EvmExceptionType RunDispatchLoop<TTracingInst, TCancelable>(
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref nint programCounter)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        if ((nuint)programCounter >= (nuint)stack.CodeLength)
            return EvmExceptionType.None;

        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, EvmExceptionType>[] handlers =
            GetOpcodeHandlers<TTracingInst, TCancelable>();

        // Safety: the 256-entry opcode table remains pinned for the complete tail-call chain. Every
        // bytecode read is preceded by a program-counter bounds check, and a byte is a valid table index.
        fixed (delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, EvmExceptionType>* opcodeHandlers = &handlers[0])
        {
            DispatchState state = new()
            {
                OpcodeHandlers = opcodeHandlers,
                Vm = this,
                CallDepth = VmState.Env.CallDepth,
#if DEBUG
                Debugger = _txTracer.GetTracer<DebugTracer<TGasPolicy>>(),
#endif
            };

            byte opcode = Unsafe.Add(ref stack.Code, programCounter);
            EvmExceptionType exceptionType = opcodeHandlers[opcode](ref stack, ref gas, ref state, programCounter);
            OpCodeCount += state.OpCodeCount;
            programCounter = state.FinalProgramCounter;
            return exceptionType;
        }
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static EvmExceptionType ExecuteOpcode<TOpcode, TTracingInst, TCancelable>(
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref DispatchState state,
        nint pc)
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        VirtualMachine<TGasPolicy> vm = state.Vm;
#if DEBUG
        if (state.SkipDebuggerWait)
        {
            state.SkipDebuggerWait = false;
        }
        else
        {
            nint dispatchedProgramCounter = pc;
            state.Debugger?.TryWait(ref vm._currentState, ref pc, ref gas, ref stack.Head);
            if (pc != dispatchedProgramCounter)
            {
                if ((nuint)pc >= (nuint)stack.CodeLength)
                {
                    state.FinalProgramCounter = pc;
                    return EvmExceptionType.None;
                }
                state.SkipDebuggerWait = true;
                goto DispatchNext;
            }
        }
#endif
        Instruction instruction = (Instruction)Unsafe.Add(ref stack.Code, pc);

        if (TCancelable.IsActive && (state.OpCodeCount & CancellationCheckMask) == 0 && vm._txTracer.IsCancelled)
            ThrowOperationCanceledException();

        TGasPolicy.OnBeforeInstructionTrace(in gas, (int)pc, instruction, state.CallDepth);

        if (TTracingInst.IsActive)
            vm.StartInstructionTrace(instruction, TGasPolicy.GetRemainingGas(in gas), (int)pc, in stack);

        pc++;
        state.OpCodeCount++;
        EvmExceptionType exceptionType = TOpcode.Execute(ref stack, ref gas, vm, ref pc);

        if (ShouldExitFrame(exceptionType, TGasPolicy.IsOutOfGas(in gas)))
            goto Exit;

        Debug.Assert(vm.ReturnData is null,
            "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

        TGasPolicy.OnAfterInstructionTrace(in gas);

        if (TTracingInst.IsActive)
            vm.EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

        if ((nuint)pc >= (nuint)stack.CodeLength)
        {
            state.FinalProgramCounter = pc;
            return EvmExceptionType.None;
        }

#if DEBUG
    DispatchNext:
#endif
        byte nextOpcode = Unsafe.Add(ref stack.Code, pc);
        nint next = (nint)state.OpcodeHandlers[nextOpcode];
        // Keep the target in a real local so InlineIL can place it above the outgoing arguments.
        IL.EnsureLocal(in next);

        IL.Emit.Ldarg(nameof(stack));
        IL.Emit.Ldarg(nameof(gas));
        IL.Emit.Ldarg(nameof(state));
        IL.Emit.Ldarg(nameof(pc));
        IL.Push(next);
        IL.Emit.Tail();
        IL.Emit.Calli(new StandAloneMethodSig(
            CallingConventions.Standard,
            TypeRef.Type<EvmExceptionType>(),
            TypeRef.Type<EvmStack>().MakeByRefType(),
            TypeRef.Type<TGasPolicy>().MakeByRefType(),
            TypeRef.Type<DispatchState>().MakeByRefType(),
            TypeRef.Type<nint>()));
        IL.Emit.Ret();
        throw IL.Unreachable();

    Exit:
        state.FinalProgramCounter = pc;
        if (TGasPolicy.IsOutOfGas(in gas))
        {
            TGasPolicy.SetOutOfGas(ref gas);
            return EvmExceptionType.OutOfGas;
        }

        TGasPolicy.OnAfterInstructionTrace(in gas);
        return exceptionType;
    }
}
