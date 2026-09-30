// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using InlineIL;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

using static Nethermind.Evm.VirtualMachineStatics;

// PROTOTYPE (perf/host-dispatch-registers-proto): the untraced host tables carry the remaining execution gas and the
// stack head in registers from handler to handler, after the guest design of VirtualMachine.Dispatch.zkevm.cs. The
// handlers take six integer arguments, which SysV x64 passes in registers (rdi, rsi, rdx, rcx, r8, r9); Windows x64
// passes four, so gas and head reach the stack there.
public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <summary>Whether the tables for <typeparamref name="TTracingInst"/> carry gas and head in the dispatch signature.</summary>
    /// <remarks>
    /// Untraced tables under <see cref="EthereumGasPolicy"/> only: the chain writes the carried gas back through the
    /// policy's <see cref="EthereumGasPolicy.Value"/>, which is all a fixed charge moves. Traced tables keep the
    /// five-argument signature, since every traced opcode reads both from memory anyway.
    /// </remarks>
    internal static bool CarriesRegisters<TTracingInst>() where TTracingInst : struct, IFlag =>
        !TTracingInst.IsActive && typeof(TGasPolicy) == typeof(EthereumGasPolicy);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetCarriedGas(ref TGasPolicy gas, ulong value) =>
        Unsafe.As<TGasPolicy, EthereumGasPolicy>(ref gas).Value = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType> AsTableEntry(
        delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint, EvmExceptionType> handler) =>
        (delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>)handler;

    /// <summary>The untraced counterpart of the chain entry in <c>RunDispatchLoop</c>, for tables that carry gas and head.</summary>
    [SkipLocalsInit]
    private EvmExceptionType RunCarriedChain<TTracingInst, TCancelable>(
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref nint programCounter,
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>* opcodeHandlers)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        // Unscoped because a function pointer cannot declare its parameters scoped; the chain ends before this call does.
        DispatchState state = new()
        {
            Gas = ref Unsafe.AsRef(in gas),
            OpcodeHandlers = opcodeHandlers,
            Vm = this,
            CancellationPollAt = CancellationPollInterval,
        };
        nint* table = (nint*)opcodeHandlers;

        if (TCancelable.IsActive && _txTracer.IsCancelled)
            ThrowOperationCanceledException();

        nint pc = programCounter;
        nint opCodeCount = 0;
        EvmExceptionType exceptionType;
        while (true)
        {
            byte opcode = Unsafe.Add(ref stack.Code, pc);
            exceptionType = ((delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint, EvmExceptionType>)table[opcode])(
                ref stack, TGasPolicy.GetRemainingGas(in gas), ref state, pc, stack.Head, opCodeCount);

            // A poll unwind is the only successful return with a spent budget and a successor.
            if (!TCancelable.IsActive || exceptionType != EvmExceptionType.None ||
                state.OpCodeCount < state.CancellationPollAt ||
                (nuint)state.FinalProgramCounter >= (nuint)stack.CodeLength)
                break;

            if (_txTracer.IsCancelled)
                ThrowOperationCanceledException();

            pc = state.FinalProgramCounter;
            opCodeCount = state.OpCodeCount;
            state.CancellationPollAt = opCodeCount + CancellationPollInterval;
        }

        OpCodeCount += (int)state.OpCodeCount;
        programCounter = state.FinalProgramCounter;
        if (HaltedInPadding<TTracingInst>(exceptionType, programCounter, stack.CodeLength))
            OpCodeCount--;
        return exceptionType;
    }

    private static partial class RawCalliHelper
    {
        /// <summary>An untraced handler that carries the remaining gas and the stack head in its arguments.</summary>
        /// <remarks>
        /// A body that <see cref="IOpcodeBody.CarriesRegisters"/> runs on the carried values: a checked one pays its fixed
        /// cost from them and runs on a copy of the stack that holds the carried head; a fixed-gas one runs on a local
        /// policy and the same copy. Every other body is adapted: the handler writes gas and head back to the frame,
        /// runs the body exactly as <see cref="ExecuteOpcode{TOpcode, TTracingInst, TCancelable, TContinuable}"/> would,
        /// and reloads both after it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteCarriedOpcode<TOpcode, TCancelable, TContinuable>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint opCodeCount)
            where TOpcode : struct, IOpcodeBody
            where TCancelable : struct, IFlag
            where TContinuable : struct, IFlag
        {
            EvmExceptionType exceptionType;
            if (TOpcode.CarriesRegisters && TOpcode.HasCheckedBody)
            {
                pc++;
                opCodeCount++;
                // A policy holding only the remaining gas sees the same fixed charge, and a local one stays in registers.
                Unsafe.SkipInit(out TGasPolicy fixedGas);
                SetCarriedGas(ref fixedGas, gas);
                if (!TOpcode.TryConsumeGas(ref fixedGas))
                    return ExitCarried(ref stack, ref state, TGasPolicy.GetRemainingGas(in fixedGas), pc, head, opCodeCount, EvmExceptionType.OutOfGas);
                gas = TGasPolicy.GetRemainingGas(in fixedGas);

                if (TOpcode.StackInputs != 0 && TOpcode.StackGrowth > 0)
                {
                    // One unsigned test bounds the depth on both sides: below the inputs the difference wraps past the limit.
                    nint aboveInputs = head - TOpcode.StackInputs;
                    if ((nuint)aboveInputs >= (nuint)(EvmStack.MaxStackSize - TOpcode.StackGrowth - TOpcode.StackInputs))
                        return ExitCarried(ref stack, ref state, gas, pc, head, opCodeCount,
                            aboveInputs < 0 ? EvmExceptionType.StackUnderflow : EvmExceptionType.StackOverflow);
                }
                else
                {
                    if (TOpcode.StackInputs != 0 && head < TOpcode.StackInputs)
                        return ExitCarried(ref stack, ref state, gas, pc, head, opCodeCount, EvmExceptionType.StackUnderflow);
                    if (TOpcode.StackGrowth > 0 && head >= EvmStack.MaxStackSize - TOpcode.StackGrowth)
                        return ExitCarried(ref stack, ref state, gas, pc, head, opCodeCount, EvmExceptionType.StackOverflow);
                }

                if (TOpcode.MovesHeadOnly)
                {
                    head += TOpcode.StackGrowth;
                }
                else
                {
                    EvmStack local = new(in stack, head);
                    EvmExceptionType checkedResult = TOpcode.Execute(ref local, ref fixedGas, null!, ref pc);
                    Debug.Assert(checkedResult == EvmExceptionType.None, "HasCheckedBody must not fail after dispatch validates its preconditions.");
                    head += TOpcode.StackGrowth;
                    Debug.Assert(local.Head == head, "StackGrowth must be the net change the checked body makes.");
                }
                exceptionType = EvmExceptionType.None;
            }
            else if (TOpcode.CarriesRegisters && TOpcode.ChargesFixedGas)
            {
                pc++;
                opCodeCount++;
                Unsafe.SkipInit(out TGasPolicy fixedGas);
                SetCarriedGas(ref fixedGas, gas);
                EvmStack local = new(in stack, head);
                exceptionType = TOpcode.Execute(ref local, ref fixedGas, TOpcode.UsesVm ? state.Vm : null!, ref pc);
                head = local.Head;
                gas = TGasPolicy.GetRemainingGas(in fixedGas);
            }
            else
            {
                // The write-back adapter: the body reads and writes both through the frame.
                stack.Head = head;
                SetCarriedGas(ref state.Gas, gas);
                if (TOpcode.HasUntracedFastPath)
                {
                    if (!TOpcode.TryExecuteFast(ref stack, ref state.Gas, ref state))
                    {
                        // A tail call, so the fast case stays frameless. The plain handler starts the opcode over from the
                        // unchanged arguments.
                        nint fallback = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[Unsafe.Add(ref stack.Code, pc)];
                        IL.EnsureLocal(in fallback);

                        IL.Emit.Ldarg(nameof(stack));
                        IL.Emit.Ldarg(nameof(gas));
                        IL.Emit.Ldarg(nameof(state));
                        IL.Emit.Ldarg(nameof(pc));
                        IL.Emit.Ldarg(nameof(head));
                        IL.Emit.Ldarg(nameof(opCodeCount));
                        IL.Push(fallback);
                        IL.Emit.Tail();
                        IL.Emit.Calli(new StandAloneMethodSig(
                            CallingConventions.Standard,
                            TypeRef.Type<EvmExceptionType>(),
                            TypeRef.Type<EvmStack>().MakeByRefType(),
                            TypeRef.Type<ulong>(),
                            TypeRef.Type<DispatchState>().MakeByRefType(),
                            TypeRef.Type<nint>(),
                            TypeRef.Type<nint>(),
                            TypeRef.Type<nint>()));
                        IL.Emit.Ret();
                        throw IL.Unreachable();
                    }

                    pc++;
                    opCodeCount++;
                    exceptionType = EvmExceptionType.None;
                }
                else
                {
                    pc++;
                    opCodeCount++;
                    if (TOpcode.HasCheckedBody)
                    {
                        if (!TOpcode.TryConsumeGas(ref state.Gas))
                            return ExitFrame(ref state, pc, opCodeCount, EvmExceptionType.OutOfGas);
                        if (TOpcode.StackInputs != 0 && TOpcode.StackGrowth > 0)
                        {
                            if ((nuint)(stack.Head - TOpcode.StackInputs) >= (nuint)(EvmStack.MaxStackSize - TOpcode.StackGrowth - TOpcode.StackInputs))
                                return ExitFrame(ref state, pc, opCodeCount,
                                    stack.Head < TOpcode.StackInputs ? EvmExceptionType.StackUnderflow : EvmExceptionType.StackOverflow);
                        }
                        else
                        {
                            if (TOpcode.StackInputs != 0 && !stack.EnsureDepth(TOpcode.StackInputs))
                                return ExitFrame(ref state, pc, opCodeCount, EvmExceptionType.StackUnderflow);
                            if (TOpcode.StackGrowth > 0 && stack.Head >= EvmStack.MaxStackSize - TOpcode.StackGrowth)
                                return ExitFrame(ref state, pc, opCodeCount, EvmExceptionType.StackOverflow);
                        }

                        EvmExceptionType checkedResult = TOpcode.Execute(ref stack, ref state.Gas, TOpcode.UsesVm ? state.Vm : null!, ref pc);
                        Debug.Assert(checkedResult == EvmExceptionType.None, "HasCheckedBody must not fail after dispatch validates its preconditions.");
                        exceptionType = EvmExceptionType.None;
                    }
                    else
                    {
                        exceptionType = TOpcode.Execute(ref stack, ref state.Gas, state.Vm, ref pc);
                    }
                }

                head = stack.Head;
                gas = TGasPolicy.GetRemainingGas(in state.Gas);
            }

            if (!TContinuable.IsActive)
                goto Exit;

            // Untraced code is padded, so wherever the counter lands it reads an opcode; the load overlaps the halt checks.
            nint next = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, pc)];

            if (!TOpcode.HasCheckedBody && exceptionType != EvmExceptionType.None)
                goto Exit;

            Debug.Assert(state.Vm.ReturnData is null,
                "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

            if (TCancelable.IsActive && TOpcode.MayJump && opCodeCount >= state.CancellationPollAt)
                goto Exit;

            // Keep the target in a real local so InlineIL can place it above the outgoing arguments.
            IL.EnsureLocal(in next);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(head));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Push(next);
            IL.Emit.Tail();
            IL.Emit.Calli(new StandAloneMethodSig(
                CallingConventions.Standard,
                TypeRef.Type<EvmExceptionType>(),
                TypeRef.Type<EvmStack>().MakeByRefType(),
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();

        Exit:
            return ExitCarried(ref stack, ref state, gas, pc, head, opCodeCount, exceptionType);
        }

        /// <summary>Writes back what the chain carries, and leaves it with <paramref name="exceptionType"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static EvmExceptionType ExitCarried(ref EvmStack stack, ref DispatchState state, ulong gas, nint pc, nint head,
            nint opCodeCount, EvmExceptionType exceptionType)
        {
            stack.Head = head;
            SetCarriedGas(ref state.Gas, gas);
            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return exceptionType;
        }

        /// <summary>Leaves the chain from an adapted body, whose gas and head the frame already holds.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static EvmExceptionType ExitFrame(ref DispatchState state, nint pc, nint opCodeCount, EvmExceptionType exceptionType)
        {
            state.OpCodeCount = opCodeCount;
            state.FinalProgramCounter = pc;
            return exceptionType;
        }

        /// <summary>JUMPI through the write-back adapter.</summary>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteCarriedJumpIf<TCancelable>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint opCodeCount)
            where TCancelable : struct, IFlag
        {
            stack.Head = head;
            SetCarriedGas(ref state.Gas, gas);
            pc++;
            opCodeCount++;
            nint fallthroughPc = pc;
            OpcodeResult result = EvmInstructions.InstructionJumpIfAndSkipJumpDest(ref stack, ref state.Gas, state.Vm, pc);
            pc = result.ProgramCounter;
            head = stack.Head;
            gas = TGasPolicy.GetRemainingGas(in state.Gas);

            if (result.Exception != EvmExceptionType.None)
                goto Exit;

            Debug.Assert(state.Vm.ReturnData is null,
                "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

            // Each outcome resolves its own successor and transfers from its own site, so the predictor gets a taken
            // entry and a fall-through entry to learn separately.
            if (pc != fallthroughPc)
            {
                if (TCancelable.IsActive && opCodeCount >= state.CancellationPollAt)
                    goto Exit;

                nint taken = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, pc)];
                IL.EnsureLocal(in taken);

                IL.Emit.Ldarg(nameof(stack));
                IL.Emit.Ldarg(nameof(gas));
                IL.Emit.Ldarg(nameof(state));
                IL.Emit.Ldarg(nameof(pc));
                IL.Emit.Ldarg(nameof(head));
                IL.Emit.Ldarg(nameof(opCodeCount));
                IL.Push(taken);
                IL.Emit.Tail();
                IL.Emit.Calli(new StandAloneMethodSig(
                    CallingConventions.Standard,
                    TypeRef.Type<EvmExceptionType>(),
                    TypeRef.Type<EvmStack>().MakeByRefType(),
                    TypeRef.Type<ulong>(),
                    TypeRef.Type<DispatchState>().MakeByRefType(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
            }
            else
            {
                nint notTaken = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, fallthroughPc)];
                IL.EnsureLocal(in notTaken);

                IL.Emit.Ldarg(nameof(stack));
                IL.Emit.Ldarg(nameof(gas));
                IL.Emit.Ldarg(nameof(state));
                IL.Emit.Ldarg(nameof(pc));
                IL.Emit.Ldarg(nameof(head));
                IL.Emit.Ldarg(nameof(opCodeCount));
                IL.Push(notTaken);
                IL.Emit.Tail();
                IL.Emit.Calli(new StandAloneMethodSig(
                    CallingConventions.Standard,
                    TypeRef.Type<EvmExceptionType>(),
                    TypeRef.Type<EvmStack>().MakeByRefType(),
                    TypeRef.Type<ulong>(),
                    TypeRef.Type<DispatchState>().MakeByRefType(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
            }

            throw IL.Unreachable();

        Exit:
            return ExitCarried(ref stack, ref state, gas, pc, head, opCodeCount, result.Exception);
        }
    }
}
