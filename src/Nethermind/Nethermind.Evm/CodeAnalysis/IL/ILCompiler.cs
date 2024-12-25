// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Config;
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
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
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

            localJumpdests = EmitSegmentBody(method, metadata, segmentIndex, config);
            ExecuteSegment dynEmitedDelegate = method.CreateDelegate();

            return new PrecompiledChunk
            {
                PrecompiledSegment = dynEmitedDelegate,
                Data = metadata.EmbeddedData,
            };
        }

        private static int[] EmitSegmentBody(Emit<ExecuteSegment> method, ContractMetadata contractMetadata, int segmentIndex, IVMConfig config)
        {
            var segmentMetadata = contractMetadata.Segments[segmentIndex];
            var envLoader = new PartialAotEnvLoader();
            var opEmitter = new DefaultOpcodeEmitter<ExecuteSegment>();
            using var locals = new Locals<ExecuteSegment>(method);
            bool bakeInTracerCalls = config.BakeInTracingInPartialAotMode;

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
                    default:
                        opEmitter.Emit(config, contractMetadata, segmentMetadata, i, op, method, locals, envLoader, evmExceptionLabels, ret);
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
