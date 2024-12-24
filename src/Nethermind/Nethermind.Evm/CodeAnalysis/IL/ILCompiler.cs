// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Config;
using Nethermind.Evm.IL;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.State;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Intrinsics;
using System.Security;
using static Nethermind.Evm.IL.EmitExtensions;
using Label = Sigil.Label;


namespace Nethermind.Evm.CodeAnalysis.IL;
internal static class ILCompiler
{
    public static class PartialAOT
    {
        public delegate void ExecuteSegment(
        ulong chainId,

        ref EvmState vmstate,
        in ExecutionEnvironment env,
        in TxExecutionContext txCtx,
        in BlockExecutionContext blkCtx,
        ref EvmPooledMemory memory,

        ref Word stackHeadRef,
        ref int stackHeadIdx,

        IBlockhashProvider blockhashProvider,
        IWorldState worldState,
        ICodeInfoRepository codeInfoRepository,
        IReleaseSpec spec,
        ITxTracer trace,
        ref int programCounter,
        ref long gasAvailable,

        ReadOnlyMemory<byte> machineCode,
        in ReadOnlyMemory<byte> calldata,

        byte[][] immediatesData,

        ref ILChunkExecutionState result);

        private const int CHAINID_INDEX = 0;
        private const int REF_VMSTATE_INDEX = 1;
        private const int REF_ENV_INDEX = 2;
        private const int REF_TXCTX_INDEX = 3;
        private const int REF_BLKCTX_INDEX = 4;
        private const int REF_MEMORY_INDEX = 5;
        private const int REF_CURR_STACK_HEAD_INDEX = 6;
        private const int STACK_HEAD_INDEX = 7;
        private const int BLOCKHASH_PROVIDER_INDEX = 8;
        private const int WORLD_STATE_INDEX = 9;
        private const int CODE_INFO_REPOSITORY_INDEX = 10;
        private const int SPEC_INDEX = 11;
        private const int TXTRACER_INDEX = 12;
        private const int PROGRAM_COUNTER_INDEX = 13;
        private const int GAS_AVAILABLE_INDEX = 14;
        private const int MACHINE_CODE_INDEX = 15;
        private const int REF_CALLDATA_INDEX = 16;
        private const int IMMEDIATES_DATA_INDEX = 17;
        private const int REF_RESULT_INDEX = 18;

        public static PrecompiledChunk CompileSegment(string segmentName, ContractMetadata metadata, int segmentIndex, IVMConfig config, out int[] localJumpdests)
        {
            // code is optimistic assumes stackHeadRef underflow and stackHeadRef overflow to not occure (WE NEED EOF FOR THIS)
            // Note(Ayman) : remove dependency on ILEVMSTATE and move out all arguments needed to function signature
            Emit<ExecuteSegment> method = Emit<ExecuteSegment>.NewDynamicMethod(segmentName, doVerify: true, strictBranchVerification: true);

            localJumpdests = EmitSegmentBody(method, metadata, segmentIndex, config.BakeInTracingInPartialAotMode);
            ExecuteSegment dynEmitedDelegate = method.CreateDelegate();

            return new PrecompiledChunk
            {
                PrecompiledSegment = dynEmitedDelegate,
                Data = metadata.EmbeddedData,
            };
        }

        private static int[] EmitSegmentBody(Emit<ExecuteSegment> method, ContractMetadata contractMetadata, int segmentIndex, bool bakeInTracerCalls)
        {
            var segmentMetadata = contractMetadata.Segments[segmentIndex];

            using Local jmpDestination = method.DeclareLocal(typeof(int));

            using Local address = method.DeclareLocal(typeof(Address));

            using Local hash256 = method.DeclareLocal(typeof(Hash256));

            using Local wordRef256A = method.DeclareLocal(typeof(Word).MakeByRefType());
            using Local wordRef256B = method.DeclareLocal(typeof(Word).MakeByRefType());
            using Local wordRef256C = method.DeclareLocal(typeof(Word).MakeByRefType());

            using Local uint256A = method.DeclareLocal(typeof(UInt256));
            using Local uint256B = method.DeclareLocal(typeof(UInt256));
            using Local uint256C = method.DeclareLocal(typeof(UInt256));
            using Local uint256R = method.DeclareLocal(typeof(UInt256));

            using Local localReadOnlyMemory = method.DeclareLocal(typeof(ReadOnlyMemory<byte>));
            using Local localReadonOnlySpan = method.DeclareLocal(typeof(ReadOnlySpan<byte>));
            using Local localZeroPaddedSpan = method.DeclareLocal(typeof(ZeroPaddedSpan));
            using Local localSpan = method.DeclareLocal(typeof(Span<byte>));
            using Local localMemory = method.DeclareLocal(typeof(Memory<byte>));
            using Local localArray = method.DeclareLocal(typeof(byte[]));
            using Local uint64A = method.DeclareLocal(typeof(ulong));

            using Local uint32A = method.DeclareLocal(typeof(uint));
            using Local uint32B = method.DeclareLocal(typeof(uint));
            using Local int64A = method.DeclareLocal(typeof(long));
            using Local int64B = method.DeclareLocal(typeof(long));
            using Local byte8A = method.DeclareLocal(typeof(byte));
            using Local lbool = method.DeclareLocal(typeof(bool));
            using Local byte8B = method.DeclareLocal(typeof(byte));

            using Local storageCell = method.DeclareLocal(typeof(StorageCell));

            using Local gasAvailable = method.DeclareLocal(typeof(long));
            using Local programCounter = method.DeclareLocal(typeof(int));

            using Local stackHeadRef = method.DeclareLocal(typeof(Word).MakeByRefType());
            using Local stackHeadIdx = method.DeclareLocal(typeof(int));

            using Local header = method.DeclareLocal(typeof(BlockHeader));

            Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();

            Label exit = method.DefineLabel(); // the label just before return
            Label jumpTable = method.DefineLabel(); // jump table
            Label isContinuation = method.DefineLabel(); // jump table
            Label ret = method.DefineLabel();

            method.LoadRefArgument(STACK_HEAD_INDEX, typeof(int));
            method.StoreLocal(stackHeadIdx);

            method.LoadArgument(REF_CURR_STACK_HEAD_INDEX);
            method.StoreLocal(stackHeadRef);

            // set gas to local
            method.LoadRefArgument(GAS_AVAILABLE_INDEX, typeof(long));
            method.StoreLocal(gasAvailable);

            // set pc to local
            method.LoadRefArgument(PROGRAM_COUNTER_INDEX, typeof(int));
            method.StoreLocal(programCounter);

            // if last ilvmstate was a jump
            method.LoadArgument(REF_RESULT_INDEX);
            method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ShouldJump)));
            method.BranchIfTrue(isContinuation);

            Dictionary<int, Label> jumpDestinations = new();

            // Note : Combine all the static analysis into one method
            if (bakeInTracerCalls)
            {
                segmentMetadata.StackOffsets.Fill(0);
            }

            SubSegmentMetadata currentSegment = default;


            // Idea(Ayman) : implement every opcode as a method, and then inline the IL of the method in the main method
            for (int i = 0; i < segmentMetadata.Segment.Length; i++)
            {
                OpcodeInfo op = segmentMetadata.Segment[i];

                if (!bakeInTracerCalls && segmentMetadata.SubSegments.ContainsKey(i))
                {
                    currentSegment = segmentMetadata.SubSegments[i];
                }
                // if tracing mode is off, 
                if (!bakeInTracerCalls)
                {
                    // we skip compiling unreachable code
                    if (!currentSegment.IsReachable)
                    {
                        i = currentSegment.End;
                        continue;
                    }

                    // and we emit failure for failing jumpless segment at start 
                    if (currentSegment.IsFailing)
                    {
                        method.Branch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                        i = currentSegment.End;
                        continue;
                    }
                }

                if (op.Operation is Instruction.JUMPDEST)
                {
                    // mark the jump destination
                    method.MarkLabel(jumpDestinations[op.ProgramCounter] = method.DefineLabel());
                    method.LoadConstant(op.ProgramCounter);
                    method.StoreLocal(programCounter);
                }

                if (bakeInTracerCalls)
                {
                    EmitCallToStartInstructionTrace(method, gasAvailable, stackHeadIdx, op);
                }

                // check if opcode is activated in current spec, we skip this check for opcodes that are always enabled
                if (op.Operation.RequiresAvailabilityCheck())
                {
                    method.LoadArgument(SPEC_INDEX);
                    method.LoadConstant((byte)op.Operation);
                    method.Call(typeof(InstructionExtensions).GetMethod(nameof(InstructionExtensions.IsEnabled)));
                    method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                }

                // if tracing mode is off, we consume the static gas only at the start of segment
                if (!bakeInTracerCalls)
                {
                    if (currentSegment.StaticGasSubSegmentes.TryGetValue(i, out long gasCost) && gasCost > 0)
                    {
                        method.LoadLocal(gasAvailable);
                        method.LoadConstant(gasCost);
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));
                    }
                }
                else
                {
                    // otherwise we update the gas after each instruction
                    method.LoadLocal(gasAvailable);
                    method.LoadConstant(op.Metadata.GasCost);
                    method.Subtract();
                    method.Duplicate();
                    method.StoreLocal(gasAvailable);
                    method.LoadConstant((long)0);
                    method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));
                }

                // if tracing mode is off, we update the pc only at the end of segment and in jumps
                if (!bakeInTracerCalls)
                {
                    if (i == segmentMetadata.Segment.Length - 1 || op.IsTerminating)
                    {
                        method.LoadConstant(op.ProgramCounter + op.Metadata.AdditionalBytes);
                        method.StoreLocal(programCounter);
                    }
                }
                else
                {
                    // otherwise we update the pc after each instruction
                    method.LoadConstant(op.ProgramCounter + op.Metadata.AdditionalBytes);
                    method.StoreLocal(programCounter);
                }

                // if tracing is off, we check the stackHeadRef requirement of the full jumpless segment at once
                if (!bakeInTracerCalls)
                {
                    // we check if stackHeadRef underflow can occur
                    if (currentSegment.RequiredStack != 0)
                    {
                        method.LoadLocal(stackHeadIdx);
                        method.LoadConstant(currentSegment.RequiredStack);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackUnderflow));
                    }

                    // we check if stackHeadRef overflow can occur
                    if (currentSegment.MaxStack != 0)
                    {
                        method.LoadLocal(stackHeadIdx);
                        method.LoadConstant(currentSegment.MaxStack);
                        method.Add();
                        method.LoadConstant(EvmStack.MaxStackSize);
                        method.BranchIfGreaterOrEqual(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackOverflow));
                    }
                }
                else
                {
                    // otherwise we check the stackHeadRef requirement of each instruction
                    method.LoadLocal(stackHeadIdx);
                    method.LoadConstant(op.Metadata.StackBehaviorPop);
                    method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackUnderflow));

                    int delta = op.Metadata.StackBehaviorPush - op.Metadata.StackBehaviorPop;
                    method.LoadLocal(stackHeadIdx);
                    method.LoadConstant(delta);
                    method.Add();
                    method.LoadConstant(EvmStack.MaxStackSize);
                    method.BranchIfGreaterOrEqual(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackOverflow));
                }

                // else emit
                switch (op.Operation)
                {
                    case Instruction.JUMPDEST:
                        // we do nothing
                        break;
                    case Instruction.STOP:
                        {
                            method.LoadArgument(REF_RESULT_INDEX);
                            method.LoadConstant(true);
                            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldStop)));
                            method.FakeBranch(ret);
                        }
                        break;
                    case Instruction.CHAINID:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(CHAINID_INDEX);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.NOT:
                        {
                            MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
                            MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo notVector256Method = typeof(Vector256)
                                .GetMethod(nameof(Vector256.OnesComplement), BindingFlags.Public | BindingFlags.Static)!
                                .MakeGenericMethod(typeof(byte));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(refWordToRefByteMethod);
                            method.Duplicate();
                            method.Call(readVector256Method);
                            method.Call(notVector256Method);
                            method.Call(writeVector256Method);
                        }
                        break;
                    case Instruction.JUMP:
                        {
                            // we jump into the jump table
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);

                            if (bakeInTracerCalls)
                            {
                                UpdateStackHeadIdxAndPushRefOpcodeMode(method, stackHeadRef, stackHeadIdx, op);
                                EmitCallToEndInstructionTrace(method, gasAvailable);
                            }
                            else
                            {
                                UpdateStackHeadAndPushRerSegmentMode(method, stackHeadRef, stackHeadIdx, i, currentSegment);
                            }
                            method.FakeBranch(jumpTable);
                        }
                        break;
                    case Instruction.JUMPI:
                        {// consume the jump condition
                            Label noJump = method.DefineLabel();
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.EmitIsZeroCheck();
                            // if the jump condition is false, we do not jump
                            method.BranchIfTrue(noJump);

                            // we jump into the jump table

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);

                            if (bakeInTracerCalls)
                            {
                                UpdateStackHeadIdxAndPushRefOpcodeMode(method, stackHeadRef, stackHeadIdx, op);
                                EmitCallToEndInstructionTrace(method, gasAvailable);
                            }
                            else
                            {
                                UpdateStackHeadAndPushRerSegmentMode(method, stackHeadRef, stackHeadIdx, i, currentSegment);
                            }
                            method.Branch(jumpTable);

                            method.MarkLabel(noJump);
                        }
                        break;
                    case Instruction.PUSH0:
                        {
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                        }
                        break;
                    case Instruction.PUSH1:
                    case Instruction.PUSH2:
                    case Instruction.PUSH3:
                    case Instruction.PUSH4:
                    case Instruction.PUSH5:
                    case Instruction.PUSH6:
                    case Instruction.PUSH7:
                    case Instruction.PUSH8:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.SpecialPushOpcode(op, contractMetadata.EmbeddedData);
                        }
                        break;
                    case Instruction.PUSH9:
                    case Instruction.PUSH10:
                    case Instruction.PUSH11:
                    case Instruction.PUSH12:
                    case Instruction.PUSH13:
                    case Instruction.PUSH14:
                    case Instruction.PUSH15:
                    case Instruction.PUSH16:
                    case Instruction.PUSH17:
                    case Instruction.PUSH18:
                    case Instruction.PUSH19:
                    case Instruction.PUSH20:
                    case Instruction.PUSH21:
                    case Instruction.PUSH22:
                    case Instruction.PUSH23:
                    case Instruction.PUSH24:
                    case Instruction.PUSH25:
                    case Instruction.PUSH26:
                    case Instruction.PUSH27:
                    case Instruction.PUSH28:
                    case Instruction.PUSH29:
                    case Instruction.PUSH30:
                    case Instruction.PUSH31:
                    case Instruction.PUSH32:
                        {// we load the stackHeadRef
                            if (contractMetadata.EmbeddedData[op.Arguments.Value].IsZero())
                            {
                                method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            }
                            else
                            {
                                method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                                method.LoadArgument(IMMEDIATES_DATA_INDEX);
                                method.LoadConstant(op.Arguments.Value);
                                method.LoadElement<byte[]>();
                                method.Call(Word.SetArray);
                            }
                        }
                        break;
                    case Instruction.ADD:
                        {

                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            method.LoadLocal(wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Add();
                            method.StoreLocal(uint64A);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitBinaryUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.SUB:
                        {
                            Label pushNegItemB = method.DefineLabel();
                            Label pushItemA = method.DefineLabel();
                            // b - a a::b
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the stackHeadRef
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            method.LoadLocal(wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushItemA);

                            method.LoadLocal(wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushNegItemB);

                            method.LoadLocal(wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(wordRef256A);
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.LoadLocal(wordRef256B);
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.BranchIfLess(fallbackToUInt256Call);

                            method.LoadLocal(wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Subtract();
                            method.StoreLocal(uint64A);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushItemA);
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushNegItemB);
                            method.LoadLocal(wordRef256B);
                            method.Call(Word.ToNegative);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitBinaryUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.MUL:
                        {
                            Label push0Zero = method.DefineLabel();
                            Label pushItemA = method.DefineLabel();
                            Label pushItemB = method.DefineLabel();
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the stackHeadRef
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            method.LoadLocal(wordRef256A);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.LoadLocal(wordRef256B);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256B);

                            method.LoadLocal(wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(push0Zero);

                            method.LoadLocal(wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(endofOpcode);

                            method.LoadLocal(wordRef256A);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(endofOpcode);

                            method.LoadLocal(wordRef256B);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(pushItemA);

                            method.LoadLocal(wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Multiply();
                            method.StoreLocal(uint64A);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitBinaryUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B);
                            method.Branch(endofOpcode);

                            method.MarkLabel(push0Zero);
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushItemA);
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));

                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.MOD:
                        {
                            Label pushZeroLabel = method.DefineLabel();
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            method.LoadLocal(wordRef256B);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfTrue(pushZeroLabel);

                            method.LoadLocal(wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallBackToOldBehavior);
                            method.LoadLocal(wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.LoadLocal(wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Remainder();
                            method.StoreLocal(uint64A);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.SMOD:
                        {
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            // if b is 1 or 0 result is always 0
                            method.LoadLocal(wordRef256B);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Mod), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, uint256A, uint256B);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.DIV:
                        {
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label pushZeroLabel = method.DefineLabel();
                            Label pushALabel = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            // if a or b are 0 result is directly 0
                            method.LoadLocal(wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);
                            method.LoadLocal(wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);

                            // if b is 1 result is by default a
                            method.LoadLocal(wordRef256B);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(pushALabel);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushALabel);
                            method.LoadLocal(wordRef256B);
                            method.LoadLocal(wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B);

                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.SDIV:
                        {
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label pushZeroLabel = method.DefineLabel();
                            Label pushALabel = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            // if b is 0 or a is 0 then the result is 0
                            method.LoadLocal(wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);
                            method.LoadLocal(wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);

                            // if b is 1 in all cases the result is a
                            method.LoadLocal(wordRef256B);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(pushALabel);

                            // if b is -1 and a is 2^255 then the result is 2^255
                            method.LoadLocal(wordRef256B);
                            method.EmitIsMinusOneCheck();
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.LoadLocal(wordRef256A);
                            method.EmitIsP255Check();
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.Branch(endofOpcode);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushALabel);
                            method.LoadLocal(wordRef256B);
                            method.LoadLocal(wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Divide), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, uint256A, uint256B);

                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.ADDMOD:
                        {
                            Label push0Zero = method.DefineLabel();
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.StoreLocal(wordRef256C);

                            // if c is 1 or 0 result is 0
                            method.LoadLocal(wordRef256C);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitTrinaryUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.AddMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B, uint256C);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.MULMOD:
                        {
                            Label push0Zero = method.DefineLabel();
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the stackHeadRef
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.StoreLocal(wordRef256C);

                            // since (a * b) % c 
                            // if a or b are 0 then the result is 0
                            // if c is 0 or 1 then the result is 0
                            method.LoadLocal(wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(wordRef256C);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);

                            // since (a * b) % c == (a % c * b % c) % c
                            // if a or b are equal to c, then the result is 0
                            method.LoadLocal(wordRef256A);
                            method.LoadLocal(wordRef256C);
                            method.Call(Word.AreEqual);
                            method.BranchIfTrue(push0Zero);
                            method.LoadLocal(wordRef256B);
                            method.LoadLocal(wordRef256C);
                            method.Call(Word.AreEqual);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.MarkLabel(push0Zero);
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitTrinaryUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.MultiplyMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B, uint256C);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.SHL:
                        EmitShiftUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), isLeft: true, evmExceptionLabels, uint256A, uint256B);
                        break;
                    case Instruction.SHR:
                        EmitShiftUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), isLeft: false, evmExceptionLabels, uint256A, uint256B);
                        break;
                    case Instruction.SAR:
                        EmitShiftInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), evmExceptionLabels, uint256A, uint256B);
                        break;
                    case Instruction.AND:
                        EmitBitwiseUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseAnd), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                        break;
                    case Instruction.OR:
                        EmitBitwiseUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseOr), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                        break;
                    case Instruction.XOR:
                        EmitBitwiseUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Vector256).GetMethod(nameof(Vector256.Xor), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                        break;
                    case Instruction.EXP:
                        {
                            Label powerIsZero = method.DefineLabel();
                            Label baseIsOneOrZero = method.DefineLabel();
                            Label endOfExpImpl = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Duplicate();
                            method.Call(Word.LeadingZeroProp);
                            method.StoreLocal(uint64A);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256B);

                            method.LoadLocalAddress(uint256B);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(powerIsZero);

                            // load spec
                            method.LoadLocal(gasAvailable);
                            method.LoadArgument(SPEC_INDEX);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExpByteCost)));
                            method.LoadConstant((long)32);
                            method.LoadLocal(uint64A);
                            method.Subtract();
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadLocalAddress(uint256A);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZeroOrOne)).GetMethod!);
                            method.BranchIfTrue(baseIsOneOrZero);

                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256B);
                            method.LoadLocalAddress(uint256R);
                            method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(uint256R);
                            method.Call(Word.SetUInt256);

                            method.Branch(endOfExpImpl);

                            method.MarkLabel(powerIsZero);
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadConstant(1);
                            method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                            method.Branch(endOfExpImpl);

                            method.MarkLabel(baseIsOneOrZero);
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(uint256A);
                            method.Call(Word.SetUInt256);
                            method.Branch(endOfExpImpl);

                            method.MarkLabel(endOfExpImpl);
                        }
                        break;
                    case Instruction.LT:
                        {
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the stackHeadRef
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            method.LoadLocal(wordRef256A);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(wordRef256B);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.CompareLessThan();
                            method.StoreLocal(byte8B);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(byte8B);
                            method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitComparaisonUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, uint256A, uint256B);
                            method.MarkLabel(endofOpcode);
                        }

                        break;
                    case Instruction.GT:
                        {
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the stackHeadRef
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            method.LoadLocal(wordRef256A);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(wordRef256B);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.CompareGreaterThan();
                            method.StoreLocal(byte8B);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(byte8B);
                            method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitComparaisonUInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, uint256A, uint256B);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.SLT:
                        EmitComparaisonInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), false, evmExceptionLabels, uint256A, uint256B);
                        break;
                    case Instruction.SGT:
                        EmitComparaisonInt256Method(method, uint256R, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), true, evmExceptionLabels, uint256A, uint256B);
                        break;
                    case Instruction.EQ:
                        {
                            MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
                            MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo operationUnegenerified = typeof(Vector256).GetMethod(nameof(Vector256.EqualsAll), BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(byte));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(refWordToRefByteMethod);
                            method.Call(readVector256Method);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(refWordToRefByteMethod);
                            method.Call(readVector256Method);

                            method.Call(operationUnegenerified);
                            method.StoreLocal(lbool);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(lbool);
                            method.Convert<uint>();
                            method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.ISZERO:
                        {// we load the stackHeadRef
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Duplicate();
                            method.Duplicate();
                            method.EmitIsZeroCheck();
                            method.StoreLocal(lbool);
                            method.Call(Word.SetToZero);
                            method.LoadLocal(lbool);
                            method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.POP:
                        {
                            // idk what to put here ¯\_(ツ)_/¯
                        }
                        break;
                    case Instruction.DUP1:
                    case Instruction.DUP2:
                    case Instruction.DUP3:
                    case Instruction.DUP4:
                    case Instruction.DUP5:
                    case Instruction.DUP6:
                    case Instruction.DUP7:
                    case Instruction.DUP8:
                    case Instruction.DUP9:
                    case Instruction.DUP10:
                    case Instruction.DUP11:
                    case Instruction.DUP12:
                    case Instruction.DUP13:
                    case Instruction.DUP14:
                    case Instruction.DUP15:
                    case Instruction.DUP16:
                        {
                            int count = (int)op.Operation - (int)Instruction.DUP1 + 1;
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], count);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                        }
                        break;
                    case Instruction.SWAP1:
                    case Instruction.SWAP2:
                    case Instruction.SWAP3:
                    case Instruction.SWAP4:
                    case Instruction.SWAP5:
                    case Instruction.SWAP6:
                    case Instruction.SWAP7:
                    case Instruction.SWAP8:
                    case Instruction.SWAP9:
                    case Instruction.SWAP10:
                    case Instruction.SWAP11:
                    case Instruction.SWAP12:
                    case Instruction.SWAP13:
                    case Instruction.SWAP14:
                    case Instruction.SWAP15:
                    case Instruction.SWAP16:
                        {
                            int count = (int)op.Operation - (int)Instruction.SWAP1 + 1;

                            method.LoadLocalAddress(uint256R);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], count + 1);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], count + 1);
                            method.LoadLocalAddress(uint256R);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                        }
                        break;
                    case Instruction.CODESIZE:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadConstant(contractMetadata.TargetCodeInfo.MachineCode.Length);
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.PC:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadConstant(op.ProgramCounter);
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.COINBASE:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_BLKCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasBeneficiary), false, out _));
                            method.Call(Word.SetAddress);
                        }
                        break;
                    case Instruction.TIMESTAMP:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_BLKCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Timestamp), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.NUMBER:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_BLKCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Number), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.GASLIMIT:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_BLKCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasLimit), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.CALLER:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadRefArgument(REF_ENV_INDEX, typeof(ExecutionEnvironment));
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Caller)));
                            method.Call(Word.SetAddress);
                        }
                        break;
                    case Instruction.ADDRESS:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadRefArgument(REF_ENV_INDEX, typeof(ExecutionEnvironment));
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.Call(Word.SetAddress);
                        }
                        break;
                    case Instruction.ORIGIN:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_TXCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.Origin), false, out _));
                            method.Call(Word.SetAddress);
                        }
                        break;
                    case Instruction.CALLVALUE:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadRefArgument(REF_ENV_INDEX, typeof(ExecutionEnvironment));
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    case Instruction.GASPRICE:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_TXCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.GasPrice), false, out _));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    case Instruction.CALLDATACOPY:
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256B);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256C);

                            method.LoadLocal(gasAvailable);
                            method.LoadLocalAddress(uint256C);
                            method.LoadLocalAddress(lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Memory);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadRefArgument(REF_CALLDATA_INDEX, typeof(ReadOnlyMemory<byte>));
                            method.LoadLocalAddress(uint256B);
                            method.LoadLocal(uint256C);
                            method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                            method.Convert<int>();
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.StoreLocal(localZeroPaddedSpan);

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(localZeroPaddedSpan);
                            method.CallVirtual(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.CALLDATALOAD:
                        {
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            method.LoadRefArgument(REF_CALLDATA_INDEX, typeof(ReadOnlyMemory<byte>));
                            method.LoadLocalAddress(uint256A);
                            method.LoadConstant(Word.Size);
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.Call(Word.SetZeroPaddedSpan);
                        }
                        break;
                    case Instruction.CALLDATASIZE:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_CALLDATA_INDEX);
                            method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.MSIZE:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.MSTORE:
                        {
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(wordRef256B);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadConstant(Word.Size);
                            method.Call(ConvertionExplicit<UInt256, int>());
                            method.StoreLocal(uint256C);
                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocal(wordRef256B);
                            method.Call(Word.GetMutableSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveWord)));
                        }
                        break;
                    case Instruction.MSTORE8:
                        {
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.CallGetter(Word.GetByte0, BitConverter.IsLittleEndian);
                            method.StoreLocal(byte8A);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadConstant(1);
                            method.Call(ConvertionExplicit<UInt256, int>());
                            method.StoreLocal(uint256C);
                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocal(byte8A);

                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveByte)));
                        }
                        break;
                    case Instruction.MLOAD:
                        {
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadFieldAddress(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BigInt32)));
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType()]));
                            method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                            method.StoreLocal(localReadonOnlySpan);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(localReadonOnlySpan);
                            method.Call(Word.SetReadOnlySpan);
                        }
                        break;
                    case Instruction.MCOPY:
                        {
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256B);

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256C);

                            method.LoadLocal(gasAvailable);
                            method.LoadLocalAddress(uint256C);
                            method.LoadLocalAddress(lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.VeryLow);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256B);
                            method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Max)));
                            method.StoreLocal(uint256R);
                            method.LoadLocalAddress(uint256R);
                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256B);
                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));
                        }
                        break;
                    case Instruction.KECCAK256:
                        {
                            MethodInfo refWordToRefValueHashMethod = UnsafeEmit.GetAsMethodInfo<Word, ValueHash256>();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256B);

                            method.LoadLocal(gasAvailable);
                            method.LoadLocalAddress(uint256B);
                            method.LoadLocalAddress(lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Sha3Word);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256B);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256B);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                            method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(refWordToRefValueHashMethod);
                            method.Call(typeof(KeccakCache).GetMethod(nameof(KeccakCache.ComputeTo), [typeof(ReadOnlySpan<byte>), typeof(ValueHash256).MakeByRefType()]));
                        }
                        break;
                    case Instruction.BYTE:
                        {// load a
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Duplicate();
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.StoreLocal(uint32A);
                            method.StoreLocal(wordRef256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetReadOnlySpan);
                            method.StoreLocal(localReadonOnlySpan);

                            Label pushZeroLabel = method.DefineLabel();
                            Label endOfInstructionImpl = method.DefineLabel();
                            method.LoadLocal(wordRef256A);
                            method.Call(Word.GetIsUint16);
                            method.BranchIfFalse(pushZeroLabel);
                            method.LoadLocal(wordRef256A);
                            method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
                            method.LoadConstant(Word.Size);
                            method.BranchIfGreaterOrEqual(pushZeroLabel);
                            method.LoadLocal(wordRef256A);
                            method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
                            method.LoadConstant(0);
                            method.BranchIfLess(pushZeroLabel);

                            method.LoadLocalAddress(localReadonOnlySpan);
                            method.LoadLocal(uint32A);
                            method.Call(typeof(ReadOnlySpan<byte>).GetMethod("get_Item"));
                            method.LoadIndirect<byte>();
                            method.Convert<uint>();
                            method.StoreLocal(uint32A);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(uint32A);
                            method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                            method.Branch(endOfInstructionImpl);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.MarkLabel(endOfInstructionImpl);
                        }
                        break;
                    case Instruction.CODECOPY:
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256C);

                            method.LoadLocal(gasAvailable);
                            method.LoadLocalAddress(uint256C);
                            method.LoadLocalAddress(lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Memory);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256B);

                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadArgument(MACHINE_CODE_INDEX);
                            method.StoreLocal(localReadOnlyMemory);

                            method.LoadLocal(localReadOnlyMemory);
                            method.LoadLocalAddress(uint256B);
                            method.LoadLocalAddress(uint256C);
                            method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                            method.Convert<int>();
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.StoreLocal(localZeroPaddedSpan);

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(localZeroPaddedSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.GAS:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadLocal(gasAvailable);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.RETURNDATASIZE:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_RESULT_INDEX);
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.RETURNDATACOPY:
                        {
                            Label endOfOpcode = method.DefineLabel();
                            using Local tempResult = method.DeclareLocal(typeof(UInt256));


                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256B);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256C);

                            method.LoadLocalAddress(uint256B);
                            method.LoadLocalAddress(uint256C);
                            method.LoadLocalAddress(tempResult);
                            method.Call(typeof(UInt256).GetMethod(nameof(UInt256.AddOverflow)));
                            method.LoadLocalAddress(tempResult);
                            method.LoadRefArgument(REF_RESULT_INDEX, typeof(ILChunkExecutionState));
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            method.Call(typeof(ReadOnlyMemory<byte>).GetProperty(nameof(ReadOnlyMemory<byte>.Length)).GetMethod!);
                            method.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                            method.Or();
                            method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.AccessViolation));

                            method.LoadLocal(gasAvailable);
                            method.LoadLocalAddress(uint256C);
                            method.LoadLocalAddress(lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Memory);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            // Note : check if c + b > returnData.Size

                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadArgument(REF_RESULT_INDEX);
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            method.LoadObject(typeof(ReadOnlyMemory<byte>));
                            method.LoadLocalAddress(uint256B);
                            method.LoadLocalAddress(uint256C);
                            method.Call(MethodInfo<UInt256>("op_Explicit", typeof(Int32), new[] { typeof(UInt256).MakeByRefType() }));
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.StoreLocal(localZeroPaddedSpan);

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(localZeroPaddedSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.RETURN or Instruction.REVERT:
                        {
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256B);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256B);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadRefArgument(REF_RESULT_INDEX, typeof(ILChunkExecutionState));
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256B);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Load), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                            method.StoreObject<ReadOnlyMemory<byte>>();

                            method.LoadArgument(REF_RESULT_INDEX);
                            method.LoadConstant(true);
                            switch (op.Operation)
                            {
                                case Instruction.REVERT:
                                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldRevert)));
                                    break;
                                case Instruction.RETURN:
                                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldReturn)));
                                    break;
                            }
                            method.FakeBranch(ret);
                        }
                        break;
                    case Instruction.BASEFEE:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_BLKCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.BaseFeePerGas), false, out _));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    case Instruction.BLOBBASEFEE:
                        {
                            using Local uint256Nullable = method.DeclareLocal(typeof(UInt256?));
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(REF_BLKCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.BlobBaseFee), false, out _));
                            method.StoreLocal(uint256Nullable);
                            method.LoadLocalAddress(uint256Nullable);
                            method.Call(GetPropertyInfo(typeof(UInt256?), nameof(Nullable<UInt256>.Value), false, out _));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    case Instruction.PREVRANDAO:
                        {
                            Label isPostMergeBranch = method.DefineLabel();
                            Label endOfOpcode = method.DefineLabel();
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);

                            method.LoadArgument(REF_BLKCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Duplicate();
                            method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.IsPostMerge), false, out _));
                            method.BranchIfFalse(isPostMergeBranch);
                            method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.Random), false, out _));
                            method.LoadField(typeof(Hash256).GetField("_hash256", BindingFlags.Instance | BindingFlags.NonPublic));
                            method.Call(Word.SetKeccak);
                            method.Branch(endOfOpcode);

                            method.MarkLabel(isPostMergeBranch);
                            method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.Difficulty), false, out _));
                            method.Call(Word.SetUInt256);

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.BLOBHASH:
                        {
                            Label blobVersionedHashNotFound = method.DefineLabel();
                            Label indexTooLarge = method.DefineLabel();
                            Label endOfOpcode = method.DefineLabel();
                            using Local byteMatrix = method.DeclareLocal(typeof(byte[][]));

                            method.LoadArgument(REF_TXCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes), false, out _));
                            method.StoreLocal(byteMatrix);

                            method.LoadLocal(byteMatrix);
                            method.LoadNull();
                            method.BranchIfEqual(blobVersionedHashNotFound);

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);

                            method.LoadLocalAddress(uint256A);
                            method.LoadLocal(byteMatrix);
                            method.Call(GetPropertyInfo(typeof(byte[][]), nameof(Array.Length), false, out _));
                            method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                            method.BranchIfFalse(indexTooLarge);

                            method.LoadLocal(byteMatrix);
                            method.LoadLocal(uint256A);
                            method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                            method.Convert<int>();
                            method.LoadElement<Byte[]>();
                            method.StoreLocal(localArray);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(localArray);
                            method.Call(Word.SetArray);
                            method.Branch(endOfOpcode);

                            method.MarkLabel(blobVersionedHashNotFound);
                            method.MarkLabel(indexTooLarge);
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.BLOCKHASH:
                        {
                            Label blockHashReturnedNull = method.DefineLabel();
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);

                            method.LoadLocalAddress(uint256A);
                            method.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
                            method.StoreLocal(int64A);

                            method.LoadArgument(BLOCKHASH_PROVIDER_INDEX);
                            method.LoadArgument(REF_BLKCTX_INDEX);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.LoadLocalAddress(int64A);
                            method.CallVirtual(typeof(IBlockhashProvider).GetMethod(nameof(IBlockhashProvider.GetBlockhash), [typeof(BlockHeader), typeof(long).MakeByRefType()]));
                            method.Duplicate();
                            method.StoreLocal(hash256);
                            method.LoadNull();
                            method.BranchIfEqual(blockHashReturnedNull);

                            // not equal
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(hash256);
                            method.Call(GetPropertyInfo(typeof(Hash256), nameof(Hash256.Bytes), false, out _));
                            method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                            method.Call(Word.SetReadOnlySpan);
                            method.Branch(endOfOpcode);
                            // equal to null

                            method.MarkLabel(blockHashReturnedNull);
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.SIGNEXTEND:
                        {
                            Label signIsNegative = method.DefineLabel();
                            Label endOfOpcodeHandling = method.DefineLabel();
                            Label argumentGt32 = method.DefineLabel();
                            using Local wordSpan = method.DeclareLocal(typeof(Span<byte>));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Duplicate();
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.StoreLocal(uint32A);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);

                            method.LoadLocalAddress(uint256A);
                            method.LoadConstant(32);
                            method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                            method.BranchIfFalse(argumentGt32);

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetMutableSpan);
                            method.StoreLocal(wordSpan);

                            method.LoadConstant((uint)31);
                            method.LoadLocal(uint32A);
                            method.Subtract();
                            method.StoreLocal(uint32A);

                            method.LoadItemFromSpan<ExecuteSegment, byte>(wordSpan, uint32A);
                            method.LoadIndirect<byte>();
                            method.Convert<sbyte>();
                            method.LoadConstant((sbyte)0);
                            method.BranchIfLess(signIsNegative);

                            method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesZero32), BindingFlags.Static | BindingFlags.Public));
                            method.Branch(endOfOpcodeHandling);

                            method.MarkLabel(signIsNegative);
                            method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesMax32), BindingFlags.Static | BindingFlags.Public));

                            method.MarkLabel(endOfOpcodeHandling);
                            method.LoadConstant(0);
                            method.LoadLocal(uint32A);
                            method.EmitAsSpan();
                            method.StoreLocal(localSpan);

                            method.LoadLocalAddress(localSpan);
                            method.LoadLocalAddress(wordSpan);
                            method.LoadConstant(0);
                            method.LoadLocal(uint32A);
                            method.Call(typeof(Span<byte>).GetMethod(nameof(Span<byte>.Slice), [typeof(int), typeof(int)]));
                            method.Call(typeof(Span<byte>).GetMethod(nameof(Span<byte>.CopyTo), [typeof(Span<byte>)]));

                            method.MarkLabel(argumentGt32);
                        }
                        break;
                    case Instruction.LOG0:
                    case Instruction.LOG1:
                    case Instruction.LOG2:
                    case Instruction.LOG3:
                    case Instruction.LOG4:
                        {
                            sbyte topicsCount = (sbyte)(op.Operation - Instruction.LOG0);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
                            method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StaticCallViolation));

                            EmitLogMethod(method, (stackHeadRef, stackHeadIdx, segmentMetadata.StackOffsets[i]), topicsCount, evmExceptionLabels, uint256A, uint256B, int64A, gasAvailable, hash256, localReadOnlyMemory);
                        }
                        break;
                    case Instruction.TSTORE:
                        {
                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
                            method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StaticCallViolation));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetArray);
                            method.StoreLocal(localArray);

                            method.LoadRefArgument(REF_ENV_INDEX, typeof(ExecutionEnvironment));
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.LoadLocalAddress(uint256A);
                            method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                            method.StoreLocal(storageCell);

                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocalAddress(storageCell);
                            method.LoadLocal(localArray);
                            method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.SetTransientState), [typeof(StorageCell).MakeByRefType(), typeof(byte[])]));
                        }
                        break;
                    case Instruction.TLOAD:
                        {
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);

                            method.LoadRefArgument(REF_ENV_INDEX, typeof(ExecutionEnvironment));
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.LoadLocalAddress(uint256A);
                            method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                            method.StoreLocal(storageCell);

                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocalAddress(storageCell);
                            method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.GetTransientState), [typeof(StorageCell).MakeByRefType()]));
                            method.StoreLocal(localReadonOnlySpan);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(localReadonOnlySpan);
                            method.Call(Word.SetReadOnlySpan);
                        }
                        break;
                    case Instruction.SSTORE:
                        {
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetReadOnlySpan);
                            method.StoreLocal(localReadonOnlySpan);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(localReadonOnlySpan);
                            method.LoadArgument(SPEC_INDEX);
                            method.LoadArgument(TXTRACER_INDEX);

                            MethodInfo nonTracingSStoreMethod = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>)
                                        .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.InstructionSStore), BindingFlags.Static | BindingFlags.NonPublic)
                                        .MakeGenericMethod(typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing));

                            MethodInfo tracingSStoreMethod = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>)
                                        .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.InstructionSStore), BindingFlags.Static | BindingFlags.NonPublic)
                                        .MakeGenericMethod(typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing));

                            if (!bakeInTracerCalls)
                            {
                                method.Call(nonTracingSStoreMethod);
                            }
                            else
                            {
                                Label callNonTracingMode = method.DefineLabel();
                                Label skipBeyondCalls = method.DefineLabel();
                                method.LoadArgument(TXTRACER_INDEX);
                                method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
                                method.BranchIfFalse(callNonTracingMode);
                                method.Call(tracingSStoreMethod);
                                method.Branch(skipBeyondCalls);
                                method.MarkLabel(callNonTracingMode);
                                method.Call(nonTracingSStoreMethod);
                                method.MarkLabel(skipBeyondCalls);
                            }

                            Label endOfOpcode = method.DefineLabel();
                            method.Duplicate();
                            method.StoreLocal(uint32A);
                            method.LoadConstant((int)EvmExceptionType.None);
                            method.BranchIfEqual(endOfOpcode);

                            method.LoadArgument(REF_RESULT_INDEX);
                            method.LoadLocal(uint32A);
                            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

                            method.LoadArgument(GAS_AVAILABLE_INDEX);
                            method.LoadLocal(gasAvailable);
                            method.StoreIndirect<long>();
                            method.Branch(exit);

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.SLOAD:
                        {
                            method.LoadLocal(gasAvailable);
                            method.LoadArgument(SPEC_INDEX);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetSLoadCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);

                            method.LoadRefArgument(REF_ENV_INDEX, typeof(ExecutionEnvironment));
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.LoadLocalAddress(uint256A);
                            method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                            method.StoreLocal(storageCell);

                            method.LoadLocalAddress(gasAvailable);
                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(storageCell);
                            method.LoadConstant((int)VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.StorageAccessType.SLOAD);
                            method.LoadArgument(SPEC_INDEX);
                            method.LoadArgument(TXTRACER_INDEX);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeStorageAccessGas), BindingFlags.Static | BindingFlags.NonPublic));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocalAddress(storageCell);
                            method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.Get), [typeof(StorageCell).MakeByRefType()]));
                            method.StoreLocal(localReadonOnlySpan);

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(localReadonOnlySpan);
                            method.Call(Word.SetReadOnlySpan);
                        }
                        break;
                    case Instruction.EXTCODESIZE:
                        {
                            method.LoadLocal(gasAvailable);
                            method.LoadArgument(SPEC_INDEX);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(address);

                            method.LoadLocalAddress(gasAvailable);
                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocal(address);
                            method.LoadConstant(true);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadArgument(SPEC_INDEX);
                            method.LoadArgument(TXTRACER_INDEX);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            method.LoadArgument(CODE_INFO_REPOSITORY_INDEX);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocal(address);
                            method.LoadArgument(SPEC_INDEX);
                            method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                            method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));
                            method.StoreLocal(localReadOnlyMemory);
                            method.LoadLocalAddress(localReadOnlyMemory);
                            method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));

                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.EXTCODECOPY:
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 4);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256C);

                            method.LoadLocal(gasAvailable);
                            method.LoadArgument(SPEC_INDEX);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                            method.LoadLocalAddress(uint256C);
                            method.LoadLocalAddress(lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Memory);
                            method.Multiply();
                            method.Add();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(address);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256A);
                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(uint256B);

                            method.LoadLocalAddress(gasAvailable);
                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocal(address);
                            method.LoadConstant(true);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadArgument(SPEC_INDEX);
                            method.LoadArgument(TXTRACER_INDEX);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocalAddress(gasAvailable);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadArgument(CODE_INFO_REPOSITORY_INDEX);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocal(address);
                            method.LoadArgument(SPEC_INDEX);
                            method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                            method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));

                            method.LoadLocalAddress(uint256B);
                            method.LoadLocal(uint256C);
                            method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                            method.Convert<int>();
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.StoreLocal(localZeroPaddedSpan);

                            method.LoadArgument(REF_MEMORY_INDEX);
                            method.LoadLocalAddress(uint256A);
                            method.LoadLocalAddress(localZeroPaddedSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.EXTCODEHASH:
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.LoadLocal(gasAvailable);
                            method.LoadArgument(SPEC_INDEX);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeHashCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(address);

                            method.LoadLocalAddress(gasAvailable);
                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocal(address);
                            method.LoadConstant(true);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadArgument(SPEC_INDEX);
                            method.LoadArgument(TXTRACER_INDEX);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            Label pushZeroLabel = method.DefineLabel();
                            Label pushhashcodeLabel = method.DefineLabel();

                            // account exists
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocal(address);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.AccountExists)));
                            method.BranchIfFalse(pushZeroLabel);

                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocal(address);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.IsDeadAccount)));
                            method.BranchIfTrue(pushZeroLabel);

                            using Local delegateAddress = method.DeclareLocal<Address>();
                            method.LoadArgument(CODE_INFO_REPOSITORY_INDEX);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocal(address);
                            method.LoadLocalAddress(delegateAddress);
                            method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.TryGetDelegation), [typeof(IWorldState), typeof(Address), typeof(Address).MakeByRefType()]));
                            method.BranchIfFalse(pushhashcodeLabel);

                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocal(delegateAddress);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.AccountExists)));
                            method.BranchIfFalse(pushZeroLabel);

                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocal(delegateAddress);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.IsDeadAccount)));
                            method.BranchIfTrue(pushZeroLabel);

                            method.MarkLabel(pushhashcodeLabel);
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadArgument(CODE_INFO_REPOSITORY_INDEX);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocal(address);
                            method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.GetExecutableCodeHash), [typeof(IWorldState), typeof(Address)]));
                            method.Call(Word.SetKeccak);
                            method.Branch(endOfOpcode);

                            // Push 0
                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.SELFBALANCE:
                        {
                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadRefArgument(REF_ENV_INDEX, typeof(ExecutionEnvironment));
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    case Instruction.BALANCE:
                        {
                            method.LoadLocal(gasAvailable);
                            method.LoadArgument(SPEC_INDEX);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetBalanceCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(address);

                            method.LoadLocalAddress(gasAvailable);
                            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
                            method.LoadLocal(address);
                            method.LoadConstant(false);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadArgument(SPEC_INDEX);
                            method.LoadArgument(TXTRACER_INDEX);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.CleanAndLoadWord(stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadArgument(WORLD_STATE_INDEX);
                            method.LoadLocal(address);
                            method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    default:
                        {
                            method.FakeBranch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));

                        }
                        break;
                }

                if (bakeInTracerCalls)
                {
                    UpdateStackHeadIdxAndPushRefOpcodeMode(method, stackHeadRef, stackHeadIdx, op);
                    EmitCallToEndInstructionTrace(method, gasAvailable);
                }
                else
                {
                    UpdateStackHeadAndPushRerSegmentMode(method, stackHeadRef, stackHeadIdx, i, currentSegment);
                }
            }

            Label jumpIsLocal = method.DefineLabel();
            Label jumpIsNotLocal = method.DefineLabel();
            Local isEphemeralJump = method.DeclareLocal<bool>();
            Label skipProgramCounterSetting = method.DefineLabel();
            // prepare ILEvmState
            // check if returnState is null
            method.MarkLabel(ret);
            // we get stackHeadRef size
            method.LoadArgument(STACK_HEAD_INDEX);
            method.LoadLocal(stackHeadIdx);
            method.StoreIndirect<int>();

            // set gas available
            method.LoadArgument(GAS_AVAILABLE_INDEX);
            method.LoadLocal(gasAvailable);
            method.StoreIndirect<long>();

            // set program counter
            method.LoadLocal(isEphemeralJump);
            method.BranchIfTrue(skipProgramCounterSetting);

            method.LoadArgument(PROGRAM_COUNTER_INDEX);
            method.LoadLocal(programCounter);
            method.LoadConstant(1);
            method.Add();
            method.StoreIndirect<int>();

            method.MarkLabel(skipProgramCounterSetting);

            // return
            method.MarkLabel(exit);
            method.Return();

            // isContinuation
            method.MarkLabel(isContinuation);
            method.LoadLocal(programCounter);
            method.StoreLocal(jmpDestination);
            method.LoadArgument(REF_RESULT_INDEX);
            method.LoadConstant(false);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldJump)));
            method.Branch(jumpIsLocal);

            // jump table
            method.MarkLabel(jumpTable);
            method.LoadLocal(wordRef256A);

            method.Duplicate();
            method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
            method.StoreLocal(jmpDestination);

            method.Call(Word.GetIsUint16);
            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

            // if (jumpDest <= maxJump)
            method.LoadLocal(jmpDestination);
            method.LoadConstant(segmentMetadata.Boundaries.End.Value);
            method.BranchIfGreater(jumpIsNotLocal);

            // if (jumpDest >= minJump)
            method.LoadLocal(jmpDestination);
            method.LoadConstant(segmentMetadata.Boundaries.Start.Value);
            method.BranchIfLess(jumpIsNotLocal);

            method.Branch(jumpIsLocal);

            method.MarkLabel(jumpIsNotLocal);
            method.LoadArgument(REF_RESULT_INDEX);
            method.LoadConstant(true);
            method.Duplicate();
            method.StoreLocal(isEphemeralJump);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldJump)));
            method.LoadArgument(PROGRAM_COUNTER_INDEX);
            method.LoadLocal(jmpDestination);
            method.StoreIndirect<int>();
            method.Branch(ret);

            method.MarkLabel(jumpIsLocal);

            if (jumpDestinations.Count < 64)
            {
                foreach (KeyValuePair<int, Label> jumpdest in jumpDestinations)
                {
                    method.LoadLocal(jmpDestination);
                    method.LoadConstant(jumpdest.Key);
                    method.BranchIfEqual(jumpdest.Value);
                }
                method.Branch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));
            }
            else
            {
                method.FindCorrectBranchAndJump(jmpDestination, jumpDestinations, evmExceptionLabels);
            }

            foreach (KeyValuePair<EvmExceptionType, Label> kvp in evmExceptionLabels)
            {
                method.MarkLabel(kvp.Value);
                if (bakeInTracerCalls)
                {
                    EmitCallToErrorTrace(method, gasAvailable, kvp);
                }

                method.LoadArgument(REF_RESULT_INDEX);
                method.LoadConstant((int)kvp.Key);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                method.Branch(exit);
            }

            return jumpDestinations.Keys.ToArray();
        }

        private static void UpdateStackHeadAndPushRerSegmentMode(Emit<ExecuteSegment> method, Local stackHeadRef, Local stackHeadIdx, int pc, SubSegmentMetadata stackMetadata)
        {
            if (stackMetadata.LeftOutStack != 0 && pc == stackMetadata.End)
            {
                method.StackSetHead(stackHeadRef, stackMetadata.LeftOutStack);
                method.LoadLocal(stackHeadIdx);
                method.LoadConstant(stackMetadata.LeftOutStack);
                method.Add();
                method.StoreLocal(stackHeadIdx);
            }
        }

        private static void UpdateStackHeadIdxAndPushRefOpcodeMode(Emit<ExecuteSegment> method, Local stackHeadRef, Local stackHeadIdx, OpcodeInfo op)
        {
            int delta = op.Metadata.StackBehaviorPush - op.Metadata.StackBehaviorPop;
            method.LoadLocal(stackHeadIdx);
            method.LoadConstant(delta);
            method.Add();
            method.StoreLocal(stackHeadIdx);

            method.StackSetHead(stackHeadRef, delta);
        }

        private static void EmitCallToErrorTrace(Emit<ExecuteSegment> method, Local gasAvailable, KeyValuePair<EvmExceptionType, Label> kvp)
        {
            Label skipTracing = method.DefineLabel();
            method.LoadArgument(TXTRACER_INDEX);
            method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
            method.BranchIfFalse(skipTracing);

            method.LoadArgument(TXTRACER_INDEX);
            method.LoadLocal(gasAvailable);
            method.LoadConstant((int)kvp.Key);
            method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.EndInstructionTraceError), BindingFlags.Static | BindingFlags.NonPublic));

            method.MarkLabel(skipTracing);
        }
        private static void EmitCallToEndInstructionTrace(Emit<ExecuteSegment> method, Local gasAvailable)
        {
            Label skipTracing = method.DefineLabel();
            method.LoadArgument(TXTRACER_INDEX);
            method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
            method.BranchIfFalse(skipTracing);

            method.LoadArgument(TXTRACER_INDEX);
            method.LoadLocal(gasAvailable);
            method.LoadArgument(REF_MEMORY_INDEX);
            method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
            method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.EndInstructionTrace), BindingFlags.Static | BindingFlags.NonPublic));

            method.MarkLabel(skipTracing);
        }
        private static void EmitCallToStartInstructionTrace(Emit<ExecuteSegment> method, Local gasAvailable, Local head, OpcodeInfo op)
        {
            Label skipTracing = method.DefineLabel();
            method.LoadArgument(TXTRACER_INDEX);
            method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
            method.BranchIfFalse(skipTracing);

            method.LoadArgument(TXTRACER_INDEX);
            method.LoadConstant((int)op.Operation);
            method.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
            method.LoadLocal(gasAvailable);
            method.LoadConstant(op.ProgramCounter);
            method.LoadLocal(head);
            method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.StartInstructionTrace), BindingFlags.Static | BindingFlags.NonPublic));

            method.MarkLabel(skipTracing);
        }
        private static void EmitShiftUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, bool isLeft, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
        {
            MethodInfo shiftOp = typeof(UInt256).GetMethod(isLeft ? nameof(UInt256.LeftShift) : nameof(UInt256.RightShift));
            Label skipPop = il.DefineLabel();
            Label endOfOpcode = il.DefineLabel();

            // Note: Use Vector256 directoly if UInt256 does not use it internally
            // we the two uint256 from the stackHeadRef
            Local shiftBit = il.DeclareLocal<uint>();

            il.StackLoadPrevious(stack.headRef, stack.offset, 1);
            il.Call(Word.GetUInt256);
            il.Duplicate();
            il.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
            il.Convert<uint>();
            il.StoreLocal(shiftBit);
            il.StoreLocal(locals[0]);

            il.LoadLocalAddress(locals[0]);
            il.LoadConstant(Word.FullSize);
            il.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
            il.BranchIfFalse(skipPop);

            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[1]);
            il.LoadLocalAddress(locals[1]);

            il.LoadLocal(shiftBit);

            il.LoadLocalAddress(uint256R);

            il.Call(shiftOp);

            il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
            il.LoadLocal(uint256R);
            il.Call(Word.SetUInt256);
            il.Branch(endOfOpcode);

            il.MarkLabel(skipPop);

            il.CleanWord(stack.headRef, stack.offset, 2);

            il.MarkLabel(endOfOpcode);
        }
        private static void EmitShiftInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
        {
            Label aBiggerOrEqThan256 = il.DefineLabel();
            Label signIsNeg = il.DefineLabel();
            Label endOfOpcode = il.DefineLabel();

            // Note: Use Vector256 directoly if UInt256 does not use it internally
            // we the two uint256 from the stackHeadRef
            il.StackLoadPrevious(stack.headRef, stack.offset, 1);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[0]);

            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[1]);

            il.LoadLocalAddress(locals[0]);
            il.LoadConstant(Word.FullSize);
            il.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
            il.BranchIfFalse(aBiggerOrEqThan256);

            using Local shiftBits = il.DeclareLocal<int>();


            il.LoadLocalAddress(locals[1]);
            il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
            il.LoadLocalAddress(locals[0]);
            il.LoadField(GetFieldInfo<UInt256>(nameof(UInt256.u0)));
            il.Convert<int>();
            il.LoadLocalAddress(uint256R);
            il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
            il.Call(typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.RightShift), [typeof(int), typeof(Int256.Int256).MakeByRefType()]));
            il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
            il.LoadLocal(uint256R);
            il.Call(Word.SetUInt256);
            il.Branch(endOfOpcode);

            il.MarkLabel(aBiggerOrEqThan256);

            il.LoadLocalAddress(locals[1]);
            il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
            il.Call(GetPropertyInfo(typeof(Int256.Int256), nameof(Int256.Int256.Sign), false, out _));
            il.LoadConstant(0);
            il.BranchIfLess(signIsNeg);

            il.CleanWord(stack.headRef, stack.offset, 2);
            il.Branch(endOfOpcode);

            // sign
            il.MarkLabel(signIsNeg);
            il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
            il.LoadFieldAddress(GetFieldInfo(typeof(Int256.Int256), nameof(Int256.Int256.MinusOne)));
            il.Call(UnsafeEmit.GetAsMethodInfo<Int256.Int256, UInt256>());
            il.LoadObject<UInt256>();
            il.Call(Word.SetUInt256);
            il.Branch(endOfOpcode);

            il.MarkLabel(endOfOpcode);
        }
        private static void EmitBitwiseUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
        {
            // Note: Use Vector256 directoly if UInt256 does not use it internally
            // we the two uint256 from the stack.headRef
            MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
            MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
            MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
            MethodInfo operationUnegenerified = operation.MakeGenericMethod(typeof(byte));

            using Local vectorResult = il.DeclareLocal<Vector256<byte>>();

            il.StackLoadPrevious(stack.headRef, stack.offset, 1);
            il.Call(refWordToRefByteMethod);
            il.Call(readVector256Method);
            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(refWordToRefByteMethod);
            il.Call(readVector256Method);

            il.Call(operationUnegenerified);
            il.StoreLocal(vectorResult);

            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(refWordToRefByteMethod);
            il.LoadLocal(vectorResult);
            il.Call(writeVector256Method);
        }
        private static void EmitComparaisonUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
        {
            // we the two uint256 from the stack.headRef
            il.StackLoadPrevious(stack.headRef, stack.offset, 1);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[0]);
            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[1]);

            // invoke op  on the uint256
            il.LoadLocalAddress(locals[0]);
            il.LoadLocalAddress(locals[1]);
            il.Call(operation);

            // convert to conv_i
            il.Convert<int>();
            il.Call(ConvertionExplicit<UInt256, int>());
            il.StoreLocal(uint256R);

            // push the result to the stack.headRef
            il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
            il.LoadLocal(uint256R); // stack.headRef: word*, uint256
            il.Call(Word.SetUInt256);
        }
        private static void EmitComparaisonInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, bool isGreaterThan, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
        {
            Label endOpcodeHandling = il.DefineLabel();
            Label pushOnehandling = il.DefineLabel();
            // we the two uint256 from the stack.headRef
            il.StackLoadPrevious(stack.headRef, stack.offset, 1);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[0]);
            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[1]);

            // invoke op  on the uint256
            il.LoadLocalAddress(locals[0]);
            il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
            il.LoadLocalAddress(locals[1]);
            il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
            il.LoadObject<Int256.Int256>();
            il.Call(operation);
            il.LoadConstant(0);
            if (isGreaterThan)
            {
                il.BranchIfGreater(pushOnehandling);
            }
            else
            {
                il.BranchIfLess(pushOnehandling);
            }

            il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
            il.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.Zero)));
            il.Branch(endOpcodeHandling);

            il.MarkLabel(pushOnehandling);
            il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
            il.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.One)));
            il.Branch(endOpcodeHandling);

            // push the result to the stack.headRef
            il.MarkLabel(endOpcodeHandling);
            il.Call(Word.SetUInt256);
        }
        private static void EmitBinaryUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
        {
            Label label = il.DefineLabel();
            il.StackLoadPrevious(stack.headRef, stack.offset, 1);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[0]);
            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[1]);

            // incase of custom handling, we branch to the label
            customHandling?.Invoke(il, label, locals);

            // invoke op  on the uint256
            il.LoadLocalAddress(locals[0]);
            il.LoadLocalAddress(locals[1]);
            il.LoadLocalAddress(uint256R);
            il.Call(operation);

            // skip the main handling
            il.MarkLabel(label);

            // push the result to the stack.headRef
            il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
            il.LoadLocal(uint256R); // stack.headRef: word*, uint256
            il.Call(Word.SetUInt256);
        }
        private static void EmitBinaryInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
        {
            Label label = il.DefineLabel();

            // we the two uint256 from the stack.headRef
            il.StackLoadPrevious(stack.headRef, stack.offset, 1);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[0]);
            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[1]);

            // incase of custom handling, we branch to the label
            customHandling?.Invoke(il, label, locals);

            // invoke op  on the uint256
            il.LoadLocalAddress(locals[0]);
            il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
            il.LoadLocalAddress(locals[1]);
            il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
            il.LoadLocalAddress(uint256R);
            il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
            il.Call(operation);

            // skip the main handling
            il.MarkLabel(label);

            // push the result to the stack.headRef
            il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
            il.LoadLocal(uint256R); // stack.headRef: word*, uint256
            il.Call(Word.SetUInt256);
        }
        private static void EmitTrinaryUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
        {
            Label label = il.DefineLabel();

            // we the two uint256 from the stackHeadRef
            il.StackLoadPrevious(stack.headRef, stack.offset, 1);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[0]);
            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[1]);
            il.StackLoadPrevious(stack.headRef, stack.offset, 3);
            il.Call(Word.GetUInt256);
            il.StoreLocal(locals[2]);

            // incase of custom handling, we branch to the label
            customHandling?.Invoke(il, label, locals);

            // invoke op  on the uint256
            il.LoadLocalAddress(locals[0]);
            il.LoadLocalAddress(locals[1]);
            il.LoadLocalAddress(locals[2]);
            il.LoadLocalAddress(uint256R);
            il.Call(operation);

            // skip the main handling
            il.MarkLabel(label);

            // push the result to the stack.headRef
            il.CleanAndLoadWord(stack.headRef, stack.offset, 3);
            il.LoadLocal(uint256R); // stack.headRef: word*, uint256
            il.Call(Word.SetUInt256);
        }

        private static void EmitLogMethod<T>(
            Emit<T> il,
            (Local headRef, Local headIdx, int offset) stack,
            sbyte topicsCount,
            Dictionary<EvmExceptionType, Label> exceptions,
            Local uint256Position, Local uint256Length, Local int64A, Local gasAvailable, Local hash256, Local localReadOnlyMemory
        )
        {
            using Local logEntry = il.DeclareLocal<LogEntry>();

            il.StackLoadPrevious(stack.headRef, stack.offset, 1);
            il.Call(Word.GetUInt256);
            il.StoreLocal(uint256Position); // position
            il.StackLoadPrevious(stack.headRef, stack.offset, 2);
            il.Call(Word.GetUInt256);
            il.StoreLocal(uint256Length); // length
                                          // UpdateMemoryCost
            il.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
            il.LoadLocalAddress(gasAvailable);
            il.LoadLocalAddress(uint256Position); // position
            il.LoadLocalAddress(uint256Length); // length
            il.Call(
                typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(
                    nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)
                )
            );
            il.BranchIfFalse(il.AddExceptionLabel(exceptions, EvmExceptionType.OutOfGas));

            // update gasAvailable
            il.LoadLocal(gasAvailable);
            il.LoadConstant(topicsCount * GasCostOf.LogTopic);
            il.Convert<ulong>();
            il.LoadLocalAddress(uint256Length); // length
            il.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
            il.Convert<ulong>();
            il.LoadConstant(GasCostOf.LogData);
            il.Multiply();
            il.Add();
            il.Subtract();
            il.Duplicate();
            il.StoreLocal(gasAvailable); // gasAvailable -= gasCost
            il.LoadConstant((ulong)0);
            il.BranchIfLess(il.AddExceptionLabel(exceptions, EvmExceptionType.OutOfGas));

            il.LoadArgument(REF_ENV_INDEX);
            il.LoadField(
                GetFieldInfo(
                    typeof(ExecutionEnvironment),
                    nameof(ExecutionEnvironment.ExecutingAccount)
                )
            );

            il.LoadArgument(REF_MEMORY_INDEX);
            il.LoadLocalAddress(uint256Position); // position
            il.LoadLocalAddress(uint256Length); // length
            il.Call(
                typeof(EvmPooledMemory).GetMethod(
                    nameof(EvmPooledMemory.Load),
                    [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]
                )
            );
            il.StoreLocal(localReadOnlyMemory);
            il.LoadLocalAddress(localReadOnlyMemory);
            il.Call(typeof(ReadOnlyMemory<byte>).GetMethod(nameof(ReadOnlyMemory<byte>.ToArray)));

            il.LoadConstant(topicsCount);
            il.NewArray<Hash256>();
            for (int i = 0; i < topicsCount; i++)
            {
                il.Duplicate();
                il.LoadConstant(i);
                using (Local keccak = il.DeclareLocal(typeof(ValueHash256)))
                {
                    il.StackLoadPrevious(stack.headRef, stack.offset - 2, i + 1);
                    il.Call(Word.GetKeccak);
                    il.StoreLocal(keccak);
                    il.LoadLocalAddress(keccak);
                    il.NewObject(typeof(Hash256), typeof(ValueHash256).MakeByRefType());
                }
                il.StoreElement<Hash256>();
            }
            // Creat an LogEntry Object from Items on the Stack
            il.NewObject(typeof(LogEntry), typeof(Address), typeof(byte[]), typeof(Hash256[]));
            il.StoreLocal(logEntry);

            il.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
            il.LoadFieldAddress(typeof(EvmState).GetField("_accessTracker", BindingFlags.Instance | BindingFlags.NonPublic));
            il.CallVirtual(GetPropertyInfo(typeof(StackAccessTracker), nameof(StackAccessTracker.Logs), getSetter: false, out _));
            il.LoadLocal(logEntry);
            il.CallVirtual(
                typeof(ICollection<LogEntry>).GetMethod(nameof(ICollection<LogEntry>.Add))
            );
        }

        private static void EmitGasAvailabilityCheck<T>(
            Emit<T> il,
            Local gasAvailable,
            Label outOfGasLabel)
        {
            il.LoadLocal(gasAvailable);
            il.LoadConstant(0);
            il.BranchIfLess(outOfGasLabel);
        }
    }
}
