// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using Nethermind.Core.Attributes;
using Nethermind.Core.Specs;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Org.BouncyCastle.Asn1.Cms;
using Sigil;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;

using Label = Sigil.Label;

namespace Nethermind.Evm.CodeAnalysis.IL;

internal static class Precompiler
{
    public static IPrecompiledContract CompileContract(string contractName, CodeInfo codeInfo, ContractCompilerMetadata metadata, IVMConfig config)
    {
        AssemblyBuilder asmBuilder;
        if(config.IlEvmPersistPrecompiledContractsOnDisk)
        {
            asmBuilder = new PersistedAssemblyBuilder(new AssemblyName(contractName), typeof(Object).Assembly);
        } else
        {
            asmBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(contractName), AssemblyBuilderAccess.Run);
        }

        var moduleBuilder = asmBuilder.DefineDynamicModule("MainModule");

        // Define a type that implements MoveNext
        var typeBuilder = moduleBuilder.DefineType("ContractType",
            TypeAttributes.Public | TypeAttributes.Class);

        typeBuilder.AddInterfaceImplementation(typeof(IPrecompiledContract));

        // Define the MoveNext method
        EmitMoveNext(Emit<MoveNext>.BuildMethod(typeBuilder, "MoveNext", MethodAttributes.Public | MethodAttributes.Virtual, CallingConventions.HasThis, allowUnverifiableCode: true, doVerify: false), codeInfo, metadata, config).CreateMethod();

        // Finalize the type
        var finalizedType = typeBuilder.CreateType();

        if(config.IlEvmPersistPrecompiledContractsOnDisk)
        {
            var assemblyPath = Path.Combine(config.IlEvmPrecompiledContractsPath, IVMConfig.DllName(contractName));
            using var fileStream = new FileStream(assemblyPath, FileMode.OpenOrCreate);
            fileStream.SetLength(0); // Clear the file
            ((PersistedAssemblyBuilder)asmBuilder).Save(fileStream);  // or pass filename to save into a file

            fileStream.Seek(0, SeekOrigin.Begin);
            var assembly = AssemblyLoadContext.Default.LoadFromStream(fileStream);
            finalizedType = assembly.GetType("ContractType");
        }

        IPrecompiledContract contract = (IPrecompiledContract)Activator.CreateInstance(finalizedType);
        return contract;
    }

    public static Emit<MoveNext> EmitMoveNext(Emit<MoveNext> method, CodeInfo codeInfo, ContractCompilerMetadata contractMetadata, IVMConfig config)
    {
        var machineCodeAsSpan = codeInfo.MachineCode.Span;

        using var locals = new Locals<MoveNext>(method);

        Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();
        Dictionary<int, Label> jumpDestinations = new();
        Dictionary<int, Label> entryPoints = new();

        // set up spec
        method.CacheSpec(locals);
        method.CacheBlockContext(locals);
        method.CacheTxContext(locals);

        ReleaseSpecEmit.DeclareOpcodeValidityCheckVariables(method, contractMetadata, locals);

        Label exit = method.DefineLabel(locals.GetLabelName()); // the label just before return
        Label jumpTable = method.DefineLabel(locals.GetLabelName()); // jump table
        Label isContinuation = method.DefineLabel(locals.GetLabelName()); // jump table
        Label ret = method.DefineLabel(locals.GetLabelName());


        method.LoadStackHead(locals, false);
        method.StoreLocal(locals.stackHeadIdx);

        method.LoadCurrStackHead(locals, true);
        method.StoreLocal(locals.stackHeadRef);

        // set gas to local
        method.LoadGasAvailable(locals, false);
        method.StoreLocal(locals.gasAvailable);

        // set pc to local
        method.LoadProgramCounter(locals, false);
        method.StoreLocal(locals.programCounter);

        method.LoadResult(locals, false);
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

            if (contractMetadata.SegmentsBoundaries.ContainsKey(i))
            {
                endOfSegment = contractMetadata.SegmentsBoundaries[i];
                method.MarkLabel(entryPoints[i] = method.DefineLabel(locals.GetLabelName()));
            }

            hasEmittedJump |= opcodeInfo.Instruction.IsJump();

            if (opcodeInfo.Instruction is Instruction.JUMPDEST)
            {
                // mark the jump destination
                method.MarkLabel(jumpDestinations[i] = method.DefineLabel(locals.GetLabelName()));
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
                        method.EmitAmortizedStaticEnvCheck(currentSubsegment, locals, evmExceptionLabels);
                    }

                    if (currentSubsegment.RequiresOpcodeCheck)
                    {
                        method.EmitAmortizedOpcodeCheck(currentSubsegment, locals, evmExceptionLabels);
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
                EmitCallToStartInstructionTrace(method, locals.gasAvailable, locals.stackHeadIdx, i, opcodeInfo.Instruction, locals);
                if (opcodeInfo.Instruction.RequiresAvailabilityCheck())
                {
                    method.LoadSpec(locals, false);
                    method.LoadConstant((byte)opcodeInfo.Instruction);
                    method.Call(typeof(InstructionExtensions).GetMethod(nameof(InstructionExtensions.IsEnabled)));
                    method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                }

                if(opcodeInfo.Metadata.IsNotStaticOpcode)
                {
                    method.EmitAmortizedStaticEnvCheck(currentSubsegment, locals, evmExceptionLabels);
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

            FullAotEnvLoader.LoadEnv(method, locals, true);
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

            method.GetOpcodeILEmitter(opcodeInfo.Instruction, codeInfo, config, contractMetadata, currentSubsegment, i, opcodeInfo.Metadata, locals, evmExceptionLabels, (ret, jumpTable, exit));

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
                    EmitCallToEndInstructionTrace(method, locals.gasAvailable, locals);
                }
            }
            i += 1;

            if (opcodeInfo.Instruction.IsTerminating() && !hasEmittedJump)
            {
                goto exitLoops;
            }
        }

        method.LoadResult(locals, true);
        method.LoadConstant((int)ContractState.Finished);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

    exitLoops:
        method.MarkLabel(ret);
        // we get locals.stackHeadRef size
        method.LoadStackHead(locals, true);
        method.LoadLocal(locals.stackHeadIdx);
        method.StoreIndirect<int>();

        // set gas available
        method.LoadGasAvailable(locals, true);
        method.LoadLocal(locals.gasAvailable);
        method.StoreIndirect<long>();

        // set program counter
        method.LoadProgramCounter(locals, true);
        method.LoadLocal(locals.programCounter);
        method.LoadConstant(1);
        method.Add();
        method.StoreIndirect<int>();

        method.MarkLabel(exit);

        method.LoadResult(locals, true);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ContractState)));
        method.LoadConstant((int)ContractState.Halted);
        method.CompareEqual();
        method.Return();

        // isContinuation
        Label skipJumpValidation = method.DefineLabel(locals.GetLabelName());
        method.MarkLabel(isContinuation);

        method.LoadLocal(locals.programCounter);
        method.StoreLocal(locals.jmpDestination);
        method.LoadResult(locals, true);
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
            method.FindCorrectBranchAndJump(locals.jmpDestination, locals, jumpDestinations, evmExceptionLabels);
        }

        foreach (KeyValuePair<EvmExceptionType, Label> kvp in evmExceptionLabels)
        {
            method.MarkLabel(kvp.Value);
            if (!config.IsIlEvmAggressiveModeEnabled)
                EmitCallToErrorTrace(method, locals.gasAvailable, kvp, locals);

            method.LoadResult(locals, true);
            method.Duplicate();
            method.LoadConstant((int)kvp.Key);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

            method.LoadConstant((int)ContractState.Failed);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
            method.Branch(exit);
        }

        return method;
    }
}
