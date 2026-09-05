// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using InlineIL;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm;

using static Nethermind.Evm.VirtualMachineStatics;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    // Poll cancellation every 1024 opcodes (low bits of the per-frame op counter).
    private const int CancellationCheckMask = 1023;

    internal struct DispatchState
    {
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>* OpcodeHandlers;
        public VirtualMachine<TGasPolicy> Vm;

        /// <summary>Where the chain stopped. Written only as the chain leaves.</summary>
        /// <remarks>
        /// This and <see cref="OpCodeCount"/> ride the dispatch signature while the chain runs. A counter
        /// in the struct would be a narrow read-modify-write through a byref on every opcode, which the
        /// zkEVM guest charges at roughly twenty times an aligned load.
        /// </remarks>
        public nint FinalProgramCounter;

        /// <summary>How many opcodes the chain ran. Written only as the chain leaves.</summary>
        public int OpCodeCount;
    }

    /// <summary>The dispatch table the running transaction uses, resolved once by <c>PrepareOpcodes</c>.</summary>
    private delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[] _opcodeHandlers;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[]
        GetOpcodeHandlers<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        GetOpcodeTable().GetHandlers<TTracingInst, TCancelable>(Spec);

    /// <summary>Whether the dispatch table should be rebuilt before the coming transaction.</summary>
    private partial bool ShouldRefreshOpcodes();

    /// <summary>Resolves the dispatch table the coming transaction runs on.</summary>
    /// <remarks>
    /// Once per transaction rather than once per frame: the tracing and cancellation flags hold for the
    /// whole transaction and the spec for the whole block, so every frame the transaction enters or
    /// resumes would resolve the same table, and the per-spec lookup behind it is not free.
    /// </remarks>
    private void PrepareOpcodes<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (DispatchFlags.Cancelable(_isCancelableCached))
            PrepareOpcodes<TTracingInst, OnFlag>();
        else
            PrepareOpcodes<TTracingInst, OffFlag>();
    }

    private void PrepareOpcodes<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        // The fork comes from Spec here and in GetOpcodeTable, so the cache key and the table contents
        // cannot describe different forks.
        IReleaseSpec spec = Spec;
        // Per transaction, not per table build: a cached table would otherwise let a later block
        // outside the compiled fork range run against rules that do not describe it.
        SpecFlags.Validate(spec);
        OpcodeTable table = GetOpcodeTable();

        // Traced tables are left alone: a tracing run is short, and rebuilding one would cost more than
        // the promoted code it could pick up.
        if (!TTracingInst.IsActive && ShouldRefreshOpcodes())
            table.RefreshNonTraced<TTracingInst>(spec);

        _executionHandlers = table.GetExecutionHandlers(spec);
        _opcodeHandlers = table.GetHandlers<TTracingInst, TCancelable>(spec);
    }

    private sealed unsafe class OpcodeTable
    {
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[]? NoTrace;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[]? NoTraceCancelable;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[]? Traced;
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[]? TracedCancelable;

        private ExecutionHandlers? _executionHandlers;

        public ExecutionHandlers GetExecutionHandlers(IReleaseSpec spec)
        {
            ExecutionHandlers? handlers = System.Threading.Volatile.Read(ref _executionHandlers);
            if (handlers is not null) return handlers;
            handlers = new ExecutionHandlers(spec);
            return System.Threading.Interlocked.CompareExchange(ref _executionHandlers, handlers, null) ?? handlers;
        }

        /// <summary>The table for this combination of flags, built on first use.</summary>
        /// <param name="spec">The fork whose opcode set the table describes.</param>
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[]
            GetHandlers<TTracingInst, TCancelable>(IReleaseSpec spec)
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
        {
            ref delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[]? table =
                ref TTracingInst.IsActive
                    ? ref (TCancelable.IsActive ? ref TracedCancelable : ref Traced)
                    : ref (TCancelable.IsActive ? ref NoTraceCancelable : ref NoTrace);

            return table ??= GenerateOpcodeHandlers<TTracingInst, TCancelable>(spec);
        }

        /// <summary>Rebuilds both non-traced tables from whatever the JIT has promoted since the last build.</summary>
        /// <remarks>
        /// A captured function pointer keeps pointing at the code it was taken from. Both non-traced tables
        /// share one cadence, so a rebuild has to cover both: which one a given transaction asks for follows
        /// the node's mix of block processing and RPC, and block processing is what the cadence exists for.
        /// </remarks>
        public void RefreshNonTraced<TTracingInst>(IReleaseSpec spec)
            where TTracingInst : struct, IFlag
        {
            NoTrace = GenerateOpcodeHandlers<TTracingInst, OffFlag>(spec);
            NoTraceCancelable = GenerateOpcodeHandlers<TTracingInst, OnFlag>(spec);
            System.Threading.Volatile.Write(ref _executionHandlers, null);
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

        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[] handlers = _opcodeHandlers;

        // Safety: the 256-entry opcode table remains pinned for the complete tail-call chain. Every
        // bytecode read is preceded by a program-counter bounds check, and a byte is a valid table index.
        fixed (delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>* opcodeHandlers = &handlers[0])
        {
            if (!TCancelable.IsActive)
            {
                DispatchState state = new()
                {
                    OpcodeHandlers = opcodeHandlers,
                    Vm = this,
                };

                byte opcode = Unsafe.Add(ref stack.Code, programCounter);
                EvmExceptionType ordinaryExceptionType = opcodeHandlers[opcode](ref stack, ref gas, ref state, programCounter, 0);
                OpCodeCount += state.OpCodeCount;
                programCounter = state.FinalProgramCounter;
                return ordinaryExceptionType;
            }

            DispatchState cancelableState = new()
            {
                OpcodeHandlers = opcodeHandlers,
                Vm = this,
            };

            if (_txTracer.IsCancelled)
                ThrowOperationCanceledException();

            nint pc = programCounter;
            int opCodeCount = 0;
            EvmExceptionType exceptionType;
            while (true)
            {
                byte opcode = Unsafe.Add(ref stack.Code, pc);
                exceptionType = opcodeHandlers[opcode](ref stack, ref gas, ref cancelableState, pc, opCodeCount);

                // A boundary unwind is the only successful return with a complete batch and a successor.
                if (exceptionType != EvmExceptionType.None ||
                    (cancelableState.OpCodeCount & CancellationCheckMask) != 0 ||
                    (nuint)cancelableState.FinalProgramCounter >= (nuint)stack.CodeLength)
                    break;

                if (_txTracer.IsCancelled)
                    ThrowOperationCanceledException();

                pc = cancelableState.FinalProgramCounter;
                opCodeCount = cancelableState.OpCodeCount;
            }

            OpCodeCount += cancelableState.OpCodeCount;
            programCounter = cancelableState.FinalProgramCounter;
            return exceptionType;
        }
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static EvmExceptionType ExecuteOpcode<TOpcode, TTracingInst, TCancelable, TContinuable>(
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref DispatchState state,
        nint pc,
        int opCodeCount)
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TContinuable : struct, IFlag
    {
        // Only a traced run reads the opcode out of the bytecode. The read costs two dependent loads.
        if (TTracingInst.IsActive)
        {
            Instruction instruction = (Instruction)Unsafe.Add(ref stack.Code, pc);
            state.Vm.StartInstructionTrace(instruction, TGasPolicy.GetRemainingGas(in gas), (int)pc, in stack);
        }

        pc++;
        opCodeCount++;
        EvmExceptionType exceptionType;
        if (TOpcode.HasCheckedBody)
        {
            if (!TOpcode.TryConsumeGas(ref gas))
                return ExitCheckedOpcode(ref state, pc, opCodeCount, EvmExceptionType.OutOfGas);
            if (TOpcode.StackInputs != 0 && !stack.EnsureDepth(TOpcode.StackInputs))
                return ExitCheckedOpcode(ref state, pc, opCodeCount, EvmExceptionType.StackUnderflow);
            if (TOpcode.StackGrowth > 0 && stack.Head >= EvmStack.MaxStackSize - TOpcode.StackGrowth)
                return ExitCheckedOpcode(ref state, pc, opCodeCount, EvmExceptionType.StackOverflow);

            // HasCheckedBody guarantees that Execute needs neither guards nor a VM reference.
            _ = TOpcode.Execute(ref stack, ref gas, null!, ref pc);
            exceptionType = EvmExceptionType.None;
        }
        else
        {
            exceptionType = TOpcode.Execute(ref stack, ref gas, state.Vm, ref pc);
        }

        if (!TContinuable.IsActive)
            goto Exit;

        // The counter is final here, so the target resolves before the halt checks instead of after them.
        // Its load chain then overlaps the rest of the handler. Zero means the counter ran off the end of
        // the code. No table entry is null, so zero cannot mean anything else.
        nint next = 0;
        if ((nuint)pc < (nuint)stack.CodeLength)
            next = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, pc)];

        if (!TOpcode.HasCheckedBody && exceptionType != EvmExceptionType.None)
            goto Exit;

        Debug.Assert(state.Vm.ReturnData is null,
            "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

        if (TTracingInst.IsActive)
            state.Vm.EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

        // Reaching here means the halt check passed, so the status is None and gas is valid: the exit
        // block returns exactly that, and one copy of it is smaller than two.
        if (next == 0)
            goto Exit;

        if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0)
        {
            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return EvmExceptionType.None;
        }

        // Keep the target in a real local so InlineIL can place it above the outgoing arguments.
        IL.EnsureLocal(in next);

        IL.Emit.Ldarg(nameof(stack));
        IL.Emit.Ldarg(nameof(gas));
        IL.Emit.Ldarg(nameof(state));
        IL.Emit.Ldarg(nameof(pc));
        IL.Emit.Ldarg(nameof(opCodeCount));
        IL.Push(next);
        IL.Emit.Tail();
        IL.Emit.Calli(new StandAloneMethodSig(
            CallingConventions.Standard,
            TypeRef.Type<EvmExceptionType>(),
            TypeRef.Type<EvmStack>().MakeByRefType(),
            TypeRef.Type<TGasPolicy>().MakeByRefType(),
            TypeRef.Type<DispatchState>().MakeByRefType(),
            TypeRef.Type<nint>(),
            TypeRef.Type<int>()));
        IL.Emit.Ret();
        throw IL.Unreachable();

    Exit:
        state.OpCodeCount = opCodeCount;
        state.FinalProgramCounter = pc;
        return exceptionType;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType ExitCheckedOpcode(ref DispatchState state, nint pc, int opCodeCount, EvmExceptionType exceptionType)
    {
        state.OpCodeCount = opCodeCount;
        state.FinalProgramCounter = pc;
        return exceptionType;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static EvmExceptionType ExecuteJumpIfOpcode<TTracingInst, TCancelable>(
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref DispatchState state,
        nint pc,
        int opCodeCount)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        VirtualMachine<TGasPolicy> vm = state.Vm;

        if (TTracingInst.IsActive)
        {
            Instruction instruction = (Instruction)Unsafe.Add(ref stack.Code, pc);
            vm.StartInstructionTrace(instruction, TGasPolicy.GetRemainingGas(in gas), (int)pc, in stack);
        }

        pc++;
        opCodeCount++;
        nint fallthroughPc = pc;
        OpcodeResult result = TTracingInst.IsActive
            ? EvmInstructions.InstructionJumpIf(ref stack, ref gas, vm, pc)
            : EvmInstructions.InstructionJumpIfAndSkipJumpDest(ref stack, ref gas, vm, pc);
        pc = result.ProgramCounter;

        if (result.Exception != EvmExceptionType.None)
            goto Exit;

        Debug.Assert(vm.ReturnData is null,
            "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

        if (TTracingInst.IsActive)
            vm.EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

        if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0)
        {
            if ((nuint)pc >= (nuint)stack.CodeLength)
                goto Exit;

            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return EvmExceptionType.None;
        }

        // Each outcome resolves its own successor and transfers from its own site, so the predictor gets
        // a taken entry and a fall-through entry to learn separately. Sharing one lookup would let the
        // JIT fold the two transfers back into a single indirect branch.
        if (pc != fallthroughPc)
        {
            if ((nuint)pc >= (nuint)stack.CodeLength)
                goto Exit;

            nint taken = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, pc)];
            IL.EnsureLocal(in taken);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Push(taken);
            IL.Emit.Tail();
            IL.Emit.Calli(new StandAloneMethodSig(
                CallingConventions.Standard,
                TypeRef.Type<EvmExceptionType>(),
                TypeRef.Type<EvmStack>().MakeByRefType(),
                TypeRef.Type<TGasPolicy>().MakeByRefType(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<int>()));
            IL.Emit.Ret();
        }
        else
        {
            if ((nuint)fallthroughPc >= (nuint)stack.CodeLength)
                goto Exit;

            nint notTaken = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, fallthroughPc)];
            IL.EnsureLocal(in notTaken);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Push(notTaken);
            IL.Emit.Tail();
            IL.Emit.Calli(new StandAloneMethodSig(
                CallingConventions.Standard,
                TypeRef.Type<EvmExceptionType>(),
                TypeRef.Type<EvmStack>().MakeByRefType(),
                TypeRef.Type<TGasPolicy>().MakeByRefType(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<int>()));
            IL.Emit.Ret();
        }

        throw IL.Unreachable();

    Exit:
        state.OpCodeCount = opCodeCount;
        state.FinalProgramCounter = pc;
        return result.Exception;
    }
}
