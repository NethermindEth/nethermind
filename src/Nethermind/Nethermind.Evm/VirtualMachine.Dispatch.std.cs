// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InlineIL;
using Nethermind.Core;

namespace Nethermind.Evm;

using static Nethermind.Evm.VirtualMachineStatics;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    internal struct DispatchState
    {
        public delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>* OpcodeHandlers;
        public VirtualMachine<TGasPolicy> Vm;

        /// <summary>Where the chain stopped. Written only as the chain leaves.</summary>
        /// <remarks>
        /// This and <see cref="OpCodeCount"/> ride the dispatch signature while the chain runs. A counter
        /// in the struct would be a narrow read-modify-write through a byref on every opcode, which the
        /// zkEVM guest charges at roughly twenty times an aligned load.
        /// </remarks>
        public nint FinalProgramCounter;

        /// <summary>How many opcodes the chain ran. Written only as the chain leaves.</summary>
        /// <remarks>
        /// Held as <see langword="nint"/> rather than <see langword="int"/> so the counter the tail-call
        /// chain threads through every handler is register-width: a 32-bit one makes a target whose
        /// registers are wider sign-extend it on each increment and each hand-off.
        /// </remarks>
        public nint OpCodeCount;

        /// <summary>The opcode count from which a taken jump leaves the chain for a cancellation poll.</summary>
        /// <remarks>Set by the cancelable driver before it enters the chain; the chain reads it only in bodies that may jump.</remarks>
        public nint CancellationPollAt;
    }

    /// <summary>Whether dispatch may read past the end of the code instead of checking the program counter.</summary>
    /// <remarks>
    /// No opcode moves the counter more than <see cref="CodeAnalysis.CodeInfo.ExecutionPadding"/> - 1 bytes past the end,
    /// and the padding is all STOP, so running off the end halts on its own. Traced runs keep the checks because an
    /// implicit STOP is traced at the original end of the code.
    /// </remarks>
    private static bool ReadsPastCodeEnd<TTracingInst>() where TTracingInst : struct, IFlag => !TTracingInst.IsActive;

    /// <summary>Whether the chain halted on a STOP read from the padding rather than from the code.</summary>
    /// <remarks>
    /// A STOP inside the code leaves the counter at most at the code length; one in the padding leaves it past it.
    /// Only the opcode count needs adjusting: <c>RunByteCode</c> treats <c>Stop</c> as it does the <c>None</c> of an
    /// implicit STOP, and the one check that tells them apart runs only when tracing, which never reads the padding.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HaltedInPadding<TTracingInst>(EvmExceptionType exceptionType, nint finalProgramCounter, nint codeLength)
        where TTracingInst : struct, IFlag =>
        ReadsPastCodeEnd<TTracingInst>() && exceptionType == EvmExceptionType.Stop && (nuint)finalProgramCounter > (nuint)codeLength;

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

        Debug.Assert(Unsafe.AreSame(ref stack.Code, ref MemoryMarshal.GetReference(VmState.Env.CodeInfo.ExecutionCodeSpan)),
            "Dispatch must run over CodeInfo's own bytes, which carry the padding it may read into.");

        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] handlers = _opcodeHandlers;
        Debug.Assert(!TablesHaveFastPaths<TTracingInst>() || handlers.Length == 2 * FallbackHandlersOffset,
            "A table with fast paths must carry the plain handlers its fallbacks read.");

        // Safety: the opcode table remains pinned for the complete tail-call chain. Every bytecode read is
        // preceded by a program-counter bounds check, or lands in the padding that follows
        // CodeInfo.ExecutionCodeSpan, and a byte is a valid index into its first 256 entries.
        // A table that holds a fast path carries the plain handlers its fallbacks read in the 256 entries
        // from FallbackHandlersOffset; a 256-entry table must hold none.
        fixed (delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>* opcodeHandlers = &handlers[0])
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
                OpCodeCount += (int)state.OpCodeCount;
                programCounter = state.FinalProgramCounter;
                // The padding STOP is not an opcode of the code, so running off the end counts as under checked dispatch.
                if (HaltedInPadding<TTracingInst>(ordinaryExceptionType, programCounter, stack.CodeLength))
                    OpCodeCount--;
                return ordinaryExceptionType;
            }

            DispatchState cancelableState = new()
            {
                OpcodeHandlers = opcodeHandlers,
                Vm = this,
                CancellationPollAt = CancellationPollInterval,
            };

            if (_txTracer.IsCancelled)
                ThrowOperationCanceledException();

            nint pc = programCounter;
            nint opCodeCount = 0;
            EvmExceptionType exceptionType;
            while (true)
            {
                byte opcode = Unsafe.Add(ref stack.Code, pc);
                exceptionType = opcodeHandlers[opcode](ref stack, ref gas, ref cancelableState, pc, opCodeCount);

                // A poll unwind is the only successful return with a spent budget and a successor.
                if (exceptionType != EvmExceptionType.None ||
                    cancelableState.OpCodeCount < cancelableState.CancellationPollAt ||
                    (nuint)cancelableState.FinalProgramCounter >= (nuint)stack.CodeLength)
                    break;

                if (_txTracer.IsCancelled)
                    ThrowOperationCanceledException();

                pc = cancelableState.FinalProgramCounter;
                opCodeCount = cancelableState.OpCodeCount;
                cancelableState.CancellationPollAt = opCodeCount + CancellationPollInterval;
            }

            OpCodeCount += (int)cancelableState.OpCodeCount;
            programCounter = cancelableState.FinalProgramCounter;
            if (HaltedInPadding<TTracingInst>(exceptionType, programCounter, stack.CodeLength))
                OpCodeCount--;
            return exceptionType;
        }
    }

    /// <summary>The dispatch handlers, each of which ends in a tail call through the opcode table.</summary>
    /// <remarks>
    /// The name is NativeAOT's opt-out from fat function pointers (ILC's <c>IsFatPointerCandidate</c>).
    /// Otherwise every managed <c>calli</c> is treated as possibly fat and guarded by a tag test and a second
    /// calling sequence that passes a generic context ahead of the arguments, and the register allocator then
    /// keeps the handler's arguments where that sequence wants them, moving them back for the ordinary call.
    /// The opt-out is sound only while every table entry is exact (unshared) code, hence a thin pointer. The
    /// <c>struct</c> constraints do not guarantee that: a policy such as <c>Policy&lt;string&gt;</c> is shared
    /// through <c>Policy&lt;__Canon&gt;</c>. Table generation therefore rejects fat entries up front
    /// (<see cref="EnsureThinHandler"/>).
    /// </remarks>
    private static class RawCalliHelper
    {
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteOpcode<TOpcode, TTracingInst, TCancelable, TContinuable>(
            ref EvmStack stack,
            ref TGasPolicy gas,
            ref DispatchState state,
            nint pc,
            nint opCodeCount)
            where TOpcode : struct, IOpcodeBody
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
            where TContinuable : struct, IFlag
        {
            // Only a traced run reads the opcode out of the bytecode. The read costs two dependent loads.
            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
            {
                Instruction instruction = (Instruction)Unsafe.Add(ref stack.Code, pc);
                state.Vm.StartInstructionTrace(instruction, TGasPolicy.GetRemainingGas(in gas), (int)pc, in stack);
            }

            if (!TTracingInst.IsActive && TOpcode.HasUntracedFastPath && !TOpcode.TryExecuteFast(ref stack, ref gas, ref state))
            {
                // A tail call, so the fast case stays frameless. The plain handler starts the opcode over from the
                // unchanged arguments, and it and this handler both dispatch their successor through the same table.
                nint fallback = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[Unsafe.Add(ref stack.Code, pc)];
                IL.EnsureLocal(in fallback);

                IL.Emit.Ldarg(nameof(stack));
                IL.Emit.Ldarg(nameof(gas));
                IL.Emit.Ldarg(nameof(state));
                IL.Emit.Ldarg(nameof(pc));
                IL.Emit.Ldarg(nameof(opCodeCount));
                IL.Push(fallback);
                IL.Emit.Tail();
                IL.Emit.Calli(new StandAloneMethodSig(
                    CallingConventions.Standard,
                    TypeRef.Type<EvmExceptionType>(),
                    TypeRef.Type<EvmStack>().MakeByRefType(),
                    TypeRef.Type<TGasPolicy>().MakeByRefType(),
                    TypeRef.Type<DispatchState>().MakeByRefType(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
                throw IL.Unreachable();
            }

            pc++;
            opCodeCount++;
            EvmExceptionType exceptionType;
            if (!TTracingInst.IsActive && TOpcode.HasUntracedFastPath)
            {
                exceptionType = EvmExceptionType.None;
            }
            else if (TOpcode.HasCheckedBody)
            {
                if (!TOpcode.TryConsumeGas(ref gas))
                    return ExitCheckedOpcode(ref state, pc, opCodeCount, EvmExceptionType.OutOfGas);
                if (TOpcode.StackInputs != 0 && TOpcode.StackGrowth > 0)
                {
                    // The head has to lie in [inputs, limit - growth), so one unsigned compare covers both bounds
                    // and only the exit works out which one failed.
                    if ((nuint)(stack.Head - TOpcode.StackInputs) >= (nuint)(EvmStack.MaxStackSize - TOpcode.StackGrowth - TOpcode.StackInputs))
                        return ExitCheckedOpcode(ref state, pc, opCodeCount,
                            stack.Head < TOpcode.StackInputs ? EvmExceptionType.StackUnderflow : EvmExceptionType.StackOverflow);
                }
                else
                {
                    if (TOpcode.StackInputs != 0 && !stack.EnsureDepth(TOpcode.StackInputs))
                        return ExitCheckedOpcode(ref state, pc, opCodeCount, EvmExceptionType.StackUnderflow);
                    if (TOpcode.StackGrowth > 0 && stack.Head >= EvmStack.MaxStackSize - TOpcode.StackGrowth)
                        return ExitCheckedOpcode(ref state, pc, opCodeCount, EvmExceptionType.StackOverflow);
                }

                EvmExceptionType checkedResult = TOpcode.Execute(ref stack, ref gas, TOpcode.UsesVm ? state.Vm : null!, ref pc);
                Debug.Assert(checkedResult == EvmExceptionType.None, "HasCheckedBody must not fail after dispatch validates its preconditions.");
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
            if (ReadsPastCodeEnd<TTracingInst>() || (nuint)pc < (nuint)stack.CodeLength)
                next = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, pc)];

            if (!TOpcode.HasCheckedBody && exceptionType != EvmExceptionType.None)
                goto Exit;

            Debug.Assert(state.Vm.ReturnData is null,
                "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
                state.Vm.EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

            // Reaching here means the halt check passed, so the status is None and gas is valid: the exit
            // block returns exactly that, and one copy of it is smaller than two.
            if (!ReadsPastCodeEnd<TTracingInst>() && next == 0)
                goto Exit;

            if (TCancelable.IsActive && TOpcode.MayJump && opCodeCount >= state.CancellationPollAt)
                goto Exit;

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
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();

        Exit:
            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return exceptionType;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static EvmExceptionType ExitCheckedOpcode(ref DispatchState state, nint pc, nint opCodeCount, EvmExceptionType exceptionType)
        {
            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return exceptionType;
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteJumpIfOpcode<TTracingInst, TCancelable>(
            ref EvmStack stack,
            ref TGasPolicy gas,
            ref DispatchState state,
            nint pc,
            nint opCodeCount)
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
        {
            VirtualMachine<TGasPolicy> vm = state.Vm;

            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
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

            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
                vm.EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

            // Each outcome resolves its own successor and transfers from its own site, so the predictor gets
            // a taken entry and a fall-through entry to learn separately. Sharing one lookup would let the
            // JIT fold the two transfers back into a single indirect branch.
            if (pc != fallthroughPc)
            {
                if (!ReadsPastCodeEnd<TTracingInst>() && (nuint)pc >= (nuint)stack.CodeLength)
                    goto Exit;

                if (TCancelable.IsActive && opCodeCount >= state.CancellationPollAt)
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
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
            }
            else
            {
                if (!ReadsPastCodeEnd<TTracingInst>() && (nuint)fallthroughPc >= (nuint)stack.CodeLength)
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
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
            }

            throw IL.Unreachable();

        Exit:
            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return result.Exception;
        }
    }
}
