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
using Nethermind.Evm.Tracing;
using Nethermind.State;
using static Nethermind.Evm.VirtualMachine;

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
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">Reference to the operand stack.</param>
    /// <param name="gasAvailable">Reference to the remaining gas counter.</param>
    /// <param name="programCounter">Reference to the current program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating the outcome.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnDataSize<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Deduct base gas cost for this instruction.
        gasAvailable -= GasCostOf.Base;

        // Push the length of the return data buffer (as a 32-bit unsigned integer) onto the stack.
        stack.PushUInt32<TTracingInst>((uint)vm.ReturnDataBuffer.Length);

        return EvmExceptionType.None;
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
    public static EvmExceptionType InstructionReturnDataCopy<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
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
        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in size, out bool outOfGas);
        if (outOfGas) goto OutOfGas;

        ReadOnlyMemory<byte> returnDataBuffer = vm.ReturnDataBuffer;
        // For legacy (non-EOF) code, ensure that the copy does not exceed the available return data.
        if (vm.EvmState.Env.CodeInfo.Version == 0 &&
            (UInt256.AddOverflow(size, sourceOffset, out UInt256 result) || result > returnDataBuffer.Length))
        {
            goto AccessViolation;
        }

        // Only perform the copy if size is non-zero.
        if (!size.IsZero)
        {
            // Update memory cost for expanding memory to accommodate the destination slice.
            if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in destOffset, size))
                return EvmExceptionType.OutOfGas;

            // Get the source slice; if the requested range exceeds the buffer length, it is zero-padded.
            ZeroPaddedSpan slice = returnDataBuffer.Span.SliceWithZeroPadding(sourceOffset, (int)size);
            vm.EvmState.Memory.Save(in destOffset, in slice);

            // Report the memory change if tracing is active.
            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportMemoryChange(destOffset, in slice);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    AccessViolation:
        return EvmExceptionType.AccessViolation;
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
    public static EvmExceptionType InstructionDataLoad<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        // Ensure the instruction is only valid for non-legacy (EOF) code.
        if (codeInfo.Version == 0)
            goto BadInstruction;

        // Deduct gas required for data loading.
        if (!UpdateGas(GasCostOf.DataLoad, ref gasAvailable))
            goto OutOfGas;

        // Pop the offset from the stack.
        stack.PopUInt256(out UInt256 offset);
        // Load 32 bytes from the data section at the given offset (with zero padding if necessary).
        ZeroPaddedSpan bytes = codeInfo.DataSection.SliceWithZeroPadding(offset, 32);
        stack.PushBytes<TTracingInst>(bytes);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Loads 32 bytes from the data section using an immediate two-byte offset embedded in the code.
    /// Advances the program counter accordingly.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataLoadN<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.DataLoadN, ref gasAvailable))
            goto OutOfGas;

        // Read a 16-bit immediate operand from the code.
        ushort offset = codeInfo.CodeSection.Span.Slice(programCounter, EofValidator.TWO_BYTE_LENGTH).ReadEthUInt16();
        // Load the 32-byte word from the data section at the immediate offset.
        ZeroPaddedSpan bytes = codeInfo.DataSection.SliceWithZeroPadding(offset, 32);
        stack.PushBytes<TTracingInst>(bytes);

        // Advance the program counter past the immediate operand.
        programCounter += EofValidator.TWO_BYTE_LENGTH;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Pushes the size of the code's data section onto the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataSize<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.DataSize, ref gasAvailable))
            goto OutOfGas;

        stack.PushUInt32<TTracingInst>((uint)codeInfo.DataSection.Length);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Copies a slice of bytes from the code's data section into the VM's memory.
    /// The source offset, destination memory offset, and number of bytes are specified on the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataCopy<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        // Pop destination memory offset, data section offset, and size.
        if (!stack.PopUInt256(out UInt256 memOffset) ||
            !stack.PopUInt256(out UInt256 offset) ||
            !stack.PopUInt256(out UInt256 size))
        {
            goto StackUnderflow;
        }

        // Calculate memory expansion gas cost and deduct overall gas for data copy.
        if (!UpdateGas(GasCostOf.DataCopy + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in size, out bool outOfGas), ref gasAvailable)
            || outOfGas)
        {
            goto OutOfGas;
        }

        if (!size.IsZero)
        {
            // Update memory cost for the destination region.
            if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in memOffset, size))
                goto OutOfGas;
            // Retrieve the slice from the data section with zero padding if necessary.
            ZeroPaddedSpan dataSectionSlice = codeInfo.DataSection.SliceWithZeroPadding(offset, (int)size);
            vm.EvmState.Memory.Save(in memOffset, dataSectionSlice);

            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportMemoryChange(memOffset, dataSectionSlice);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Performs a relative jump in the code.
    /// Reads a two-byte signed offset from the code section and adjusts the program counter accordingly.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionRelativeJump(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.RJump, ref gasAvailable))
            goto OutOfGas;

        // Read a signed 16-bit offset and adjust the program counter.
        short offset = codeInfo.CodeSection.Span.Slice(programCounter, EofValidator.TWO_BYTE_LENGTH).ReadEthInt16();
        programCounter += EofValidator.TWO_BYTE_LENGTH + offset;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Conditionally performs a relative jump based on the top-of-stack condition.
    /// Pops a condition value; if non-zero, jumps by the signed offset embedded in the code.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionRelativeJumpIf(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.RJumpi, ref gasAvailable))
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

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Implements a jump table dispatch.
    /// Uses the top-of-stack value as an index into a list of jump offsets, then jumps accordingly.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionJumpTable(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.RJumpv, ref gasAvailable))
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

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Performs a subroutine call within the code.
    /// Sets up the return state and verifies stack and call depth constraints before transferring control.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionCallFunction(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        ICodeInfo iCodeInfo = vm.EvmState.Env.CodeInfo;
        if (iCodeInfo.Version == 0)
            goto BadInstruction;

        EofCodeInfo codeInfo = (EofCodeInfo)iCodeInfo;

        if (!UpdateGas(GasCostOf.Callf, ref gasAvailable))
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
        if (vm.EvmState.ReturnStackHead == Eof1.RETURN_STACK_MAX_HEIGHT)
            goto InvalidSubroutineEntry;

        // Push current state onto the return stack.
        vm.EvmState.ReturnStack[vm.EvmState.ReturnStackHead++] = new EvmState.ReturnState
        {
            Index = vm.SectionIndex,
            Height = stack.Head - inputCount,
            Offset = programCounter + EofValidator.TWO_BYTE_LENGTH
        };

        // Set up the subroutine call.
        vm.SectionIndex = index;
        programCounter = codeInfo.CodeSectionOffset(index).Start;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    InvalidSubroutineEntry:
        return EvmExceptionType.InvalidSubroutineEntry;
    StackOverflow:
        return EvmExceptionType.StackOverflow;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Returns from a subroutine call by restoring the state from the return stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnFunction(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.Retf, ref gasAvailable))
            goto OutOfGas;

        // Pop the return state from the return stack.
        EvmState.ReturnState stackFrame = vm.EvmState.ReturnStack[--vm.EvmState.ReturnStackHead];
        vm.SectionIndex = stackFrame.Index;
        programCounter = stackFrame.Offset;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Performs an unconditional jump to a subroutine using a section index read from the code.
    /// Verifies that the target section does not cause a stack overflow.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionJumpFunction(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        ICodeInfo iCodeInfo = vm.EvmState.Env.CodeInfo;
        if (iCodeInfo.Version == 0)
            goto BadInstruction;

        EofCodeInfo codeInfo = (EofCodeInfo)iCodeInfo;

        if (!UpdateGas(GasCostOf.Jumpf, ref gasAvailable))
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

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackOverflow:
        return EvmExceptionType.StackOverflow;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Duplicates a stack item based on an immediate operand.
    /// The immediate value (n) specifies that the (n+1)th element from the top is duplicated.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionDupN<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.Dupn, ref gasAvailable))
            goto OutOfGas;

        // Read the immediate operand.
        int imm = codeInfo.CodeSection.Span[programCounter];
        // Duplicate the (imm+1)th stack element.
        stack.Dup<TTracingInst>(imm + 1);

        programCounter += 1;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Swaps two stack items. The immediate operand specifies the swap distance.
    /// Swaps the top-of-stack with the (n+1)th element.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSwapN<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.Swapn, ref gasAvailable))
            goto OutOfGas;

        // Immediate operand determines the swap index.
        int n = 1 + (int)codeInfo.CodeSection.Span[programCounter];
        if (!stack.Swap<TTracingInst>(n + 1))
            goto StackUnderflow;

        programCounter += 1;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Exchanges two stack items using a combined immediate operand.
    /// The high nibble and low nibble of the operand specify the two swap distances.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExchange<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (codeInfo.Version == 0)
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.Swapn, ref gasAvailable))
            goto OutOfGas;

        ReadOnlySpan<byte> codeSection = codeInfo.CodeSection.Span;
        // Extract two 4-bit values from the immediate operand.
        int n = 1 + (int)(codeSection[programCounter] >> 0x04);
        int m = 1 + (int)(codeSection[programCounter] & 0x0f);

        // Exchange the elements at the calculated positions.
        stack.Exchange<TTracingInst>(n + 1, m + n + 1);

        programCounter += 1;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
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
    public static EvmExceptionType InstructionEofCreate<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        Metrics.IncrementCreates();
        vm.ReturnData = null;

        IReleaseSpec spec = vm.Spec;
        ref readonly ExecutionEnvironment env = ref vm.EvmState.Env;
        if (env.CodeInfo.Version == 0)
            goto BadInstruction;

        if (vm.EvmState.IsStatic)
            goto StaticCallViolation;

        // Cast the current code info to EOF-specific container type.
        EofCodeInfo container = env.CodeInfo as EofCodeInfo;
        ExecutionType currentContext = ExecutionType.EOFCREATE;

        // 1. Deduct the creation gas cost.
        if (!UpdateGas(GasCostOf.TxCreate, ref gasAvailable))
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
        if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in dataOffset, dataSize))
            goto OutOfGas;

        // 5. Load the init code (EOF subContainer) from the container using the given index.
        ReadOnlySpan<byte> initContainer = container.ContainerSection.Span[(Range)container.ContainerSectionOffset(initContainerIndex).Value];
        // EIP-3860: Check that the init code size does not exceed the maximum allowed.
        if (spec.IsEip3860Enabled)
        {
            if (initContainer.Length > spec.MaxInitCodeSize)
                goto OutOfGas;
        }

        // 6. Deduct gas for keccak256 hashing of the init code.
        long numberOfWordsInInitCode = EvmPooledMemory.Div32Ceiling((UInt256)initContainer.Length, out bool outOfGas);
        long hashCost = GasCostOf.Sha3Word * numberOfWordsInInitCode;
        if (outOfGas || !UpdateGas(hashCost, ref gasAvailable))
            goto OutOfGas;

        IWorldState state = vm.WorldState;
        // 7. Check call depth and caller's balance before proceeding with creation.
        UInt256 balance = state.GetBalance(env.ExecutingAccount);
        if (env.CallDepth >= MaxCallDepth || value > balance)
        {
            // In case of failure, do not consume additional gas.
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();
            return EvmExceptionType.None;
        }

        // 8. Prepare the callData from the caller’s memory slice.
        Span<byte> callData = vm.EvmState.Memory.LoadSpan(dataOffset, dataSize);

        // 9. Determine gas available for the new contract execution, applying the 63/64 rule if enabled.
        long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!UpdateGas(callGas, ref gasAvailable))
            goto OutOfGas;

        // 10. Increment the nonce of the sender account.
        UInt256 accountNonce = state.GetNonce(env.ExecutingAccount);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();
            return EvmExceptionType.None;
        }
        state.IncrementNonce(env.ExecutingAccount);

        // 11. Calculate the new contract address.
        Address contractAddress = ContractAddress.From(env.ExecutingAccount, salt, initContainer);
        if (spec.UseHotAndColdStorage)
        {
            // Warm up the target address for subsequent storage accesses.
            vm.EvmState.AccessTracker.WarmUp(contractAddress);
        }

        if (TTracingInst.IsActive)
            vm.EndInstructionTrace(gasAvailable);

        // Take a snapshot before modifying state for the new contract.
        Snapshot snapshot = state.TakeSnapshot();

        bool accountExists = state.AccountExists(contractAddress);

        // If the account already exists and is non-zero, then the creation fails.
        if (accountExists && contractAddress.IsNonZeroAccount(spec, vm.CodeInfoRepository, state))
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();
            return EvmExceptionType.None;
        }

        // If the account is marked as dead, clear its storage.
        if (state.IsDeadAccount(contractAddress))
        {
            state.ClearStorage(contractAddress);
        }

        // Deduct the transferred value from the caller's balance.
        state.SubtractFromBalance(env.ExecutingAccount, value, spec);

        // Create new code info for the init code.
        ICodeInfo codeInfo = CodeInfoFactory.CreateCodeInfo(initContainer.ToArray(), spec, ValidationStrategy.ExtractHeader);

        // Set up the execution environment for the new contract.
        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: env.ExecutingAccount,
            executingAccount: contractAddress,
            codeSource: null,
            codeInfo: codeInfo,
            inputData: callData.ToArray(),
            transferValue: value,
            value: value
        );
        vm.ReturnData = EvmState.RentFrame(
            callGas,
            outputDestination: 0,
            outputLength: 0,
            executionType: currentContext,
            isStatic: vm.EvmState.IsStatic,
            isCreateOnPreExistingAccount: accountExists,
            in snapshot,
            env: in callEnv,
            in vm.EvmState.AccessTracker
        );

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Returns the contract creation result.
    /// Extracts the deployment code from a specified container section and prepares the return data.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnCode(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // This instruction is only valid in create contexts.
        if (!vm.EvmState.ExecutionType.IsAnyCreateEof())
            goto BadInstruction;

        if (!UpdateGas(GasCostOf.ReturnCode, ref gasAvailable))
            goto OutOfGas;

        IReleaseSpec spec = vm.Spec;
        EofCodeInfo codeInfo = (EofCodeInfo)vm.EvmState.Env.CodeInfo;

        // Read the container section index from the code.
        byte sectionIdx = codeInfo.CodeSection.Span[programCounter++];
        // Retrieve the deployment code using the container section offset.
        ReadOnlyMemory<byte> deployCode = codeInfo.ContainerSection[(Range)codeInfo.ContainerSectionOffset(sectionIdx)];
        EofCodeInfo deployCodeInfo = (EofCodeInfo)CodeInfoFactory.CreateCodeInfo(deployCode, spec, ValidationStrategy.ExtractHeader);

        // Pop memory offset and size for the return data.
        stack.PopUInt256(out UInt256 a);
        stack.PopUInt256(out UInt256 b);

        if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in a, b))
            goto OutOfGas;

        int projectedNewSize = (int)b + deployCodeInfo.DataSection.Length;
        // Ensure the projected size is within valid bounds.
        if (projectedNewSize < deployCodeInfo.EofContainer.Header.DataSection.Size || projectedNewSize > UInt16.MaxValue)
        {
            return EvmExceptionType.AccessViolation;
        }

        // Load the memory slice as the return data buffer.
        vm.ReturnDataBuffer = vm.EvmState.Memory.Load(a, b);
        vm.ReturnData = deployCodeInfo;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Loads 32 bytes from the return data buffer using an offset from the stack,
    /// then pushes the retrieved value onto the stack.
    /// This instruction is only valid when EOF is enabled.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnDataLoad<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;
        ICodeInfo codeInfo = vm.EvmState.Env.CodeInfo;
        if (!spec.IsEofEnabled || codeInfo.Version == 0)
            goto BadInstruction;

        gasAvailable -= GasCostOf.VeryLow;

        if (!stack.PopUInt256(out UInt256 offset))
            goto StackUnderflow;

        ZeroPaddedSpan slice = vm.ReturnDataBuffer.Span.SliceWithZeroPadding(offset, 32);
        stack.PushBytes<TTracingInst>(slice);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
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
    public static EvmExceptionType InstructionEofCall<TOpEofCall, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEofCall : struct, IOpEofCall
        where TTracingInst : struct, IFlag
    {
        Metrics.IncrementCalls();

        const int MIN_RETAINED_GAS = 5000;

        IReleaseSpec spec = vm.Spec;
        vm.ReturnData = null;
        ref readonly ExecutionEnvironment env = ref vm.EvmState.Env;
        IWorldState state = vm.WorldState;

        // This instruction is only available for EOF-enabled contracts.
        if (env.CodeInfo.Version == 0)
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
            callValue = env.Value;
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
        if (vm.EvmState.IsStatic && !transferValue.IsZero)
            goto StaticCallViolation;
        // 4. Charge additional gas if a value is transferred in a standard call.
        if (typeof(TOpEofCall) == typeof(OpEofCall) && !transferValue.IsZero && !UpdateGas(GasCostOf.CallValue, ref gasAvailable))
            goto OutOfGas;

        // 5. Validate that the targetBytes represent a proper 20-byte address (high 12 bytes must be zero).
        if (!targetBytes[0..12].IsZero())
            goto AddressOutOfRange;

        Address caller = typeof(TOpEofCall) == typeof(OpEofDelegateCall) ? env.Caller : env.ExecutingAccount;
        Address codeSource = new(targetBytes[12..].ToArray());
        // For delegate calls, the target remains the executing account.
        Address target = typeof(TOpEofCall) == typeof(OpEofDelegateCall)
            ? env.ExecutingAccount
            : codeSource;

        // 6. Update memory cost for the call data.
        if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in dataOffset, in dataLength))
            goto OutOfGas;
        // 7. Account access gas: ensure target is warm or charge extra gas for cold access.
        if (!ChargeAccountAccessGasWithDelegation(ref gasAvailable, vm, codeSource))
            goto OutOfGas;

        // 8. If the target does not exist or is considered a "dead" account when value is transferred,
        // charge for account creation.
        if ((!spec.ClearEmptyAccountWhenTouched && !state.AccountExists(codeSource))
            || (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && state.IsDeadAccount(codeSource)))
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                goto OutOfGas;
        }

        // 9. Compute the gas available to the callee after reserving a minimum.
        long callGas = gasAvailable - Math.Max(gasAvailable / 64, MIN_RETAINED_GAS);

        // 10. Check that the call gas is sufficient, the caller has enough balance, and the call depth is within limits.
        if (callGas < GasCostOf.CallStipend ||
            (!transferValue.IsZero && state.GetBalance(env.ExecutingAccount) < transferValue) ||
            env.CallDepth >= MaxCallDepth)
        {
            vm.ReturnData = null;
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushOne<TTracingInst>();

            // If tracing is active, record additional details regarding the failure.
            ITxTracer txTracer = vm.TxTracer;
            if (TTracingInst.IsActive)
            {
                ReadOnlyMemory<byte> memoryTrace = vm.EvmState.Memory.Inspect(in dataOffset, 32);
                txTracer.ReportMemoryChange(dataOffset, memoryTrace.Span);
                txTracer.ReportOperationRemainingGas(gasAvailable);
                txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
                txTracer.ReportGasUpdateForVmTrace(callGas, gasAvailable);
            }

            return EvmExceptionType.None;
        }

        // 11. Retrieve and prepare the target code for execution.
        ICodeInfo targetCodeInfo = vm.CodeInfoRepository.GetCachedCodeInfo(state, codeSource, spec);

        // For delegate calls, calling a non-EOF (legacy) target is disallowed.
        if (typeof(TOpEofCall) == typeof(OpEofDelegateCall)
            && targetCodeInfo.Version == 0)
        {
            vm.ReturnData = null;
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushOne<TTracingInst>();
            return EvmExceptionType.None;
        }

        // 12. Deduct gas for the call and prepare the call data.
        if (!UpdateGas(callGas, ref gasAvailable))
            goto OutOfGas;

        ReadOnlyMemory<byte> callData = vm.EvmState.Memory.Load(in dataOffset, dataLength);

        // Snapshot the state before the call.
        Snapshot snapshot = state.TakeSnapshot();
        // Deduct the transferred value from the caller.
        state.SubtractFromBalance(caller, transferValue, spec);

        // Set up the new execution environment for the call.
        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: caller,
            codeSource: codeSource,
            executingAccount: target,
            transferValue: transferValue,
            value: callValue,
            inputData: callData,
            codeInfo: targetCodeInfo
        );
        vm.ReturnData = EvmState.RentFrame(
            callGas,
            outputDestination: 0,
            outputLength: 0,
            TOpEofCall.ExecutionType,
            isStatic: TOpEofCall.IsStatic || vm.EvmState.IsStatic,
            isCreateOnPreExistingAccount: false,
            in snapshot,
            env: in callEnv,
            in vm.EvmState.AccessTracker
        );

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    AddressOutOfRange:
        return EvmExceptionType.AddressOutOfRange;
    }
}
