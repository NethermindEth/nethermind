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
    public static class FullAOR
    {
        public delegate bool MoveNextDelegate(ref int gasAvailable, ref int programCounter, ref int stackHead, ref Word stackHeadRef); // it returns true if current staet is HALTED or FINISHED and Sets Current.CallResult in case of CALL or CREATE

        public static void CompileContract(ContractMetadata contractMetadata, IVMConfig vmConfig)
        {
            PersistedAssemblyBuilder assemblyBuilder = new PersistedAssemblyBuilder(new AssemblyName("Nethermind.Evm.Precompiled.Live"), typeof(object).Assembly);
            ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("ContractsModule");
            TypeBuilder contractStructBuilder = moduleBuilder.DefineType($"{contractMetadata.TargetCodeInfo.Address}", TypeAttributes.Public |
                TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));

            // create a property ILChunkExecutionState Current
            PropertyBuilder currentProp = contractStructBuilder.EmitProperty<ILChunkExecutionState>("Current", true, true);
            // create a property IContractState State
            PropertyBuilder stateProp = contractStructBuilder.EmitProperty<IContractState>("State", true, true);
            // create a property EvmState EvmState
            PropertyBuilder evmStateProp = contractStructBuilder.EmitProperty<EvmState>("EvmState", true, false);
            // create a property ITxTracer Tracer
            PropertyBuilder tracerProp = contractStructBuilder.EmitProperty<ITxTracer>("Tracer", true, false);
            // create a property IReleaseSpec Spec
            PropertyBuilder specProp = contractStructBuilder.EmitProperty<IReleaseSpec>("Spec", true, false);
            // create a property IWorldState WorldState
            PropertyBuilder worldStateProp = contractStructBuilder.EmitProperty<IWorldState>("WorldState", true, false);
            // create a property IBlockhashProvider BlockhashProvider
            PropertyBuilder blockhashProviderProp = contractStructBuilder.EmitProperty<IBlockhashProvider>("BlockhashProvider", true, false);
            // create a property ICodeInfoRepository CodeInfoRepository
            PropertyBuilder codeInfoRepositoryProp = contractStructBuilder.EmitProperty<ICodeInfoRepository>("CodeInfoRepository", true, false);

            // create a constructor for the contract
            ConstructorBuilder constructor = contractStructBuilder.DefineDefaultConstructor(MethodAttributes.Public);

            // create a constructor for the contract that takes all the properties
            ConstructorBuilder fullConstructor = contractStructBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new Type[] { typeof(EvmState), typeof(IWorldState), typeof(ICodeInfoRepository), typeof(IReleaseSpec), typeof(IBlockhashProvider), typeof(ITxTracer) });
            ILGenerator fullConstructorIL = fullConstructor.GetILGenerator();
            fullConstructorIL.Emit(OpCodes.Ldarg_0);
            fullConstructorIL.Emit(OpCodes.Ldarg_1);
            fullConstructorIL.Emit(OpCodes.Call, evmStateProp.GetSetMethod());
            fullConstructorIL.Emit(OpCodes.Ldarg_0);
            fullConstructorIL.Emit(OpCodes.Ldarg_2);
            fullConstructorIL.Emit(OpCodes.Call, worldStateProp.GetSetMethod());
            fullConstructorIL.Emit(OpCodes.Ldarg_0);
            fullConstructorIL.Emit(OpCodes.Ldarg_3);
            fullConstructorIL.Emit(OpCodes.Call, codeInfoRepositoryProp.GetSetMethod());
            fullConstructorIL.Emit(OpCodes.Ldarg_0);
            fullConstructorIL.Emit(OpCodes.Ldarg_S, 4);
            fullConstructorIL.Emit(OpCodes.Call, specProp.GetSetMethod());
            fullConstructorIL.Emit(OpCodes.Ldarg_0);
            fullConstructorIL.Emit(OpCodes.Ldarg_S, 5);
            fullConstructorIL.Emit(OpCodes.Call, blockhashProviderProp.GetSetMethod());
            fullConstructorIL.Emit(OpCodes.Ldarg_0);
            fullConstructorIL.Emit(OpCodes.Ldarg_S, 6);
            fullConstructorIL.Emit(OpCodes.Call, tracerProp.GetSetMethod());
            fullConstructorIL.Emit(OpCodes.Ret);

            // create a method MoveNext
            MethodBuilder moveNextMethod = contractStructBuilder.DefineMethod("MoveNext", MethodAttributes.Public | MethodAttributes.Virtual, typeof(bool), new Type[] { typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(Word).MakeByRefType() });

        }

        public static void EmitMoveNext(TypeBuilder contractBuilder, ContractMetadata metadata, IVMConfig config)
        {
            Emit<MoveNextDelegate> method = Emit<MoveNextDelegate>.BuildInstanceMethod(
                contractBuilder,
                "MoveNext",
                MethodAttributes.Public | MethodAttributes.Virtual);
        }
    }
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

        private class PartialAotEnvLoader : EnvLoader<ExecuteSegment>
        {
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
            public override void LoadBlockContext(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(REF_BLKCTX_INDEX);
            }

            public override void LoadBlockhashProvider(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(BLOCKHASH_PROVIDER_INDEX);
            }

            public override void LoadCalldata(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(REF_CALLDATA_INDEX);
            }

            public void LoadCalldataRef(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadRefArgument(REF_CALLDATA_INDEX, typeof(ReadOnlyMemory<byte>));
            }

            public override void LoadChainId(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(CHAINID_INDEX);
            }

            public override void LoadCodeInfoRepository(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(CODE_INFO_REPOSITORY_INDEX);
            }

            public override void LoadCurrStackHead(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                //il.LoadLocal(locals.stackHeadIdx);
                il.LoadArgument(REF_CURR_STACK_HEAD_INDEX);
            }

            public override void LoadEnv(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(REF_ENV_INDEX);
            }

            public void LoadEnvByRef(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadRefArgument(REF_ENV_INDEX, typeof(ExecutionEnvironment));
            }

            public override void LoadGasAvailable(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                //il.LoadLocal(locals.gasAvailable);
                il.LoadArgument(GAS_AVAILABLE_INDEX);
            }

            public void LoadGasAvailableByRef(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadRefArgument(GAS_AVAILABLE_INDEX, typeof(long));
            }

            public override void LoadImmediatesData(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(IMMEDIATES_DATA_INDEX);
            }

            public override void LoadMachineCode(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(MACHINE_CODE_INDEX);
            }

            public override void LoadMemory(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(REF_MEMORY_INDEX);
            }

            public override void LoadProgramCounter(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(PROGRAM_COUNTER_INDEX);
            }

            public void LoadProgramCounterByRef(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadRefArgument(PROGRAM_COUNTER_INDEX, typeof(int));
            }

            public override void LoadResult(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(REF_RESULT_INDEX);
            }

            public void LoadResultByRef(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadRefArgument(REF_RESULT_INDEX, typeof(ILChunkExecutionState));
            }

            public override void LoadSpec(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(SPEC_INDEX);
            }

            public override void LoadStackHead(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                //il.LoadLocal(locals.stackHeadIdx);
                il.LoadArgument(STACK_HEAD_INDEX);
            }

            public void LoadStackHeadByRef(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadRefArgument(STACK_HEAD_INDEX, typeof(int));
            }

            public void LoadStackHeadRef(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(REF_CURR_STACK_HEAD_INDEX);
            }

            public override void LoadTxContext(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(REF_TXCTX_INDEX);
            }

            public override void LoadTxTracer(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(TXTRACER_INDEX);
            }

            public override void LoadVmState(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(REF_VMSTATE_INDEX);
            }

            public void LoadVmStateByRef(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadRefArgument(REF_VMSTATE_INDEX, typeof(EvmState));
            }

            public override void LoadWorldState(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals)
            {
                il.LoadArgument(WORLD_STATE_INDEX);
            }
        }

        public static PrecompiledChunk CompileSegment(string segmentName, ContractMetadata metadata, int segmentIndex, IVMConfig config, out int[] localJumpdests)
        {
            // code is optimistic assumes locals.stackHeadRef underflow and locals.stackHeadRef overflow to not occure (WE NEED EOF FOR THIS)
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
            var envLoader = new PartialAotEnvLoader();
            using var locals = new Locals<ExecuteSegment>(method);

            Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();

            Label exit = method.DefineLabel(); // the label just before return
            Label jumpTable = method.DefineLabel(); // jump table
            Label isContinuation = method.DefineLabel(); // jump table
            Label ret = method.DefineLabel();

            envLoader.LoadStackHeadByRef(method, locals);
            method.StoreLocal(locals.stackHeadIdx);

            envLoader.LoadStackHeadRef(method, locals);
            method.StoreLocal(locals.stackHeadRef);

            // set gas to local
            envLoader.LoadGasAvailableByRef(method, locals);
            method.StoreLocal(locals.gasAvailable);

            // set pc to local
            envLoader.LoadProgramCounterByRef(method, locals);
            method.StoreLocal(locals.programCounter);

            // if last ilvmstate was a jump
            envLoader.LoadResult(method, locals);
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
                    method.StoreLocal(locals.programCounter);
                }

                if (bakeInTracerCalls)
                {
                    EmitCallToStartInstructionTrace(method, locals.gasAvailable, locals.stackHeadIdx, op, envLoader, locals);
                }

                // check if opcode is activated in current spec, we skip this check for opcodes that are always enabled
                if (op.Operation.RequiresAvailabilityCheck())
                {
                    envLoader.LoadSpec(method, locals);
                    method.LoadConstant((byte)op.Operation);
                    method.Call(typeof(InstructionExtensions).GetMethod(nameof(InstructionExtensions.IsEnabled)));
                    method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                }

                // if tracing mode is off, we consume the static gas only at the start of segment
                if (!bakeInTracerCalls)
                {
                    if (currentSegment.StaticGasSubSegmentes.TryGetValue(i, out long gasCost) && gasCost > 0)
                    {
                        method.LoadLocal(locals.gasAvailable);
                        method.LoadConstant(gasCost);
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));
                    }
                }
                else
                {
                    // otherwise we update the gas after each instruction
                    method.LoadLocal(locals.gasAvailable);
                    method.LoadConstant(op.Metadata.GasCost);
                    method.Subtract();
                    method.Duplicate();
                    method.StoreLocal(locals.gasAvailable);
                    method.LoadConstant((long)0);
                    method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));
                }

                // if tracing mode is off, we update the pc only at the end of segment and in jumps
                if (!bakeInTracerCalls)
                {
                    if (i == segmentMetadata.Segment.Length - 1 || op.IsTerminating)
                    {
                        method.LoadConstant(op.ProgramCounter + op.Metadata.AdditionalBytes);
                        method.StoreLocal(locals.programCounter);
                    }
                }
                else
                {
                    // otherwise we update the pc after each instruction
                    method.LoadConstant(op.ProgramCounter + op.Metadata.AdditionalBytes);
                    method.StoreLocal(locals.programCounter);
                }

                // if tracing is off, we check the locals.stackHeadRef requirement of the full jumpless segment at once
                if (!bakeInTracerCalls)
                {
                    // we check if locals.stackHeadRef underflow can occur
                    if (currentSegment.RequiredStack != 0)
                    {
                        method.LoadLocal(locals.stackHeadIdx);
                        method.LoadConstant(currentSegment.RequiredStack);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackUnderflow));
                    }

                    // we check if locals.stackHeadRef overflow can occur
                    if (currentSegment.MaxStack != 0)
                    {
                        method.LoadLocal(locals.stackHeadIdx);
                        method.LoadConstant(currentSegment.MaxStack);
                        method.Add();
                        method.LoadConstant(EvmStack.MaxStackSize);
                        method.BranchIfGreaterOrEqual(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackOverflow));
                    }
                }
                else
                {
                    // otherwise we check the locals.stackHeadRef requirement of each instruction
                    method.LoadLocal(locals.stackHeadIdx);
                    method.LoadConstant(op.Metadata.StackBehaviorPop);
                    method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackUnderflow));

                    int delta = op.Metadata.StackBehaviorPush - op.Metadata.StackBehaviorPop;
                    method.LoadLocal(locals.stackHeadIdx);
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
                            envLoader.LoadResult(method, locals);
                            method.LoadConstant(true);
                            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldStop)));
                            method.FakeBranch(ret);
                        }
                        break;
                    case Instruction.CHAINID:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadChainId(method, locals);
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

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
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
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);

                            if (bakeInTracerCalls)
                            {
                                UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, op);
                                EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                            }
                            else
                            {
                                UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, i, currentSegment);
                            }
                            method.FakeBranch(jumpTable);
                        }
                        break;
                    case Instruction.JUMPI:
                        {// consume the jump condition
                            Label noJump = method.DefineLabel();
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.EmitIsZeroCheck();
                            // if the jump condition is false, we do not jump
                            method.BranchIfTrue(noJump);

                            // we jump into the jump table

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);

                            if (bakeInTracerCalls)
                            {
                                UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, op);
                                EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                            }
                            else
                            {
                                UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, i, currentSegment);
                            }
                            method.Branch(jumpTable);

                            method.MarkLabel(noJump);
                        }
                        break;
                    case Instruction.PUSH0:
                        {
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
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
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
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
                        {// we load the locals.stackHeadRef
                            if (contractMetadata.EmbeddedData[op.Arguments.Value].IsZero())
                            {
                                method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            }
                            else
                            {
                                method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                                envLoader.LoadImmediatesData(method, locals);
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
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Add();
                            method.StoreLocal(locals.uint64A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
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
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushItemA);

                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushNegItemB);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.BranchIfLess(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Subtract();
                            method.StoreLocal(locals.uint64A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushItemA);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushNegItemB);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.ToNegative);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
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
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(push0Zero);

                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(endofOpcode);

                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(endofOpcode);

                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(pushItemA);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Multiply();
                            method.StoreLocal(locals.uint64A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.Branch(endofOpcode);

                            method.MarkLabel(push0Zero);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushItemA);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.wordRef256A);
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

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfTrue(pushZeroLabel);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallBackToOldBehavior);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Remainder();
                            method.StoreLocal(locals.uint64A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.SMOD:
                        {
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            // if b is 1 or 0 result is always 0
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Mod), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.DIV:
                        {
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label pushZeroLabel = method.DefineLabel();
                            Label pushALabel = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            // if a or b are 0 result is directly 0
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);
                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);

                            // if b is 1 result is by default a
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(pushALabel);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushALabel);
                            method.LoadLocal(locals.wordRef256B);
                            method.LoadLocal(locals.wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);

                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.SDIV:
                        {
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label pushZeroLabel = method.DefineLabel();
                            Label pushALabel = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            // if b is 0 or a is 0 then the result is 0
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);
                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);

                            // if b is 1 in all cases the result is a
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(pushALabel);

                            // if b is -1 and a is 2^255 then the result is 2^255
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsMinusOneCheck();
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsP255Check();
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.Branch(endofOpcode);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushALabel);
                            method.LoadLocal(locals.wordRef256B);
                            method.LoadLocal(locals.wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Divide), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);

                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.ADDMOD:
                        {
                            Label push0Zero = method.DefineLabel();
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.StoreLocal(locals.wordRef256C);

                            // if c is 1 or 0 result is 0
                            method.LoadLocal(locals.wordRef256C);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitTrinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.AddMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B, locals.uint256C);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.MULMOD:
                        {
                            Label push0Zero = method.DefineLabel();
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.StoreLocal(locals.wordRef256C);

                            // since (a * b) % c 
                            // if a or b are 0 then the result is 0
                            // if c is 0 or 1 then the result is 0
                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256C);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);

                            // since (a * b) % c == (a % c * b % c) % c
                            // if a or b are equal to c, then the result is 0
                            method.LoadLocal(locals.wordRef256A);
                            method.LoadLocal(locals.wordRef256C);
                            method.Call(Word.AreEqual);
                            method.BranchIfTrue(push0Zero);
                            method.LoadLocal(locals.wordRef256B);
                            method.LoadLocal(locals.wordRef256C);
                            method.Call(Word.AreEqual);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.MarkLabel(push0Zero);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitTrinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.MultiplyMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B, locals.uint256C);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.SHL:
                        EmitShiftUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), isLeft: true, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        break;
                    case Instruction.SHR:
                        EmitShiftUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), isLeft: false, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        break;
                    case Instruction.SAR:
                        EmitShiftInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), evmExceptionLabels, locals.uint256A, locals.uint256B);
                        break;
                    case Instruction.AND:
                        EmitBitwiseUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseAnd), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                        break;
                    case Instruction.OR:
                        EmitBitwiseUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseOr), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                        break;
                    case Instruction.XOR:
                        EmitBitwiseUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Vector256).GetMethod(nameof(Vector256.Xor), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                        break;
                    case Instruction.EXP:
                        {
                            Label powerIsZero = method.DefineLabel();
                            Label baseIsOneOrZero = method.DefineLabel();
                            Label endOfExpImpl = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Duplicate();
                            method.Call(Word.LeadingZeroProp);
                            method.StoreLocal(locals.uint64A);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.LoadLocalAddress(locals.uint256B);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(powerIsZero);

                            // load spec
                            method.LoadLocal(locals.gasAvailable);
                            envLoader.LoadSpec(method, locals);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExpByteCost)));
                            method.LoadConstant((long)32);
                            method.LoadLocal(locals.uint64A);
                            method.Subtract();
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadLocalAddress(locals.uint256A);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZeroOrOne)).GetMethod!);
                            method.BranchIfTrue(baseIsOneOrZero);

                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.uint256R);
                            method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint256R);
                            method.Call(Word.SetUInt256);

                            method.Branch(endOfExpImpl);

                            method.MarkLabel(powerIsZero);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadConstant(1);
                            method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                            method.Branch(endOfExpImpl);

                            method.MarkLabel(baseIsOneOrZero);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint256A);
                            method.Call(Word.SetUInt256);
                            method.Branch(endOfExpImpl);

                            method.MarkLabel(endOfExpImpl);
                        }
                        break;
                    case Instruction.LT:
                        {
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.CompareLessThan();
                            method.StoreLocal(locals.byte8B);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.byte8B);
                            method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitComparaisonUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        }

                        break;
                    case Instruction.GT:
                        {
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.CompareGreaterThan();
                            method.StoreLocal(locals.byte8B);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.byte8B);
                            method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitComparaisonUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        }
                        break;
                    case Instruction.SLT:
                        EmitComparaisonInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), false, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        break;
                    case Instruction.SGT:
                        EmitComparaisonInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), true, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        break;
                    case Instruction.EQ:
                        {
                            MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
                            MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo operationUnegenerified = typeof(Vector256).GetMethod(nameof(Vector256.EqualsAll), BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(byte));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(refWordToRefByteMethod);
                            method.Call(readVector256Method);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(refWordToRefByteMethod);
                            method.Call(readVector256Method);

                            method.Call(operationUnegenerified);
                            method.StoreLocal(locals.lbool);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.lbool);
                            method.Convert<uint>();
                            method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.ISZERO:
                        {// we load the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Duplicate();
                            method.Duplicate();
                            method.EmitIsZeroCheck();
                            method.StoreLocal(locals.lbool);
                            method.Call(Word.SetToZero);
                            method.LoadLocal(locals.lbool);
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
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], count);
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

                            method.LoadLocalAddress(locals.uint256R);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], count + 1);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], count + 1);
                            method.LoadLocalAddress(locals.uint256R);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                        }
                        break;
                    case Instruction.CODESIZE:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadConstant(contractMetadata.TargetCodeInfo.MachineCode.Length);
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.PC:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadConstant(op.ProgramCounter);
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.COINBASE:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasBeneficiary), false, out _));
                            method.Call(Word.SetAddress);
                        }
                        break;
                    case Instruction.TIMESTAMP:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Timestamp), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.NUMBER:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Number), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.GASLIMIT:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasLimit), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.CALLER:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadEnvByRef(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Caller)));
                            method.Call(Word.SetAddress);
                        }
                        break;
                    case Instruction.ADDRESS:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadEnvByRef(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.Call(Word.SetAddress);
                        }
                        break;
                    case Instruction.ORIGIN:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadTxContext(method, locals);
                            method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.Origin), false, out _));
                            method.Call(Word.SetAddress);
                        }
                        break;
                    case Instruction.CALLVALUE:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadEnvByRef(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    case Instruction.GASPRICE:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadTxContext(method, locals);
                            method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.GasPrice), false, out _));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    case Instruction.CALLDATACOPY:
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256C);

                            method.LoadLocal(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256C);
                            method.LoadLocalAddress(locals.lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Memory);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadCalldataRef(method, locals);
                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocal(locals.uint256C);
                            method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                            method.Convert<int>();
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.StoreLocal(locals.localZeroPaddedSpan);

                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.localZeroPaddedSpan);
                            method.CallVirtual(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.CALLDATALOAD:
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            envLoader.LoadCalldataRef(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadConstant(Word.Size);
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.Call(Word.SetZeroPaddedSpan);
                        }
                        break;
                    case Instruction.CALLDATASIZE:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadCalldata(method, locals);
                            method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.MSIZE:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);

                            envLoader.LoadMemory(method, locals);
                            method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.MSTORE:
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadConstant(Word.Size);
                            method.Call(ConvertionExplicit<UInt256, int>());
                            method.StoreLocal(locals.uint256C);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetMutableSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveWord)));
                        }
                        break;
                    case Instruction.MSTORE8:
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.CallGetter(Word.GetByte0, BitConverter.IsLittleEndian);
                            method.StoreLocal(locals.byte8A);

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadConstant(1);
                            method.Call(ConvertionExplicit<UInt256, int>());
                            method.StoreLocal(locals.uint256C);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocal(locals.byte8A);

                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveByte)));
                        }
                        break;
                    case Instruction.MLOAD:
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadFieldAddress(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BigInt32)));
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType()]));
                            method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                            method.StoreLocal(locals.localReadonOnlySpan);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.localReadonOnlySpan);
                            method.Call(Word.SetReadOnlySpan);
                        }
                        break;
                    case Instruction.MCOPY:
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256C);

                            method.LoadLocal(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256C);
                            method.LoadLocalAddress(locals.lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.VeryLow);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256B);
                            method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Max)));
                            method.StoreLocal(locals.uint256R);
                            method.LoadLocalAddress(locals.uint256R);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));
                        }
                        break;
                    case Instruction.KECCAK256:
                        {
                            MethodInfo refWordToRefValueHashMethod = UnsafeEmit.GetAsMethodInfo<Word, ValueHash256>();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.LoadLocal(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Sha3Word);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256B);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256B);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                            method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(refWordToRefValueHashMethod);
                            method.Call(typeof(KeccakCache).GetMethod(nameof(KeccakCache.ComputeTo), [typeof(ReadOnlySpan<byte>), typeof(ValueHash256).MakeByRefType()]));
                        }
                        break;
                    case Instruction.BYTE:
                        {// load a
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Duplicate();
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.StoreLocal(locals.uint32A);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetReadOnlySpan);
                            method.StoreLocal(locals.localReadonOnlySpan);

                            Label pushZeroLabel = method.DefineLabel();
                            Label endOfInstructionImpl = method.DefineLabel();
                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint16);
                            method.BranchIfFalse(pushZeroLabel);
                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
                            method.LoadConstant(Word.Size);
                            method.BranchIfGreaterOrEqual(pushZeroLabel);
                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
                            method.LoadConstant(0);
                            method.BranchIfLess(pushZeroLabel);

                            method.LoadLocalAddress(locals.localReadonOnlySpan);
                            method.LoadLocal(locals.uint32A);
                            method.Call(typeof(ReadOnlySpan<byte>).GetMethod("get_Item"));
                            method.LoadIndirect<byte>();
                            method.Convert<uint>();
                            method.StoreLocal(locals.uint32A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint32A);
                            method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                            method.Branch(endOfInstructionImpl);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.MarkLabel(endOfInstructionImpl);
                        }
                        break;
                    case Instruction.CODECOPY:
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256C);

                            method.LoadLocal(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256C);
                            method.LoadLocalAddress(locals.lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Memory);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMachineCode(method, locals);
                            method.StoreLocal(locals.localReadOnlyMemory);

                            method.LoadLocal(locals.localReadOnlyMemory);
                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.uint256C);
                            method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                            method.Convert<int>();
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.StoreLocal(locals.localZeroPaddedSpan);

                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.localZeroPaddedSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.GAS:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadLocal(locals.gasAvailable);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.RETURNDATASIZE:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadResult(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.RETURNDATACOPY:
                        {
                            Label endOfOpcode = method.DefineLabel();
                            using Local tempResult = method.DeclareLocal(typeof(UInt256));


                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256C);

                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.uint256C);
                            method.LoadLocalAddress(tempResult);
                            method.Call(typeof(UInt256).GetMethod(nameof(UInt256.AddOverflow)));
                            method.LoadLocalAddress(tempResult);
                            envLoader.LoadResult(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            method.Call(typeof(ReadOnlyMemory<byte>).GetProperty(nameof(ReadOnlyMemory<byte>.Length)).GetMethod!);
                            method.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                            method.Or();
                            method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.AccessViolation));

                            method.LoadLocal(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256C);
                            method.LoadLocalAddress(locals.lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Memory);
                            method.Multiply();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            // Note : check if c + b > returnData.Size

                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadResult(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            method.LoadObject(typeof(ReadOnlyMemory<byte>));
                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(MethodInfo<UInt256>("op_Explicit", typeof(Int32), new[] { typeof(UInt256).MakeByRefType() }));
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.StoreLocal(locals.localZeroPaddedSpan);

                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.localZeroPaddedSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.RETURN or Instruction.REVERT:
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256B);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadResultByRef(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256B);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Load), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                            method.StoreObject<ReadOnlyMemory<byte>>();

                            envLoader.LoadResult(method, locals);
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
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.BaseFeePerGas), false, out _));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    case Instruction.BLOBBASEFEE:
                        {
                            using Local uint256Nullable = method.DeclareLocal(typeof(UInt256?));
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals);
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
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);

                            envLoader.LoadBlockContext(method, locals);
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

                            envLoader.LoadTxContext(method, locals);
                            method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes), false, out _));
                            method.StoreLocal(byteMatrix);

                            method.LoadLocal(byteMatrix);
                            method.LoadNull();
                            method.BranchIfEqual(blobVersionedHashNotFound);

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocal(byteMatrix);
                            method.Call(GetPropertyInfo(typeof(byte[][]), nameof(Array.Length), false, out _));
                            method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                            method.BranchIfFalse(indexTooLarge);

                            method.LoadLocal(byteMatrix);
                            method.LoadLocal(locals.uint256A);
                            method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                            method.Convert<int>();
                            method.LoadElement<Byte[]>();
                            method.StoreLocal(locals.localArray);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.localArray);
                            method.Call(Word.SetArray);
                            method.Branch(endOfOpcode);

                            method.MarkLabel(blobVersionedHashNotFound);
                            method.MarkLabel(indexTooLarge);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.BLOCKHASH:
                        {
                            Label blockHashReturnedNull = method.DefineLabel();
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.LoadLocalAddress(locals.uint256A);
                            method.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
                            method.StoreLocal(locals.int64A);

                            envLoader.LoadBlockhashProvider(method, locals);
                            envLoader.LoadBlockContext(method, locals);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.LoadLocalAddress(locals.int64A);
                            method.CallVirtual(typeof(IBlockhashProvider).GetMethod(nameof(IBlockhashProvider.GetBlockhash), [typeof(BlockHeader), typeof(long).MakeByRefType()]));
                            method.Duplicate();
                            method.StoreLocal(locals.hash256);
                            method.LoadNull();
                            method.BranchIfEqual(blockHashReturnedNull);

                            // not equal
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.hash256);
                            method.Call(GetPropertyInfo(typeof(Hash256), nameof(Hash256.Bytes), false, out _));
                            method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                            method.Call(Word.SetReadOnlySpan);
                            method.Branch(endOfOpcode);
                            // equal to null

                            method.MarkLabel(blockHashReturnedNull);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.SIGNEXTEND:
                        {
                            Label signIsNegative = method.DefineLabel();
                            Label endOfOpcodeHandling = method.DefineLabel();
                            Label argumentGt32 = method.DefineLabel();
                            using Local wordSpan = method.DeclareLocal(typeof(Span<byte>));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Duplicate();
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.StoreLocal(locals.uint32A);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadConstant(32);
                            method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                            method.BranchIfFalse(argumentGt32);

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetMutableSpan);
                            method.StoreLocal(wordSpan);

                            method.LoadConstant((uint)31);
                            method.LoadLocal(locals.uint32A);
                            method.Subtract();
                            method.StoreLocal(locals.uint32A);

                            method.LoadItemFromSpan<ExecuteSegment, byte>(wordSpan, locals.uint32A);
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
                            method.LoadLocal(locals.uint32A);
                            method.EmitAsSpan();
                            method.StoreLocal(locals.localSpan);

                            method.LoadLocalAddress(locals.localSpan);
                            method.LoadLocalAddress(wordSpan);
                            method.LoadConstant(0);
                            method.LoadLocal(locals.uint32A);
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

                            envLoader.LoadVmStateByRef(method, locals);
                            method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
                            method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StaticCallViolation));

                            EmitLogMethod(method, envLoader, locals, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), topicsCount, evmExceptionLabels, locals.uint256A, locals.uint256B, locals.int64A, locals.gasAvailable, locals.hash256, locals.localReadOnlyMemory);
                        }
                        break;
                    case Instruction.TSTORE:
                        {
                            envLoader.LoadVmStateByRef(method, locals);
                            method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
                            method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StaticCallViolation));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetArray);
                            method.StoreLocal(locals.localArray);

                            envLoader.LoadEnvByRef(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.LoadLocalAddress(locals.uint256A);
                            method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                            method.StoreLocal(locals.storageCell);

                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocalAddress(locals.storageCell);
                            method.LoadLocal(locals.localArray);
                            method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.SetTransientState), [typeof(StorageCell).MakeByRefType(), typeof(byte[])]));
                        }
                        break;
                    case Instruction.TLOAD:
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            envLoader.LoadEnvByRef(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.LoadLocalAddress(locals.uint256A);
                            method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                            method.StoreLocal(locals.storageCell);

                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocalAddress(locals.storageCell);
                            method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.GetTransientState), [typeof(StorageCell).MakeByRefType()]));
                            method.StoreLocal(locals.localReadonOnlySpan);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.localReadonOnlySpan);
                            method.Call(Word.SetReadOnlySpan);
                        }
                        break;
                    case Instruction.SSTORE:
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetReadOnlySpan);
                            method.StoreLocal(locals.localReadonOnlySpan);

                            envLoader.LoadVmStateByRef(method, locals);
                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.localReadonOnlySpan);
                            envLoader.LoadSpec(method, locals);
                            envLoader.LoadTxTracer(method, locals);

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
                                envLoader.LoadTxTracer(method, locals);
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
                            method.StoreLocal(locals.uint32A);
                            method.LoadConstant((int)EvmExceptionType.None);
                            method.BranchIfEqual(endOfOpcode);

                            envLoader.LoadResult(method, locals);
                            method.LoadLocal(locals.uint32A);
                            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

                            envLoader.LoadGasAvailable(method, locals);
                            method.LoadLocal(locals.gasAvailable);
                            method.StoreIndirect<long>();
                            method.Branch(exit);

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.SLOAD:
                        {
                            method.LoadLocal(locals.gasAvailable);
                            envLoader.LoadSpec(method, locals);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetSLoadCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            envLoader.LoadEnvByRef(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.LoadLocalAddress(locals.uint256A);
                            method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                            method.StoreLocal(locals.storageCell);

                            method.LoadLocalAddress(locals.gasAvailable);
                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.storageCell);
                            method.LoadConstant((int)VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.StorageAccessType.SLOAD);
                            envLoader.LoadSpec(method, locals);
                            envLoader.LoadTxTracer(method, locals);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeStorageAccessGas), BindingFlags.Static | BindingFlags.NonPublic));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocalAddress(locals.storageCell);
                            method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.Get), [typeof(StorageCell).MakeByRefType()]));
                            method.StoreLocal(locals.localReadonOnlySpan);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.localReadonOnlySpan);
                            method.Call(Word.SetReadOnlySpan);
                        }
                        break;
                    case Instruction.EXTCODESIZE:
                        {
                            method.LoadLocal(locals.gasAvailable);
                            envLoader.LoadSpec(method, locals);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(locals.address);

                            method.LoadLocalAddress(locals.gasAvailable);
                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocal(locals.address);
                            method.LoadConstant(true);
                            envLoader.LoadWorldState(method, locals);
                            envLoader.LoadSpec(method, locals);
                            envLoader.LoadTxTracer(method, locals);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            envLoader.LoadCodeInfoRepository(method, locals);
                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocal(locals.address);
                            envLoader.LoadSpec(method, locals);
                            method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                            method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));
                            method.StoreLocal(locals.localReadOnlyMemory);
                            method.LoadLocalAddress(locals.localReadOnlyMemory);
                            method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));

                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        }
                        break;
                    case Instruction.EXTCODECOPY:
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 4);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256C);

                            method.LoadLocal(locals.gasAvailable);
                            envLoader.LoadSpec(method, locals);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                            method.LoadLocalAddress(locals.uint256C);
                            method.LoadLocalAddress(locals.lbool);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                            method.LoadConstant(GasCostOf.Memory);
                            method.Multiply();
                            method.Add();
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(locals.address);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.LoadLocalAddress(locals.gasAvailable);
                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocal(locals.address);
                            method.LoadConstant(true);
                            envLoader.LoadWorldState(method, locals);
                            envLoader.LoadSpec(method, locals);
                            envLoader.LoadTxTracer(method, locals);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadCodeInfoRepository(method, locals);
                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocal(locals.address);
                            envLoader.LoadSpec(method, locals);
                            method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                            method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));

                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocal(locals.uint256C);
                            method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                            method.Convert<int>();
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.StoreLocal(locals.localZeroPaddedSpan);

                            envLoader.LoadMemory(method, locals);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.localZeroPaddedSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.EXTCODEHASH:
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.LoadLocal(locals.gasAvailable);
                            envLoader.LoadSpec(method, locals);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeHashCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(locals.address);

                            method.LoadLocalAddress(locals.gasAvailable);
                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocal(locals.address);
                            method.LoadConstant(true);
                            envLoader.LoadWorldState(method, locals);
                            envLoader.LoadSpec(method, locals);
                            envLoader.LoadTxTracer(method, locals);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            Label pushZeroLabel = method.DefineLabel();
                            Label pushhashcodeLabel = method.DefineLabel();

                            // account exists
                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocal(locals.address);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.AccountExists)));
                            method.BranchIfFalse(pushZeroLabel);

                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocal(locals.address);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.IsDeadAccount)));
                            method.BranchIfTrue(pushZeroLabel);

                            using Local delegateAddress = method.DeclareLocal<Address>();
                            envLoader.LoadCodeInfoRepository(method, locals);
                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocal(locals.address);
                            method.LoadLocalAddress(delegateAddress);
                            method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.TryGetDelegation), [typeof(IWorldState), typeof(Address), typeof(Address).MakeByRefType()]));
                            method.BranchIfFalse(pushhashcodeLabel);

                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocal(delegateAddress);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.AccountExists)));
                            method.BranchIfFalse(pushZeroLabel);

                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocal(delegateAddress);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.IsDeadAccount)));
                            method.BranchIfTrue(pushZeroLabel);

                            method.MarkLabel(pushhashcodeLabel);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            envLoader.LoadCodeInfoRepository(method, locals);
                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocal(locals.address);
                            method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.GetExecutableCodeHash), [typeof(IWorldState), typeof(Address)]));
                            method.Call(Word.SetKeccak);
                            method.Branch(endOfOpcode);

                            // Push 0
                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            method.MarkLabel(endOfOpcode);
                        }
                        break;
                    case Instruction.SELFBALANCE:
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadWorldState(method, locals);
                            envLoader.LoadEnvByRef(method, locals);
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                            method.Call(Word.SetUInt256);
                        }
                        break;
                    case Instruction.BALANCE:
                        {
                            method.LoadLocal(locals.gasAvailable);
                            envLoader.LoadSpec(method, locals);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetBalanceCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(locals.address);

                            method.LoadLocalAddress(locals.gasAvailable);
                            envLoader.LoadVmStateByRef(method, locals);
                            method.LoadLocal(locals.address);
                            method.LoadConstant(false);
                            envLoader.LoadWorldState(method, locals);
                            envLoader.LoadSpec(method, locals);
                            envLoader.LoadTxTracer(method, locals);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            envLoader.LoadWorldState(method, locals);
                            method.LoadLocal(locals.address);
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
                    UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, op);
                    EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                }
                else
                {
                    UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, i, currentSegment);
                }
            }

            Label jumpIsLocal = method.DefineLabel();
            Label jumpIsNotLocal = method.DefineLabel();
            Local isEphemeralJump = method.DeclareLocal<bool>();
            Label skipProgramCounterSetting = method.DefineLabel();
            // prepare ILEvmState
            // check if returnState is null
            method.MarkLabel(ret);
            // we get locals.stackHeadRef size
            envLoader.LoadStackHead(method, locals);
            method.LoadLocal(locals.stackHeadIdx);
            method.StoreIndirect<int>();

            // set gas available
            envLoader.LoadGasAvailable(method, locals);
            method.LoadLocal(locals.gasAvailable);
            method.StoreIndirect<long>();

            // set program counter
            method.LoadLocal(isEphemeralJump);
            method.BranchIfTrue(skipProgramCounterSetting);

            envLoader.LoadProgramCounter(method, locals);
            method.LoadLocal(locals.programCounter);
            method.LoadConstant(1);
            method.Add();
            method.StoreIndirect<int>();

            method.MarkLabel(skipProgramCounterSetting);

            // return
            method.MarkLabel(exit);
            method.Return();

            // isContinuation
            method.MarkLabel(isContinuation);
            method.LoadLocal(locals.programCounter);
            method.StoreLocal(locals.jmpDestination);
            envLoader.LoadResult(method, locals);
            method.LoadConstant(false);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldJump)));
            method.Branch(jumpIsLocal);

            // jump table
            method.MarkLabel(jumpTable);
            method.LoadLocal(locals.wordRef256A);

            method.Duplicate();
            method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
            method.StoreLocal(locals.jmpDestination);

            method.Call(Word.GetIsUint16);
            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

            // if (jumpDest <= maxJump)
            method.LoadLocal(locals.jmpDestination);
            method.LoadConstant(segmentMetadata.Boundaries.End.Value);
            method.BranchIfGreater(jumpIsNotLocal);

            // if (jumpDest >= minJump)
            method.LoadLocal(locals.jmpDestination);
            method.LoadConstant(segmentMetadata.Boundaries.Start.Value);
            method.BranchIfLess(jumpIsNotLocal);

            method.MarkLabel(jumpIsLocal);
            if (jumpDestinations.Count < 64)
            {
                foreach (KeyValuePair<int, Label> jumpdest in jumpDestinations)
                {
                    method.LoadLocal(locals.jmpDestination);
                    method.LoadConstant(jumpdest.Key);
                    method.BranchIfEqual(jumpdest.Value);
                }
                method.Branch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));
            }
            else
            {
                method.FindCorrectBranchAndJump(locals.jmpDestination, jumpDestinations, evmExceptionLabels);
            }

            method.MarkLabel(jumpIsNotLocal);
            envLoader.LoadResult(method, locals);
            method.LoadConstant(true);
            method.Duplicate();
            method.StoreLocal(isEphemeralJump);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldJump)));

            envLoader.LoadProgramCounter(method, locals);
            method.LoadLocal(locals.jmpDestination);
            method.StoreIndirect<int>();
            method.Branch(ret);


            foreach (KeyValuePair<EvmExceptionType, Label> kvp in evmExceptionLabels)
            {
                method.MarkLabel(kvp.Value);
                if (bakeInTracerCalls)
                {
                    EmitCallToErrorTrace(method, locals.gasAvailable, kvp, envLoader, locals);
                }

                envLoader.LoadResult(method, locals);
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

        private static void EmitCallToErrorTrace(Emit<ExecuteSegment> method, Local gasAvailable, KeyValuePair<EvmExceptionType, Label> kvp, PartialAotEnvLoader envLoader, Locals<ExecuteSegment> locals)
        {
            Label skipTracing = method.DefineLabel();
            envLoader.LoadTxTracer(method, locals);
            method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
            method.BranchIfFalse(skipTracing);

            envLoader.LoadTxTracer(method, locals);
            method.LoadLocal(gasAvailable);
            method.LoadConstant((int)kvp.Key);
            method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.EndInstructionTraceError), BindingFlags.Static | BindingFlags.NonPublic));

            method.MarkLabel(skipTracing);
        }
        private static void EmitCallToEndInstructionTrace(Emit<ExecuteSegment> method, Local gasAvailable, PartialAotEnvLoader envLoader, Locals<ExecuteSegment> locals)
        {
            Label skipTracing = method.DefineLabel();
            envLoader.LoadTxTracer(method, locals);
            method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
            method.BranchIfFalse(skipTracing);

            envLoader.LoadTxTracer(method, locals);
            method.LoadLocal(gasAvailable);
            envLoader.LoadMemory(method, locals);
            method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
            method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.EndInstructionTrace), BindingFlags.Static | BindingFlags.NonPublic));

            method.MarkLabel(skipTracing);
        }
        private static void EmitCallToStartInstructionTrace(Emit<ExecuteSegment> method, Local gasAvailable, Local head, OpcodeInfo op, PartialAotEnvLoader envLoader, Locals<ExecuteSegment> locals)
        {
            Label skipTracing = method.DefineLabel();
            envLoader.LoadTxTracer(method, locals);
            method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
            method.BranchIfFalse(skipTracing);

            envLoader.LoadTxTracer(method, locals);
            method.LoadConstant((int)op.Operation);
            envLoader.LoadVmStateByRef(method, locals);
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
            // we the two uint256 from the locals.stackHeadRef
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
            // we the two uint256 from the locals.stackHeadRef
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

            // we the two uint256 from the locals.stackHeadRef
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
            Emit<T> il, EnvLoader<T> envLoader, Locals<T> locals,
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
            envLoader.LoadVmState(il, locals);
            il.LoadIndirect(typeof(EvmState));

            il.LoadLocalAddress(gasAvailable);
            il.LoadLocalAddress(uint256Position); // position
            il.LoadLocalAddress(uint256Length); // length
            il.Call(
                typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(
                    nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)
                )
            );
            il.BranchIfFalse(il.AddExceptionLabel(exceptions, EvmExceptionType.OutOfGas));

            // update locals.gasAvailable
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
            il.StoreLocal(gasAvailable); // locals.gasAvailable -= gasCost
            il.LoadConstant((ulong)0);
            il.BranchIfLess(il.AddExceptionLabel(exceptions, EvmExceptionType.OutOfGas));

            envLoader.LoadEnv(il, locals);
            il.LoadField(
                GetFieldInfo(
                    typeof(ExecutionEnvironment),
                    nameof(ExecutionEnvironment.ExecutingAccount)
                )
            );

            envLoader.LoadMemory(il, locals);
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

            envLoader.LoadVmState(il, locals);
            il.LoadIndirect(typeof(EvmState));

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
