// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Int256;
using static System.Runtime.CompilerServices.Unsafe;
using static Nethermind.Evm.VirtualMachine;

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


    /// <summary>
    /// Implements the BYTE opcode.
    /// Extracts a byte from a 256-bit word at the position specified by the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionByte<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.VeryLow;

        // Pop the byte position and the 256-bit word.
        if (!stack.PopUInt256(out UInt256 a))
            goto StackUnderflow;
        Span<byte> bytes = stack.PopWord256();

        // If the position is out-of-range, push zero.
        if (a >= BigInt32)
        {
            stack.PushZero<TTracingInst>();
        }
        else
        {
            int adjustedPosition = bytes.Length - 32 + (int)a;
            if (adjustedPosition < 0)
            {
                stack.PushZero<TTracingInst>();
            }
            else
            {
                // Push the extracted byte.
                stack.PushByte<TTracingInst>(bytes[adjustedPosition]);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Implements the SIGNEXTEND opcode.
    /// Performs sign extension on a 256-bit integer in-place based on a specified byte index.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSignExtend<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Low;

        // Pop the index to determine which byte to use for sign extension.
        if (!stack.PopUInt256(out UInt256 a))
            goto StackUnderflow;
        if (a >= BigInt32)
        {
            // If the index is out-of-range, no extension is needed.
            if (!stack.EnsureDepth(1))
                goto StackUnderflow;
            return EvmExceptionType.None;
        }

        int position = 31 - (int)a;

        // Peek at the 256-bit word without removing it.
        Span<byte> bytes = stack.PeekWord256();
        sbyte sign = (sbyte)bytes[position];

        // Extend the sign by replacing higher-order bytes.
        if (sign >= 0)
        {
            // Fill with zero bytes.
            BytesZero32.AsSpan(0, position).CopyTo(bytes[..position]);
        }
        else
        {
            // Fill with 0xFF bytes.
            BytesMax32.AsSpan(0, position).CopyTo(bytes[..position]);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
