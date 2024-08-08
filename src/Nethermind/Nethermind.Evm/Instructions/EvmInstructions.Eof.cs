// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EOF;
using Nethermind.State;

namespace Nethermind.Evm;
using Int256;

using static Nethermind.Evm.VirtualMachine;

internal sealed partial class EvmInstructions
{
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnDataSize(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (!vm.Spec.ReturnDataOpcodesEnabled) return EvmExceptionType.BadInstruction;

        gasAvailable -= GasCostOf.Base;

        UInt256 result = (UInt256)vm.ReturnDataBuffer.Length;
        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnDataCopy(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (!vm.Spec.ReturnDataOpcodesEnabled) return EvmExceptionType.BadInstruction;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 c)) return EvmExceptionType.StackUnderflow;
        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in c);

        ReadOnlyMemory<byte> returnDataBuffer = vm.ReturnDataBuffer;
        if (vm.State.Env.CodeInfo.Version == 0 && (UInt256.AddOverflow(c, b, out UInt256 result) || result > returnDataBuffer.Length))
        {
            return EvmExceptionType.AccessViolation;
        }

        if (!c.IsZero)
        {
            if (!UpdateMemoryCost(vm.State, ref gasAvailable, in a, c)) return EvmExceptionType.OutOfGas;
            ZeroPaddedSpan slice = returnDataBuffer.Span.SliceWithZeroPadding(b, (int)c);
            vm.State.Memory.Save(in a, in slice);
            if (vm.TxTracer.IsTracingInstructions)
            {
                vm.TxTracer.ReportMemoryChange((long)a, in slice);
            }
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataLoad(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.DataLoad, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        stack.PopUInt256(out var a);
        ZeroPaddedSpan zpbytes = codeInfo.DataSection.SliceWithZeroPadding(a, 32);
        stack.PushBytes(zpbytes);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataLoadN(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.DataLoadN, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        var offset = codeInfo.CodeSection.Span.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthUInt16();
        ZeroPaddedSpan zpbytes = codeInfo.DataSection.SliceWithZeroPadding(offset, 32);
        stack.PushBytes(zpbytes);

        programCounter += EvmObjectFormat.TWO_BYTE_LENGTH;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataSize(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.DataSize, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        stack.PushUInt32(codeInfo.DataSection.Length);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDataCopy(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        stack.PopUInt256(out UInt256 memOffset);
        stack.PopUInt256(out UInt256 offset);
        stack.PopUInt256(out UInt256 size);

        if (!UpdateGas(GasCostOf.DataCopy + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in size), ref gasAvailable))
            return EvmExceptionType.OutOfGas;

        if (!size.IsZero)
        {
            if (!UpdateMemoryCost(vm.State, ref gasAvailable, in memOffset, size))
                return EvmExceptionType.OutOfGas;
            ZeroPaddedSpan dataSectionSlice = codeInfo.DataSection.SliceWithZeroPadding(offset, (int)size);
            vm.State.Memory.Save(in memOffset, dataSectionSlice);
            if (vm.TxTracer.IsTracingInstructions)
            {
                vm.TxTracer.ReportMemoryChange((long)memOffset, dataSectionSlice);
            }
        }

        stack.PushUInt32(codeInfo.DataSection.Length);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionRelativeJump(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.RJump, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        short offset = codeInfo.CodeSection.Span.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthInt16();
        programCounter += EvmObjectFormat.TWO_BYTE_LENGTH + offset;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionRelativeJumpIf(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.RJumpi, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        Span<byte> condition = stack.PopWord256();
        short offset = codeInfo.CodeSection.Span.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthInt16();
        if (!condition.IsZero())
        {
            programCounter += offset;
        }
        programCounter += EvmObjectFormat.TWO_BYTE_LENGTH;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionJumpTable(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.RJumpv, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        stack.PopUInt256(out var a);
        var codeSection = codeInfo.CodeSection.Span;

        var count = codeSection[programCounter] + 1;
        var immediates = (ushort)(count * EvmObjectFormat.TWO_BYTE_LENGTH + EvmObjectFormat.ONE_BYTE_LENGTH);
        if (a < count)
        {
            int case_v = programCounter + EvmObjectFormat.ONE_BYTE_LENGTH + (int)a * EvmObjectFormat.TWO_BYTE_LENGTH;
            int offset = codeSection.Slice(case_v, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthInt16();
            programCounter += offset;
        }
        programCounter += immediates;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCallFunction(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.Callf, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        var codeSection = codeInfo.CodeSection.Span;
        var index = (int)codeSection.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthUInt16();
        (int inputCount, _, int maxStackHeight) = codeInfo.GetSectionMetadata(index);

        if (EvmObjectFormat.Eof1.MAX_STACK_HEIGHT - maxStackHeight + inputCount < stack.Head)
        {
            return EvmExceptionType.StackOverflow;
        }

        if (vm.State.ReturnStackHead == EvmObjectFormat.Eof1.RETURN_STACK_MAX_HEIGHT)
            return EvmExceptionType.InvalidSubroutineEntry;

        vm.State.ReturnStack[vm.State.ReturnStackHead++] = new EvmState.ReturnState
        {
            Index = vm.SectionIndex,
            Height = stack.Head - inputCount,
            Offset = programCounter + EvmObjectFormat.TWO_BYTE_LENGTH
        };

        vm.SectionIndex = index;
        programCounter = codeInfo.CodeSectionOffset(index).Start;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnFunction(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.Retf, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        (_, int outputCount, _) = codeInfo.GetSectionMetadata(vm.SectionIndex);

        var stackFrame = vm.State.ReturnStack[--vm.State.ReturnStackHead];
        vm.SectionIndex = stackFrame.Index;
        programCounter = stackFrame.Offset;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionJumpFunction(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.Jumpf, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        var index = (int)codeInfo.CodeSection.Span.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthUInt16();
        (int inputCount, _, int maxStackHeight) = codeInfo.GetSectionMetadata(index);

        if (EvmObjectFormat.Eof1.MAX_STACK_HEIGHT - maxStackHeight + inputCount < stack.Head)
        {
            return EvmExceptionType.StackOverflow;
        }
        vm.SectionIndex = index;
        programCounter = codeInfo.CodeSectionOffset(index).Start;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionDupN(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.Dupn, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        int imm = (int)codeInfo.CodeSection.Span[programCounter];
        stack.Dup(imm + 1);

        programCounter += 1;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSwapN(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.Swapn, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        int n = 1 + (int)codeInfo.CodeSection.Span[programCounter];
        if (!stack.Swap(n + 1)) return EvmExceptionType.StackUnderflow;

        programCounter += 1;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionExchange(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        var codeInfo = vm.State.Env.CodeInfo;
        if (!vm.Spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (!UpdateGas(GasCostOf.Swapn, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        var codeSection = codeInfo.CodeSection.Span;
        int n = 1 + (int)(codeSection[programCounter] >> 0x04);
        int m = 1 + (int)(codeSection[programCounter] & 0x0f);

        stack.Exchange(n + 1, m + n + 1);

        programCounter += 1;

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionEofCreate(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        vm.ReturnData = null;
        var spec = vm.Spec;
        var codeInfo = vm.State.Env.CodeInfo;
        if (!spec.IsEofEnabled || codeInfo.Version == 0)
            return EvmExceptionType.BadInstruction;

        if (vm.State.IsStatic) return EvmExceptionType.StaticCallViolation;

        ref readonly ExecutionEnvironment env = ref vm.State.Env;
        EofCodeInfo container = env.CodeInfo as EofCodeInfo;
        var currentContext = ExecutionType.EOFCREATE;

        // 1 - deduct TX_CREATE_COST gas
        if (!UpdateGas(GasCostOf.TxCreate, ref gasAvailable))
            return EvmExceptionType.OutOfGas;

        var codeSection = codeInfo.CodeSection.Span;
        // 2 - read immediate operand initcontainer_index, encoded as 8-bit unsigned value
        int initcontainerIndex = codeSection[programCounter++];

        // 3 - pop value, salt, input_offset, input_size from the operand stack
        // no stack checks becaue EOF guarantees no stack undeflows
        stack.PopUInt256(out UInt256 value);
        stack.PopWord256(out Span<byte> salt);
        stack.PopUInt256(out UInt256 dataOffset);
        stack.PopUInt256(out UInt256 dataSize);

        // 4 - perform (and charge for) memory expansion using [input_offset, input_size]
        if (!UpdateMemoryCost(vm.State, ref gasAvailable, in dataOffset, dataSize)) return EvmExceptionType.OutOfGas;

        // 5 - load initcode EOF subcontainer at initcontainer_index in the container from which EOFCREATE is executed
        // let initcontainer be that EOF container, and initcontainer_size its length in bytes declared in its parent container header
        ReadOnlySpan<byte> initContainer = container.ContainerSection.Span[(Range)container.ContainerSectionOffset(initcontainerIndex).Value];
        // Eip3860
        if (spec.IsEip3860Enabled)
        {
            //if (!UpdateGas(GasCostOf.InitCodeWord * numberOfWordInInitcode, ref gasAvailable))
            //    return (EvmExceptionType.OutOfGas, null);
            if (initContainer.Length > spec.MaxInitCodeSize) return EvmExceptionType.OutOfGas;
        }

        // 6 - deduct GAS_KECCAK256_WORD * ((initcontainer_size + 31) // 32) gas (hashing charge)
        long numberOfWordsInInitCode = EvmPooledMemory.Div32Ceiling((UInt256)initContainer.Length);
        long hashCost = GasCostOf.Sha3Word * numberOfWordsInInitCode;
        if (!UpdateGas(hashCost, ref gasAvailable))
            return EvmExceptionType.OutOfGas;

        var state = vm.WorldState;
        // 7 - check that current call depth is below STACK_DEPTH_LIMIT and that caller balance is enough to transfer value
        // in case of failure return 0 on the stack, caller’s nonce is not updated and gas for initcode execution is not consumed.
        UInt256 balance = state.GetBalance(env.ExecutingAccount);
        if (env.CallDepth >= MaxCallDepth || value > balance)
        {
            // TODO: need a test for this
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return EvmExceptionType.None;
        }

        // 8 - caller’s memory slice [input_offset:input_size] is used as calldata
        Span<byte> calldata = vm.State.Memory.LoadSpan(dataOffset, dataSize);

        // 9 - execute the container and deduct gas for execution. The 63/64th rule from EIP-150 applies.
        long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!UpdateGas(callGas, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        // 10 - increment sender account’s nonce
        UInt256 accountNonce = state.GetNonce(env.ExecutingAccount);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return EvmExceptionType.None;
        }
        state.IncrementNonce(env.ExecutingAccount);

        // 11 - calculate new_address as keccak256(0xff || sender || salt || keccak256(initcontainer))[12:]
        Address contractAddress = ContractAddress.From(env.ExecutingAccount, salt, initContainer);
        if (spec.UseHotAndColdStorage)
        {
            // EIP-2929 assumes that warm-up cost is included in the costs of CREATE and CREATE2
            vm.State.WarmUp(contractAddress);
        }


        // if (vm.TxTracer.IsTracingInstructions) EndInstructionTrace(gasAvailable, vm.State?.Memory.Size ?? 0);
        // todo: === below is a new call - refactor / move

        Snapshot snapshot = state.TakeSnapshot();

        bool accountExists = state.AccountExists(contractAddress);

        if (accountExists && contractAddress.IsNonZeroAccount(spec, vm.CodeInfoRepository, state))
        {
            /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
            //if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"Contract collision at {contractAddress}");
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return EvmExceptionType.None;
        }

        if (state.IsDeadAccount(contractAddress))
        {
            state.ClearStorage(contractAddress);
        }

        state.SubtractFromBalance(env.ExecutingAccount, value, spec);


        ICodeInfo codeinfo = CodeInfoFactory.CreateCodeInfo(initContainer.ToArray(), spec, EvmObjectFormat.ValidationStrategy.ExractHeader);

        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: env.ExecutingAccount,
            executingAccount: contractAddress,
            codeSource: null,
            codeInfo: codeinfo,
            inputData: calldata.ToArray(),
            transferValue: value,
            value: value
        );
        vm.ReturnData = new EvmState(
            callGas,
            callEnv,
            currentContext,
            false,
            snapshot,
            0L,
            0L,
            vm.State.IsStatic,
            vm.State,
            false,
            accountExists);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnContract(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        if (!vm.Spec.IsEofEnabled || !vm.State.ExecutionType.IsAnyCreateEof())
            return EvmExceptionType.BadInstruction;


        if (!UpdateGas(GasCostOf.ReturnContract, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        var spec = vm.Spec;
        var codeInfo = vm.State.Env.CodeInfo;

        byte sectionIdx = codeInfo.CodeSection.Span[programCounter++];
        ReadOnlyMemory<byte> deployCode = codeInfo.ContainerSection[(Range)codeInfo.ContainerSectionOffset(sectionIdx)];
        EofCodeInfo deploycodeInfo = (EofCodeInfo)CodeInfoFactory.CreateCodeInfo(deployCode, spec, EvmObjectFormat.ValidationStrategy.ExractHeader);

        stack.PopUInt256(out var a);
        stack.PopUInt256(out var b);
        ReadOnlyMemory<byte> auxData = ReadOnlyMemory<byte>.Empty;

        if (!UpdateMemoryCost(vm.State, ref gasAvailable, in a, b)) return EvmExceptionType.OutOfGas;

        int projectedNewSize = (int)b + deploycodeInfo.DataSection.Length;
        if (projectedNewSize < deploycodeInfo.Header.DataSection.Size || projectedNewSize > UInt16.MaxValue)
        {
            return EvmExceptionType.AccessViolation;
        }

        vm.ReturnDataBuffer = vm.State.Memory.Load(a, b);
        vm.ReturnData = deploycodeInfo;

        return EvmExceptionType.None;
    }
}
