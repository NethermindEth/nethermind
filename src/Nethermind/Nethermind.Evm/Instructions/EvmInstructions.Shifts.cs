// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
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
    internal interface IOpShift : IGasCost
    {
        /// <summary>
        /// The gas cost for executing a shift operation.
        /// </summary>
        static ulong IGasCost.GasCost => GasCostOf.VeryLow;

        /// <summary>
        /// Performs the shift operation.
        /// An amount of 256 or more (including any amount above 64 bits) yields zero.
        /// </summary>
        /// <remarks>The value operand and result may alias. A zero shift must preserve the value.</remarks>
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
    internal static EvmExceptionType InstructionShift<TGasPolicy, TOpShift, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpShift : struct, IOpShift
        where TTracingInst : struct, IFlag
    {
        // Deduct gas cost specific to the shift operation.
        if (!TGasPolicy.UpdateGas<TOpShift>(ref gas)) return EvmExceptionType.OutOfGas;

        return ShiftCore<TOpShift, TTracingInst, OnFlag>(ref stack);
    }

    /// <summary>Gas-free body of <see cref="InstructionShift{TGasPolicy, TOpShift, TTracingInst}"/>.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType ShiftCore<TOpShift, TTracingInst, TCheckDepth>(ref EvmStack stack)
        where TOpShift : struct, IOpShift
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        if (TCheckDepth.IsActive && !stack.EnsureDepth(2)) goto StackUnderflow;
        ref byte topRef = ref stack.Pop1Peek32BytesUnchecked();
        ref UInt256 a = ref As<byte, UInt256>(ref Add(ref topRef, EvmStack.WordSize));

        // Direct limb access avoids the full 256-bit vector compare the JIT emits for `a >= 256`.
        if (!a.IsUint64 || a.u0 >= 256)
        {
            EvmStack.WriteUInt256ToSlot(ref topRef, in UInt256.Zero);
            if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
            return EvmExceptionType.None;
        }

        // Perform the shift operation using the specific implementation.
        if (a.u0 != 0)
        {
            ref UInt256 value = ref As<byte, UInt256>(ref topRef);
            TOpShift.Operation(in a, in value, out value);
        }
        if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
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
        if (!TGasPolicy.UpdateGas<VeryLowGasCost>(ref gas)) return EvmExceptionType.OutOfGas;

        return SarCore<TTracingInst, OnFlag>(ref stack);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType SarCore<TTracingInst, TCheckDepth>(ref EvmStack stack)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        if (TCheckDepth.IsActive && !stack.EnsureDepth(2)) return EvmExceptionType.StackUnderflow;
        ref byte slot = ref stack.Pop1Peek32BytesUnchecked(out UInt256 shift);
        ref UInt256 value = ref As<byte, UInt256>(ref slot);
        if (!shift.IsUint64 || shift.u0 >= 256)
            value = (long)value.u3 < 0 ? UInt256.MaxValue : UInt256.Zero;
        else if (shift.u0 != 0)
        {
            ref Int256 signed = ref As<UInt256, Int256>(ref value);
            signed.RightShift((int)shift.u0, out signed);
        }
        if (TTracingInst.IsActive) stack.ReportPushWord(ref slot);
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Implements a left shift operation.
    /// The shift amount is taken from the lower 8 bits of the first operand, and the value from the second operand.
    /// </summary>
    internal struct OpShl : IOpShift
    {
        /// <summary>
        /// Performs a left shift: shifts <paramref name="b"/> left by the number of bits specified in <paramref name="a"/>.
        /// </summary>
        /// <param name="a">The shift amount; 256 or more yields zero.</param>
        /// <param name="b">The value to be shifted.</param>
        /// <param name="result">The result of the left shift operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            if (!a.IsUint64 || a.u0 >= 256)
            {
                result = default;
                return;
            }

            ShiftLeft(b.u0, b.u1, b.u2, b.u3, (int)a.u0, out result);
        }
    }

    /// <summary>
    /// Implements a right shift operation.
    /// The shift amount is taken from the lower 8 bits of the first operand, and the value from the second operand.
    /// </summary>
    internal struct OpShr : IOpShift
    {
        /// <summary>
        /// Performs a logical right shift: shifts <paramref name="b"/> right by the number of bits specified in <paramref name="a"/>.
        /// </summary>
        /// <param name="a">The shift amount; 256 or more yields zero.</param>
        /// <param name="b">The value to be shifted.</param>
        /// <param name="result">The result of the right shift operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            if (!a.IsUint64 || a.u0 >= 256)
            {
                result = default;
                return;
            }

            ShiftRight(b.u0, b.u1, b.u2, b.u3, (int)a.u0, out result);
        }
    }

    // SHL and SHR shift the four limbs inline. UInt256.LeftShift/RightShift reach an out-of-line
    // helper, and one call anywhere in an opcode handler makes the JIT save and restore the
    // callee-saved registers on every execution of that handler (it does not shrink-wrap).
    // The limbs arrive as values, so the result may be written to the slot they came from.

    /// <summary>Logical left shift of a 256-bit value by <paramref name="shift"/> in [0, 255].</summary>
    /// <remarks>Limb 0 is the least significant.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ShiftLeft(ulong v0, ulong v1, ulong v2, ulong v3, int shift, out UInt256 result)
    {
        int words = shift >> 6;
        int bits = shift & 63;
        if (words == 1)
        {
            v3 = v2; v2 = v1; v1 = v0; v0 = 0;
        }
        else if (words == 2)
        {
            v3 = v1; v2 = v0; v1 = 0; v0 = 0;
        }
        else if (words == 3)
        {
            v3 = v0; v2 = 0; v1 = 0; v0 = 0;
        }

        if (bits != 0)
        {
            int carry = 64 - bits;
            v3 = (v3 << bits) | (v2 >> carry);
            v2 = (v2 << bits) | (v1 >> carry);
            v1 = (v1 << bits) | (v0 >> carry);
            v0 <<= bits;
        }

        result = new UInt256(v0, v1, v2, v3);
    }

    /// <summary>Logical right shift of a 256-bit value by <paramref name="shift"/> in [0, 255].</summary>
    /// <remarks>Limb 0 is the least significant.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ShiftRight(ulong v0, ulong v1, ulong v2, ulong v3, int shift, out UInt256 result)
    {
        int words = shift >> 6;
        int bits = shift & 63;
        if (words == 1)
        {
            v0 = v1; v1 = v2; v2 = v3; v3 = 0;
        }
        else if (words == 2)
        {
            v0 = v2; v1 = v3; v2 = 0; v3 = 0;
        }
        else if (words == 3)
        {
            v0 = v3; v1 = 0; v2 = 0; v3 = 0;
        }

        if (bits != 0)
        {
            int carry = 64 - bits;
            v0 = (v0 >> bits) | (v1 << carry);
            v1 = (v1 >> bits) | (v2 << carry);
            v2 = (v2 >> bits) | (v3 << carry);
            v3 >>= bits;
        }

        result = new UInt256(v0, v1, v2, v3);
    }
}
