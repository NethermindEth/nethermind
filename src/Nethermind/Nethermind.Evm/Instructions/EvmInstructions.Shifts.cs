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
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully;
    /// otherwise, <see cref="EvmExceptionType.StackUnderflow"/> if there are insufficient stack elements.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionShift<TGasPolicy, TOpShift, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpShift : struct, IOpShift
        where TTracingInst : struct, IFlag
    {
        // Deduct gas cost specific to the shift operation.
        TGasPolicy.Consume<TOpShift>(ref gas);

        return ShiftCore<TOpShift, TTracingInst>(ref stack);
    }

    /// <summary>Gas-free body of <see cref="InstructionShift{TGasPolicy, TOpShift, TTracingInst}"/>, also run directly by the stream executor inside precharged blocks.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType ShiftCore<TOpShift, TTracingInst>(ref EvmStack stack)
        where TOpShift : struct, IOpShift
        where TTracingInst : struct, IFlag
    {
        // Amortise the bounds check across both operands (mirrors InstructionSar).
        if (!stack.PopUInt256(out UInt256 a, out UInt256 b)) goto StackUnderflow;

        // Direct limb access avoids the full 256-bit vector compare the JIT emits for `a >= 256`.
        if (!a.IsUint64 || a.u0 >= 256)
        {
            return stack.PushZero<TTracingInst>();
        }

        // Perform the shift operation using the specific implementation.
        TOpShift.Operation(in a, in b, out UInt256 result);
        return stack.PushUInt256<TTracingInst>(in result);
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
    /// <param name="vm">The virtual machine instance (unused in the operation logic).</param>
    /// <param name="stack">The EVM stack used for operands and result storage.</param>
    /// <param name="gas">The gas state which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if successful; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>
    /// if insufficient stack elements are available.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSar<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume<VeryLowGasCost>(ref gas);

        if (!stack.PopUInt256(out UInt256 a, out UInt256 b)) goto StackUnderflow;

        // If the shift amount is 256 or more, the result depends solely on the sign of the value.
        // Direct limb access avoids the full 256-bit vector compare the JIT emits for `a >= 256`.
        if (!a.IsUint64 || a.u0 >= 256)
        {
            return As<UInt256, Int256>(ref b).Sign >= 0
                ? stack.PushZero<TTracingInst>()
                : stack.PushSignedInt256<TTracingInst>(in Int256.MinusOne);
        }

        As<UInt256, Int256>(ref b).RightShift((int)a, out Int256 result);
        return stack.PushUInt256<TTracingInst>(in As<Int256, UInt256>(ref result));
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
