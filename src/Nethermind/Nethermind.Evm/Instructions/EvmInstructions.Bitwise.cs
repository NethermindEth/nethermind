// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmWord CreateScalarWord(ulong value)
    {
        Unsafe.SkipInit(out EvmWord word);
        ref ulong parts = ref As<EvmWord, ulong>(ref word);
        parts = 0;
        Add(ref parts, 1) = 0;
        Add(ref parts, 2) = 0;
        Add(ref parts, 3) = BinaryPrimitives.ReverseEndianness(value);
        return word;
    }

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
    /// <returns>An <see cref="EvmExceptionType"/> indicating success or a stack underflow error.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBitwise<TGasPolicy, TOpBitwise>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> _)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpBitwise : struct, IOpBitwise
    {
        // Deduct the operation's gas cost.
        TGasPolicy.Consume<TOpBitwise>(ref gas);

        return BitwiseCore<TOpBitwise>(ref stack);
    }

    /// <summary>Gas-free body of <see cref="InstructionBitwise{TGasPolicy, TOpBitwise}"/>.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType BitwiseCore<TOpBitwise>(ref EvmStack stack)
        where TOpBitwise : struct, IOpBitwise
    {
        if (!Vector128.IsHardwareAccelerated &&
            (typeof(TOpBitwise) == typeof(OpBitwiseAnd) ||
             typeof(TOpBitwise) == typeof(OpBitwiseOr) ||
             typeof(TOpBitwise) == typeof(OpBitwiseXor)))
            return BitwiseScalar<TOpBitwise>(ref stack);

        // One depth check, then one address computation: the popped slot sits one word above the
        // slot the result overwrites.
        if (!stack.EnsureDepth(2)) goto StackUnderflow;
        ref byte topRef = ref stack.Pop1Peek32BytesUnchecked();

        EvmWord aVec = ReadUnaligned<EvmWord>(ref Add(ref topRef, EvmStack.WordSize));
        EvmWord bVec = ReadUnaligned<EvmWord>(ref topRef);

        // Write the result directly into the memory of the top stack element.
        WriteUnaligned(ref topRef, TOpBitwise.Operation(aVec, bVec));

        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType BitwiseScalar<TOpBitwise>(ref EvmStack stack)
        where TOpBitwise : struct, IOpBitwise
    {
        if (!stack.EnsureDepth(2))
            return EvmExceptionType.StackUnderflow;

        ref byte bBytes = ref stack.Pop1Peek32BytesUnchecked();

        ref ulong b = ref As<byte, ulong>(ref bBytes);
        ref ulong a = ref Add(ref b, EvmStack.WordSize / sizeof(ulong));
        if (typeof(TOpBitwise) == typeof(OpBitwiseAnd))
        {
            b &= a;
            Add(ref b, 1) &= Add(ref a, 1);
            Add(ref b, 2) &= Add(ref a, 2);
            Add(ref b, 3) &= Add(ref a, 3);
        }
        else if (typeof(TOpBitwise) == typeof(OpBitwiseOr))
        {
            b |= a;
            Add(ref b, 1) |= Add(ref a, 1);
            Add(ref b, 2) |= Add(ref a, 2);
            Add(ref b, 3) |= Add(ref a, 3);
        }
        else
        {
            b ^= a;
            Add(ref b, 1) ^= Add(ref a, 1);
            Add(ref b, 2) ^= Add(ref a, 2);
            Add(ref b, 3) ^= Add(ref a, 3);
        }

        return EvmExceptionType.None;
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
        /// <summary>The word a true comparison pushes: one, in the stack's big-endian layout.</summary>
        /// <remarks>
        /// Property form so the JIT folds it to a PC-relative rodata load. As a static field it was a
        /// class-initialized test, a materialized absolute address and an indirect load on the taken path.
        /// </remarks>
        public static EvmWord One
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Vector256.Create(
                (byte)
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 1
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmWord Operation(in EvmWord a, in EvmWord b)
        {
            if (Vector256.IsHardwareAccelerated)
                return a == b ? One : default;

            ref ulong pa = ref As<EvmWord, ulong>(ref AsRef(in a));
            ref ulong pb = ref As<EvmWord, ulong>(ref AsRef(in b));
            ulong diff = (pa ^ pb)
                | (Add(ref pa, 1) ^ Add(ref pb, 1))
                | (Add(ref pa, 2) ^ Add(ref pb, 2))
                | (Add(ref pa, 3) ^ Add(ref pb, 3));

            return diff == 0UL ? CreateScalarWord(1) : default;
        }
    }
}
