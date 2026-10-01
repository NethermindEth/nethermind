// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;
using InlineIL;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <inheritdoc/>
    /// <remarks>
    /// The untraced tables run every comparison a compiler branches on - ISZERO, EQ, LT, GT, SLT and SGT - through
    /// <see cref="RawCalliHelper.ExecuteCompareBranch{TCondition, TCancelable}"/>, which fuses it with the branch after it.
    /// The traced tables keep the plain handlers, so a trace still sees every opcode.
    /// </remarks>
    static partial void ConfigureBuildHandlers<TTracingInst, TCancelable>(
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] lookup,
        IReleaseSpec spec)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        // The fused handlers charge EthereumGasPolicy's gas directly and fall back on the table's plain half.
        if (!TablesHaveFastPaths<TTracingInst>()) return;

        lookup[(int)Instruction.ISZERO] = &RawCalliHelper.ExecuteCompareBranch<RawCalliHelper.IsZeroCondition, TCancelable>;
        lookup[(int)Instruction.EQ] = &RawCalliHelper.ExecuteCompareBranch<RawCalliHelper.EqualCondition, TCancelable>;
        lookup[(int)Instruction.LT] = &RawCalliHelper.ExecuteCompareBranch<RawCalliHelper.OrderCondition<EvmInstructions.OpLt>, TCancelable>;
        lookup[(int)Instruction.GT] = &RawCalliHelper.ExecuteCompareBranch<RawCalliHelper.OrderCondition<EvmInstructions.OpGt>, TCancelable>;
        lookup[(int)Instruction.SLT] = &RawCalliHelper.ExecuteCompareBranch<RawCalliHelper.OrderCondition<EvmInstructions.OpSLt>, TCancelable>;
        lookup[(int)Instruction.SGT] = &RawCalliHelper.ExecuteCompareBranch<RawCalliHelper.OrderCondition<EvmInstructions.OpSGt>, TCancelable>;
    }

    private static partial class RawCalliHelper
    {
        /// <summary>
        /// A comparison, fused with the branch after it - <c>PUSH2</c> <c>JUMPI</c>, or <c>ISZERO</c> <c>PUSH2</c> <c>JUMPI</c> -
        /// by <see cref="ExecuteFusedBranch{TCondition, TCancelable, TInverted}"/>, or run alone when no branch follows.
        /// </summary>
        /// <remarks>
        /// The opcode after the comparison, a PUSH2 or an ISZERO, hands the branch on through a constant target to the fused
        /// handler, which checks the rest of it, so the handler holds nothing in a register across it: the comparison it runs
        /// alone needs no callee-saved register, as the plain handler's does not, and the fused handler starts with all of them
        /// free. A short stack or gas runs the comparison's plain handler, which faults on it.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteCompareBranch<TCondition, TCancelable>(
            ref EvmStack stack,
            ref TGasPolicy gas,
            ref DispatchState state,
            nint pc,
            nint opCodeCount)
            where TCondition : struct, IStackCondition
            where TCancelable : struct, IFlag
        {
            ref ulong gasLeft = ref Unsafe.As<TGasPolicy, EthereumGasPolicy>(ref gas).Value;
            if (gasLeft < VeryLowGasCost.GasCost)
                goto Plain;

            // Only a PUSH2 or an ISZERO after the comparison can start a branch, and the fused handler checks the rest of it,
            // so the path that runs the comparison alone pays one load and two branches not taken for the look.
            nint following = Unsafe.Add(ref stack.Code, pc + 1);
            if (following == (byte)Instruction.PUSH2 || following == (byte)Instruction.ISZERO)
                goto Branch;

            // The comparison checks the depth itself, as the plain handler's body does, and changes nothing on a short stack.
            if (TCondition.ExecuteAlone(ref stack) != EvmExceptionType.None)
                goto Plain;
            gasLeft -= VeryLowGasCost.GasCost;
            pc++;
            opCodeCount++;

            // ISZERO dispatches on the opcode already read. A two-word comparison reads it again: holding it across the
            // comparison would take a callee-saved register.
            nint next = (nint)state.OpcodeHandlers[TCondition.Inputs == 1 ? following : Unsafe.Add(ref stack.Code, pc)];
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

        Branch:
            nint fused = following == (byte)Instruction.PUSH2
                ? (nint)(delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>)
                    &ExecuteFusedBranch<TCondition, TCancelable, OffFlag>
                : (nint)(delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>)
                    &ExecuteFusedBranch<TCondition, TCancelable, OnFlag>;
            goto Fused;

        Plain:
            nint plain = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[Unsafe.Add(ref stack.Code, pc)];
            IL.EnsureLocal(in plain);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Push(plain);
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

        Fused:
            IL.EnsureLocal(in fused);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Push(fused);
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

        /// <summary>
        /// A comparison and the <c>PUSH2</c> <c>JUMPI</c> after it, with an <c>ISZERO</c> between them when
        /// <typeparamref name="TInverted"/> is active, run as one step whenever every opcode of that sequence succeeds.
        /// </summary>
        /// <remarks>
        /// Compilers branch on nearly every comparison they emit, so fusing saves the comparison its result slot, and the
        /// PUSH2 and any ISZERO before it their dispatches. The fused step charges and counts every opcode it covers, the
        /// JUMPDEST a taken branch lands on included, and like JUMPI it leaves for a cancellation poll only on a taken
        /// branch. When the opcodes after the comparison are not that sequence, the comparison runs alone, as in its own
        /// handler. With too little gas for all of them, a stack the PUSH2 would overflow, or a taken branch onto an invalid
        /// destination, the comparison's plain handler runs the comparison alone and the opcodes after it run as they
        /// would, so a fault lands on the same opcode with the same gas. Only the comparison's own handler enters it, once
        /// it has seen the PUSH2 or the ISZERO.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteFusedBranch<TCondition, TCancelable, TInverted>(
            ref EvmStack stack,
            ref TGasPolicy gas,
            ref DispatchState state,
            nint pc,
            nint opCodeCount)
            where TCondition : struct, IStackCondition
            where TCancelable : struct, IFlag
            where TInverted : struct, IFlag
        {
            if (stack.Head < TCondition.Inputs)
                goto Plain;
            // The comparison's handler has seen the PUSH2, or the ISZERO, after it; the code is padded with STOP, so reading
            // past it stays inside the code, and a branch that the end of the code cuts short does not match.
            if (TInverted.IsActive ? !IsBranch(ref stack.Code, pc + 2) : Unsafe.Add(ref stack.Code, pc + 4) != (byte)Instruction.JUMPI)
                goto Alone;
            // The branch's PUSH2 overflows a stack the comparison leaves full, which only ISZERO can.
            if (TCondition.Inputs == 1 && stack.Head >= EvmStack.MaxStackSize - 1)
                goto Plain;

            ref ulong gasLeft = ref Unsafe.As<TGasPolicy, EthereumGasPolicy>(ref gas).Value;
            // The comparison, the ISZERO if there is one, and the PUSH2 cost very low gas each.
            ulong notTakenCost = (TInverted.IsActive ? 3UL : 2UL) * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
            // An ISZERO between the comparison and the branch only inverts what the branch tests.
            if (TCondition.Evaluate(ref stack.PeekBytesByRefUnchecked()) == TInverted.IsActive)
            {
                // A volatile read is not merged with the subtraction's, so the gas never becomes a local live across the
                // fallback's tail call, which would cost a callee-saved register.
                if (Volatile.Read(ref gasLeft) < notTakenCost)
                    goto Plain;
                gasLeft -= notTakenCost;
                stack.Head -= TCondition.Inputs;
                opCodeCount += TInverted.IsActive ? 4 : 3;
                pc += TInverted.IsActive ? 6 : 5;

                // Each outcome transfers from its own site, as JUMPI's do, so the predictor learns them apart.
                nint notTaken = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, pc)];
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
                throw IL.Unreachable();
            }

            int destination = BinaryPrimitives.ReverseEndianness(
                Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref stack.Code, pc + (TInverted.IsActive ? 3 : 2))));
            if (!stack.IsJumpDestination(destination))
                goto Plain;
            if (Volatile.Read(ref gasLeft) < notTakenCost + JumpDestGasCost.GasCost)
                goto Plain;
            gasLeft -= notTakenCost + JumpDestGasCost.GasCost;
            stack.Head -= TCondition.Inputs;
            opCodeCount += TInverted.IsActive ? 5 : 4;
            pc = destination + 1;
            if (TCancelable.IsActive && opCodeCount >= state.CancellationPollAt)
            {
                state.OpCodeCount = opCodeCount;
                state.FinalProgramCounter = pc;
                return EvmExceptionType.None;
            }

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
            throw IL.Unreachable();

        Alone:
            // No branch after all, as in ISZERO ISZERO PUSH2 JUMPI: the comparison runs alone, and the ISZERO after it may
            // fuse in turn. Out of line, so that this handler keeps its registers for the branch.
            nint alone = (nint)(delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>)
                &ExecuteCompareAlone<TCondition>;
            IL.EnsureLocal(in alone);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Push(alone);
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

        Plain:
            nint plain = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[Unsafe.Add(ref stack.Code, pc)];
            IL.EnsureLocal(in plain);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Push(plain);
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

        /// <summary>
        /// A comparison that <see cref="ExecuteFusedBranch{TCondition, TCancelable, TInverted}"/> found no branch after, run
        /// alone as <see cref="ExecuteCompareBranch{TCondition, TCancelable}"/> runs one; otherwise its plain handler faults.
        /// </summary>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteCompareAlone<TCondition>(
            ref EvmStack stack,
            ref TGasPolicy gas,
            ref DispatchState state,
            nint pc,
            nint opCodeCount)
            where TCondition : struct, IStackCondition
        {
            ref ulong gasLeft = ref Unsafe.As<TGasPolicy, EthereumGasPolicy>(ref gas).Value;
            if (gasLeft < VeryLowGasCost.GasCost || TCondition.ExecuteAlone(ref stack) != EvmExceptionType.None)
                goto Plain;
            gasLeft -= VeryLowGasCost.GasCost;
            pc++;
            opCodeCount++;

            nint next = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, pc)];
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

        Plain:
            nint plain = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[Unsafe.Add(ref stack.Code, pc)];
            IL.EnsureLocal(in plain);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Push(plain);
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

        /// <summary>Whether a PUSH2 at <paramref name="at"/> pushes the destination of a JUMPI right after it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsBranch(ref byte code, nint at) =>
            Unsafe.Add(ref code, at) == (byte)Instruction.PUSH2 && Unsafe.Add(ref code, at + 3) == (byte)Instruction.JUMPI;

        /// <summary>A comparison of the words on top of the stack, as <see cref="ExecuteCompareBranch{TCondition, TCancelable}"/> runs it.</summary>
        internal interface IStackCondition
        {
            /// <summary>How many words the comparison consumes.</summary>
            static abstract int Inputs { get; }

            /// <summary>Evaluates the comparison on the words from the top slot down, in limb layout.</summary>
            /// <param name="top">The top slot; the caller has established that the stack holds <see cref="Inputs"/> words.</param>
            static abstract bool Evaluate(ref byte top);

            /// <summary>Runs the comparison alone, as its plain handler's body does.</summary>
            /// <returns><see cref="EvmExceptionType.StackUnderflow"/>, having changed nothing, on a stack short of its inputs.</returns>
            /// <remarks>
            /// The plain body rather than <see cref="Evaluate"/>: on AVX2 hosts the plain ISZERO and EQ store one of two
            /// constants behind a branch, so the next opcode reads a value that waits on nothing but the prediction. A result
            /// built from the flags waits on the load and the test, and through the slot so does every opcode reading it, which
            /// tripled a chain of ISZERO NOT.
            /// </remarks>
            static abstract EvmExceptionType ExecuteAlone(ref EvmStack stack);
        }

        /// <summary>ISZERO: whether the top word is zero.</summary>
        internal readonly struct IsZeroCondition : IStackCondition
        {
            public static int Inputs => 1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) => EvmStack.IsSlotZero(ref top);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static EvmExceptionType ExecuteAlone(ref EvmStack stack) =>
                EvmInstructions.Math1ParamCore<EvmInstructions.OpIsZero, OffFlag, OnFlag>(ref stack);
        }

        /// <summary>EQ: whether the top two words are equal.</summary>
        internal readonly struct EqualCondition : IStackCondition
        {
            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top)
            {
                ref byte second = ref Unsafe.Subtract(ref top, EvmStack.WordSize);
                if (Vector256.IsHardwareAccelerated)
                    return Unsafe.ReadUnaligned<Vector256<byte>>(ref top) == Unsafe.ReadUnaligned<Vector256<byte>>(ref second);

                ref ulong a = ref Unsafe.As<byte, ulong>(ref top);
                ref ulong b = ref Unsafe.As<byte, ulong>(ref second);
                return ((a ^ b) | (Unsafe.Add(ref a, 1) ^ Unsafe.Add(ref b, 1)) |
                    (Unsafe.Add(ref a, 2) ^ Unsafe.Add(ref b, 2)) | (Unsafe.Add(ref a, 3) ^ Unsafe.Add(ref b, 3))) == 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static EvmExceptionType ExecuteAlone(ref EvmStack stack) =>
                EvmInstructions.BitwiseCore<EvmInstructions.OpBitwiseEq, OffFlag, OnFlag>(ref stack);
        }

        /// <summary>LT, GT, SLT or SGT: how the top word orders against the one under it.</summary>
        /// <typeparam name="TOpMath">The comparison's plain operation, whose scalar form decides it.</typeparam>
        internal readonly struct OrderCondition<TOpMath> : IStackCondition
            where TOpMath : struct, EvmInstructions.IOpMath2Param
        {
            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) => EvmInstructions.CompareScalar<TOpMath>(
                ref Unsafe.As<byte, ulong>(ref top), ref Unsafe.As<byte, ulong>(ref Unsafe.Subtract(ref top, EvmStack.WordSize)));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static EvmExceptionType ExecuteAlone(ref EvmStack stack) =>
                EvmInstructions.Math2ParamCore<TOpMath, OffFlag, OnFlag>(ref stack);
        }
    }
}
