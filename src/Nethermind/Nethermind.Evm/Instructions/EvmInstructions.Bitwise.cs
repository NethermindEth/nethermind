// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Evm.GasPolicy;
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
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpBitwise">The specific bitwise operation to execute.</typeparam>
    /// <param name="_">An unused virtual machine instance parameter.</param>
    /// <param name="stack">The EVM stack from which operands are retrieved and where the result is stored.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter (unused in this operation).</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating success or a stack underflow error.</returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionBitwise<TGasPolicy, TOpBitwise>(VirtualMachine<TGasPolicy> _, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpBitwise : struct, IOpBitwise
    {
        // Deduct the operation's gas cost.
        TGasPolicy.Consume(ref gas, TOpBitwise.GasCost);

        // Single bounds check: pop one and get ref to new top (popped is at top + WordSize)
        ref byte top = ref stack.PopPeekBytesByRef();
        if (IsNullRef(ref top)) goto StackUnderflow;

        // Read both 256-bit vectors (popped element is 32 bytes after top)
        ref byte popped = ref Add(ref top, EvmStack.WordSize);
        Word aVec = ReadUnaligned<Word>(ref popped);
        Word bVec = ReadUnaligned<Word>(ref top);

        // Write the result directly into the memory of the new top stack element
        WriteUnaligned(ref top, TOpBitwise.Operation(aVec, bVec));

        return new(programCounter);

    // Forward jump - unpredicted by branch predictor for error path
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    }

    /// <summary>
    /// Implements the bitwise AND operation on two 256-bit vectors.
    /// </summary>
    public struct OpBitwiseAnd : IOpBitwise
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Word Operation(in Word a, in Word b) => Vector256.BitwiseAnd(a, b);
    }

    /// <summary>
    /// Implements the bitwise OR operation on two 256-bit vectors.
    /// </summary>
    public struct OpBitwiseOr : IOpBitwise
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Word Operation(in Word a, in Word b) => Vector256.BitwiseOr(a, b);
    }

    /// <summary>
    /// Implements the bitwise XOR operation on two 256-bit vectors.
    /// </summary>
    public struct OpBitwiseXor : IOpBitwise
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Word Operation(in Word a, in Word b) => Vector256.Xor(a, b);
    }

    /// <summary>
    /// Performs a bitwise equality check between two 256-bit vectors.
    /// If the vectors are equal, returns a vector with the least significant byte set;
    /// otherwise, returns a zero vector.
    /// </summary>
    public struct OpBitwiseEq : IOpBitwise
    {
        // Returns a non-zero marker vector if the operands are equal.
        // Computes result inline to avoid static field initialization overhead.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Word Operation(in Word a, in Word b)
        {
            // Compare all bytes and extract mask (0xFFFFFFFF if all equal, else has zero bits)
            int mask = (int)Vector256.ExtractMostSignificantBits(Vector256.Equals(a, b));
            // Convert mask to EVM boolean: 1 if all equal (-1 mask), else 0
            return IsAllOnesAsBoolVector(mask);
        }
    }

    /// <summary>
    /// Converts an all-ones mask (-1) to a 256-bit vector representing EVM boolean true (byte 31 = 1),
    /// or returns zero vector for any other mask value.
    /// </summary>
    /// <param name="mask">32-bit mask from ExtractMostSignificantBits - should be -1 (all bits set) for true.</param>
    /// <returns>EVM boolean vector: 0x00...01 if mask == -1, else 0x00...00</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Word IsAllOnesAsBoolVector(int mask)
    {
        // mask == -1 (all bytes matched) means we should return 1.
        // Add 1: if mask was -1, sum = 0; otherwise sum != 0.
        // Branchless zero-check: (~(x | -x)) >> 31 returns 1 iff x == 0.
        uint sum = (uint)(mask + 1);
        ulong isTrue = (~(sum | (uint)-(int)sum)) >> 31;
        // Place the 1 in byte 31 (high byte of ulong[3]) for big-endian EVM representation
        return Vector256.Create(0UL, 0UL, 0UL, isTrue << 56).AsByte();
    }
}
