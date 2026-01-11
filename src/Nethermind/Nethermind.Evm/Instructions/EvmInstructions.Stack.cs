// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
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
            if (usedFromCode == Size)
            {
                // Directly push the single byte.
                return stack.PushByte<TTracingInst>(Add(ref stack.Code, programCounter));
            }
            else
            {
                // Fallback when immediate data is incomplete.
                return stack.PushZero<TTracingInst>();
            }
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

    [GenerateStackOpcode(3)]
    public partial struct Op3 : IOpCount;

    [GenerateStackOpcode(4)]
    public partial struct Op4 : IOpCount;

    [GenerateStackOpcode(5)]
    public partial struct Op5 : IOpCount;

    [GenerateStackOpcode(6)]
    public partial struct Op6 : IOpCount;

    [GenerateStackOpcode(7)]
    public partial struct Op7 : IOpCount;

    [GenerateStackOpcode(8)]
    public partial struct Op8 : IOpCount;

    [GenerateStackOpcode(9)]
    public partial struct Op9 : IOpCount;

    [GenerateStackOpcode(10)]
    public partial struct Op10 : IOpCount;

    [GenerateStackOpcode(11)]
    public partial struct Op11 : IOpCount;

    [GenerateStackOpcode(12)]
    public partial struct Op12 : IOpCount;

    [GenerateStackOpcode(13)]
    public partial struct Op13 : IOpCount;

    [GenerateStackOpcode(14)]
    public partial struct Op14 : IOpCount;

    [GenerateStackOpcode(15)]
    public partial struct Op15 : IOpCount;

    [GenerateStackOpcode(16)]
    public partial struct Op16 : IOpCount;

    [GenerateStackOpcode(17)]
    public partial struct Op17 : IOpCount;

    [GenerateStackOpcode(18)]
    public partial struct Op18 : IOpCount;

    [GenerateStackOpcode(19)]
    public partial struct Op19 : IOpCount;

    [GenerateStackOpcode(20)]
    public partial struct Op20 : IOpCount;

    [GenerateStackOpcode(21)]
    public partial struct Op21 : IOpCount;

    [GenerateStackOpcode(22)]
    public partial struct Op22 : IOpCount;

    [GenerateStackOpcode(23)]
    public partial struct Op23 : IOpCount;

    [GenerateStackOpcode(24)]
    public partial struct Op24 : IOpCount;

    [GenerateStackOpcode(25)]
    public partial struct Op25 : IOpCount;

    [GenerateStackOpcode(26)]
    public partial struct Op26 : IOpCount;

    [GenerateStackOpcode(27)]
    public partial struct Op27 : IOpCount;

    [GenerateStackOpcode(28)]
    public partial struct Op28 : IOpCount;

    [GenerateStackOpcode(29)]
    public partial struct Op29 : IOpCount;

    [GenerateStackOpcode(30)]
    public partial struct Op30 : IOpCount;

    [GenerateStackOpcode(31)]
    public partial struct Op31 : IOpCount;

    [GenerateStackOpcode(32)]
    public partial struct Op32 : IOpCount;

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
        if (!stack.PopUInt256(out UInt256 position) || !stack.PopUInt256(out UInt256 length)) goto StackUnderflow;

        // The number of topics is defined by the generic parameter.
        long topicsCount = TOpCount.Count;

        // Ensure that the memory expansion for the log data is accounted for.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in position, length, vmState)) goto OutOfGas;
        // Deduct gas for the log entry itself, including per-topic and per-byte data costs.
        long dataSize = (long)length;
        if (!TGasPolicy.ConsumeLogEmission(ref gas, topicsCount, dataSize)) goto OutOfGas;

        // Load the log data from memory.
        if (!vmState.Memory.TryLoad(in position, length, out ReadOnlyMemory<byte> data))
            goto OutOfGas;

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
