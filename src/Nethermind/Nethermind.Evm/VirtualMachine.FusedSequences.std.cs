// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using InlineIL;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The untraced tables under <see cref="EthereumGasPolicy"/> get fused handlers that run a short opcode sequence compilers
    /// emit over and over as one dispatch with one gas check: a comparison or ISZERO followed by <c>PUSH2</c> <c>JUMPI</c>,
    /// with or without an ISZERO in between, the <c>DUP1</c> <c>PUSH4</c> <c>EQ</c> <c>PUSH2</c> <c>JUMPI</c> of a
    /// selector dispatch, and runs of POP.
    /// </para>
    /// <para>
    /// The handler of the first opcode reads the bytes after it when it runs, so the code, its jump destinations and the
    /// program counters stay as they are, and a jump to an opcode after the first runs that opcode alone. With too little
    /// gas or stack for the whole sequence, or a taken branch to a byte that is not a JUMPDEST, the handler hands the
    /// first opcode to its plain handler, which runs it and every opcode after it one at a time, so a fault happens at the
    /// same opcode, with the same gas and opcode count, as without the sequence.
    /// </para>
    /// <para>
    /// Every opcode of a sequence has a constant cost, which the handlers charge from <see cref="EthereumGasPolicy"/>'s
    /// constants directly, so no other policy gets them. A traced table keeps the plain handlers, so a tracer still sees
    /// each opcode.
    /// </para>
    /// </remarks>
    static partial void ConfigureBuildHandlers<TTracingInst, TCancelable>(
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] lookup,
        IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        if (!TablesHaveFastPaths<TTracingInst>()) return;

        SetFusedHandler<Math1Opcode<EvmInstructions.OpIsZero, OffFlag>>(lookup, Instruction.ISZERO,
            AsTableEntry(&RawCalliHelper.ExecuteBranchCondition<RawCalliHelper.IsZeroCondition, Math1Opcode<EvmInstructions.OpIsZero, OffFlag>, TCancelable>));
        SetFusedHandler<BitwiseOpcode<EvmInstructions.OpBitwiseEq, OffFlag>>(lookup, Instruction.EQ,
            AsTableEntry(&RawCalliHelper.ExecuteBranchCondition<RawCalliHelper.EqualCondition, BitwiseOpcode<EvmInstructions.OpBitwiseEq, OffFlag>, TCancelable>));
        SetFusedHandler<Math2Opcode<EvmInstructions.OpLt, OffFlag>>(lookup, Instruction.LT,
            AsTableEntry(&RawCalliHelper.ExecuteBranchCondition<RawCalliHelper.LessThanCondition, Math2Opcode<EvmInstructions.OpLt, OffFlag>, TCancelable>));
        SetFusedHandler<Math2Opcode<EvmInstructions.OpGt, OffFlag>>(lookup, Instruction.GT,
            AsTableEntry(&RawCalliHelper.ExecuteBranchCondition<RawCalliHelper.GreaterThanCondition, Math2Opcode<EvmInstructions.OpGt, OffFlag>, TCancelable>));
        SetFusedHandler<Math2Opcode<EvmInstructions.OpSLt, OffFlag>>(lookup, Instruction.SLT,
            AsTableEntry(&RawCalliHelper.ExecuteBranchCondition<RawCalliHelper.SignedLessThanCondition, Math2Opcode<EvmInstructions.OpSLt, OffFlag>, TCancelable>));
        SetFusedHandler<Math2Opcode<EvmInstructions.OpSGt, OffFlag>>(lookup, Instruction.SGT,
            AsTableEntry(&RawCalliHelper.ExecuteBranchCondition<RawCalliHelper.SignedGreaterThanCondition, Math2Opcode<EvmInstructions.OpSGt, OffFlag>, TCancelable>));
        SetFusedHandler<DupOpcode<EvmInstructions.Op1, OffFlag>>(lookup, Instruction.DUP1,
            AsTableEntry(&RawCalliHelper.ExecuteSelectorBranch<DupOpcode<EvmInstructions.Op1, OffFlag>, TCancelable>));
        SetFusedHandler<PopOpcode>(lookup, Instruction.POP,
            AsTableEntry(&RawCalliHelper.ExecutePopRun));
    }

    /// <summary>Installs a fused handler for <paramref name="opcode"/>, recorded as the body it runs when no sequence follows.</summary>
    private static void SetFusedHandler<TOpcode>(
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] lookup,
        Instruction opcode,
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType> handler)
        where TOpcode : struct, IOpcodeBody
    {
        lookup[(int)opcode] = handler;
        RecordBody<TOpcode>((nint)handler, OpcodeFlow.Continues);
    }

    private static partial class RawCalliHelper
    {
        /// <summary>A comparison that a fused handler evaluates in place, before it knows whether its result is pushed.</summary>
        internal interface IBranchCondition
        {
            /// <summary>The words the comparison pops.</summary>
            static abstract int Inputs { get; }

            /// <summary>The comparison of the top word, at <paramref name="top"/>, with the word below it where there is one.</summary>
            static abstract bool Evaluate(ref byte top);
        }

        internal readonly struct IsZeroCondition : IBranchCondition
        {
            public static int Inputs => 1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) => EvmStack.IsSlotZero(ref top);
        }

        internal readonly struct EqualCondition : IBranchCondition
        {
            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) =>
                Unsafe.ReadUnaligned<EvmWord>(ref top) == Unsafe.ReadUnaligned<EvmWord>(ref Unsafe.Subtract(ref top, EvmStack.WordSize));
        }

        internal readonly struct LessThanCondition : IBranchCondition
        {
            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) =>
                Unsafe.As<byte, UInt256>(ref top) < Unsafe.As<byte, UInt256>(ref Unsafe.Subtract(ref top, EvmStack.WordSize));
        }

        internal readonly struct GreaterThanCondition : IBranchCondition
        {
            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) =>
                Unsafe.As<byte, UInt256>(ref top) > Unsafe.As<byte, UInt256>(ref Unsafe.Subtract(ref top, EvmStack.WordSize));
        }

        internal readonly struct SignedLessThanCondition : IBranchCondition
        {
            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) =>
                Unsafe.As<byte, Int256.Int256>(ref top).CompareTo(Unsafe.As<byte, Int256.Int256>(ref Unsafe.Subtract(ref top, EvmStack.WordSize))) < 0;
        }

        internal readonly struct SignedGreaterThanCondition : IBranchCondition
        {
            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) =>
                Unsafe.As<byte, Int256.Int256>(ref top).CompareTo(Unsafe.As<byte, Int256.Int256>(ref Unsafe.Subtract(ref top, EvmStack.WordSize))) > 0;
        }

        /// <summary>The four code bytes from <paramref name="offset"/>, the first in the low byte.</summary>
        /// <remarks>
        /// A fused handler runs only on an opcode inside the code, and the code is followed by
        /// <see cref="CodeAnalysis.CodeInfo.ExecutionPadding"/> bytes of STOP, so the read stays in the buffer. A shape
        /// that runs into the padding fails to match on its STOP.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ReadCode(ref byte code, nint offset)
        {
            uint word = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref code, offset));
            return BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word);
        }

        /// <summary>
        /// A comparison or ISZERO, run with the <c>PUSH2</c> <c>JUMPI</c> after it, or the <c>ISZERO</c> <c>PUSH2</c>
        /// <c>JUMPI</c>, as one opcode; otherwise the comparison alone.
        /// </summary>
        /// <remarks>
        /// Compilers branch on nearly every comparison they emit. Run as one opcode, the result never reaches the stack and the
        /// push and the branch need no dispatch of their own.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteBranchCondition<TCondition, TOpcode, TCancelable>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint opCodeCount)
            where TCondition : struct, IBranchCondition
            where TOpcode : struct, IOpcodeBody
            where TCancelable : struct, IFlag
        {
            ref byte code = ref stack.Code;
            nint head = stack.Head;
            // The branch's PUSH2 overflows a stack that the condition leaves full, which only ISZERO can.
            if (head >= TCondition.Inputs && (TCondition.Inputs > 1 || head < EvmStack.MaxStackSize - 1))
            {
                uint following = ReadCode(ref code, pc + 1);
                nint skipped;
                ulong cost;
                bool condition;
                uint branch;
                if ((byte)following == (byte)Instruction.PUSH2 && (byte)(following >> 24) == (byte)Instruction.JUMPI)
                {
                    skipped = 0;
                    branch = following;
                    cost = 2 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
                    condition = TCondition.Evaluate(ref stack.PeekBytesByRefUnchecked());
                }
                else if ((byte)following == (byte)Instruction.ISZERO
                    && (byte)(following >> 8) == (byte)Instruction.PUSH2
                    && (byte)((branch = ReadCode(ref code, pc + 2)) >> 24) == (byte)Instruction.JUMPI)
                {
                    skipped = 1;
                    cost = 3 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
                    condition = !TCondition.Evaluate(ref stack.PeekBytesByRefUnchecked());
                }
                else
                {
                    goto Plain;
                }

                if (!condition)
                {
                    if (gas < cost)
                        goto Fallback;

                    gas -= cost;
                    stack.Head = head - TCondition.Inputs;
                    pc += 1 + skipped + 4;
                    opCodeCount += 3 + skipped;
                    goto Sequential;
                }

                // The taken branch skips the JUMPDEST it lands on, as the plain JUMPI does, charging it here.
                cost += JumpDestGasCost.GasCost;
                int destination = (int)(((branch >> 8) & 0xFF) << 8 | ((branch >> 16) & 0xFF));
                if (gas < cost || !stack.IsJumpDestination(destination))
                    goto Fallback;

                gas -= cost;
                stack.Head = head - TCondition.Inputs;
                pc = destination + 1;
                opCodeCount += 4 + skipped;

                if (TCancelable.IsActive && opCodeCount >= state.CancellationPollAt)
                    return ExitOpcode(ref state, carriesExecutionGas: true, gas, pc, opCodeCount, EvmExceptionType.None);

                nint taken = (nint)state.OpcodeHandlers[Unsafe.Add(ref code, pc)];
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
                    TypeRef.Type<ulong>(),
                    TypeRef.Type<DispatchState>().MakeByRefType(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
                throw IL.Unreachable();
            }

        Plain:
            if (!TryExecuteCheckedInPlace<TOpcode>(ref stack, ref gas, ref state, ref pc))
                goto Fallback;
            opCodeCount++;

        Sequential:
            nint next = (nint)state.OpcodeHandlers[Unsafe.Add(ref code, pc)];
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
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();

        Fallback:
            // The arguments are unchanged on every path here: the plain handler starts the opcode over.
            nint fallback = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[Unsafe.Add(ref code, pc)];
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
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();
        }

        /// <summary>
        /// DUP1, run with the <c>PUSH4</c> <c>EQ</c> <c>PUSH2</c> <c>JUMPI</c> after it as one opcode; otherwise DUP1 alone.
        /// </summary>
        /// <remarks>
        /// A contract's entry point tests the call's selector against each function it has with this sequence, so a call
        /// runs it once per function it passes on the way. Run as one opcode, the selector is compared with the top word in
        /// place and neither the copy nor the selector reaches the stack.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteSelectorBranch<TOpcode, TCancelable>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint opCodeCount)
            where TOpcode : struct, IOpcodeBody
            where TCancelable : struct, IFlag
        {
            ref byte code = ref stack.Code;
            nint head = stack.Head;
            // DUP1 and PUSH4 each grow the stack before EQ shrinks it.
            if (head >= 1 && head < EvmStack.MaxStackSize - 2
                && Unsafe.Add(ref code, pc + 1) == (byte)Instruction.PUSH4)
            {
                uint branch = ReadCode(ref code, pc + 6);
                if ((byte)branch == (byte)Instruction.EQ
                    && (byte)(branch >> 8) == (byte)Instruction.PUSH2
                    && Unsafe.Add(ref code, pc + 10) == (byte)Instruction.JUMPI)
                {
                    ulong cost = 4 * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
                    uint selector = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref code, pc + 2));
                    if (BitConverter.IsLittleEndian)
                        selector = BinaryPrimitives.ReverseEndianness(selector);
                    // Limb layout: the selector can only equal a word whose low limb holds it and whose other limbs are zero.
                    ref ulong limbs = ref Unsafe.As<byte, ulong>(ref stack.PeekBytesByRefUnchecked());
                    bool equal = ((limbs ^ selector) | Unsafe.Add(ref limbs, 1) | Unsafe.Add(ref limbs, 2) | Unsafe.Add(ref limbs, 3)) == 0;
                    if (!equal)
                    {
                        if (gas < cost)
                            goto Fallback;

                        gas -= cost;
                        pc += 11;
                        opCodeCount += 5;
                        goto Sequential;
                    }

                    ulong takenCost = cost + JumpDestGasCost.GasCost;
                    int destination = (int)(((branch >> 16) & 0xFF) << 8 | ((branch >> 24) & 0xFF));
                    if (gas < takenCost || !stack.IsJumpDestination(destination))
                        goto Fallback;

                    gas -= takenCost;
                    pc = destination + 1;
                    opCodeCount += 6;

                    if (TCancelable.IsActive && opCodeCount >= state.CancellationPollAt)
                        return ExitOpcode(ref state, carriesExecutionGas: true, gas, pc, opCodeCount, EvmExceptionType.None);

                    nint taken = (nint)state.OpcodeHandlers[Unsafe.Add(ref code, pc)];
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
                        TypeRef.Type<ulong>(),
                        TypeRef.Type<DispatchState>().MakeByRefType(),
                        TypeRef.Type<nint>(),
                        TypeRef.Type<nint>()));
                    IL.Emit.Ret();
                    throw IL.Unreachable();
                }
            }

            if (!TryExecuteCheckedInPlace<TOpcode>(ref stack, ref gas, ref state, ref pc))
                goto Fallback;
            opCodeCount++;

        Sequential:
            nint next = (nint)state.OpcodeHandlers[Unsafe.Add(ref code, pc)];
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
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();

        Fallback:
            nint fallback = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[Unsafe.Add(ref code, pc)];
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
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();
        }

        /// <summary>POP, run with up to three POPs after it as one opcode.</summary>
        /// <remarks>Compilers drop the words a scope leaves in runs, so a run saves a dispatch per POP after the first.</remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecutePopRun(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint opCodeCount)
        {
            ref byte code = ref stack.Code;
            uint following = ReadCode(ref code, pc + 1);
            nint count = (byte)following != (byte)Instruction.POP ? 1
                : (byte)(following >> 8) != (byte)Instruction.POP ? 2
                : (byte)(following >> 16) != (byte)Instruction.POP ? 3
                : 4;
            ulong cost = (ulong)count * BaseGasCost.GasCost;
            nint head = stack.Head;
            if (head < count || gas < cost)
                goto Fallback;

            gas -= cost;
            stack.Head = head - count;
            pc += count;
            opCodeCount += count;

            nint next = (nint)state.OpcodeHandlers[Unsafe.Add(ref code, pc)];
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
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();

        Fallback:
            // Starting over from the first POP: the plain handler faults on it, or on the POP that runs out.
            nint fallback = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[Unsafe.Add(ref code, pc)];
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
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();
        }

        /// <summary>Runs a checked opcode alone on the carried gas, or reports, having changed nothing, that it would fault.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryExecuteCheckedInPlace<TOpcode>(ref EvmStack stack, ref ulong gas, ref DispatchState state, ref nint pc)
            where TOpcode : struct, IOpcodeBody
        {
            LoadFixedGas(out TGasPolicy localGas, gas);
            if (!CheckedStackFits<TOpcode>(in stack) || !TOpcode.TryConsumeGas(ref localGas))
                return false;

            pc++;
            ExecuteCheckedBodyAfterChecks<TOpcode>(ref stack, ref localGas, ref state, ref pc);
            gas = GetExecutionGas(ref localGas);
            return true;
        }
    }
}
