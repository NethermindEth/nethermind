// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using static System.Runtime.CompilerServices.Unsafe;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

using Word = Vector256<byte>;

internal static partial class EvmInstructions
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
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpMath">A struct implementing <see cref="IOpMath1Param"/> for the specific math operation.</typeparam>
    /// <param name="_">An unused virtual machine instance.</param>
    /// <param name="stack">The EVM stack from which the operand is read and where the result is written.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully; otherwise,
    /// <see cref="EvmExceptionType.StackUnderflow"/> if the stack is empty.
    /// </returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionMath1Param<TGasPolicy, TOpMath>(VirtualMachine<TGasPolicy> _, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpMath : struct, IOpMath1Param
    {
        // Deduct the gas cost associated with the math operation.
        TGasPolicy.Consume(ref gas, TOpMath.GasCost);

        // Peek at the top element of the stack without removing it.
        // This avoids an unnecessary pop/push sequence.
        ref byte bytesRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;

        // Read a 256-bit value from unaligned memory on the stack.
        Word result = TOpMath.Operation(ReadUnaligned<Word>(ref bytesRef));

        // Write the computed result directly back to the stack slot.
        WriteUnaligned(ref bytesRef, result);

        return new(programCounter, EvmExceptionType.None);
    // Label for error handling when the stack does not have the required element.
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    }

    /// <summary>
    /// Implements the bitwise NOT operation.
    /// Computes the ones' complement of the input 256‐bit vector.
    /// </summary>
    public struct OpNot : IOpMath1Param
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Word Operation(Word value) => Vector256.OnesComplement(value);
    }

    /// <summary>
    /// Implements the ISZERO operation.
    /// Compares the input 256‐bit vector to zero and returns a predefined marker if the value is zero;
    /// otherwise, returns a zero vector.
    /// </summary>
    public struct OpIsZero : IOpMath1Param
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Word Operation(Word value)
        {
            // Compare all bytes with zero and extract mask
            int mask = (int)Vector256.ExtractMostSignificantBits(Vector256.Equals(value, default));
            // Convert mask to EVM boolean: 1 if all zero (-1 mask), else 0
            return IsAllOnesAsBoolVector(mask);
        }
    }

    /// <summary>
    /// Implements the CLZ opcode.
    /// Counts leading 0's of 256‐bit vector
    /// </summary>
    public struct OpCLZ : IOpMath1Param
    {
        public static long GasCost => GasCostOf.Low;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Word Operation(Word value) => value == default
            ? Vector256.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0)
            : Vector256.Create(0UL, 0UL, 0UL, (ulong)value.CountLeadingZeroBits() << 56).AsByte();
    }

    /// <summary>
    /// Implements the BYTE opcode.
    /// Extracts a byte from a 256-bit word at the position specified by the stack.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionByte<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);

        // Only need to check if a < 32 and get the value - no full UInt256 needed
        if (!stack.TryPopSmallIndex(out uint a))
            goto StackUnderflow;

        ref byte bytes = ref stack.PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes))
            goto StackUnderflow;

        // If the position is out-of-range, push zero.
        if (a >= EvmStack.WordSize)
        {
            return new(programCounter, stack.PushZero<TTracingInst>());
        }
        else
        {
            return new(programCounter, stack.PushByte<TTracingInst>(Unsafe.Add(ref bytes, (nuint)a)));
        }

    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    }

    /// <summary>
    /// Implements the SIGNEXTEND opcode.
    /// Performs sign extension on a 256-bit integer in-place based on a specified byte index.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionSignExtend<TGasPolicy>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        TGasPolicy.Consume(ref gas, GasCostOf.Low);

        // Only need to check if a < 32 and get the value - no full UInt256 needed
        if (!stack.TryPopSmallIndex(out uint a))
            goto StackUnderflow;

        // If a >= 32, the value already fits - no sign extension needed
        if (a >= EvmStack.WordSize)
        {
            if (!stack.EnsureDepth(1))
                goto StackUnderflow;
            return new(programCounter, EvmExceptionType.None);
        }

        // Get direct reference to the stack slot (big-endian, so position 0 is MSB)
        ref byte bytesRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
        int position = 31 - (int)a;  // Byte position to sign-extend from

        // Get sign of the byte at position
        sbyte sign = (sbyte)Unsafe.Add(ref bytesRef, position);

        // Based on benchmarks: AVX2 has ~1.8ns constant overhead but processes all 32 bytes at once.
        // For small fills (0-7 bytes), scalar is faster. For large fills (8+ bytes), AVX2 wins.
        if (Avx2.IsSupported && position >= 8)
        {
            SignExtendAvx2(ref bytesRef, position, sign);
        }
        else
        {
            SignExtendScalar(ref bytesRef, position, sign);
        }

        return new(programCounter, EvmExceptionType.None);

    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SignExtendAvx2(ref byte slot, int position, sbyte sign)
        {
            // Load current value
            Vector256<byte> value = Unsafe.As<byte, Vector256<byte>>(ref slot);

            // Create mask: 0xFF for bytes we want to replace (indices < position)
            // Use comparison with broadcast position
            Vector256<byte> indices = Vector256.Create(
                (byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
                16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31);
            Vector256<byte> posVec = Vector256.Create((byte)position);

            // mask[i] = 0xFF if i < position, else 0x00
            Vector256<byte> mask = Avx2.CompareGreaterThan(posVec.AsSByte(), indices.AsSByte()).AsByte();

            // Fill value: 0x00 if sign >= 0, 0xFF if sign < 0
            // Arithmetic right shift of sign by 7 gives 0x00 or 0xFF
            Vector256<byte> fill = Vector256.Create((byte)(sign >> 7));

            // Blend: keep original where mask is 0, use fill where mask is FF
            Vector256<byte> result = Avx2.BlendVariable(value, fill, mask);

            Unsafe.As<byte, Vector256<byte>>(ref slot) = result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SignExtendScalar(ref byte slot, int position, sbyte sign)
        {
            // Early exit for no-op (position = 0 means nothing to fill)
            if (position == 0) return;

            byte fill = (byte)(sign >> 7); // 0x00 or 0xFF
            ulong fillWord = fill == 0 ? 0UL : ulong.MaxValue;

            // Use chunked writes for efficiency: 8-byte, 4-byte, 2-byte, 1-byte
            int i = 0;
            while (i + 8 <= position)
            {
                WriteUnaligned(ref Unsafe.Add(ref slot, i), fillWord);
                i += 8;
            }
            if (i + 4 <= position)
            {
                WriteUnaligned(ref Unsafe.Add(ref slot, i), (uint)fillWord);
                i += 4;
            }
            if (i + 2 <= position)
            {
                WriteUnaligned(ref Unsafe.Add(ref slot, i), (ushort)fillWord);
                i += 2;
            }
            if (i < position)
            {
                Unsafe.Add(ref slot, i) = fill;
            }
        }
    }
}
