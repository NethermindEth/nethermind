// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

using static Unsafe;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Pops a value from the EVM stack.
    /// Deducts the base gas cost and returns an exception if the stack is underflowed.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> if successful; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OpcodeResult InstructionPop<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // Deduct the minimal gas cost for a POP operation.
        TGasPolicy.Consume(ref gas, GasCostOf.Base);
        // Pop from the stack; if nothing to pop, signal a stack underflow.
        return new(programCounter, stack.PopLimbo() ? EvmExceptionType.None : EvmExceptionType.StackUnderflow);
    }

    /// <summary>
    /// Interface for series of items based operations.
    /// The <c>Count</c> property specifies the expected number of items.
    /// </summary>
    public interface IOpCount
    {
        /// <summary>
        /// The number of items expected.
        /// </summary>
        abstract static int Count { get; }

        /// <summary>
        /// This is the default implementation for push operations.
        /// Pushes immediate data from the code onto the stack.
        /// If insufficient bytes are available, pads the value to the expected length.
        /// </summary>
        /// <param name="length">The expected length of the data.</param>
        /// <param name="stack">The execution stack.</param>
        /// <param name="programCounter">The program counter.</param>
        /// <param name="code">The code segment containing the immediate data.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        abstract static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag;
    }

    // Some push operations override the default Push method to handle fixed-size optimizations.

    /// <summary>
    /// 0 item operations.
    /// </summary>
    public struct Op0 : IOpCount
    {
        public static int Count => 0;

        /// <summary>
        /// Push operation for zero
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            return stack.PushZero<TTracingInst>();
        }
    }

    /// <summary>
    /// 1 item operations.
    /// </summary>
    public struct Op1 : IOpCount
    {
        const int Size = sizeof(byte);
        public static int Count => Size;

        /// <summary>
        /// Push operation for a single byte.
        /// If exactly one byte is available, it is pushed; otherwise, zero is pushed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            // Determine how many bytes can be used from the code.
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            return usedFromCode == Size ?
                stack.PushByte<TTracingInst>(Add(ref stack.Code, programCounter)) :
                stack.PushZero<TTracingInst>();
        }
    }

    /// <summary>
    /// 2 item operations.
    /// </summary>
    public struct Op2 : IOpCount
    {
        public static int Count => 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            throw new NotSupportedException($"Use the {nameof(InstructionPush2)} opcode instead");
        }
    }

    /// <summary>
    /// Push operation for two bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OpcodeResult InstructionPush2<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        const int Size = sizeof(ushort);
        // Deduct a very low gas cost for the push operation.
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);
        // Retrieve the code segment containing immediate data.
        ref byte bytes = ref stack.Code;
        int remainingCode = stack.CodeLength - programCounter;
        Instruction nextInstruction;
        if (!TTracingInst.IsActive &&
            remainingCode > Size &&
            stack.Head < EvmStack.MaxStackSize - 1 &&
            ((nextInstruction = (Instruction)Add(ref bytes, programCounter + Size))
                is Instruction.JUMP or Instruction.JUMPI))
        {
            // If next instruction is a JUMP we can skip the PUSH+POP from stack
            ushort destination = As<byte, ushort>(ref Add(ref bytes, programCounter));
            if (BitConverter.IsLittleEndian)
            {
                destination = BinaryPrimitives.ReverseEndianness(destination);
            }

            if (nextInstruction == Instruction.JUMP)
            {
                TGasPolicy.Consume(ref gas, GasCostOf.Jump);
                vm.OpCodeCount++;
            }
            else
            {
                TGasPolicy.Consume(ref gas, GasCostOf.JumpI);
                vm.OpCodeCount++;
                bool shouldJump = TestJumpCondition(ref stack, out bool isOverflow);
                if (isOverflow) goto StackUnderflow;
                if (!shouldJump)
                {
                    // Move forward by 2 bytes + JUMPI
                    programCounter += Size + 1;
                    goto Success;
                }
            }

            // Validate the jump destination and update the program counter if valid.
            if (!Jump((int)destination, ref programCounter, vm.VmState.Env))
                goto InvalidJumpDestination;
            // Prefetch the cache line at the jump destination
            // since hardware prefetcher can't predict jumps.
            PrefetchCodeAtDestination(ref stack, programCounter);

            goto Success;
        }

        ref byte start = ref Add(ref bytes, programCounter);
        EvmExceptionType result;
        if (remainingCode >= Size)
        {
            // Optimized push for exactly two bytes.
            result = stack.Push2Bytes<TTracingInst>(ref start);
        }
        else if (remainingCode == Op1.Count)
        {
            // Directly push the single byte.
            result = stack.PushByte<TTracingInst>(start);
        }
        else
        {
            // Fallback when immediate data is incomplete.
            result = stack.PushZero<TTracingInst>();
        }
        programCounter += Size;
        return new(programCounter, result);
    Success:
        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    InvalidJumpDestination:
        return new(programCounter, EvmExceptionType.InvalidJumpDestination);
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    }

    /// <summary>
    /// 3 item operations.
    /// </summary>
    public struct Op3 : IOpCount
    {
        const int Size = 3;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 3-byte value (common case); otherwise padded push.
                stack.Push3Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 4 item operations.
    /// </summary>
    public struct Op4 : IOpCount
    {
        const int Size = 4;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 4-byte value (common case); otherwise padded push.
                stack.Push4Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 5 item operations.
    /// </summary>
    public struct Op5 : IOpCount
    {
        const int Size = 5;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 5-byte value (common case); otherwise padded push.
                stack.Push5Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 6 item operations.
    /// </summary>
    public struct Op6 : IOpCount
    {
        const int Size = 6;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 6-byte value (common case); otherwise padded push.
                stack.Push6Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 7 item operations.
    /// </summary>
    public struct Op7 : IOpCount
    {
        const int Size = 7;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 7-byte value (common case); otherwise padded push.
                stack.Push7Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 8 item operations.
    /// </summary>
    public struct Op8 : IOpCount
    {
        const int Size = 8;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 8-byte value (common case); otherwise padded push.
                stack.Push8Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 9 item operations.
    /// </summary>
    public struct Op9 : IOpCount
    {
        const int Size = 9;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode != Size ?
                stack.Push9Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 10 item operations.
    /// </summary>
    public struct Op10 : IOpCount
    {
        const int Size = 10;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 10-byte value.
                stack.Push10Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 11 item operations.
    /// </summary>
    public struct Op11 : IOpCount
    {
        const int Size = 11;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 11-byte value.
                stack.Push11Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 12 item operations.
    /// </summary>
    public struct Op12 : IOpCount
    {
        const int Size = 12;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 12-byte value.
                stack.Push12Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 13 item operations.
    /// </summary>
    public struct Op13 : IOpCount
    {
        const int Size = 13;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 13-byte value.
                stack.Push13Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 14 item operations.
    /// </summary>
    public struct Op14 : IOpCount
    {
        const int Size = 14;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 14-byte value.
                stack.Push14Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 15 item operations.
    /// </summary>
    public struct Op15 : IOpCount
    {
        const int Size = 15;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 15-byte value.
                stack.Push15Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 16 item operations.
    /// </summary>
    public struct Op16 : IOpCount
    {
        const int Size = 16;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 16-byte value.
                stack.Push16Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 17 item operations.
    /// </summary>
    public struct Op17 : IOpCount
    {
        const int Size = 17;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 17-byte value.
                stack.Push17Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 18 item operations.
    /// </summary>
    public struct Op18 : IOpCount
    {
        const int Size = 18;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 18-byte value.
                stack.Push18Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 19 item operations.
    /// </summary>
    public struct Op19 : IOpCount
    {
        const int Size = 19;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 19-byte value.
                stack.Push19Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 20 item operations.
    /// </summary>
    public struct Op20 : IOpCount
    {
        const int Size = 20;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 20-byte value.
                stack.Push20Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 21 item operations.
    /// </summary>
    public struct Op21 : IOpCount
    {
        const int Size = 21;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 21-byte value.
                stack.Push21Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 22 item operations.
    /// </summary>
    public struct Op22 : IOpCount
    {
        const int Size = 22;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 22-byte value.
                stack.Push22Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 23 item operations.
    /// </summary>
    public struct Op23 : IOpCount
    {
        const int Size = 23;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 23-byte value.
                stack.Push23Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 24 item operations.
    /// </summary>
    public struct Op24 : IOpCount
    {
        const int Size = 24;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 24-byte value.
                stack.Push24Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 25 item operations.
    /// </summary>
    public struct Op25 : IOpCount
    {
        const int Size = 25;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 25-byte value.
                stack.Push25Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 26 item operations.
    /// </summary>
    public struct Op26 : IOpCount
    {
        const int Size = 26;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 26-byte value.
                stack.Push26Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 27 item operations.
    /// </summary>
    public struct Op27 : IOpCount
    {
        const int Size = 27;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 27-byte value.
                stack.Push27Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 28 item operations.
    /// </summary>
    public struct Op28 : IOpCount
    {
        const int Size = 28;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 28-byte value.
                stack.Push28Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 29 item operations.
    /// </summary>
    public struct Op29 : IOpCount
    {
        const int Size = 29;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 29-byte value.
                stack.Push29Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 30 item operations.
    /// </summary>
    public struct Op30 : IOpCount
    {
        const int Size = 30;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 30-byte value.
                stack.Push30Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 31 item operations.
    /// </summary>
    public struct Op31 : IOpCount
    {
        const int Size = 31;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 31-byte value.
                stack.Push31Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// 32 item operations.
    /// </summary>
    public struct Op32 : IOpCount
    {
        const int Size = 32;
        public static int Count => Size;

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmExceptionType Push<TTracingInst>(int length, ref EvmStack stack, int programCounter)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(stack.CodeLength - programCounter, length);
            ref byte start = ref Unsafe.Add(ref stack.Code, programCounter);
            return usedFromCode == Size ?
                // Direct push of a 32-byte value.
                stack.Push32Bytes<TTracingInst>(ref start) :
                stack.PushBothPaddedBytes<TTracingInst>(ref start, usedFromCode, length);
        }
    }

    /// <summary>
    /// Handles the PUSH0 opcode which pushes a zero onto the stack.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success.</returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionPush0<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, GasCostOf.Base);
        return new(programCounter, stack.PushZero<TTracingInst>());
    }

    /// <summary>
    /// Executes a PUSH instruction.
    /// Reads immediate data of a fixed length from the code and pushes it onto the stack.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy implementation.</typeparam>
    /// <typeparam name="TOpCount">The push operation implementation defining the byte count.</typeparam>
    /// <typeparam name="TTracingInst">The tracing flag.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter, which will be advanced.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success.</returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionPush<TGasPolicy, TOpCount, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCount : struct, IOpCount
        where TTracingInst : struct, IFlag
    {
        // Deduct a very low gas cost for the push operation.
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);
        // Use the push method defined by the specific push operation.
        EvmExceptionType result = TOpCount.Push<TTracingInst>(TOpCount.Count, ref stack, programCounter);
        // Advance the program counter by the number of bytes consumed.
        programCounter += TOpCount.Count;
        return new(programCounter, result);
    }

    /// <summary>
    /// Executes a DUP operation which duplicates the nth stack element.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy implementation.</typeparam>
    /// <typeparam name="TOpCount">The duplicate operation implementation that defines which element to duplicate.</typeparam>
    /// <typeparam name="TTracingInst">The tracing flag.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success or <see cref="EvmExceptionType.StackUnderflow"/> if insufficient stack elements.</returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionDup<TGasPolicy, TOpCount, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCount : struct, IOpCount
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);

        return new(programCounter, stack.Dup<TTracingInst>(TOpCount.Count));
    }

    /// <summary>
    /// Executes a SWAP operation which swaps the top element with the (n+1)th element.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy implementation.</typeparam>
    /// <typeparam name="TOpCount">The swap operation implementation that defines the swap depth.</typeparam>
    /// <typeparam name="TTracingInst">The tracing flag.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success or <see cref="EvmExceptionType.StackUnderflow"/> if insufficient elements.</returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionSwap<TGasPolicy, TOpCount, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCount : struct, IOpCount
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);
        // Swap the top element with the (n+1)th element; ensure adequate stack depth.
        return new(programCounter, stack.Swap<TTracingInst>(TOpCount.Count + 1));
    }

    /// <summary>
    /// Executes a LOG operation which records a log entry with topics and data.
    /// Pops data offset and length, then pops a fixed number of topics from the stack.
    /// Validates memory expansion and deducts gas accordingly.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy implementation.</typeparam>
    /// <typeparam name="TOpCount">Specifies the number of log topics (as defined by its Count property).</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the log is successfully recorded; otherwise, an appropriate exception type such as
    /// <see cref="EvmExceptionType.StackUnderflow"/>, <see cref="EvmExceptionType.StaticCallViolation"/>, or <see cref="EvmExceptionType.OutOfGas"/>.
    /// </returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionLog<TGasPolicy, TOpCount>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCount : struct, IOpCount
    {
        VmState<TGasPolicy> vmState = vm.VmState;
        // Logging is not permitted in static call contexts.
        if (vmState.IsStatic) goto StaticCallViolation;

        // Pop memory offset and length for the log data.
        if (!stack.PopUInt256(out UInt256 position, out UInt256 length)) goto StackUnderflow;

        // Ensure that the memory expansion for the log data is accounted for.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in position, length, vmState)) goto OutOfGas;
        // Deduct gas for the log entry itself, including per-topic and per-byte data costs.
        long dataSize = (long)length;
        if (!TGasPolicy.ConsumeLogEmission(ref gas, TOpCount.Count, dataSize)) goto OutOfGas;

        // Load the log data from memory.
        if (!vmState.Memory.TryLoad(in position, length, out ReadOnlyMemory<byte> data))
            goto OutOfGas;

        // Prepare the topics array by popping the corresponding number of words from the stack.
        Hash256[] topics = TOpCount.Count == 0 ? [] : new Hash256[TOpCount.Count];
        for (int i = 0; i < topics.Length; i++)
        {
            topics[i] = new Hash256(stack.PopWord256());
        }

        // Create a new log entry with the executing account, log data, and topics.
        LogEntry logEntry = new(
            vmState.Env.ExecutingAccount,
            data.ToArray(),
            topics);
        vmState.AccessTracker.Logs.Add(logEntry);

        // Optionally report the log if tracing is enabled.
        if (vm.TxTracer.IsTracingLogs)
        {
            vm.TxTracer.ReportLog(logEntry);
        }

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    StaticCallViolation:
        return new(programCounter, EvmExceptionType.StaticCallViolation);
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    }
}
