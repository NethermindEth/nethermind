// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Org.BouncyCastle.Asn1.Cms;
using Sigil;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using Label = Sigil.Label;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes;
internal static class Precompiler
{
    public static PrecompiledContract CompileContract(string contractName, CodeInfo codeInfo, ContractCompilerMetadata metadata, IVMConfig config)
    {
        // code is optimistic assumes locals.stackHeadRef underflow and locals.stackHeadRef overflow to not occure (WE NEED EOF FOR THIS)
        // Note(Ayman) : remove dependency on ILEVMSTATE and move out all arguments needed to function signature
        var method = Emit<PrecompiledContract>.NewDynamicMethod(contractName, doVerify: false, strictBranchVerification: true);
        
        EmitMoveNext(method, codeInfo, metadata, config);
        PrecompiledContract dynEmitedDelegate = method.CreateDelegate(OptimizationOptions.All & (~OptimizationOptions.EnableBranchPatching));
        return dynEmitedDelegate;
    }

    public static void EmitMoveNext(Emit<PrecompiledContract> method, CodeInfo codeInfo, ContractCompilerMetadata contractMetadata, IVMConfig config)
    {
        var machineCodeAsSpan = codeInfo.MachineCode.Span;

        var locals = new Locals<PrecompiledContract>(method);
        var envLoader = new FullAotEnvLoader();

        Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();
        Dictionary<int, Label> jumpDestinations = new();
        Dictionary<int, Label> entryPoints = new();

        // set up spec
        envLoader.CacheSpec(method, locals);
        envLoader.CacheBlockContext(method, locals);
        envLoader.CacheTxContext(method, locals);

        ReleaseSpecEmit.DeclareOpcodeValidityCheckVariables(method, contractMetadata, locals);

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

        envLoader.CacheSpec(method, locals);
        envLoader.CacheBlockContext(method, locals);
        envLoader.CacheTxContext(method, locals);

        envLoader.LoadResult(method, locals, false);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ContractState)));
        method.LoadConstant((int)ContractState.Halted);
        method.BranchIfEqual(isContinuation);


        // just hotwire
        bool hasEmittedJump = false;

        if (!config.IsIlEvmAggressiveModeEnabled)
            contractMetadata.StackOffsets.Clear();

        SubSegmentMetadata currentSubsegment = default;
        int endOfSegment = codeInfo.MachineCode.Length;

        // Idea(Ayman) : implement every opcode as a method, and then inline the IL of the method in the main method
        for (var i = 0; i < codeInfo.MachineCode.Length; )
        {
            (Instruction Instruction, OpcodeMetadata Metadata) opcodeInfo = ((Instruction)machineCodeAsSpan[i], OpcodeMetadata.GetMetadata((Instruction)machineCodeAsSpan[i]));

            var ILGenerator = AotOpcodeEmitter<PrecompiledContract>.GetOpcodeILEmitter(opcodeInfo.Instruction);

            if (contractMetadata.SegmentsBoundaries.ContainsKey(i))
            {
                endOfSegment = contractMetadata.SegmentsBoundaries[i];
                method.MarkLabel(entryPoints[i] = method.DefineLabel());
            }

            hasEmittedJump |= opcodeInfo.Instruction.IsJump();

            if (opcodeInfo.Instruction is Instruction.JUMPDEST)
            {
                // mark the jump destination
                method.MarkLabel(jumpDestinations[i] = method.DefineLabel());
            }

            if (config.IsIlEvmAggressiveModeEnabled)
            {
                if (contractMetadata.SubSegments.ContainsKey(i))
                {
                    currentSubsegment = contractMetadata.SubSegments[i];

                    if (!currentSubsegment.IsReachable)
                    {
                        i = currentSubsegment.End + 1;
                        continue;
                    }

                    if (currentSubsegment.IsFailing)
                    {
                        method.FakeBranch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                        i = currentSubsegment.End + 1;
                        continue;
                    }

                    if(currentSubsegment.RequiresStaticEnvCheck)
                    {
                        method.EmitAmortizedStaticEnvCheck(currentSubsegment, locals, envLoader, evmExceptionLabels);
                    }

                    if (currentSubsegment.RequiresOpcodeCheck)
                    {
                        method.EmitAmortizedOpcodeCheck(currentSubsegment, locals, envLoader, evmExceptionLabels);
                    }
                    // and we emit failure for failing jumpless segment at start 

                    if (currentSubsegment.RequiredStack != 0)
                    {
                        method.LoadLocal(locals.stackHeadIdx);
                        method.LoadConstant(currentSubsegment.RequiredStack);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackUnderflow));
                    }
                    // we check if locals.stackHeadRef overflow can occur
                    if (currentSubsegment.MaxStack != 0)
                    {
                        method.LoadLocal(locals.stackHeadIdx);
                        method.LoadConstant(currentSubsegment.MaxStack);
                        method.Add();
                        method.LoadConstant(EvmStack.MaxStackSize);
                        method.BranchIfGreaterOrEqual(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackOverflow));
                    }
                }

                if (contractMetadata.StaticGasSubSegmentes.TryGetValue(i, out var gasCost) && gasCost > 0)
                {
                    method.EmitStaticGasCheck(locals.gasAvailable, gasCost, evmExceptionLabels);
                }

                if (i == endOfSegment - 1 || opcodeInfo.Instruction.IsTerminating())
                {
                    method.LoadConstant(i + opcodeInfo.Metadata.AdditionalBytes);
                    method.StoreLocal(locals.programCounter);
                }
            }
            else
            {
                EmitCallToStartInstructionTrace(method, locals.gasAvailable, locals.stackHeadIdx, i, opcodeInfo.Instruction, envLoader, locals);
                if (opcodeInfo.Instruction.RequiresAvailabilityCheck())
                {
                    envLoader.LoadSpec(method, locals, false);
                    method.LoadConstant((byte)opcodeInfo.Instruction);
                    method.Call(typeof(InstructionExtensions).GetMethod(nameof(InstructionExtensions.IsEnabled)));
                    method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                }

                if(opcodeInfo.Metadata.IsNotStaticOpcode)
                {
                    method.EmitAmortizedStaticEnvCheck(currentSubsegment, locals, envLoader, evmExceptionLabels);
                }

                method.EmitStaticGasCheck(locals.gasAvailable, opcodeInfo.Metadata.GasCost, evmExceptionLabels);

                method.LoadConstant(i + opcodeInfo.Metadata.AdditionalBytes);
                method.StoreLocal(locals.programCounter);

                method.LoadLocal(locals.stackHeadIdx);
                method.LoadConstant(opcodeInfo.Metadata.StackBehaviorPop);
                method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackUnderflow));

                var delta = opcodeInfo.Metadata.StackBehaviorPush - opcodeInfo.Metadata.StackBehaviorPop;
                method.LoadLocal(locals.stackHeadIdx);
                method.LoadConstant(delta);
                method.Add();
                method.LoadConstant(EvmStack.MaxStackSize);
                method.BranchIfGreaterOrEqual(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackOverflow));
            }

            /*
            using Local depth = method.DeclareLocal<int>();

            envLoader.LoadEnv(method, locals, true);
            method.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.CallDepth)));
            method.StoreLocal(depth);

            using Local pc = method.DeclareLocal<int>();
            method.LoadConstant(i);
            method.StoreLocal(pc);

            using Local stackOffset = method.DeclareLocal<short>();
            method.LoadConstant((short)contractMetadata.StackOffsets.GetValueOrDefault(i, (short)0));
            method.StoreLocal(stackOffset);

            using Local instructionName = method.DeclareLocal<string>();
            method.LoadConstant(opcodeInfo.Instruction.ToString());
            method.StoreLocal(instructionName);

            // method.WriteLine("Depth: {0}, ProgramCounter: {1}, Opcode: {2}, GasAvailable: {3}, StackOffset: {4}, StackDelta: {5}", depth, pc, instructionName, locals.gasAvailable, locals.stackHeadIdx, stackOffset);
            */

            ILGenerator(codeInfo, config, contractMetadata, currentSubsegment, i, opcodeInfo.Instruction, opcodeInfo.Metadata, method, locals, envLoader, evmExceptionLabels, (ret, jumpTable, exit));

            i += opcodeInfo.Metadata.AdditionalBytes;
            if (!opcodeInfo.Instruction.IsTerminating())
            {
                if (config.IsIlEvmAggressiveModeEnabled)
                {
                    UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, i, currentSubsegment);
                }
                else
                {
                    UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, opcodeInfo.Metadata);
                    EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                }
            }
            i += 1;

            if (opcodeInfo.Instruction.IsTerminating() && !hasEmittedJump)
            {
                goto exitLoops;
            }
        }

        envLoader.LoadResult(method, locals, true);
        method.LoadConstant((int)ContractState.Finished);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

    exitLoops:
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
        method.LoadConstant(codeInfo.MachineCode.Length);
        method.BranchIfGreaterOrEqual(exit);

        foreach (KeyValuePair<int, Label> continuationSites in entryPoints)
        {
            method.LoadLocal(locals.jmpDestination);
            method.LoadConstant(continuationSites.Key);
            method.BranchIfEqual(continuationSites.Value);
        }

        method.Branch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        method.MarkLabel(jumpTable);
        method.LoadLocal(locals.wordRef256A);
        method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
        method.StoreLocal(locals.jmpDestination);

        method.EmitCheck(nameof(Word.IsShort), locals.wordRef256A);
        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        method.LoadLocal(locals.jmpDestination);
        method.LoadConstant(0);
        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        method.LoadLocal(locals.jmpDestination);
        method.LoadConstant(codeInfo.MachineCode.Length);
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
            if (!config.IsIlEvmAggressiveModeEnabled)
                EmitCallToErrorTrace(method, locals.gasAvailable, kvp, envLoader, locals);

            envLoader.LoadResult(method, locals, true);
            method.Duplicate();
            method.LoadConstant((int)kvp.Key);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

            method.LoadConstant((int)ContractState.Failed);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
            method.Branch(exit);
        }
    }
}
