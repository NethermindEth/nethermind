// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;
using Word = Vector256<byte>;

internal sealed partial class EvmInstructions
{
    /// <summary>
    /// Interface for single-parameter mathematical operations on 256‐bit vectors.
    /// Implementations provide a specific operation that takes one 256‐bit operand and returns a 256‐bit result.
    /// </summary>
    public interface IOpMath1Param
    {
        /// <summary>
        /// The gas cost for executing the operation.
        /// </summary>
        virtual static long GasCost => GasCostOf.VeryLow;

        /// <summary>
        /// Executes the operation on the provided 256‐bit operand.
        /// </summary>
        /// <param name="value">The input 256‐bit vector.</param>
        /// <returns>The result of the operation as a 256‐bit vector.</returns>
        abstract static Word Operation(Word value);
    }

    /// <summary>
    /// Executes a single-parameter mathematical operation on the top element of the EVM stack.
    /// The operation is defined by the generic parameter <typeparamref name="TOpMath"/>,
    /// which implements <see cref="IOpMath1Param"/>.
    /// </summary>
    /// <typeparam name="TOpMath">A struct implementing <see cref="IOpMath1Param"/> for the specific math operation.</typeparam>
    /// <param name="_">An unused virtual machine instance.</param>
    /// <param name="stack">The EVM stack from which the operand is read and where the result is written.</param>
    /// <param name="gasAvailable">Reference to the available gas, reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully; otherwise,
    /// <see cref="EvmExceptionType.StackUnderflow"/> if the stack is empty.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath1Param<TOpMath>(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpMath : struct, IOpMath1Param
    {
        // Deduct the gas cost associated with the math operation.
        gasAvailable -= TOpMath.GasCost;

        // Peek at the top element of the stack without removing it.
        // This avoids an unnecessary pop/push sequence.
        ref byte bytesRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;

        // Read a 256-bit value from unaligned memory on the stack.
        Word result = TOpMath.Operation(ReadUnaligned<Word>(ref bytesRef));

        // Write the computed result directly back to the stack slot.
        WriteUnaligned(ref bytesRef, result);

        return EvmExceptionType.None;
    // Label for error handling when the stack does not have the required element.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Implements the bitwise NOT operation.
    /// Computes the ones' complement of the input 256‐bit vector.
    /// </summary>
    public struct OpNot : IOpMath1Param
    {
        public static Word Operation(Word value) => Vector256.OnesComplement(value);
    }

    /// <summary>
    /// Implements the ISZERO operation.
    /// Compares the input 256‐bit vector to zero and returns a predefined marker if the value is zero;
    /// otherwise, returns a zero vector.
    /// </summary>
    public struct OpIsZero : IOpMath1Param
    {
        public static Word Operation(Word value) => value == default ? OpBitwiseEq.One : default;
    }
}
