// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Logging;
using System.Reflection.Metadata;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using Label = Sigil.Label;
using DotNetty.Common.Utilities;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes.FullAOT;
internal static class FullAOT
{
    public delegate bool MoveNextDelegate(ulong chainId, ref long gasAvailable, ref int programCounter, ref int stackHead, ref Word stackHeadRef, ref ReadOnlyMemory<byte> returnDataBuffer); // it returns true if current staet is HALTED or FINISHED and Sets Current.CallResult in case of CALL or CREATE

    
    public static Type CompileContract(ContractMetadata contractMetadata, IVMConfig vmConfig)
    {
        // need to use PersistedAssemblyBuilder to avoid the issue with the same assembly name
        // var assemblyBuilder = new PersistedAssemblyBuilder(new AssemblyName("Nethermind.Evm.Precompiled.Live"), typeof(object).Assembly);
        // temporary solution to avoid the issue with the same assembly name
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Nethermind.Evm.Precompiled.Live"), AssemblyBuilderAccess.Run);

        ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("ContractsModule");
        TypeBuilder contractStructBuilder = moduleBuilder.DefineType($"{contractMetadata.TargetCodeInfo.Address}", TypeAttributes.Public |
            TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType), [typeof(IPrecompiledContract)]);

        FullAotEnvLoader envLoader = new FullAotEnvLoader(contractStructBuilder, contractMetadata);

        EmitMoveNext(contractStructBuilder, contractMetadata, envLoader, vmConfig);

        return contractStructBuilder.CreateType();
    }

    public static void EmitMoveNext(TypeBuilder contractBuilder, ContractMetadata contractMetadata, FullAotEnvLoader envLoader, IVMConfig config)
    {
        var method = Emit<MoveNextDelegate>.BuildInstanceMethod(
            contractBuilder,
            "MoveNext",
            MethodAttributes.Public | MethodAttributes.Virtual);

        var locals = new Locals<MoveNextDelegate>(method);
        var opEmitter = new FullAotOpcodeEmitter<MoveNextDelegate>();


        Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();

        Label exit = method.DefineLabel(); // the label just before return
        Label jumpTable = method.DefineLabel(); // jump table
        Label isContinuation = method.DefineLabel(); // jump table
        Label ret = method.DefineLabel();

        Dictionary<int, Label> jumpDestinations = new();

        /*envLoader.LoadResult(method, locals, false);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ShouldStop)));
        method.BranchIfTrue(exit);

        envLoader.LoadResult(method, locals, false);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ShouldFail)));
        method.BranchIfTrue(exit);

        envLoader.LoadResult(method, locals, false);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ShouldReturn)));
        method.BranchIfTrue(exit);

        envLoader.LoadResult(method, locals, false);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ShouldRevert)));
        method.BranchIfTrue(exit);*/

        envLoader.LoadStackHead(method, locals, false);
        method.StoreLocal(locals.stackHeadIdx);

        envLoader.LoadCurrStackHead(method, locals, true);
        method.StoreLocal(locals.stackHeadRef);

        // set gas to local
        envLoader.LoadGasAvailable(method, locals, false);
        method.StoreLocal(locals.gasAvailable);

        // set pc to local
        envLoader.LoadProgramCounter(method, locals, false);
        method.StoreLocal(locals.programCounter);

        envLoader.LoadResult(method, locals, false);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ShouldContinue)));
        method.BranchIfTrue(isContinuation);

        foreach (var segmentMetadata in contractMetadata.Segments)
        {
            method.MarkLabel(jumpDestinations[segmentMetadata.Boundaries.Start.Value] = method.DefineLabel());

            if (config.BakeInTracingInAotModes)
                segmentMetadata.StackOffsets.Fill(0);

            SubSegmentMetadata currentSegment = default;


            // Idea(Ayman) : implement every opcode as a method, and then inline the IL of the method in the main method
            for (var i = 0; i < segmentMetadata.Segment.Length; i++)
            {
                OpcodeInfo op = segmentMetadata.Segment[i];

                if (!config.BakeInTracingInAotModes && segmentMetadata.SubSegments.ContainsKey(i))
                    currentSegment = segmentMetadata.SubSegments[i];
                // if tracing mode is off, 
                if (!config.BakeInTracingInAotModes)
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

                if (config.BakeInTracingInAotModes)
                    EmitCallToStartInstructionTrace(method, locals.gasAvailable, locals.stackHeadIdx, op, envLoader, locals);

                // check if opcode is activated in current spec, we skip this check for opcodes that are always enabled
                if (op.Operation.RequiresAvailabilityCheck())
                {
                    envLoader.LoadSpec(method, locals, false);
                    method.LoadConstant((byte)op.Operation);
                    method.Call(typeof(InstructionExtensions).GetMethod(nameof(InstructionExtensions.IsEnabled)));
                    method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                }

                // if tracing mode is off, we consume the static gas only at the start of segment
                if (!config.BakeInTracingInAotModes)
                {
                    if (currentSegment.StaticGasSubSegmentes.TryGetValue(i, out var gasCost) && gasCost > 0)
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
                if (!config.BakeInTracingInAotModes)
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
                if (!config.BakeInTracingInAotModes)
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

                    var delta = op.Metadata.StackBehaviorPush - op.Metadata.StackBehaviorPop;
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

                            if (config.BakeInTracingInAotModes)
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

                            if (config.BakeInTracingInAotModes)
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
                        opEmitter.Emit(config, contractMetadata, segmentMetadata, i, op, method, locals, envLoader, evmExceptionLabels, (ret, exit));
                        break;

                }

                if (config.BakeInTracingInAotModes)
                {
                    UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, op);
                    EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                }
                else
                {
                    UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, i, currentSegment);
                }
            }

        }

        method.MarkLabel(ret);
        // we get locals.stackHeadRef size
        envLoader.LoadStackHead(method, locals, true);
        method.LoadLocal(locals.stackHeadIdx);
        method.StoreIndirect<int>();

        // set gas available
        envLoader.LoadGasAvailable(method, locals, true);
        method.LoadLocal(locals.gasAvailable);
        method.StoreIndirect<long>();

        // set program counter
        envLoader.LoadProgramCounter(method, locals, true);
        method.LoadLocal(locals.programCounter);
        method.LoadConstant(1);
        method.Add();
        method.StoreIndirect<int>();

        // return
        Label returnTrue = method.DefineLabel();
        Label returnFalse = method.DefineLabel();

        method.MarkLabel(exit);

        envLoader.LoadResult(method, locals, true);
        method.Call(typeof(ILChunkExecutionState).GetProperty(nameof(ILChunkExecutionState.ShouldAbort)).GetMethod);
        method.BranchIfTrue(returnFalse);

        envLoader.LoadResult(method, locals, true);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ShouldContinue)));
        method.Return();

        method.MarkLabel(returnFalse);
        method.LoadConstant(false);
        method.Return();

        // isContinuation
        Label skipJumpValidation = method.DefineLabel();
        method.MarkLabel(isContinuation);
        method.LoadLocal(locals.programCounter);
        method.StoreLocal(locals.jmpDestination);
        envLoader.LoadResult(method, locals, true);
        method.LoadConstant(false);
        method.StoreField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ShouldContinue)));
        method.Branch(skipJumpValidation);

        method.MarkLabel(jumpTable);
        method.LoadLocal(locals.wordRef256A);

        method.Duplicate();
        method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
        method.StoreLocal(locals.jmpDestination);

        method.Call(Word.GetIsUint16);
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        method.MarkLabel(skipJumpValidation);
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

        foreach (KeyValuePair<EvmExceptionType, Label> kvp in evmExceptionLabels)
        {
            method.MarkLabel(kvp.Value);
            if (config.BakeInTracingInAotModes)
                EmitCallToErrorTrace(method, locals.gasAvailable, kvp, envLoader, locals);

            envLoader.LoadResult(method, locals, true);
            method.LoadConstant((int)kvp.Key);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
            method.Branch(exit);
        }

        method.CreateMethod();
    }
}

