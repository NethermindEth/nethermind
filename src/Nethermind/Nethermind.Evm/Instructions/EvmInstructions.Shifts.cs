// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    /// <summary>
    /// Interface for shift operations.
    /// Implementers define a shift operation that uses a shift amount (provided as a UInt256)
    /// to shift a second UInt256 value, returning the shifted result.
    /// </summary>
    public interface IOpShift : IGasCost
    {
        /// <summary>
        /// The gas cost for executing a shift operation.
        /// </summary>
        static ulong IGasCost.GasCost => GasCostOf.VeryLow;

        /// <summary>
        /// Performs the shift operation.
        /// The lower 8 bits of <paramref name="a"/> (accessed as a.u0) are used as the shift amount.
        /// </summary>
        /// <param name="a">The shift amount.</param>
        /// <param name="b">The value to be shifted.</param>
        /// <param name="result">The resulting shifted value.</param>
        abstract static void Operation(in UInt256 a, in UInt256 b, out UInt256 result);
    }

    /// <summary>
    /// Executes a shift operation on the EVM stack using the specified <typeparamref name="TOpShift"/>.
    /// The operation pops the shift amount and the value to shift, unless the shift amount is 256 or more.
    /// In that case, the value operand is discarded and zero is pushed as the result.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpShift">The specific shift operation (e.g. left or right shift).</typeparam>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is updated by the operation's cost.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully;
    /// otherwise, <see cref="EvmExceptionType.StackUnderflow"/> if there are insufficient stack elements.
    /// </returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionShift<TGasPolicy, TOpShift, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpShift : struct, IOpShift
        where TTracingInst : struct, IFlag
    {
        // Deduct gas cost specific to the shift operation.
        TGasPolicy.Consume<TOpShift>(ref gas);

        return ShiftCore<TOpShift, TTracingInst>(ref stack);
    }

    /// <summary>Gas-free body of <see cref="InstructionShift{TGasPolicy, TOpShift, TTracingInst}"/>.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType ShiftCore<TOpShift, TTracingInst>(ref EvmStack stack)
        where TOpShift : struct, IOpShift
        where TTracingInst : struct, IFlag
    {
        // On x86 without a 256-bit register the JIT lowers the paired pop/push better than in-place
        // conversion. ARM64 is the other way round for every shift: it reverses a word in vector
        // registers, so the paired path buys nothing and costs the push its overflow check.
        if (Vector128.IsHardwareAccelerated &&
            !Vector256.IsHardwareAccelerated &&
            X86Base.IsSupported)
        {
            if (!stack.PopUInt256(out UInt256 shift, out UInt256 value)) goto StackUnderflow;

            if (!shift.IsUint64 || shift.u0 >= 256)
            {
                if (TTracingInst.IsActive)
                    return stack.PushUInt256<TTracingInst>(in UInt256.Zero);

                return stack.PushZero<TTracingInst>();
            }

            TOpShift.Operation(in shift, in value, out UInt256 shifted);
            return stack.PushUInt256<TTracingInst>(in shifted);
        }

        if ((!Vector128.IsHardwareAccelerated || !X86Base.IsSupported) &&
            (typeof(TOpShift) == typeof(OpShl) || typeof(TOpShift) == typeof(OpShr)))
        {
            return ShiftScalar<TOpShift, TTracingInst>(ref stack);
        }

        if (!stack.EnsureDepth(2)) goto StackUnderflow;
        ref byte topRef = ref stack.Pop1Peek32BytesUnchecked(out UInt256 a);

        // Direct limb access avoids the full 256-bit vector compare the JIT emits for `a >= 256`.
        if (!a.IsUint64 || a.u0 >= 256)
        {
            EvmStack.WriteUInt256ToSlot(ref topRef, in UInt256.Zero);
            if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
            return EvmExceptionType.None;
        }

        // Perform the shift operation using the specific implementation.
        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 b);
        TOpShift.Operation(in a, in b, out UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref topRef, in result);
        if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType ShiftScalar<TOpShift, TTracingInst>(ref EvmStack stack)
        where TOpShift : struct, IOpShift
        where TTracingInst : struct, IFlag
    {
        if (!stack.EnsureDepth(2)) return EvmExceptionType.StackUnderflow;
        ref byte topRef = ref stack.Pop1Peek32BytesUnchecked();

        ref ulong value = ref As<byte, ulong>(ref topRef);
        ref ulong shift = ref Add(ref value, EvmStack.WordSize / sizeof(ulong));
        ulong amount = BinaryPrimitives.ReverseEndianness(Add(ref shift, 3));
        if ((shift | Add(ref shift, 1) | Add(ref shift, 2)) != 0 || amount >= 256)
        {
            value = 0;
            Add(ref value, 1) = 0;
            Add(ref value, 2) = 0;
            Add(ref value, 3) = 0;
        }
        else if (amount != 0)
        {
            int wordShift = (int)(amount >> 6);
            int bitShift = (int)(amount & 63);

            if (typeof(TOpShift) == typeof(OpShl))
            {
                for (int destination = 0; destination < 4; destination++)
                {
                    int source = destination + wordShift;
                    ulong shifted = source < 4
                        ? BinaryPrimitives.ReverseEndianness(Add(ref value, source)) << bitShift
                        : 0;
                    if (bitShift != 0 && source + 1 < 4)
                    {
                        shifted |= BinaryPrimitives.ReverseEndianness(Add(ref value, source + 1)) >> (64 - bitShift);
                    }

                    Add(ref value, destination) = BinaryPrimitives.ReverseEndianness(shifted);
                }
            }
            else
            {
                for (int offset = 0; offset < 4; offset++)
                {
                    int destination = 3 - offset;
                    int source = destination - wordShift;
                    ulong shifted = source >= 0
                        ? BinaryPrimitives.ReverseEndianness(Add(ref value, source)) >> bitShift
                        : 0;
                    if (bitShift != 0 && source > 0)
                    {
                        shifted |= BinaryPrimitives.ReverseEndianness(Add(ref value, source - 1)) << (64 - bitShift);
                    }

                    Add(ref value, destination) = BinaryPrimitives.ReverseEndianness(shifted);
                }
            }
        }

        if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an arithmetic right shift (SAR) operation.
    /// Pops a shift amount and a value from the stack, interprets the value as signed,
    /// and performs an arithmetic right shift.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="stack">The EVM stack used for operands and result storage.</param>
    /// <param name="gas">The gas state which is updated by the operation's cost.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if successful; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>
    /// if insufficient stack elements are available.
    /// </returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionSar<TGasPolicy, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume<VeryLowGasCost>(ref gas);

        if (X86Base.IsSupported)
        {
            if (!stack.PopUInt256(out UInt256 shift, out UInt256 value)) goto StackUnderflow;

            if (!shift.IsUint64 || shift.u0 >= 256)
            {
                if (As<UInt256, Int256>(ref value).Sign < 0)
                    return stack.PushSignedInt256<TTracingInst>(in Int256.MinusOne);

                if (TTracingInst.IsActive)
                    return stack.PushUInt256<TTracingInst>(in UInt256.Zero);

                return stack.PushZero<TTracingInst>();
            }

            As<UInt256, Int256>(ref value).RightShift((int)shift, out Int256 shifted);
            return stack.PushUInt256<TTracingInst>(in As<Int256, UInt256>(ref shifted));
        }

        return SarScalar<TTracingInst>(ref stack);
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType SarScalar<TTracingInst>(ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (!stack.EnsureDepth(2)) return EvmExceptionType.StackUnderflow;
        ref byte topRef = ref stack.Pop1Peek32BytesUnchecked();

        ref ulong value = ref As<byte, ulong>(ref topRef);
        ref ulong shift = ref Add(ref value, EvmStack.WordSize / sizeof(ulong));
        ulong amount = BinaryPrimitives.ReverseEndianness(Add(ref shift, 3));
        ulong fill = As<byte, sbyte>(ref topRef) < 0 ? ulong.MaxValue : 0;
        if ((shift | Add(ref shift, 1) | Add(ref shift, 2)) != 0 || amount >= 256)
        {
            value = fill;
            Add(ref value, 1) = fill;
            Add(ref value, 2) = fill;
            Add(ref value, 3) = fill;
        }
        else if (amount != 0)
        {
            int wordShift = (int)(amount >> 6);
            int bitShift = (int)(amount & 63);
            for (int offset = 0; offset < 4; offset++)
            {
                int destination = 3 - offset;
                int source = destination - wordShift;
                ulong shifted = source >= 0
                    ? BinaryPrimitives.ReverseEndianness(Add(ref value, source)) >> bitShift
                    : fill;
                if (bitShift != 0)
                {
                    ulong upper = source > 0
                        ? BinaryPrimitives.ReverseEndianness(Add(ref value, source - 1))
                        : fill;
                    shifted |= upper << (64 - bitShift);
                }

                Add(ref value, destination) = BinaryPrimitives.ReverseEndianness(shifted);
            }
        }

        if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Implements a left shift operation.
    /// The shift amount is taken from the lower 8 bits of the first operand, and the value from the second operand.
    /// </summary>
    public struct OpShl : IOpShift
    {
        /// <summary>
        /// Performs a left shift: shifts <paramref name="b"/> left by the number of bits specified in <paramref name="a"/>.
        /// </summary>
        /// <param name="a">The shift amount, where only the lower 8 bits are used.</param>
        /// <param name="b">The value to be shifted.</param>
        /// <param name="result">The result of the left shift operation.</param>
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => result = b << (int)a.u0; // Use only the lowest limb (u0) as the shift count.
    }

    /// <summary>
    /// Implements a right shift operation.
    /// The shift amount is taken from the lower 8 bits of the first operand, and the value from the second operand.
    /// </summary>
    public struct OpShr : IOpShift
    {
        /// <summary>
        /// Performs a logical right shift: shifts <paramref name="b"/> right by the number of bits specified in <paramref name="a"/>.
        /// </summary>
        /// <param name="a">The shift amount, where only the lower 8 bits are used.</param>
        /// <param name="b">The value to be shifted.</param>
        /// <param name="result">The result of the right shift operation.</param>
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => result = b >> (int)a.u0; // Use only the lowest limb (u0) as the shift count.
    }
}
