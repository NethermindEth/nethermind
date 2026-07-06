// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    /// <summary>
    /// Represents a bitwise operation on 256-bit vectors.
    /// Implementers define a static operation that takes two 256-bit vectors and returns a result vector.
    /// </summary>
    public interface IOpBitwise : IGasCost
    {
        /// <summary>
        /// The gas cost for executing the bitwise operation.
        /// </summary>
        static ulong IGasCost.GasCost => GasCostOf.VeryLow;
        /// <summary>
        /// Executes the bitwise operation.
        /// </summary>
        /// <param name="a">The first operand vector.</param>
        /// <param name="b">The second operand vector.</param>
        /// <returns>The result of the bitwise operation.</returns>
        static abstract EvmWord Operation(in EvmWord a, in EvmWord b);
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
    public static EvmExceptionType InstructionBitwise<TGasPolicy, TOpBitwise>(VirtualMachine<TGasPolicy> _, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpBitwise : struct, IOpBitwise
    {
        // Deduct the operation's gas cost.
        TGasPolicy.Consume<TOpBitwise>(ref gas);

        return BitwiseCore<TOpBitwise>(ref stack);
    }

    /// <summary>Gas-free body of <see cref="InstructionBitwise{TGasPolicy, TOpBitwise}"/>, also run directly by the stream executor inside precharged blocks.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType BitwiseCore<TOpBitwise>(ref EvmStack stack)
        where TOpBitwise : struct, IOpBitwise
    {
        // Pop the first operand from the stack by reference to minimize copying.
        ref byte bytesRef = ref stack.PopBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
        // Read the 256-bit vector from unaligned memory.
        EvmWord aVec = ReadUnaligned<EvmWord>(ref bytesRef);

        // Peek at the top of the stack for the second operand without removing it.
        bytesRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
        EvmWord bVec = ReadUnaligned<EvmWord>(ref bytesRef);

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
        public static EvmWord Operation(in EvmWord a, in EvmWord b) => Vector256.BitwiseAnd(a, b);
    }

    /// <summary>
    /// Implements the bitwise OR operation on two 256-bit vectors.
    /// </summary>
    public struct OpBitwiseOr : IOpBitwise
    {
        public static EvmWord Operation(in EvmWord a, in EvmWord b) => Vector256.BitwiseOr(a, b);
    }

    /// <summary>
    /// Implements the bitwise XOR operation on two 256-bit vectors.
    /// </summary>
    public struct OpBitwiseXor : IOpBitwise
    {
        public static EvmWord Operation(in EvmWord a, in EvmWord b) => Vector256.Xor(a, b);
    }

    /// <summary>
    /// Performs a bitwise equality check between two 256-bit vectors.
    /// If the vectors are equal, returns a vector with the least significant byte set;
    /// otherwise, returns a zero vector.
    /// </summary>
    public struct OpBitwiseEq : IOpBitwise
    {
        // Precomputed vector used as a marker for equality (only the last byte is set to 1).
        public static readonly EvmWord One = Vector256.Create(
            (byte)
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 1
        );

        // Returns a non-zero marker vector if the operands are equal.
#if ZK_EVM
        // The zkVM has no hardware SIMD, so Vector256<byte> == falls back to an 8-iteration element loop.
        // EQ is hot, so compare as flat 4x ulong (endianness-agnostic for an equality test).
        public static EvmWord Operation(in EvmWord a, in EvmWord b)
        {
            ref ulong pa = ref As<EvmWord, ulong>(ref AsRef(in a));
            ref ulong pb = ref As<EvmWord, ulong>(ref AsRef(in b));
            ulong diff = (pa ^ pb)
                | (Add(ref pa, 1) ^ Add(ref pb, 1))
                | (Add(ref pa, 2) ^ Add(ref pb, 2))
                | (Add(ref pa, 3) ^ Add(ref pb, 3));

            return diff == 0UL ? One : default;
        }
#else
        public static EvmWord Operation(in EvmWord a, in EvmWord b) => a == b ? One : default;
#endif
    }
}
