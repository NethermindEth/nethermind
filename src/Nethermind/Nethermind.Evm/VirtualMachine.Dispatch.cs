// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref DispatchState, EvmExceptionType>* OpcodeHandlers;
        public nint ProgramCounter;
        public int OpCodeCount;
        public int CallDepth;
#if DEBUG
        public DebugTracer<TGasPolicy>? Debugger;
        public bool SkipDebuggerWait;
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref DispatchState, EvmExceptionType>[]
        GetOpcodeHandlers<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        GetOpcodeTable().GetHandlers<TTracingInst, TCancelable>(Spec);

    private sealed unsafe class OpcodeTable
    {
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref DispatchState, EvmExceptionType>[]? NoTrace;
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref DispatchState, EvmExceptionType>[]? NoTraceCancelable;
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref DispatchState, EvmExceptionType>[]? Traced;
        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref DispatchState, EvmExceptionType>[]? TracedCancelable;

        public delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref DispatchState, EvmExceptionType>[]
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
    /// <returns>The halting reason; <c>None</c>, <c>Stop</c> and <c>Revert</c> are normal halts.</returns>
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

        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref DispatchState, EvmExceptionType>[] handlers =
            GetOpcodeHandlers<TTracingInst, TCancelable>();

        // Safety: the 256-entry opcode table remains pinned for the complete tail-call chain. Every
        // bytecode read is preceded by a program-counter bounds check, and a byte is a valid table index.
        fixed (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref DispatchState, EvmExceptionType>* opcodeHandlers = &handlers[0])
        {
            DispatchState state = new()
            {
                OpcodeHandlers = opcodeHandlers,
                ProgramCounter = programCounter,
                CallDepth = VmState.Env.CallDepth,
#if DEBUG
                Debugger = _txTracer.GetTracer<DebugTracer<TGasPolicy>>(),
#endif
            };

            byte opcode = Unsafe.Add(ref stack.Code, programCounter);
            EvmExceptionType exceptionType = opcodeHandlers[opcode](this, ref stack, ref gas, ref state);
            OpCodeCount += state.OpCodeCount;
            programCounter = state.ProgramCounter;
            return exceptionType;
        }
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static EvmExceptionType ExecuteOpcode<TOpcode, TTracingInst, TCancelable>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref DispatchState state)
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        nint pc = state.ProgramCounter;
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
                state.ProgramCounter = pc;
                if ((nuint)pc >= (nuint)stack.CodeLength)
                    return EvmExceptionType.None;
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
        EvmExceptionType exceptionType = TOpcode.Execute(vm, ref stack, ref gas, ref pc);
        state.ProgramCounter = pc;

        if (TGasPolicy.IsOutOfGas(in gas))
        {
            TGasPolicy.SetOutOfGas(ref gas);
            return EvmExceptionType.OutOfGas;
        }

        TGasPolicy.OnAfterInstructionTrace(in gas);

        if (exceptionType != EvmExceptionType.None)
            return exceptionType;

        if (TTracingInst.IsActive)
            vm.EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

        if (instruction >= Instruction.CREATE && vm.ReturnData is not null)
            return EvmExceptionType.None;

        if ((nuint)pc >= (nuint)stack.CodeLength)
            return EvmExceptionType.None;

#if DEBUG
    DispatchNext:
#endif
        byte nextOpcode = Unsafe.Add(ref stack.Code, pc);
        nint next = (nint)state.OpcodeHandlers[nextOpcode];
        // Keep the target in a real local so InlineIL can place it above the outgoing arguments.
        IL.EnsureLocal(in next);

        IL.Emit.Ldarg(nameof(vm));
        IL.Emit.Ldarg(nameof(stack));
        IL.Emit.Ldarg(nameof(gas));
        IL.Emit.Ldarg(nameof(state));
        IL.Push(next);
        IL.Emit.Tail();
        IL.Emit.Calli(new StandAloneMethodSig(
            CallingConventions.Standard,
            TypeRef.Type<EvmExceptionType>(),
            TypeRef.Type<VirtualMachine<TGasPolicy>>(),
            TypeRef.Type<EvmStack>().MakeByRefType(),
            TypeRef.Type<TGasPolicy>().MakeByRefType(),
            TypeRef.Type<DispatchState>().MakeByRefType()));
        IL.Emit.Ret();
        throw IL.Unreachable();
    }
}
