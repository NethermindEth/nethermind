// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using Label = Sigil.Label;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes;
internal static class Precompiler
{
    public static PrecompiledChunk CompileSegment(string segmentName, ContractMetadata metadata, int segmentIndex, IVMConfig config, out int[] localJumpdests)
    {
        // code is optimistic assumes locals.stackHeadRef underflow and locals.stackHeadRef overflow to not occure (WE NEED EOF FOR THIS)
        // Note(Ayman) : remove dependency on ILEVMSTATE and move out all arguments needed to function signature
        var method = Emit<ExecuteSegment>.NewDynamicMethod(segmentName, doVerify: true, strictBranchVerification: true);

        localJumpdests = EmitSegmentBody(method, metadata, segmentIndex, config);
        ExecuteSegment dynEmitedDelegate = method.CreateDelegate();

        return new PrecompiledChunk
        {
            PrecompiledSegment = dynEmitedDelegate,
            Data = metadata.EmbeddedData,
        };
    }

    public static PrecompiledContract CompileContract(string contractName, ContractMetadata metadata, IVMConfig config)
    {
        // code is optimistic assumes locals.stackHeadRef underflow and locals.stackHeadRef overflow to not occure (WE NEED EOF FOR THIS)
        // Note(Ayman) : remove dependency on ILEVMSTATE and move out all arguments needed to function signature
        var method = Emit<PrecompiledContract>.NewDynamicMethod(contractName, doVerify: true, strictBranchVerification: true);

        EmitMoveNext(method, metadata, config);
        PrecompiledContract dynEmitedDelegate = method.CreateDelegate();
        return dynEmitedDelegate;
    }



    public static void EmitMoveNext(Emit<PrecompiledContract> method, ContractMetadata contractMetadata, IVMConfig config)
    {
        var locals = new Locals<PrecompiledContract>(method);
        var opEmitter = new AotOpcodeEmitter<PrecompiledContract>();
        var envLoader = new FullAotEnvLoader();

        Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();

        // set up spec
        envLoader.CacheSpec(method, locals);
        envLoader.CacheBlockContext(method, locals);
        envLoader.CacheTxContext(method, locals);

        ReleaseSpecEmit.DeclareOpcodeValidityCheckVariables(method, contractMetadata, locals);

        Label exit = method.DefineLabel(); // the label just before return
        Label jumpTable = method.DefineLabel(); // jump table
        Label isContinuation = method.DefineLabel(); // jump table
        Label ret = method.DefineLabel();

        Dictionary<int, Label> jumpDestinations = new();

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

        envLoader.CacheSpec(method, locals);
        envLoader.CacheBlockContext(method, locals);
        envLoader.CacheTxContext(method, locals);

        envLoader.LoadResult(method, locals, false);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ContractState)));
        method.LoadConstant((int)ContractState.Halted);
        method.BranchIfEqual(isContinuation);

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

                if (op.Operation is Instruction.JUMPDEST)
                {
                    // mark the jump destination
                    method.MarkLabel(jumpDestinations[op.ProgramCounter] = method.DefineLabel());
                    method.LoadConstant(op.ProgramCounter);
                    method.StoreLocal(locals.programCounter);
                }

                if (!config.BakeInTracingInAotModes)
                {
                    if (segmentMetadata.SubSegments.ContainsKey(i))
                    {
                        currentSegment = segmentMetadata.SubSegments[i];

                        if (currentSegment.RequiresOpcodeCheck)
                        {
                            method.EmitAmortizedOpcodeCheck(currentSegment, locals, envLoader, evmExceptionLabels);
                        }

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

                    if (currentSegment.StaticGasSubSegmentes.TryGetValue(i, out var gasCost) && gasCost > 0)
                    {
                        method.EmitStaticGasCheck(locals.gasAvailable, gasCost, evmExceptionLabels);
                    }

                    if (i == segmentMetadata.Segment.Length - 1 || op.IsTerminating)
                    {
                        method.LoadConstant(op.ProgramCounter + op.Metadata.AdditionalBytes);
                        method.StoreLocal(locals.programCounter);
                    }
                }
                else
                {
                    EmitCallToStartInstructionTrace(method, locals.gasAvailable, locals.stackHeadIdx, op, envLoader, locals);
                    if (op.Operation.RequiresAvailabilityCheck())
                    {
                        envLoader.LoadSpec(method, locals, false);
                        method.LoadConstant((byte)op.Operation);
                        method.Call(typeof(InstructionExtensions).GetMethod(nameof(InstructionExtensions.IsEnabled)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                    }
                    method.EmitStaticGasCheck(locals.gasAvailable, op.Metadata.GasCost, evmExceptionLabels);

                    method.LoadConstant(op.ProgramCounter + op.Metadata.AdditionalBytes);
                    method.StoreLocal(locals.programCounter);

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

                opEmitter.Emit(config, contractMetadata, segmentMetadata, currentSegment, i, op, method, locals, envLoader, evmExceptionLabels, (ret, jumpTable, exit));

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
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ContractState)));
        method.LoadConstant((int)ContractState.Halted);
        method.CompareEqual();
        method.Return();

        // isContinuation
        Label skipJumpValidation = method.DefineLabel();
        method.MarkLabel(isContinuation);

        method.LoadLocal(locals.programCounter);
        method.StoreLocal(locals.jmpDestination);
        envLoader.LoadResult(method, locals, true);
        method.LoadConstant((int)ContractState.Running);
        method.StoreField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ContractState)));

        method.LoadLocal(locals.jmpDestination);
        method.LoadConstant(contractMetadata.TargetCodeInfo.MachineCode.Length);
        method.BranchIfGreaterOrEqual(exit);

        method.Branch(skipJumpValidation);

        method.MarkLabel(jumpTable);
        method.LoadLocal(locals.wordRef256A);
        method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
        method.StoreLocal(locals.jmpDestination);

        method.LoadLocal(locals.wordRef256A);
        method.Call(Word.GetIsUint16);
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        method.LoadLocal(locals.jmpDestination);
        method.LoadConstant(0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        method.LoadLocal(locals.jmpDestination);
        method.LoadConstant(contractMetadata.TargetCodeInfo.MachineCode.Length);
        method.BranchIfGreaterOrEqual(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

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
            method.Duplicate();
            method.LoadConstant((int)kvp.Key);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

            method.LoadConstant((int)ContractState.Failed);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
            method.Branch(exit);
        }

        method.CreateDelegate(OptimizationOptions.All);
    }
    
    private static int[] EmitSegmentBody(Emit<ExecuteSegment> method, ContractMetadata contractMetadata, int segmentIndex, IVMConfig config)
    {
        var segmentMetadata = contractMetadata.Segments[segmentIndex];
        var envLoader = new PartialAotEnvLoader();

        var opEmitter = new AotOpcodeEmitter<ExecuteSegment>();
        using var locals = new Locals<ExecuteSegment>(method);
        var bakeInTracerCalls = config.BakeInTracingInAotModes;

        Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();

        Label exit = method.DefineLabel(); // the label just before return
        Label jumpTable = method.DefineLabel(); // jump table
        Label isContinuation = method.DefineLabel(); // jump table
        Label ret = method.DefineLabel();

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

        ReleaseSpecEmit.DeclareOpcodeValidityCheckVariables(method, contractMetadata, locals);

        // if last ilvmstate was a jump
        envLoader.LoadResult(method, locals, true);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ContractState)));
        method.LoadConstant((int)ContractState.EPHEMERAL_JUMP);
        method.BranchIfEqual(isContinuation);

        Dictionary<int, Label> jumpDestinations = new();

        // Note : Combine all the static analysis into one method
        if (bakeInTracerCalls)
            segmentMetadata.StackOffsets.Fill(0);

        SubSegmentMetadata currentSegment = default;


        // Idea(Ayman) : implement every opcode as a method, and then inline the IL of the method in the main method
        for (var i = 0; i < segmentMetadata.Segment.Length; i++)
        {
            OpcodeInfo op = segmentMetadata.Segment[i];

            if (op.Operation is Instruction.JUMPDEST)
            {
                // mark the jump destination
                method.MarkLabel(jumpDestinations[op.ProgramCounter] = method.DefineLabel());
                method.LoadConstant(op.ProgramCounter);
                method.StoreLocal(locals.programCounter);
            }

            if (!config.BakeInTracingInAotModes)
            {
                if (segmentMetadata.SubSegments.ContainsKey(i))
                {
                    currentSegment = segmentMetadata.SubSegments[i];

                    if (currentSegment.RequiresOpcodeCheck)
                        method.EmitAmortizedOpcodeCheck(currentSegment, locals, envLoader, evmExceptionLabels);

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

                if (currentSegment.StaticGasSubSegmentes.TryGetValue(i, out var gasCost) && gasCost > 0)
                    method.EmitStaticGasCheck(locals.gasAvailable, gasCost, evmExceptionLabels);

                if (i == segmentMetadata.Segment.Length - 1 || op.IsTerminating)
                {
                    method.LoadConstant(op.ProgramCounter + op.Metadata.AdditionalBytes);
                    method.StoreLocal(locals.programCounter);
                }
            }
            else
            {
                EmitCallToStartInstructionTrace(method, locals.gasAvailable, locals.stackHeadIdx, op, envLoader, locals);
                if (op.Operation.RequiresAvailabilityCheck())
                {
                    envLoader.LoadSpec(method, locals, false);
                    method.LoadConstant((byte)op.Operation);
                    method.Call(typeof(InstructionExtensions).GetMethod(nameof(InstructionExtensions.IsEnabled)));
                    method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                }
                method.EmitStaticGasCheck(locals.gasAvailable, op.Metadata.GasCost, evmExceptionLabels);

                method.LoadConstant(op.ProgramCounter + op.Metadata.AdditionalBytes);
                method.StoreLocal(locals.programCounter);

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
            opEmitter.Emit(config, contractMetadata, segmentMetadata, currentSegment, i, op, method, locals, envLoader, evmExceptionLabels, (ret, jumpTable, exit));

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
        envLoader.LoadStackHead(method, locals, true);
        method.LoadLocal(locals.stackHeadIdx);
        method.StoreIndirect<int>();

        // set gas available
        envLoader.LoadGasAvailable(method, locals, true);
        method.LoadLocal(locals.gasAvailable);
        method.StoreIndirect<long>();

        // set program counter
        method.LoadLocal(isEphemeralJump);
        method.BranchIfTrue(skipProgramCounterSetting);

        envLoader.LoadProgramCounter(method, locals, true);
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
        envLoader.LoadResult(method, locals, true);
        method.LoadConstant((int)ContractState.Running);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
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
        envLoader.LoadResult(method, locals, true);
        method.LoadConstant(true);
        method.StoreLocal(isEphemeralJump);
        method.LoadConstant((int)ContractState.EPHEMERAL_JUMP);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

        envLoader.LoadProgramCounter(method, locals, true);
        method.LoadLocal(locals.jmpDestination);
        method.StoreIndirect<int>();
        method.Branch(ret);


        foreach (KeyValuePair<EvmExceptionType, Label> kvp in evmExceptionLabels)
        {
            method.MarkLabel(kvp.Value);
            if (bakeInTracerCalls)
                EmitCallToErrorTrace(method, locals.gasAvailable, kvp, envLoader, locals);

            envLoader.LoadResult(method, locals, true);
            method.Duplicate();
            method.LoadConstant((int)kvp.Key);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

            method.LoadConstant((int)ContractState.Failed);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

            method.Branch(exit);
        }

        return jumpDestinations.Keys.ToArray();
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
