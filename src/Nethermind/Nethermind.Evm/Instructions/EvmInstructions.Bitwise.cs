// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

using Word = Vector256<byte>;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Represents a bitwise operation on 256-bit vectors.
    /// Implementers define a static operation that takes two 256-bit vectors and returns a result vector.
    /// </summary>
    public interface IOpBitwise
    {
        /// <summary>
        /// The gas cost for executing the bitwise operation.
        /// </summary>
        static virtual long GasCost => GasCostOf.VeryLow;
        /// <summary>
        /// Executes the bitwise operation.
        /// </summary>
        /// <param name="a">The first operand vector.</param>
        /// <param name="b">The second operand vector.</param>
        /// <returns>The result of the bitwise operation.</returns>
        static abstract Word Operation(in Word a, in Word b);
    }

    /// <summary>
    /// Executes a bitwise operation defined by <typeparamref name="TOpBitwise"/> on the top two stack elements.
    /// This method reads the operands as 256-bit vectors from unaligned memory and writes the result back directly.
    /// </summary>
    /// <typeparam name="TOpBitwise">The specific bitwise operation to execute.</typeparam>
    /// <param name="_">An unused virtual machine instance parameter.</param>
    /// <param name="stack">The EVM stack from which operands are retrieved and where the result is stored.</param>
    /// <param name="gasAvailable">The remaining gas, reduced by the operation’s cost.</param>
    /// <param name="programCounter">The program counter (unused in this operation).</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating success or a stack underflow error.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBitwise<TOpBitwise>(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpBitwise : struct, IOpBitwise
    {
        // Deduct the operation's gas cost.
        gasAvailable -= TOpBitwise.GasCost;

        // Pop the first operand from the stack by reference to minimize copying.
        ref byte bytesRef = ref stack.PopBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
        // Read the 256-bit vector from unaligned memory.
        Word aVec = ReadUnaligned<Word>(ref bytesRef);

        // Peek at the top of the stack for the second operand without removing it.
        bytesRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
        Word bVec = ReadUnaligned<Word>(ref bytesRef);

        // Write the result directly into the memory of the top stack element.
        WriteUnaligned(ref bytesRef, TOpBitwise.Operation(aVec, bVec));

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Implements the bitwise AND operation on two 256-bit vectors.
    /// </summary>
    public struct OpBitwiseAnd : IOpBitwise
    {
        public static Word Operation(in Word a, in Word b) => Vector256.BitwiseAnd(a, b);
    }

    /// <summary>
    /// Implements the bitwise OR operation on two 256-bit vectors.
    /// </summary>
    public struct OpBitwiseOr : IOpBitwise
    {
        public static Word Operation(in Word a, in Word b) => Vector256.BitwiseOr(a, b);
    }

    /// <summary>
    /// Implements the bitwise XOR operation on two 256-bit vectors.
    /// </summary>
    public struct OpBitwiseXor : IOpBitwise
    {
        public static Word Operation(in Word a, in Word b) => Vector256.Xor(a, b);
    }

    /// <summary>
    /// Performs a bitwise equality check between two 256-bit vectors.
    /// If the vectors are equal, returns a vector with the least significant byte set;
    /// otherwise, returns a zero vector.
    /// </summary>
    public struct OpBitwiseEq : IOpBitwise
    {
        // Precomputed vector used as a marker for equality (only the last byte is set to 1).
        public static Word One = Vector256.Create(
            (byte)
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 1
        );

        // Returns a non-zero marker vector if the operands are equal.
        public static Word Operation(in Word a, in Word b) => a == b ? One : default;
    }
}
