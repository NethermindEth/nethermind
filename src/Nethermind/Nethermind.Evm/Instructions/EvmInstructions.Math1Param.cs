// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.GasPolicy;
using static System.Runtime.CompilerServices.Unsafe;

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
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully; otherwise,
    /// <see cref="EvmExceptionType.StackUnderflow"/> if the stack is empty.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath1Param<TGasPolicy, TOpMath>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> _)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpMath : struct, IOpMath1Param
    {
        // Deduct the gas cost associated with the math operation.
        if (!TGasPolicy.UpdateGas<TOpMath>(ref gas)) return EvmExceptionType.OutOfGas;

        return Math1ParamCore<TOpMath, OnFlag>(ref stack);
    }

    /// <summary>Gas-free body of <see cref="InstructionMath1Param{TGasPolicy, TOpMath}"/>.</summary>
    /// <remarks>When <typeparamref name="TCheckDepth"/> is inactive, the caller must have verified at least 1 stack item.</remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType Math1ParamCore<TOpMath, TCheckDepth>(ref EvmStack stack)
        where TOpMath : struct, IOpMath1Param
        where TCheckDepth : struct, IFlag
    {
        // Folding a word down to a scalar through an EvmWord value makes the operand address-taken, so
        // targets with no 256-bit register home it on the frame and read it back limb by limb. Test the
        // slot where it lies instead.
        if (!Vector256.IsHardwareAccelerated && typeof(TOpMath) == typeof(OpIsZero))
        {
            if (TCheckDepth.IsActive && !stack.EnsureDepth(1))
                return EvmExceptionType.StackUnderflow;

            ref byte slot = ref stack.PeekBytesByRefUnchecked();
            WriteSmallWordToSlot(ref slot, EvmStack.IsSlotZero(ref slot) ? 1UL : 0UL);
            return EvmExceptionType.None;
        }

        if (!Vector128.IsHardwareAccelerated && typeof(TOpMath) == typeof(OpNot))
        {
            if (TCheckDepth.IsActive && !stack.EnsureDepth(1))
                return EvmExceptionType.StackUnderflow;

            ref byte valueBytes = ref stack.PeekBytesByRefUnchecked();

            ref ulong value = ref As<byte, ulong>(ref valueBytes);
            value = ~value;
            Add(ref value, 1) = ~Add(ref value, 1);
            Add(ref value, 2) = ~Add(ref value, 2);
            Add(ref value, 3) = ~Add(ref value, 3);
            return EvmExceptionType.None;
        }

        // Peek at the top element of the stack without removing it.
        // This avoids an unnecessary pop/push sequence.
        if (TCheckDepth.IsActive && !stack.EnsureDepth(1)) goto StackUnderflow;
        ref byte bytesRef = ref stack.PeekBytesByRefUnchecked();

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
        /// <remarks>Reached only where a 256-bit register exists; otherwise ISZERO never builds a value.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmWord Operation(EvmWord value) => value == default ? OpBitwiseEq.One : default;
    }

    /// <summary>
    /// Implements the CLZ opcode (EIP-7939): counts the leading zero bits of the word on the stack.
    /// </summary>
    /// <remarks>
    /// The count is read and written through the stack slot on every target. Passing the word by value
    /// would make it address-taken, which on the 256-bit path homed it on the frame, called the counter
    /// out of line to index its first non-zero byte, and reassembled the result through the frame again.
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionCountLeadingZeros<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (!TGasPolicy.UpdateGas<LowGasCost>(ref gas)) return EvmExceptionType.OutOfGas;

        return CountLeadingZerosCore<OnFlag>(ref stack);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType CountLeadingZerosCore<TCheckDepth>(ref EvmStack stack)
        where TCheckDepth : struct, IFlag
    {
        if (TCheckDepth.IsActive && !stack.EnsureDepth(1))
            return EvmExceptionType.StackUnderflow;

        ref byte slot = ref stack.PeekBytesByRefUnchecked();
        // The counter already answers 256 for a zero word, so no special case is needed for it.
        ulong count = (ulong)Bytes.CountLeadingZeroBits(ref slot);
        WriteSmallWordToSlot(ref slot, count);
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Implements the BYTE opcode.
    /// Extracts a byte from a 256-bit word at the position specified by the stack.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionByte<TGasPolicy, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TGasPolicy.UpdateGas<VeryLowGasCost>(ref gas)) return EvmExceptionType.OutOfGas;

        return ByteCore<TTracingInst, OnFlag>(ref stack);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType ByteCore<TTracingInst, TCheckDepth>(ref EvmStack stack)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        if (TCheckDepth.IsActive && !stack.EnsureDepth(2)) return EvmExceptionType.StackUnderflow;
        ref byte topRef = ref stack.Pop1Peek32BytesUnchecked();

        ref ulong result = ref As<byte, ulong>(ref topRef);
        ref ulong position = ref Add(ref result, EvmStack.WordSize / sizeof(ulong));
        ulong positionLow = Add(ref position, 3);
        nint index = (nint)(positionLow >> 56);
        byte selected = (position | Add(ref position, 1) | Add(ref position, 2) |
            (positionLow & 0x00FF_FFFF_FFFF_FFFFUL)) == 0 && index < EvmStack.WordSize
            ? Add(ref topRef, index)
            : (byte)0;

        result = 0;
        Add(ref result, 1) = 0;
        Add(ref result, 2) = 0;
        Add(ref result, 3) = (ulong)selected << 56;

        if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
        return EvmExceptionType.None;
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
    public static EvmExceptionType InstructionSignExtend<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (!TGasPolicy.UpdateGas<LowGasCost>(ref gas)) return EvmExceptionType.OutOfGas;

        // The index and the word it applies to are adjacent, so one depth check covers both.
        if (!stack.EnsureDepth(2))
            goto StackUnderflow;
        ref byte bytesRef = ref stack.Pop1Peek32BytesUnchecked();

        // Only an index below 32 extends anything, so test the index where it lies. Decoding it as
        // a 256-bit value reverses 32 bytes to reach one, and the word has to go to the frame and
        // come back to be read as scalars.
        ref ulong index = ref As<byte, ulong>(ref Add(ref bytesRef, EvmStack.WordSize));
        ulong indexLow = Add(ref index, 3);
        nint selector = (nint)(indexLow >> 56);
        if ((index | Add(ref index, 1) | Add(ref index, 2) |
            (indexLow & 0x00FF_FFFF_FFFF_FFFFUL)) != 0 || selector >= EvmStack.WordSize)
        {
            // If the index is out-of-range, no extension is needed.
            return EvmExceptionType.None;
        }

        int position = 31 - (int)selector;

        // Words are big-endian, so byte `position` carries the sign and every byte above it takes the fill.
        sbyte sign = (sbyte)Add(ref bytesRef, position);

#if !ZK_EVM
        if (Vector256.IsHardwareAccelerated)
        {
            // Filling 0..31 bytes through Span.CopyTo is a runtime-length copy, so it lowered to an
            // out-of-line Memmove on every SIGNEXTEND. Blend the whole word in registers instead: an
            // arithmetic shift broadcasts the fill without branching on the sign, and the prefix mask is a
            // single load, so nothing here depends on `position` being a constant.
            EvmWord fill = Vector256.Create((byte)(sign >> 7));
            EvmWord prefixMask = Vector256.LoadUnsafe(
                ref MemoryMarshal.GetReference(SignExtendPrefixMask), (nuint)(EvmStack.WordSize - position));
            Vector256.ConditionalSelect(prefixMask, fill, Vector256.LoadUnsafe(ref bytesRef)).StoreUnsafe(ref bytesRef);
            return EvmExceptionType.None;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            ref byte maskRef = ref Add(
                ref MemoryMarshal.GetReference(SignExtendPrefixMask), EvmStack.WordSize - position);
            Vector128<byte> fill = Vector128.Create((byte)(sign >> 7));
            Vector128<byte> prefixMask = Vector128.LoadUnsafe(ref maskRef);
            Vector128.ConditionalSelect(prefixMask, fill, Vector128.LoadUnsafe(ref bytesRef)).StoreUnsafe(ref bytesRef);
            prefixMask = Vector128.LoadUnsafe(ref maskRef, (nuint)Vector128<byte>.Count);
            Vector128.ConditionalSelect(prefixMask, fill, Vector128.LoadUnsafe(ref bytesRef, (nuint)Vector128<byte>.Count))
                .StoreUnsafe(ref bytesRef, (nuint)Vector128<byte>.Count);
            return EvmExceptionType.None;
        }
#endif

        ref ulong word = ref As<byte, ulong>(ref bytesRef);
        ulong fillWord = (ulong)(long)(sign >> 7);
        int wordIndex = position >> 3;
        switch (wordIndex)
        {
            case 3:
                word = fillWord;
                Add(ref word, 1) = fillWord;
                Add(ref word, 2) = fillWord;
                break;
            case 2:
                word = fillWord;
                Add(ref word, 1) = fillWord;
                break;
            case 1:
                word = fillWord;
                break;
        }

        int precedingBytes = position & (sizeof(ulong) - 1);
        if (precedingBytes != 0)
        {
            ulong mask = (1UL << (precedingBytes * 8)) - 1;
            ref ulong partialWord = ref Add(ref word, wordIndex);
            partialWord ^= (partialWord ^ fillWord) & mask;
        }

        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
