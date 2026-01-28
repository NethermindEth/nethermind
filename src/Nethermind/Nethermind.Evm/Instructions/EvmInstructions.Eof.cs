// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.EvmObjectFormat.Handlers;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.State;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

using Int256;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Interface defining properties for an EOF call instruction.
    /// </summary>
    public interface IOpEofCall
    {
        // Indicates whether the call must be static.
        virtual static bool IsStatic => false;
        // Specifies the execution type of the call.
        abstract static ExecutionType ExecutionType { get; }
    }

    /// <summary>
    /// Represents a standard EOF call instruction.
    /// </summary>
    public struct OpEofCall : IOpEofCall
    {
        public static ExecutionType ExecutionType => ExecutionType.EOFCALL;
    }

    /// <summary>
    /// Represents an EOF delegate call instruction.
    /// </summary>
    public struct OpEofDelegateCall : IOpEofCall
    {
        public static ExecutionType ExecutionType => ExecutionType.EOFDELEGATECALL;
    }

    /// <summary>
    /// Represents an EOF static call instruction.
    /// </summary>
    public struct OpEofStaticCall : IOpEofCall
    {
        public static bool IsStatic => true;
        public static ExecutionType ExecutionType => ExecutionType.EOFSTATICCALL;
    }

    /// <summary>
    /// Retrieves the length of the return data buffer and pushes it onto the stack.
    /// Deducts the base gas cost from the available gas.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TTracingInst">Tracing flag type.</typeparam>
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">Reference to the operand stack.</param>
    /// <param name="gas">Reference to the gas state.</param>
    /// <param name="programCounter">Reference to the current program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating the outcome.</returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionReturnDataSize<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Deduct base gas cost for this instruction.
        TGasPolicy.Consume(ref gas, GasCostOf.Base);

        // Push the length of the return data buffer (as a 32-bit unsigned integer) onto the stack.
        return new(programCounter, stack.PushUInt32<TTracingInst>((uint)vm.ReturnDataBuffer.Length));
    }

    /// <summary>
    /// Copies a slice from the VM's return data buffer into memory.
    /// Parameters are popped from the stack (destination offset, source offset, and size).
    /// Performs gas and memory expansion cost updates before copying.
    /// </summary>
    /// <typeparam name="TTracingInst">
    /// A tracing flag type to conditionally report memory changes.
    /// </typeparam>
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">Reference to the operand stack.</param>
    /// <param name="gasAvailable">Reference to the available gas.</param>
    /// <param name="programCounter">Reference to the current program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> representing success or the type of failure.</returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionReturnDataCopy<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Pop the required parameters: destination memory offset, source offset in return data, and number of bytes to copy.
        if (!stack.PopUInt256(out UInt256 destOffset) ||
            !stack.PopUInt256(out UInt256 sourceOffset) ||
            !stack.PopUInt256(out UInt256 size))
        {
            goto StackUnderflow;
        }

        // Deduct the fixed gas cost and the memory cost based on the size (rounded up to 32-byte words).
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow + GasCostOf.Memory * EvmCalculations.Div32Ceiling(in size, out bool outOfGas));
        if (outOfGas) goto OutOfGas;

        ReadOnlyMemory<byte> returnDataBuffer = vm.ReturnDataBuffer;
        // For legacy (non-EOF) code, ensure that the copy does not exceed the available return data.
        if (vm.CallFrame.CodeInfo.Version == 0 &&
            (UInt256.AddOverflow(size, sourceOffset, out UInt256 result) || result > returnDataBuffer.Length))
        {
            goto AccessViolation;
        }

        // Only perform the copy if size is non-zero.
        if (!size.IsZero)
        {
            // Update memory cost for expanding memory to accommodate the destination slice.
            if (!TGasPolicy.UpdateMemoryCost(ref gas, in destOffset, size, vm.CallFrame))
                return new(programCounter, EvmExceptionType.OutOfGas);

            // Get the source slice; if the requested range exceeds the buffer length, it is zero-padded.
            ZeroPaddedSpan slice = returnDataBuffer.Span.SliceWithZeroPadding(sourceOffset, (int)size);
            if (!vm.CallFrame.Memory.TrySave(in destOffset, in slice)) goto OutOfGas;

            // Report the memory change if tracing is active.
            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportMemoryChange(destOffset, in slice);
            }
        }

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    AccessViolation:
        return new(programCounter, EvmExceptionType.AccessViolation);
    }

    /// <summary>
    /// Loads 32 bytes from the code's data section at the offset specified on the stack.
    /// Pushes the zero-padded result onto the stack.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">Reference to the operand stack.</param>
    /// <param name="gasAvailable">Reference to the remaining gas.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> representing success or an error.</returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionDataLoad<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Ensure the instruction is only valid for non-legacy (EOF) code.
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        // Deduct gas required for data loading.
        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.DataLoad))
            goto OutOfGas;

        // Pop the offset from the stack.
        stack.PopUInt256(out UInt256 offset);
        // Load 32 bytes from the data section at the given offset (with zero padding if necessary).
        ZeroPaddedSpan bytes = codeInfo.DataSection.SliceWithZeroPadding(offset, 32);
        stack.PushBytes<TTracingInst>(bytes);

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Loads 32 bytes from the data section using an immediate two-byte offset embedded in the code.
    /// Advances the program counter accordingly.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionDataLoadN<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.DataLoadN))
            goto OutOfGas;

        // Read a 16-bit immediate operand from the code.
        ushort offset = codeInfo.CodeSection.Span.Slice(programCounter, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();
        // Load the 32-byte word from the data section at the immediate offset.
        ZeroPaddedSpan bytes = codeInfo.DataSection.SliceWithZeroPadding(offset, 32);
        stack.PushBytes<TTracingInst>(bytes);

        // Advance the program counter past the immediate operand.
        programCounter += EofValidator.TWO_BYTE_LENGTH;

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Pushes the size of the code's data section onto the stack.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionDataSize<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.DataSize))
            goto OutOfGas;

        return new(programCounter, stack.PushUInt32<TTracingInst>((uint)codeInfo.DataSection.Length));

    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Copies a slice of bytes from the code's data section into the VM's memory.
    /// The source offset, destination memory offset, and number of bytes are specified on the stack.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionDataCopy<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        // Pop destination memory offset, data section offset, and size.
        if (!stack.PopUInt256(out UInt256 memOffset) ||
            !stack.PopUInt256(out UInt256 offset) ||
            !stack.PopUInt256(out UInt256 size))
        {
            goto StackUnderflow;
        }

        // Calculate memory expansion gas cost and deduct overall gas for data copy.
        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.DataCopy + GasCostOf.Memory * EvmCalculations.Div32Ceiling(in size, out bool outOfGas))
            || outOfGas)
        {
            goto OutOfGas;
        }

        if (!size.IsZero)
        {
            // Update memory cost for the destination region.
            if (!TGasPolicy.UpdateMemoryCost(ref gas, in memOffset, size, vm.CallFrame))
                goto OutOfGas;
            // Retrieve the slice from the data section with zero padding if necessary.
            ZeroPaddedSpan dataSectionSlice = codeInfo.DataSection.SliceWithZeroPadding(offset, (int)size);
            if (!vm.CallFrame.Memory.TrySave(in memOffset, in dataSectionSlice)) goto OutOfGas;

            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportMemoryChange(memOffset, dataSectionSlice);
            }
        }

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Performs a relative jump in the code.
    /// Reads a two-byte signed offset from the code section and adjusts the program counter accordingly.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionRelativeJump<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.RJump))
            goto OutOfGas;

        // Read a signed 16-bit offset and adjust the program counter.
        short offset = codeInfo.CodeSection.Span.Slice(programCounter, EofValidator.TWO_BYTE_LENGTH).ReadEthInt16();
        programCounter += EofValidator.TWO_BYTE_LENGTH + offset;

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Conditionally performs a relative jump based on the top-of-stack condition.
    /// Pops a condition value; if non-zero, jumps by the signed offset embedded in the code.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionRelativeJumpIf<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.RJumpi))
            goto OutOfGas;

        // Pop the condition word.
        Span<byte> condition = stack.PopWord256();
        // Read the jump offset from the code.
        short offset = codeInfo.CodeSection.Span.Slice(programCounter, EofValidator.TWO_BYTE_LENGTH).ReadEthInt16();
        if (!condition.IsZero())
        {
            // Apply the offset if the condition is non-zero.
            programCounter += offset;
        }
        // Always advance past the immediate operand.
        programCounter += EofValidator.TWO_BYTE_LENGTH;

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Implements a jump table dispatch.
    /// Uses the top-of-stack value as an index into a list of jump offsets, then jumps accordingly.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionJumpTable<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.RJumpv))
            goto OutOfGas;

        // Pop the table index from the stack.
        stack.PopUInt256(out UInt256 indexValue);
        ReadOnlySpan<byte> codeSection = codeInfo.CodeSection.Span;

        // Determine the number of cases in the jump table (first byte + one).
        var count = codeSection[programCounter] + 1;
        // Calculate the total immediate bytes to skip after processing the jump table.
        var immediateCount = (ushort)(count * EofValidator.TWO_BYTE_LENGTH + EofValidator.ONE_BYTE_LENGTH);
        if (indexValue < count)
        {
            // Calculate the jump offset from the corresponding entry in the jump table.
            int case_v = programCounter + EofValidator.ONE_BYTE_LENGTH + (int)indexValue * EofValidator.TWO_BYTE_LENGTH;
            int offset = codeSection.Slice(case_v, EofValidator.TWO_BYTE_LENGTH).ReadEthInt16();
            programCounter += offset;
        }
        // Skip over the jump table immediateCount.
        programCounter += immediateCount;

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Performs a subroutine call within the code.
    /// Sets up the return state and verifies stack and call depth constraints before transferring control.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionCallFunction<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        CodeInfo iCodeInfo = vm.CallFrame.CodeInfo;
        if (iCodeInfo.Version == 0)
            goto BadInstruction;

        EofCodeInfo codeInfo = (EofCodeInfo)iCodeInfo;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.Callf))
            goto OutOfGas;

        ReadOnlySpan<byte> codeSection = codeInfo.CodeSection.Span;
        // Read the immediate two-byte index for the target section.
        var index = (int)codeSection.Slice(programCounter, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();
        // Get metadata for the target section.
        (int inputCount, _, int maxStackHeight) = codeInfo.GetSectionMetadata(index);

        // Verify that invoking the subroutine will not exceed the maximum stack height.
        if (Eof1.MAX_STACK_HEIGHT - maxStackHeight + inputCount < stack.Head)
        {
            goto StackOverflow;
        }

        // Ensure there is room on the return stack.
        if (vm.CallFrame.ReturnStackHead == Eof1.RETURN_STACK_MAX_HEIGHT)
            goto InvalidSubroutineEntry;

        // Push current state onto the return stack.
        vm.CallFrame.ReturnStack[vm.CallFrame.ReturnStackHead++] = new ReturnState
        {
            Index = vm.SectionIndex,
            Height = stack.Head - inputCount,
            Offset = programCounter + EofValidator.TWO_BYTE_LENGTH
        };

        // Set up the subroutine call.
        vm.SectionIndex = index;
        programCounter = codeInfo.CodeSectionOffset(index).Start;

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    InvalidSubroutineEntry:
        return new(programCounter, EvmExceptionType.InvalidSubroutineEntry);
    StackOverflow:
        return new(programCounter, EvmExceptionType.StackOverflow);
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Returns from a subroutine call by restoring the state from the return stack.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionReturnFunction<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        CodeInfo codeInfo = vm.CallFrame.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.Retf))
            goto OutOfGas;

        // Pop the return state from the return stack.
        ReturnState stackFrame = vm.CallFrame.ReturnStack[--vm.CallFrame.ReturnStackHead];
        vm.SectionIndex = stackFrame.Index;
        programCounter = stackFrame.Offset;

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Performs an unconditional jump to a subroutine using a section index read from the code.
    /// Verifies that the target section does not cause a stack overflow.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionJumpFunction<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        CodeInfo iCodeInfo = vm.CallFrame.CodeInfo;
        if (iCodeInfo.Version == 0)
            goto BadInstruction;

        EofCodeInfo codeInfo = (EofCodeInfo)iCodeInfo;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.Jumpf))
            goto OutOfGas;

        // Read the target section index from the code.
        int index = codeInfo.CodeSection.Span.Slice(programCounter, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();
        (int inputCount, _, int maxStackHeight) = codeInfo.GetSectionMetadata(index);

        // Check that the stack will not overflow after the jump.
        if (Eof1.MAX_STACK_HEIGHT - maxStackHeight + inputCount < stack.Head)
        {
            goto StackOverflow;
        }
        vm.SectionIndex = index;
        programCounter = codeInfo.CodeSectionOffset(index).Start;

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    StackOverflow:
        return new(programCounter, EvmExceptionType.StackOverflow);
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Duplicates a stack item based on an immediate operand.
    /// The immediate value (n) specifies that the (n+1)th element from the top is duplicated.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionDupN<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.Dupn))
            goto OutOfGas;

        // Read the immediate operand.
        int imm = codeInfo.CodeSection.Span[programCounter];
        // Duplicate the (imm+1)th stack element.
        EvmExceptionType result = stack.Dup<TTracingInst>(imm + 1);

        programCounter += 1;

        return new(programCounter, result);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Swaps two stack items. The immediate operand specifies the swap distance.
    /// Swaps the top-of-stack with the (n+1)th element.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionSwapN<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.Swapn))
            goto OutOfGas;

        // Immediate operand determines the swap index.
        int n = 1 + (int)codeInfo.CodeSection.Span[programCounter];
        EvmExceptionType result = stack.Swap<TTracingInst>(n + 1);

        programCounter += 1;

        return new(programCounter, result);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Exchanges two stack items using a combined immediate operand.
    /// The high nibble and low nibble of the operand specify the two swap distances.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionExchange<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (vm.CallFrame.CodeInfo is not EofCodeInfo codeInfo)
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.Swapn))
            goto OutOfGas;

        ReadOnlySpan<byte> codeSection = codeInfo.CodeSection.Span;
        // Extract two 4-bit values from the immediate operand.
        int n = 1 + (int)(codeSection[programCounter] >> 0x04);
        int m = 1 + (int)(codeSection[programCounter] & 0x0f);

        // Exchange the elements at the calculated positions.
        stack.Exchange<TTracingInst>(n + 1, m + n + 1);

        programCounter += 1;

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Implements the EOFCREATE instruction which creates a new contract using EOF semantics.
    /// This method performs multiple steps including gas deductions, memory expansion,
    /// reading immediate operands, balance checks, and preparing the execution environment for the new contract.
    /// </summary>
    /// <typeparam name="TTracingInst">
    /// A tracing flag type to conditionally report events.
    /// </typeparam>
    [SkipLocalsInit]
    public static OpcodeResult InstructionEofCreate<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        Metrics.IncrementCreates();
        vm.ReturnData = null;

        IReleaseSpec spec = vm.Spec;
        CallFrame<TGasPolicy> callFrame = vm.CallFrame;
        if (callFrame.CodeInfo.Version == 0)
            goto BadInstruction;

        if (callFrame.IsStatic)
            goto StaticCallViolation;

        // Cast the current code info to EOF-specific container type.
        EofCodeInfo container = callFrame.CodeInfo as EofCodeInfo;
        ExecutionType currentContext = ExecutionType.EOFCREATE;

        // 1. Deduct the creation gas cost.
        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.TxCreate))
            goto OutOfGas;

        ReadOnlySpan<byte> codeSection = container.CodeSection.Span;
        // 2. Read the immediate operand for the init container index.
        int initContainerIndex = codeSection[programCounter++];

        // 3. Pop contract creation parameters from the stack.
        if (!stack.PopUInt256(out UInt256 value) ||
            !stack.PopWord256(out Span<byte> salt) ||
            !stack.PopUInt256(out UInt256 dataOffset) ||
            !stack.PopUInt256(out UInt256 dataSize))
        {
            goto OutOfGas;
        }

        // 4. Charge for memory expansion for the input data.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in dataOffset, dataSize, vm.CallFrame))
            goto OutOfGas;

        // 5. Load the init code (EOF subContainer) from the container using the given index.
        ReadOnlyMemory<byte> initContainer = container.ContainerSection[(Range)container.ContainerSectionOffset(initContainerIndex)!.Value];
        // EIP-3860: Check that the init code size does not exceed the maximum allowed.
        if (spec.IsEip3860Enabled)
        {
            if (initContainer.Length > spec.MaxInitCodeSize)
                goto OutOfGas;
        }

        // 6. Deduct gas for keccak256 hashing of the init code.
        long numberOfWordsInInitCode = EvmCalculations.Div32Ceiling((UInt256)initContainer.Length, out bool outOfGas);
        long hashCost = GasCostOf.Sha3Word * numberOfWordsInInitCode;
        if (outOfGas || !TGasPolicy.UpdateGas(ref gas, hashCost))
            goto OutOfGas;

        IWorldState state = vm.WorldState;
        // 7. Check call depth and caller's balance before proceeding with creation.
        UInt256 balance = state.GetBalance(callFrame.ExecutingAccount);
        if (vm.CallDepth >= MaxCallDepth || value > balance)
        {
            // In case of failure, do not consume additional gas.
            vm.ReturnDataBuffer = Array.Empty<byte>();
            return new(programCounter, stack.PushZero<TTracingInst>());
        }

        // 9. Determine gas available for the new contract execution, applying the 63/64 rule if enabled.
        long gasAvailable = TGasPolicy.GetRemainingGas(in gas);
        long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!TGasPolicy.UpdateGas(ref gas, callGas))
            goto OutOfGas;

        // 10. Increment the nonce of the sender account.
        UInt256 accountNonce = state.GetNonce(callFrame.ExecutingAccount);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            return new(programCounter, stack.PushZero<TTracingInst>());
        }
        state.IncrementNonce(callFrame.ExecutingAccount);

        // 11. Calculate the new contract address.
        Address contractAddress = ContractAddress.From(callFrame.ExecutingAccount, salt, initContainer.Span);
        if (spec.UseHotAndColdStorage)
        {
            // Warm up the target address for subsequent storage accesses.
            vm.TrackingState.WarmUp(contractAddress);
        }

        if (TTracingInst.IsActive)
            vm.EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

        // Take a snapshot before modifying state for the new contract.
        Snapshot snapshot = state.TakeSnapshot();

        bool accountExists = state.AccountExists(contractAddress);

        // If the account already exists and is non-zero, then the creation fails.
        if (accountExists && contractAddress.IsNonZeroAccount(spec, vm.CodeInfoRepository, state))
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            return new(programCounter, stack.PushZero<TTracingInst>());
        }

        // If the account is marked as dead, clear its storage.
        if (state.IsDeadAccount(contractAddress))
        {
            state.ClearStorage(contractAddress);
        }

        // Deduct the transferred value from the caller's balance.
        state.SubtractFromBalance(callFrame.ExecutingAccount, value, spec);

        // Create new code info for the init code.
        CodeInfo codeInfo = CodeInfoFactory.CreateCodeInfo(initContainer, spec, ValidationStrategy.ExtractHeader);

        // 8. Prepare the callData from the caller’s memory slice.
        if (!callFrame.Memory.TryLoad(dataOffset, dataSize, out ReadOnlyMemory<byte> callData))
            goto OutOfGas;

        vm.ReturnData = CallFrame<TGasPolicy>.Rent(
            gas: TGasPolicy.FromLong(callGas),
            outputDestination: 0,
            outputLength: 0,
            executionType: currentContext,
            isStatic: callFrame.IsStatic,
            isCreateOnPreExistingAccount: accountExists,
            codeInfo: codeInfo,
            executingAccount: contractAddress,
            caller: callFrame.ExecutingAccount,
            codeSource: null,
            value: in value,
            inputData: in callData,
            stateForAccessLists: in callFrame.AccessTracker,
            trackingState: vm.TrackingState,
            snapshot: in snapshot);

        return new(programCounter, EvmExceptionType.Return);
    // Jump forward to be unpredicted by the branch predictor.
    StaticCallViolation:
        return new(programCounter, EvmExceptionType.StaticCallViolation);
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Returns the contract creation result.
    /// Extracts the deployment code from a specified container section and prepares the return data.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionReturnCode<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // This instruction is only valid in create contexts.
        if (!vm.CallFrame.ExecutionType.IsAnyCreateEof())
            goto BadInstruction;

        if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.ReturnCode))
            goto OutOfGas;

        IReleaseSpec spec = vm.Spec;
        EofCodeInfo codeInfo = (EofCodeInfo)vm.CallFrame.CodeInfo;

        // Read the container section index from the code.
        byte sectionIdx = codeInfo.CodeSection.Span[programCounter++];
        // Retrieve the deployment code using the container section offset.
        ReadOnlyMemory<byte> deployCode = codeInfo.ContainerSection[(Range)codeInfo.ContainerSectionOffset(sectionIdx)];
        EofCodeInfo deployCodeInfo = (EofCodeInfo)CodeInfoFactory.CreateCodeInfo(deployCode, spec, ValidationStrategy.ExtractHeader);

        // Pop memory offset and size for the return data.
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);

        if (!TGasPolicy.UpdateMemoryCost(ref gas, in a, b, vm.CallFrame))
            goto OutOfGas;

        int projectedNewSize = (int)b + deployCodeInfo.DataSection.Length;
        // Ensure the projected size is within valid bounds.
        if (projectedNewSize < deployCodeInfo.EofContainer.Header.DataSection.Size || projectedNewSize > UInt16.MaxValue)
        {
            return new(programCounter, EvmExceptionType.AccessViolation);
        }

        // Load the memory slice as the return data buffer.
        if (!vm.CallFrame.Memory.TryLoad(a, b, out vm.ReturnDataBuffer))
            goto OutOfGas;

        vm.ReturnData = deployCodeInfo;

        return new(programCounter, EvmExceptionType.Return);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Loads 32 bytes from the return data buffer using an offset from the stack,
    /// then pushes the retrieved value onto the stack.
    /// This instruction is only valid when EOF is enabled.
    /// </summary>
    [SkipLocalsInit]
    public static OpcodeResult InstructionReturnDataLoad<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;
        CodeInfo codeInfo = vm.CallFrame.CodeInfo;
        if (!spec.IsEofEnabled || codeInfo.Version == 0)
            goto BadInstruction;

        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);

        if (!stack.PopUInt256(out UInt256 offset))
            goto StackUnderflow;

        ZeroPaddedSpan slice = vm.ReturnDataBuffer.Span.SliceWithZeroPadding(offset, 32);
        stack.PushBytes<TTracingInst>(slice);

        return new(programCounter, EvmExceptionType.None);
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }

    /// <summary>
    /// Implements the EOF call instructions (CALL, DELEGATECALL, STATICCALL) for EOF-enabled contracts.
    /// Pops the target address, callData parameters, and (if applicable) transfer value from the stack,
    /// performs account access checks and gas adjustments, and then initiates the call.
    /// </summary>
    /// <typeparam name="TOpEofCall">
    /// The call type (standard, delegate, or static) that determines behavior and execution type.
    /// </typeparam>
    /// <typeparam name="TTracingInst">
    /// A tracing flag type used to report VM state changes during the call.
    /// </typeparam>
    [SkipLocalsInit]
    public static OpcodeResult InstructionEofCall<TGasPolicy, TOpEofCall, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpEofCall : struct, IOpEofCall
        where TTracingInst : struct, IFlag
    {
        Metrics.IncrementCalls();

        const int MIN_RETAINED_GAS = 5000;

        IReleaseSpec spec = vm.Spec;
        vm.ReturnData = null;
        CallFrame<TGasPolicy> callFrame = vm.CallFrame;
        IWorldState state = vm.WorldState;

        // This instruction is only available for EOF-enabled contracts.
        if (callFrame.CodeInfo.Version == 0)
            goto BadInstruction;

        // 1. Pop the target address (as 32 bytes) and memory offsets/length for the call data.
        if (!stack.PopWord256(out Span<byte> targetBytes) ||
            !stack.PopUInt256(out UInt256 dataOffset) ||
            !stack.PopUInt256(out UInt256 dataLength))
        {
            goto StackUnderflow;
        }

        UInt256 transferValue;
        UInt256 callValue;
        // 2. Determine transfer values based on call type.
        if (typeof(TOpEofCall) == typeof(OpEofStaticCall))
        {
            transferValue = UInt256.Zero;
            callValue = UInt256.Zero;
        }
        else if (typeof(TOpEofCall) == typeof(OpEofDelegateCall))
        {
            transferValue = UInt256.Zero;
            callValue = callFrame.Value;
        }
        else if (stack.PopUInt256(out transferValue))
        {
            callValue = transferValue;
        }
        else
        {
            goto StackUnderflow;
        }

        // 3. For non-static calls, ensure that a non-zero transfer value is not used in a static context.
        if (callFrame.IsStatic && !transferValue.IsZero)
            goto StaticCallViolation;
        // 4. Charge additional gas if a value is transferred in a standard call.
        if (typeof(TOpEofCall) == typeof(OpEofCall) && !transferValue.IsZero && !TGasPolicy.UpdateGas(ref gas, GasCostOf.CallValue))
            goto OutOfGas;

        // 5. Validate that the targetBytes represent a proper 20-byte address (high 12 bytes must be zero).
        if (!targetBytes[0..12].IsZero())
            goto AddressOutOfRange;

        Address caller = typeof(TOpEofCall) == typeof(OpEofDelegateCall) ? callFrame.Caller : callFrame.ExecutingAccount;
        Address codeSource = new(targetBytes[12..]);
        // For delegate calls, the target remains the executing account.
        Address target = typeof(TOpEofCall) == typeof(OpEofDelegateCall)
            ? callFrame.ExecutingAccount
            : codeSource;

        // 6. Update memory cost for the call data.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in dataOffset, in dataLength, callFrame))
            goto OutOfGas;
        // 7. Account access gas: ensure target is warm or charge extra gas for cold access.
        bool _ = vm.TxExecutionContext.CodeInfoRepository
            .TryGetDelegation(codeSource, vm.Spec, out CodeInfo targetCodeInfo, out Address delegated);
        if (!TGasPolicy.ConsumeAccountAccessGasWithDelegation(ref gas, vm.Spec, vm.TrackingState,
                vm.TxTracer.IsTracingAccess, codeSource, delegated)) goto OutOfGas;

        // 8. If the target does not exist or is considered a "dead" account when value is transferred,
        // charge for account creation.
        if ((!spec.ClearEmptyAccountWhenTouched && !state.AccountExists(codeSource))
            || (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && state.IsDeadAccount(codeSource)))
        {
            if (!TGasPolicy.UpdateGas(ref gas, GasCostOf.NewAccount))
                goto OutOfGas;
        }

        // 9. Compute the gas available to the callee after reserving a minimum.
        long gasAvailable = TGasPolicy.GetRemainingGas(in gas);
        long callGas = gasAvailable - Math.Max(gasAvailable / 64, MIN_RETAINED_GAS);

        // 10. Check that the call gas is sufficient, the caller has enough balance, and the call depth is within limits.
        if (callGas < GasCostOf.CallStipend ||
            (!transferValue.IsZero && state.GetBalance(callFrame.ExecutingAccount) < transferValue) ||
            vm.CallDepth >= MaxCallDepth)
        {
            vm.ReturnData = null;
            vm.ReturnDataBuffer = Array.Empty<byte>();
            EvmExceptionType result = stack.PushOne<TTracingInst>();
            if (result != EvmExceptionType.None)
            {
                return new(programCounter, result);
            }

            // If tracing is active, record additional details regarding the failure.
            ITxTracer txTracer = vm.TxTracer;
            if (TTracingInst.IsActive)
            {
                ReadOnlyMemory<byte> memoryTrace = callFrame.Memory.Inspect(in dataOffset, 32);
                txTracer.ReportMemoryChange(dataOffset, memoryTrace.Span);
                txTracer.ReportOperationRemainingGas(gasAvailable);
                txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
                txTracer.ReportGasUpdateForVmTrace(callGas, gasAvailable);
            }

            return new(programCounter, EvmExceptionType.None);
        }


        // For delegate calls, calling a non-EOF (legacy) target is disallowed.
        if (typeof(TOpEofCall) == typeof(OpEofDelegateCall)
            && targetCodeInfo.Version == 0)
        {
            vm.ReturnData = null;
            vm.ReturnDataBuffer = Array.Empty<byte>();
            return new(programCounter, stack.PushOne<TTracingInst>());
        }

        // 12. Deduct gas for the call and prepare the call data.
        if (!TGasPolicy.UpdateGas(ref gas, callGas) ||
            !callFrame.Memory.TryLoad(in dataOffset, dataLength, out ReadOnlyMemory<byte> callData))
        {
            goto OutOfGas;
        }

        // Snapshot the state before the call.
        Snapshot snapshot = state.TakeSnapshot();
        // Deduct the transferred value from the caller.
        state.SubtractFromBalance(caller, transferValue, spec);

        vm.ReturnData = CallFrame<TGasPolicy>.Rent(
            gas: TGasPolicy.FromLong(callGas),
            outputDestination: 0,
            outputLength: 0,
            executionType: TOpEofCall.ExecutionType,
            isStatic: TOpEofCall.IsStatic || callFrame.IsStatic,
            isCreateOnPreExistingAccount: false,
            codeInfo: targetCodeInfo,
            executingAccount: target,
            caller: caller,
            codeSource: codeSource,
            value: in callValue,
            inputData: in callData,
            stateForAccessLists: in callFrame.AccessTracker,
            trackingState: vm.TrackingState,
            snapshot: in snapshot);

        return new(programCounter, EvmExceptionType.Return);
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    StaticCallViolation:
        return new(programCounter, EvmExceptionType.StaticCallViolation);
    AddressOutOfRange:
        return new(programCounter, EvmExceptionType.AddressOutOfRange);
    }
}
