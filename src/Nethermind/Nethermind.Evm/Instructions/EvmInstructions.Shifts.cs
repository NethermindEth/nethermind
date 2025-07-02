// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;
using Int256;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Interface for shift operations.
    /// Implementers define a shift operation that uses a shift amount (provided as a UInt256)
    /// to shift a second UInt256 value, returning the shifted result.
    /// </summary>
    public interface IOpShift
    {
        /// <summary>
        /// The gas cost for executing a shift operation.
        /// </summary>
        virtual static long GasCost => GasCostOf.VeryLow;

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
    /// <typeparam name="TOpShift">The specific shift operation (e.g. left or right shift).</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully; 
    /// otherwise, <see cref="EvmExceptionType.StackUnderflow"/> if there are insufficient stack elements.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionShift<TOpShift, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpShift : struct, IOpShift
        where TTracingInst : struct, IFlag
    {
        // Deduct gas cost specific to the shift operation.
        gasAvailable -= TOpShift.GasCost;

        // Pop the shift amount from the stack.
        if (!stack.PopUInt256(out UInt256 a)) goto StackUnderflow;

        // If the shift amount is 256 or more, per EVM semantics, discard the second operand and push zero.
        if (a >= 256)
        {
            // Pop the second operand without using its value.
            if (!stack.PopLimbo()) goto StackUnderflow;
            stack.PushZero<TTracingInst>();
        }
        else
        {
            // Otherwise, pop the value to be shifted.
            if (!stack.PopUInt256(out UInt256 b)) goto StackUnderflow;
            // Perform the shift operation using the specific implementation.
            TOpShift.Operation(in a, in b, out UInt256 result);
            stack.PushUInt256<TTracingInst>(in result);
        }

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
    /// <param name="vm">The virtual machine instance (unused in the operation logic).</param>
    /// <param name="stack">The EVM stack used for operands and result storage.</param>
    /// <param name="gasAvailable">Reference to the available gas, reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if successful; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>
    /// if insufficient stack elements are available.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSar<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost for the arithmetic shift operation.
        gasAvailable -= GasCostOf.VeryLow;

        // Pop the shift amount and the value to be shifted.
        if (!stack.PopUInt256(out UInt256 a) || !stack.PopUInt256(out UInt256 b)) goto StackUnderflow;

        // If the shift amount is 256 or more, the result depends solely on the sign of the value.
        if (a >= 256)
        {
            // Convert the unsigned value to a signed integer to determine its sign.
            if (As<UInt256, Int256>(ref b).Sign >= 0)
            {
                // Non-negative value: result is zero.
                stack.PushZero<TTracingInst>();
            }
            else
            {
                // Negative value: result is -1 (all bits set).
                stack.PushSignedInt256<TTracingInst>(in Int256.MinusOne);
            }
        }
        else
        {
            // For a valid shift amount (<256), perform an arithmetic right shift.
            As<UInt256, Int256>(ref b).RightShift((int)a, out Int256 result);
            // Convert the signed result back to unsigned representation.
            stack.PushUInt256<TTracingInst>(in As<Int256, UInt256>(ref result));
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
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
