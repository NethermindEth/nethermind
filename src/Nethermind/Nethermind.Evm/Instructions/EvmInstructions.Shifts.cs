// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

using Int256;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Interface for shift operations.
    /// Implementers define a shift operation that uses a shift amount (provided as int)
    /// to shift a UInt256 value, returning the shifted result.
    /// </summary>
    public interface IOpShift
    {
        /// <summary>
        /// The gas cost for executing a shift operation.
        /// </summary>
        virtual static long GasCost => GasCostOf.VeryLow;

        /// <summary>
        /// Performs the shift operation.
        /// </summary>
        /// <param name="shiftAmount">The shift amount (0-255).</param>
        /// <param name="value">The value to be shifted.</param>
        /// <param name="result">The resulting shifted value.</param>
        abstract static void Operation(int shiftAmount, in UInt256 value, out UInt256 result);
    }

    /// <summary>
    /// Executes a shift operation on the EVM stack using the specified <typeparamref name="TOpShift"/>.
    /// The operation pops the shift amount and the value to shift, unless the shift amount is 256 or more.
    /// In that case, the value operand is discarded and zero is pushed as the result.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpShift">The specific shift operation (e.g. left or right shift).</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully;
    /// otherwise, <see cref="EvmExceptionType.StackUnderflow"/> if there are insufficient stack elements.
    /// </returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionShift<TGasPolicy, TOpShift, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpShift : struct, IOpShift
        where TTracingInst : struct, IFlag
    {
        // Deduct gas cost specific to the shift operation.
        TGasPolicy.Consume(ref gas, TOpShift.GasCost);

        // Only need to check if a < 256 and get the value - no full UInt256 needed
        if (!stack.TryPopSmallIndex(out uint a))
            goto StackUnderflow;

        // If the shift amount is 256 or more, per EVM semantics, discard the second operand and push zero.
        if (a >= 256)
        {
            // Pop the second operand without using its value.
            if (!stack.PopLimbo()) goto StackUnderflow;
            return new(programCounter, stack.PushZero<TTracingInst>());
        }
        else
        {
            // Otherwise, pop the value to be shifted.
            if (!stack.PopUInt256(out UInt256 b)) goto StackUnderflow;
            // Perform the shift operation directly with int shift amount (no UInt256 construction).
            TOpShift.Operation((int)a, in b, out UInt256 result);
            return new(programCounter, stack.PushUInt256<TTracingInst>(in result));
        }
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    }

    /// <summary>
    /// Executes an arithmetic right shift (SAR) operation.
    /// Pops a shift amount and a value from the stack, interprets the value as signed,
    /// and performs an arithmetic right shift.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance (unused in the operation logic).</param>
    /// <param name="stack">The EVM stack used for operands and result storage.</param>
    /// <param name="gas">The gas state which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if successful; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>
    /// if insufficient stack elements are available.
    /// </returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionSar<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost for the arithmetic shift operation.
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);

        // Pop the shift amount and the value to be shifted.
        // Only need to check if a < 256 and get the value - no full UInt256 needed
        if (!stack.TryPopSmallIndex(out uint a))
            goto StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b))
            goto StackUnderflow;

        // If the shift amount is 256 or more, the result depends solely on the sign of the value.
        if (a >= 256)
        {
            // Convert the unsigned value to a signed integer to determine its sign.
            if (As<UInt256, Int256>(ref b).Sign >= 0)
            {
                // Non-negative value: result is zero.
                return new(programCounter, stack.PushZero<TTracingInst>());
            }
            else
            {
                // Negative value: result is -1 (all bits set).
                return new(programCounter, stack.PushSignedInt256<TTracingInst>(in Int256.MinusOne));
            }
        }
        else
        {
            // For a valid shift amount (<256), perform an arithmetic right shift.
            As<UInt256, Int256>(ref b).RightShift((int)a, out Int256 result);
            // Convert the signed result back to unsigned representation.
            return new(programCounter, stack.PushUInt256<TTracingInst>(in As<Int256, UInt256>(ref result)));
        }
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    }

    /// <summary>
    /// Implements a left shift operation.
    /// </summary>
    public struct OpShl : IOpShift
    {
        /// <summary>
        /// Performs a left shift: shifts <paramref name="value"/> left by the specified number of bits.
        /// </summary>
        /// <param name="shiftAmount">The shift amount (0-255).</param>
        /// <param name="value">The value to be shifted.</param>
        /// <param name="result">The result of the left shift operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Operation(int shiftAmount, in UInt256 value, out UInt256 result)
            => result = value << shiftAmount;
    }

    /// <summary>
    /// Implements a right shift operation.
    /// </summary>
    public struct OpShr : IOpShift
    {
        /// <summary>
        /// Performs a logical right shift: shifts <paramref name="value"/> right by the specified number of bits.
        /// </summary>
        /// <param name="shiftAmount">The shift amount (0-255).</param>
        /// <param name="value">The value to be shifted.</param>
        /// <param name="result">The result of the right shift operation.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Operation(int shiftAmount, in UInt256 value, out UInt256 result)
            => result = value >> shiftAmount;
    }
}
