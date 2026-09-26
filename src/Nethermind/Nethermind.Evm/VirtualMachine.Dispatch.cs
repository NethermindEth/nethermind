// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using InlineIL;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

using static Nethermind.Evm.VirtualMachineStatics;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    // Poll cancellation every 1024 opcodes (low bits of the per-frame op counter).
    private const int CancellationCheckMask = 1023;

    internal ref struct DispatchState
    {
        /// <summary>The frame's gas.</summary>
        /// <remarks>
        /// Its remaining execution gas is stale while the chain runs: the chain carries that value in the
        /// dispatch signature and writes it back before any body that takes this policy, and as it leaves.
        /// </remarks>
        public ref TGasPolicy Gas;

        /// <summary>The table the chain dispatches through, for handlers that reload it after their body.</summary>
        public nint* OpcodeHandlers;

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
    private delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[] _opcodeHandlers = null!;

    private delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? _filteredOpcodeHandlers;
    private delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? _filteredTracedSource;
    private delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? _filteredSilentSource;
    private UInt256 _filteredInstructionMask;

    private struct SilentInstructionFlag : IFlag
    {
        public static bool IsActive => true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]
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
            table.RefreshNonTraced(spec);

        _executionHandlers = table.GetExecutionHandlers(spec);
        _opcodeHandlers = table.GetHandlers<TTracingInst, TCancelable>(spec);
        if (TTracingInst.IsActive && _txTracer is IInstructionTracingFilter filter)
        {
            UInt256 mask = filter.InstructionMask;
            if (mask != UInt256.MaxValue)
                PrepareFilteredOpcodes(mask, table.GetHandlers<SilentInstructionFlag, TCancelable>(spec));
        }
    }

    private void PrepareFilteredOpcodes(UInt256 mask,
        delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[] silent)
    {
        if (_filteredTracedSource != _opcodeHandlers || _filteredSilentSource != silent || _filteredInstructionMask != mask)
        {
            _filteredOpcodeHandlers ??= new delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[256];
            _filteredTracedSource = _opcodeHandlers;
            _filteredSilentSource = silent;
            _filteredInstructionMask = mask;
            for (int opcode = 0; opcode < _filteredOpcodeHandlers.Length; opcode++)
            {
                _filteredOpcodeHandlers[opcode] = (mask & (UInt256.One << opcode)) != UInt256.Zero
                    ? _opcodeHandlers[opcode]
                    : silent[opcode];
            }
        }
        _opcodeHandlers = _filteredOpcodeHandlers!;
    }

    private sealed unsafe class OpcodeTable
    {
        public delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? NoTrace;
        public delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? NoTraceCancelable;
        public delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? Traced;
        public delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? TracedCancelable;
        public delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? Silent;
        public delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? SilentCancelable;

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
        public delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]
            GetHandlers<TTracingInst, TCancelable>(IReleaseSpec spec)
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
        {
            ref delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[]? table =
                ref typeof(TTracingInst) == typeof(SilentInstructionFlag)
                    ? ref (TCancelable.IsActive ? ref SilentCancelable : ref Silent)
                    : ref TTracingInst.IsActive
                    ? ref (TCancelable.IsActive ? ref TracedCancelable : ref Traced)
                    : ref (TCancelable.IsActive ? ref NoTraceCancelable : ref NoTrace);

            return table ??= GenerateOpcodeHandlers<TTracingInst, TCancelable>(spec);
        }

        /// <summary>Rebuilds both non-traced tables from whatever the JIT has promoted since the last build.</summary>
        /// <remarks>
        /// A captured function pointer keeps pointing at the code it was taken from. Both non-traced tables
        /// share one cadence, so a rebuild has to cover both: which one a given transaction asks for follows
        /// the node's mix of block processing and RPC, and block processing is what the cadence exists for.
        /// The tracing flag is fixed to off here rather than taken from the caller, so a caller inside a
        /// traced run cannot fill the non-traced tables with tracing handlers.
        /// </remarks>
        public void RefreshNonTraced(IReleaseSpec spec)
        {
            NoTrace = GenerateOpcodeHandlers<OffFlag, OffFlag>(spec);
            NoTraceCancelable = GenerateOpcodeHandlers<OffFlag, OnFlag>(spec);
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

        delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>[] handlers = _opcodeHandlers;

        // Safety: the 256-entry opcode table remains pinned for the complete tail-call chain. Every
        // bytecode read is preceded by a program-counter bounds check, and a byte is a valid table index.
        fixed (delegate*<ref EvmStack, ulong, ref DispatchState, nint, int, nint*, ref byte, nint, EvmExceptionType>* opcodeHandlers = &handlers[0])
        {
            if (!TCancelable.IsActive)
            {
                // Unscoped because a function pointer cannot declare its parameters scoped; the chain ends before this call does.
                DispatchState state = new()
                {
                    Gas = ref Unsafe.AsRef(in gas),
                    OpcodeHandlers = (nint*)opcodeHandlers,
                    Vm = this,
                };

                byte opcode = Unsafe.Add(ref stack.Code, programCounter);
                EvmExceptionType ordinaryExceptionType = opcodeHandlers[opcode](ref stack, TGasPolicy.GetRemainingGas(in gas), ref state, programCounter, 0, (nint*)opcodeHandlers, ref stack.Code, stack.CodeLength);
                OpCodeCount += state.OpCodeCount;
                programCounter = state.FinalProgramCounter;
                return ordinaryExceptionType;
            }

            DispatchState cancelableState = new()
            {
                Gas = ref Unsafe.AsRef(in gas),
                OpcodeHandlers = (nint*)opcodeHandlers,
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
                exceptionType = opcodeHandlers[opcode](ref stack, TGasPolicy.GetRemainingGas(in gas), ref cancelableState, pc, opCodeCount, (nint*)opcodeHandlers, ref stack.Code, stack.CodeLength);

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

    /// <summary>The dispatch handlers, each of which ends in a tail call through the opcode table.</summary>
    /// <remarks>
    /// The name is NativeAOT's opt-out from fat function pointers (ILC's <c>IsFatPointerCandidate</c>).
    /// Otherwise every managed <c>calli</c> is treated as possibly fat and guarded by a tag test and a second
    /// calling sequence that passes a generic context ahead of the arguments, and the register allocator then
    /// keeps the handler's arguments where that sequence wants them, moving them back for the ordinary call.
    /// The opt-out is sound here because every table entry is an instantiation over structs (see the
    /// constraint on <typeparamref name="TGasPolicy"/>), hence exact code and a thin pointer.
    /// <para>
    /// The remaining gas, the table, the bytecode and its length ride the argument registers from handler
    /// to handler, so a handler whose body stays inline neither loads nor stores them. The gas is the
    /// policy's remaining execution gas; checked bodies pay their fixed cost from it directly.
    /// </para>
    /// </remarks>
    private static class RawCalliHelper
    {
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteOpcode<TOpcode, TTracingInst, TCancelable, TContinuable>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            int opCodeCount,
            nint* handlers,
            ref byte code,
            nint codeLength)
            where TOpcode : struct, IOpcodeBody
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
            where TContinuable : struct, IFlag
        {
            // Only a traced run reads the opcode out of the bytecode. The read costs two dependent loads.
            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
            {
                Instruction instruction = (Instruction)Unsafe.Add(ref code, pc);
                state.Vm.StartInstructionTrace(instruction, gas, (int)pc, in stack);
            }

            pc++;
            opCodeCount++;
            EvmExceptionType exceptionType;
            if (TOpcode.HasCheckedBody)
            {
                if (!TOpcode.TryConsumeGas(ref gas))
                    return ExitCheckedOpcode(ref state, gas, pc, opCodeCount, EvmExceptionType.OutOfGas);
                if (TOpcode.StackInputs != 0 && !stack.EnsureDepth(TOpcode.StackInputs))
                    return ExitCheckedOpcode(ref state, gas, pc, opCodeCount, EvmExceptionType.StackUnderflow);
                if (TOpcode.StackGrowth > 0 && stack.Head >= EvmStack.MaxStackSize - TOpcode.StackGrowth)
                    return ExitCheckedOpcode(ref state, gas, pc, opCodeCount, EvmExceptionType.StackOverflow);
                // Only untraced PUSH bodies opt in: no subsequent opcode can observe the final stack value.
                if (TOpcode.PushSize >= 0 && pc + TOpcode.PushSize >= codeLength)
                    return ExitCheckedOpcode(ref state, gas, pc + TOpcode.PushSize, opCodeCount, EvmExceptionType.None);

                EvmExceptionType checkedResult = TOpcode.Execute(ref stack, ref Unsafe.NullRef<TGasPolicy>(), TOpcode.UsesVm ? state.Vm : null!, ref pc);
                Debug.Assert(checkedResult == EvmExceptionType.None, "HasCheckedBody must not fail after dispatch validates its preconditions.");
                exceptionType = EvmExceptionType.None;
            }
            else if (TOpcode.ChargesFixedGas)
            {
                // A policy holding only the remaining gas sees the same fixed charges, and a local one can stay in registers.
                Unsafe.SkipInit(out TGasPolicy fixedGas);
                TGasPolicy.SetRemainingGas(ref fixedGas, gas);
                exceptionType = TOpcode.Execute(ref stack, ref fixedGas, state.Vm, ref pc);
                gas = TGasPolicy.GetRemainingGas(in fixedGas);
            }
            else
            {
                TGasPolicy.SetRemainingGas(ref state.Gas, gas);
                exceptionType = TOpcode.Execute(ref stack, ref state.Gas, state.Vm, ref pc);
                gas = TGasPolicy.GetRemainingGas(in state.Gas);
            }

            if (!TOpcode.HasCheckedBody || TOpcode.CallsOutOfLine)
            {
                // Reloaded, not held: live across an out-of-line call they would each take a callee-saved register.
                handlers = state.OpcodeHandlers;
                code = ref stack.Code;
                codeLength = stack.CodeLength;
            }

            if (!TContinuable.IsActive)
                goto Exit;

            // The counter is final here, so the target resolves before the halt checks instead of after them.
            // Its load chain then overlaps the rest of the handler. Zero means the counter ran off the end of
            // the code. No table entry is null, so zero cannot mean anything else.
            nint next = 0;
            if ((TOpcode.HasCheckedBody && TOpcode.PushSize >= 0) || (nuint)pc < (nuint)codeLength)
                next = handlers[Unsafe.Add(ref code, pc)];

            if (!TOpcode.HasCheckedBody && exceptionType != EvmExceptionType.None)
                goto Exit;

            Debug.Assert(state.Vm.ReturnData is null,
                "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
                state.Vm.EndInstructionTrace(gas);

            // Reaching here means the halt check passed, so the status is None and gas is valid: the exit
            // block returns exactly that, and one copy of it is smaller than two.
            if (!(TOpcode.HasCheckedBody && TOpcode.PushSize >= 0) && next == 0)
                goto Exit;

            if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0)
                goto Exit;

            // Keep the target in a real local so InlineIL can place it above the outgoing arguments.
            IL.EnsureLocal(in next);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Emit.Ldarg(nameof(handlers));
            IL.Emit.Ldarg(nameof(code));
            IL.Emit.Ldarg(nameof(codeLength));
            IL.Push(next);
            IL.Emit.Tail();
            IL.Emit.Calli(new StandAloneMethodSig(
                CallingConventions.Standard,
                TypeRef.Type<EvmExceptionType>(),
                TypeRef.Type<EvmStack>().MakeByRefType(),
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<int>(),
                TypeRef.Type<nint>().MakePointerType(),
                TypeRef.Type<byte>().MakeByRefType(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();

        Exit:
            TGasPolicy.SetRemainingGas(ref state.Gas, gas);
            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return exceptionType;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static EvmExceptionType ExitCheckedOpcode(ref DispatchState state, ulong gas, nint pc, int opCodeCount, EvmExceptionType exceptionType)
        {
            TGasPolicy.SetRemainingGas(ref state.Gas, gas);
            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return exceptionType;
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteJumpIfOpcode<TTracingInst, TCancelable>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            int opCodeCount,
            nint* handlers,
            ref byte code,
            nint codeLength)
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
        {
            VirtualMachine<TGasPolicy> vm = state.Vm;

            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
            {
                Instruction instruction = (Instruction)Unsafe.Add(ref code, pc);
                vm.StartInstructionTrace(instruction, gas, (int)pc, in stack);
            }

            pc++;
            opCodeCount++;
            nint fallthroughPc = pc;
            // Every JUMPI charge is fixed, as for a ChargesFixedGas body.
            Unsafe.SkipInit(out TGasPolicy fixedGas);
            TGasPolicy.SetRemainingGas(ref fixedGas, gas);
            OpcodeResult result = TTracingInst.IsActive
                ? EvmInstructions.InstructionJumpIf(ref stack, ref fixedGas, vm, pc)
                : EvmInstructions.InstructionJumpIfAndSkipJumpDest(ref stack, ref fixedGas, vm, pc);
            gas = TGasPolicy.GetRemainingGas(in fixedGas);
            pc = result.ProgramCounter;
            handlers = state.OpcodeHandlers;
            code = ref stack.Code;
            codeLength = stack.CodeLength;

            if (result.Exception != EvmExceptionType.None)
                goto Exit;

            Debug.Assert(vm.ReturnData is null,
                "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
                vm.EndInstructionTrace(gas);

            if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0)
            {
                if ((nuint)pc >= (nuint)codeLength)
                    goto Exit;

                TGasPolicy.SetRemainingGas(ref state.Gas, gas);
                state.OpCodeCount = opCodeCount;
                state.FinalProgramCounter = pc;
                return EvmExceptionType.None;
            }

            // Each outcome resolves its own successor and transfers from its own site, so the predictor gets
            // a taken entry and a fall-through entry to learn separately. Sharing one lookup would let the
            // JIT fold the two transfers back into a single indirect branch.
            if (pc != fallthroughPc)
            {
                if ((nuint)pc >= (nuint)codeLength)
                    goto Exit;

                nint taken = handlers[Unsafe.Add(ref code, pc)];
                IL.EnsureLocal(in taken);

                IL.Emit.Ldarg(nameof(stack));
                IL.Emit.Ldarg(nameof(gas));
                IL.Emit.Ldarg(nameof(state));
                IL.Emit.Ldarg(nameof(pc));
                IL.Emit.Ldarg(nameof(opCodeCount));
                IL.Emit.Ldarg(nameof(handlers));
                IL.Emit.Ldarg(nameof(code));
                IL.Emit.Ldarg(nameof(codeLength));
                IL.Push(taken);
                IL.Emit.Tail();
                IL.Emit.Calli(new StandAloneMethodSig(
                    CallingConventions.Standard,
                    TypeRef.Type<EvmExceptionType>(),
                    TypeRef.Type<EvmStack>().MakeByRefType(),
                    TypeRef.Type<ulong>(),
                    TypeRef.Type<DispatchState>().MakeByRefType(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<int>(),
                    TypeRef.Type<nint>().MakePointerType(),
                    TypeRef.Type<byte>().MakeByRefType(),
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
            }
            else
            {
                if ((nuint)fallthroughPc >= (nuint)codeLength)
                    goto Exit;

                nint notTaken = handlers[Unsafe.Add(ref code, fallthroughPc)];
                IL.EnsureLocal(in notTaken);

                IL.Emit.Ldarg(nameof(stack));
                IL.Emit.Ldarg(nameof(gas));
                IL.Emit.Ldarg(nameof(state));
                IL.Emit.Ldarg(nameof(pc));
                IL.Emit.Ldarg(nameof(opCodeCount));
                IL.Emit.Ldarg(nameof(handlers));
                IL.Emit.Ldarg(nameof(code));
                IL.Emit.Ldarg(nameof(codeLength));
                IL.Push(notTaken);
                IL.Emit.Tail();
                IL.Emit.Calli(new StandAloneMethodSig(
                    CallingConventions.Standard,
                    TypeRef.Type<EvmExceptionType>(),
                    TypeRef.Type<EvmStack>().MakeByRefType(),
                    TypeRef.Type<ulong>(),
                    TypeRef.Type<DispatchState>().MakeByRefType(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<int>(),
                    TypeRef.Type<nint>().MakePointerType(),
                    TypeRef.Type<byte>().MakeByRefType(),
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
            }

            throw IL.Unreachable();

        Exit:
            TGasPolicy.SetRemainingGas(ref state.Gas, gas);
            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return result.Exception;
        }
    }
}
