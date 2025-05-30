// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm;
using Int256;
using Word = Vector256<byte>;
using static Unsafe;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Pops a value from the EVM stack.
    /// Deducts the base gas cost and returns an exception if the stack is underflowed.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> if successful; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionPop(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Deduct the minimal gas cost for a POP operation.
        gasAvailable -= GasCostOf.Base;
        // Pop from the stack; if nothing to pop, signal a stack underflow.
        return stack.PopLimbo() ? EvmExceptionType.None : EvmExceptionType.StackUnderflow;
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
        virtual static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            // Use available bytes and pad left if fewer than expected.
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
        }
    }

    // Some push operations override the default Push method to handle fixed-size optimizations.

    /// <summary>
    /// 0 item operations.
    /// </summary>
    public struct Op0 : IOpCount { public static int Count => 0; }

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
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            // Determine how many bytes can be used from the code.
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                // Directly push the single byte.
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.PushByte<TTracingInst>(Add(ref bytes, programCounter));
            }
            else
            {
                // Fallback when immediate data is incomplete.
                stack.PushZero<TTracingInst>();
            }
        }
    }

    /// <summary>
    /// 2 item operations.
    /// </summary>
    public struct Op2 : IOpCount { public static int Count => 2; }

    /// <summary>
    /// Push operation for two bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionPush2<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        const int Size = sizeof(ushort);
        // Deduct a very low gas cost for the push operation.
        gasAvailable -= GasCostOf.VeryLow;
        // Retrieve the code segment containing immediate data.
        ReadOnlySpan<byte> code = vm.EvmState.Env.CodeInfo.CodeSection.Span;

        ref byte bytes = ref MemoryMarshal.GetReference(code);
        int remainingCode = code.Length - programCounter;
        Instruction nextInstruction;
        if (!TTracingInst.IsActive &&
            remainingCode > Size &&
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
                gasAvailable -= GasCostOf.Jump;
            }
            else
            {
                gasAvailable -= GasCostOf.JumpI;
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
            if (!Jump((int)destination, ref programCounter, in vm.EvmState.Env))
                goto InvalidJumpDestination;

            goto Success;
        }
        else if (remainingCode >= Size)
        {
            // Optimized push for exactly two bytes.
            stack.Push2Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
        }
        else if (remainingCode == Op1.Count)
        {
            // Directly push the single byte.
            stack.PushByte<TTracingInst>(Add(ref bytes, programCounter));
        }
        else
        {
            // Fallback when immediate data is incomplete.
            stack.PushZero<TTracingInst>();
        }

        programCounter += Size;
    Success:
        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    InvalidJumpDestination:
        return EvmExceptionType.InvalidJumpDestination;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// 3 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op3 : IOpCount { public static int Count => 3; }

    /// <summary>
    /// 4 item operations.
    /// </summary>
    public struct Op4 : IOpCount
    {
        const int Size = sizeof(uint);
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                // Direct push of a 4-byte value.
                stack.Push4Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }

    /// <summary>
    /// 5 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op5 : IOpCount { public static int Count => 5; }

    /// <summary>
    /// 6 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op6 : IOpCount { public static int Count => 6; }

    /// <summary>
    /// 7 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op7 : IOpCount { public static int Count => 7; }

    /// <summary>
    /// 8 item operations.
    /// </summary>
    public struct Op8 : IOpCount
    {
        const int Size = sizeof(ulong);
        public static int Count => Size;

        /// <summary>
        /// Push operation for eight bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push8Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }

    /// <summary>
    /// 9 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op9 : IOpCount { public static int Count => 9; }

    /// <summary>
    /// 10 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op10 : IOpCount { public static int Count => 10; }

    /// <summary>
    /// 11 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op11 : IOpCount { public static int Count => 11; }

    /// <summary>
    /// 12 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op12 : IOpCount { public static int Count => 12; }

    /// <summary>
    /// 13 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op13 : IOpCount { public static int Count => 13; }

    /// <summary>
    /// 14 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op14 : IOpCount { public static int Count => 14; }

    /// <summary>
    /// 15 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op15 : IOpCount { public static int Count => 15; }

    public struct Op16 : IOpCount
    {
        const int Size = 16;
        public static int Count => Size;

        /// <summary>
        /// Push operation for 16 bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push16Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }

    /// <summary>
    /// 17 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op17 : IOpCount { public static int Count => 17; }

    /// <summary>
    /// 18 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op18 : IOpCount { public static int Count => 18; }

    /// <summary>
    /// 19 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op19 : IOpCount { public static int Count => 19; }

    /// <summary>
    /// 20 item operations.
    /// </summary>
    public struct Op20 : IOpCount
    {
        const int Size = 20;
        public static int Count => Size;

        /// <summary>
        /// Push operation for 20 bytes (commonly used for addresses).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                // Optimized push for address size data.
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push20Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }


    /// <summary>
    /// 21 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op21 : IOpCount { public static int Count => 21; }

    /// <summary>
    /// 22 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op22 : IOpCount { public static int Count => 22; }

    /// <summary>
    /// 23 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op23 : IOpCount { public static int Count => 23; }

    /// <summary>
    /// 24 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op24 : IOpCount { public static int Count => 24; }

    /// <summary>
    /// 25 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op25 : IOpCount { public static int Count => 25; }

    /// <summary>
    /// 26 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op26 : IOpCount { public static int Count => 26; }

    /// <summary>
    /// 27 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op27 : IOpCount { public static int Count => 27; }

    /// <summary>
    /// 28 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op28 : IOpCount { public static int Count => 28; }

    /// <summary>
    /// 29 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op29 : IOpCount { public static int Count => 29; }

    /// <summary>
    /// 30 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op30 : IOpCount { public static int Count => 30; }

    /// <summary>
    /// 31 item operations.
    /// Uses the default implementation for pushing data.
    /// </summary>
    public struct Op31 : IOpCount { public static int Count => 31; }

    /// <summary>
    /// 32 item operations.
    /// </summary>
    public struct Op32 : IOpCount
    {
        const int Size = 32;
        public static int Count => Size;

        /// <summary>
        /// Push operation for 32 bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                // Leverage reinterpretation of bytes as a 256-bit vector.
                stack.Push32Bytes<TTracingInst>(in As<byte, Word>(ref Add(ref MemoryMarshal.GetReference(code), programCounter)));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }

    /// <summary>
    /// Handles the PUSH0 opcode which pushes a zero onto the stack.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionPush0<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.Base;
        stack.PushZero<TTracingInst>();
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes a PUSH instruction.
    /// Reads immediate data of a fixed length from the code and pushes it onto the stack.
    /// </summary>
    /// <typeparam name="TOpCount">The push operation implementation defining the byte count.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter, which will be advanced.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionPush<TOpCount, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : IOpCount
        where TTracingInst : struct, IFlag
    {
        // Deduct a very low gas cost for the push operation.
        gasAvailable -= GasCostOf.VeryLow;
        // Retrieve the code segment containing immediate data.
        ReadOnlySpan<byte> code = vm.EvmState.Env.CodeInfo.CodeSection.Span;
        // Use the push method defined by the specific push operation.
        TOpCount.Push<TTracingInst>(TOpCount.Count, ref stack, programCounter, code);
        // Advance the program counter by the number of bytes consumed.
        programCounter += TOpCount.Count;
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes a DUP operation which duplicates the nth stack element.
    /// </summary>
    /// <typeparam name="TOpCount">The duplicate operation implementation that defines which element to duplicate.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success or <see cref="EvmExceptionType.StackUnderflow"/> if insufficient stack elements.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionDup<TOpCount, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : IOpCount
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.VeryLow;
        // Duplicate the nth element from the top; if it fails, signal a stack underflow.
        if (!stack.Dup<TTracingInst>(TOpCount.Count)) goto StackUnderflow;
        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes a SWAP operation which swaps the top element with the (n+1)th element.
    /// </summary>
    /// <typeparam name="TOpCount">The swap operation implementation that defines the swap depth.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success or <see cref="EvmExceptionType.StackUnderflow"/> if insufficient elements.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSwap<TOpCount, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : IOpCount
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.VeryLow;
        // Swap the top element with the (n+1)th element; ensure adequate stack depth.
        if (!stack.Swap<TTracingInst>(TOpCount.Count + 1)) goto StackUnderflow;
        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes a LOG operation which records a log entry with topics and data.
    /// Pops data offset and length, then pops a fixed number of topics from the stack.
    /// Validates memory expansion and deducts gas accordingly.
    /// </summary>
    /// <typeparam name="TOpCount">Specifies the number of log topics (as defined by its Count property).</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the log is successfully recorded; otherwise, an appropriate exception type such as
    /// <see cref="EvmExceptionType.StackUnderflow"/>, <see cref="EvmExceptionType.StaticCallViolation"/>, or <see cref="EvmExceptionType.OutOfGas"/>.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionLog<TOpCount>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCount : struct, IOpCount
    {
        EvmState vmState = vm.EvmState;
        // Logging is not permitted in static call contexts.
        if (vmState.IsStatic) goto StaticCallViolation;

        // Pop memory offset and length for the log data.
        if (!stack.PopUInt256(out UInt256 position) || !stack.PopUInt256(out UInt256 length)) goto StackUnderflow;

        // The number of topics is defined by the generic parameter.
        long topicsCount = TOpCount.Count;

        // Ensure that the memory expansion for the log data is accounted for.
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, length)) goto OutOfGas;
        // Deduct gas for the log entry itself, including per-topic and per-byte data costs.
        if (!UpdateGas(
                GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                (long)length * GasCostOf.LogData, ref gasAvailable)) goto OutOfGas;

        // Load the log data from memory.
        ReadOnlyMemory<byte> data = vmState.Memory.Load(in position, length);
        // Prepare the topics array by popping the corresponding number of words from the stack.
        Hash256[] topics = new Hash256[topicsCount];
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

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    }
}

