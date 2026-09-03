// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using static System.Runtime.CompilerServices.Unsafe;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    /// <summary>
    /// Interface for single-parameter mathematical operations on 256‐bit vectors.
    /// Implementations provide a specific operation that takes one 256‐bit operand and returns a 256‐bit result.
    /// </summary>
    public interface IOpMath1Param : IGasCost
    {
        /// <summary>
        /// The gas cost for executing the operation.
        /// </summary>
        static ulong IGasCost.GasCost => GasCostOf.VeryLow;

        /// <summary>
        /// Executes the operation on the provided 256‐bit operand.
        /// </summary>
        /// <param name="value">The input 256‐bit vector.</param>
        /// <returns>The result of the operation as a 256‐bit vector.</returns>
        abstract static EvmWord Operation(EvmWord value);
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
    public static EvmExceptionType InstructionMath1Param<TGasPolicy, TOpMath>(VirtualMachine<TGasPolicy> _, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpMath : struct, IOpMath1Param
    {
        // Deduct the gas cost associated with the math operation.
        TGasPolicy.Consume<TOpMath>(ref gas);

        return Math1ParamCore<TOpMath>(ref stack);
    }

    /// <summary>Gas-free body of <see cref="InstructionMath1Param{TGasPolicy, TOpMath}"/>, also run directly by the stream executor inside precharged blocks.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType Math1ParamCore<TOpMath>(ref EvmStack stack)
        where TOpMath : struct, IOpMath1Param
    {
        // Peek at the top element of the stack without removing it.
        // This avoids an unnecessary pop/push sequence.
        ref byte bytesRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref bytesRef)) goto StackUnderflow;

        // Read a 256-bit value from unaligned memory on the stack.
        EvmWord result = TOpMath.Operation(ReadUnaligned<EvmWord>(ref bytesRef));

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
        public static EvmWord Operation(EvmWord value) => Vector256.OnesComplement(value);
    }

    /// <summary>
    /// Implements the ISZERO operation.
    /// Compares the input 256‐bit vector to zero and returns a predefined marker if the value is zero;
    /// otherwise, returns a zero vector.
    /// </summary>
    public struct OpIsZero : IOpMath1Param
    {
#if ZK_EVM
        // The zkVM has no hardware SIMD, so Vector256<byte> == default falls back to an 8-iteration
        // element loop. ISZERO is hot (every require/conditional), so compare as a flat 4x ulong OR
        // (endianness-agnostic for a zero test).
        public static EvmWord Operation(EvmWord value)
        {
            ref ulong p = ref As<EvmWord, ulong>(ref value);
            return (p | Add(ref p, 1) | Add(ref p, 2) | Add(ref p, 3)) == 0UL ? OpBitwiseEq.One : default;
        }
#else
        public static EvmWord Operation(EvmWord value) => value == default ? OpBitwiseEq.One : default;
#endif
    }

    /// <summary>
    /// Implements the CLZ opcode.
    /// Counts leading 0's of 256‐bit vector
    /// </summary>
    public struct OpCLZ : IOpMath1Param
    {
        static ulong IGasCost.GasCost => GasCostOf.Low;

        public static EvmWord Operation(EvmWord value) => value == default
            ? Vector256.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0)
            : Vector256.Create(0UL, 0UL, 0UL, (ulong)value.CountLeadingZeroBits() << 56).AsByte();
    }

    /// <summary>
    /// Implements the BYTE opcode.
    /// Extracts a byte from a 256-bit word at the position specified by the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionByte<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume<VeryLowGasCost>(ref gas);

        // Pop the byte position and the 256-bit word.
        if (!stack.PopUInt256(out UInt256 a))
            goto StackUnderflow;
        if (!stack.PopWord256(out Span<byte> bytes))
            goto StackUnderflow;

        // If the position is out-of-range, push zero. Using direct limb access avoids the
        // full 256-bit vector compare + defensive `in` copy the JIT emits for `a >= BigInt32`,
        // and skips the overflow-check path of `(int)a`.
        if (!a.IsUint64 || a.u0 >= 32)
        {
            return stack.PushZero<TTracingInst>();
        }

        // PopWord256 always returns 32 bytes and we've just checked a.u0 < 32, so bypass the
        // span bounds check: JIT can't prove 0 <= (int)a.u0 < bytes.Length across the ulong->int cast.
        return stack.PushByte<TTracingInst>(
            Unsafe.Add(ref MemoryMarshal.GetReference(bytes), (nint)a.u0));

        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

#if !ZK_EVM
    /// <summary>
    /// Set bytes followed by an equal run of clear ones, so loading a word at
    /// <c>WordSize - position</c> yields a mask whose leading <c>position</c> bytes are set.
    /// </summary>
    /// <remarks>Spans of constants become a rodata blob, so this costs no allocation and no static field.</remarks>
    private static ReadOnlySpan<byte> SignExtendPrefixMask =>
    [
        0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];
#endif

    /// <summary>
    /// Implements the SIGNEXTEND opcode.
    /// Performs sign extension on a 256-bit integer in-place based on a specified byte index.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSignExtend<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        TGasPolicy.Consume<LowGasCost>(ref gas);

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
        ref byte bytesRef = ref stack.PeekBytesByRef();
        if (IsNullRef(ref bytesRef))
            goto StackUnderflow;

        // Words are big-endian, so byte `position` carries the sign and every byte above it takes the fill.
        sbyte sign = (sbyte)Add(ref bytesRef, position);

#if ZK_EVM
        // No hardware SIMD in the guest: a 32-element Vector256 fallback would cost more than the copy.
        Span<byte> bytes = MemoryMarshal.CreateSpan(ref bytesRef, EvmStack.WordSize);
        (sign >= 0 ? BytesZero32 : BytesMax32).AsSpan(0, position).CopyTo(bytes[..position]);
#else
        // Filling 0..31 bytes through Span.CopyTo is a runtime-length copy, so it lowered to an
        // out-of-line Memmove on every SIGNEXTEND. Blend the whole word in registers instead: an
        // arithmetic shift broadcasts the fill without branching on the sign, and the prefix mask is a
        // single load, so nothing here depends on `position` being a constant.
        EvmWord fill = Vector256.Create((byte)(sign >> 7));
        EvmWord mask = Vector256.LoadUnsafe(
            ref MemoryMarshal.GetReference(SignExtendPrefixMask), (nuint)(EvmStack.WordSize - position));
        Vector256.ConditionalSelect(mask, fill, Vector256.LoadUnsafe(ref bytesRef)).StoreUnsafe(ref bytesRef);
#endif

        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
