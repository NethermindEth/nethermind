// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    /// emit over and over as one dispatch with one gas check: a comparison (<c>LT</c>, <c>GT</c>, <c>SLT</c>, <c>SGT</c>,
    /// <c>EQ</c>) followed by <c>PUSH2</c> <c>JUMPI</c>, with or without an ISZERO in between. Only comparisons get a fused
    /// handler: compilers branch on nearly every one, so the check for a following branch rarely goes unused.
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
            /// <summary>The opcode the comparison runs as.</summary>
            static abstract Instruction Opcode { get; }

            /// <summary>The words the comparison pops.</summary>
            static abstract int Inputs { get; }

            /// <summary>The comparison of the top word, at <paramref name="top"/>, with the word below it where there is one.</summary>
            static abstract bool Evaluate(ref byte top);
        }

        internal readonly struct EqualCondition : IBranchCondition
        {
            public static Instruction Opcode => Instruction.EQ;

            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) =>
                Unsafe.ReadUnaligned<EvmWord>(ref top) == Unsafe.ReadUnaligned<EvmWord>(ref Unsafe.Subtract(ref top, EvmStack.WordSize));
        }

        internal readonly struct LessThanCondition : IBranchCondition
        {
            public static Instruction Opcode => Instruction.LT;

            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) =>
                Unsafe.As<byte, UInt256>(ref top) < Unsafe.As<byte, UInt256>(ref Unsafe.Subtract(ref top, EvmStack.WordSize));
        }

        internal readonly struct GreaterThanCondition : IBranchCondition
        {
            public static Instruction Opcode => Instruction.GT;

            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) =>
                Unsafe.As<byte, UInt256>(ref top) > Unsafe.As<byte, UInt256>(ref Unsafe.Subtract(ref top, EvmStack.WordSize));
        }

        internal readonly struct SignedLessThanCondition : IBranchCondition
        {
            public static Instruction Opcode => Instruction.SLT;

            public static int Inputs => 2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Evaluate(ref byte top) =>
                Unsafe.As<byte, Int256.Int256>(ref top).CompareTo(Unsafe.As<byte, Int256.Int256>(ref Unsafe.Subtract(ref top, EvmStack.WordSize))) < 0;
        }

        internal readonly struct SignedGreaterThanCondition : IBranchCondition
        {
            public static Instruction Opcode => Instruction.SGT;

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
        private static uint ReadCode(ref byte code, nint offset) => Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref code, offset));

        /// <summary>
        /// A comparison followed by <c>PUSH2</c> <c>JUMPI</c>, or by <c>ISZERO</c> <c>PUSH2</c> <c>JUMPI</c>, goes to
        /// <see cref="ExecuteConditionBranch{TCondition, TInverted, TCancelable}"/>; any other comparison runs alone here.
        /// </summary>
        /// <remarks>
        /// Compilers branch on nearly every comparison they emit. Every outcome leaves through one tail call: with one per
        /// outcome, the JIT holds the values the later paths read in callee-saved registers across the earlier tail calls,
        /// and saves and restores them on every dispatch. A comparison that faults goes, with unchanged arguments, to its
        /// unfused handler, found by the known opcode rather than the code byte.
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
            nint target;
            nint head = stack.Head;
            uint following = ReadCode(ref stack.Code, pc + 1);
            // The branch's PUSH2 overflows a stack that the condition leaves full, which only ISZERO can.
            bool fits = head >= TCondition.Inputs && (TCondition.Inputs > 1 || head < EvmStack.MaxStackSize - 1);
            if (fits && (byte)following == (byte)Instruction.PUSH2 && (byte)(following >> 24) == (byte)Instruction.JUMPI)
            {
                target = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, EvmExceptionType>)
                    &ExecuteConditionBranch<TCondition, OffFlag, TCancelable>;
            }
            else if (fits && (byte)following == (byte)Instruction.ISZERO
                && (byte)(following >> 8) == (byte)Instruction.PUSH2
                && Unsafe.Add(ref stack.Code, pc + 5) == (byte)Instruction.JUMPI)
            {
                target = (nint)(delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, EvmExceptionType>)
                    &ExecuteConditionBranch<TCondition, OnFlag, TCancelable>;
            }
            else if (TryExecuteCheckedInPlace<TOpcode>(ref stack, ref gas, ref state, ref pc))
            {
                opCodeCount++;
                target = (nint)state.OpcodeHandlers[Unsafe.Add(ref stack.Code, pc)];
            }
            else
            {
                target = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[(int)TCondition.Opcode];
            }

            IL.EnsureLocal(in target);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(opCodeCount));
            IL.Push(target);
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
        /// A comparison, an <c>ISZERO</c> when <typeparamref name="TInverted"/> is set, a <c>PUSH2</c> and a <c>JUMPI</c>, run
        /// as one opcode with one gas check.
        /// </summary>
        /// <remarks>
        /// Reached only from <see cref="ExecuteBranchCondition{TCondition, TOpcode, TCancelable}"/> once the shape and the stack are
        /// known to fit. The result never reaches the stack, and the push and the branch need no dispatch of their own. With
        /// too little gas, or a taken branch to a byte that is not a JUMPDEST, the comparison goes to its unfused handler.
        /// </remarks>
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvmExceptionType ExecuteConditionBranch<TCondition, TInverted, TCancelable>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint opCodeCount)
            where TCondition : struct, IBranchCondition
            where TInverted : struct, IFlag
            where TCancelable : struct, IFlag
        {
            nint skipped = TInverted.IsActive ? 1 : 0;
            ulong cost = (ulong)(2 + skipped) * VeryLowGasCost.GasCost + JumpIGasCost.GasCost;
            bool condition = TCondition.Evaluate(ref stack.PeekBytesByRefUnchecked()) != TInverted.IsActive;
            if (!condition)
            {
                if (gas < cost)
                    goto Fallback;

                gas -= cost;
                stack.Head -= TCondition.Inputs;
                pc += 1 + skipped + 4;
                opCodeCount += 3 + skipped;

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
                    TypeRef.Type<ulong>(),
                    TypeRef.Type<DispatchState>().MakeByRefType(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
                throw IL.Unreachable();
            }

            // The taken branch skips the JUMPDEST it lands on, as the unfused JUMPI does, charging it here.
            cost += JumpDestGasCost.GasCost;
            uint branch = ReadCode(ref stack.Code, pc + 1 + skipped);
            int destination = (int)(((branch >> 8) & 0xFF) << 8 | ((branch >> 16) & 0xFF));
            if (gas < cost || !stack.IsJumpDestination(destination))
                goto Fallback;

            gas -= cost;
            stack.Head -= TCondition.Inputs;
            pc = destination + 1;
            opCodeCount += 4 + skipped;

            if (TCancelable.IsActive && opCodeCount >= state.CancellationPollAt)
                return ExitOpcode(ref state, carriesExecutionGas: true, gas, pc, opCodeCount, EvmExceptionType.None);

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
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();

        Fallback:
            // The arguments are unchanged: the unfused handler starts the opcode over.
            nint fallback = (nint)(state.OpcodeHandlers + FallbackHandlersOffset)[(int)TCondition.Opcode];
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
