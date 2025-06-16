// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Evm.Config;
using Sigil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using Label = Sigil.Label;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Evm.CodeAnalysis.IL;

public static class Precompiler
{
    /// <summary>
    /// Use for testing. All delegates will have their IL memoized.
    /// </summary>
    public static void MemoizeILForSteps()
    {
        _stepsIL = new ConditionalWeakTable<ILExecutionStep, string>();
    }

    public static bool TryGetEmittedIL(ILExecutionStep step, out string? il)
    {
        il = null;
        return _stepsIL != null && _stepsIL.TryGetValue(step, out il);
    }

    private static ConditionalWeakTable<ILExecutionStep, string>? _stepsIL;

    internal static Lazy<PersistedAssemblyBuilder> _currentPersistentAsmBuilder = new Lazy<PersistedAssemblyBuilder>(() => new PersistedAssemblyBuilder(new AssemblyName(GenerateAssemblyName()), typeof(object).Assembly));
    internal static ModuleBuilder? _currentPersistentModBuilder = null;
    internal static ModuleBuilder? _currentDynamicModBuilder = null;
    internal static int _currentBundleSize = 0;
    private static string GenerateAssemblyName() => Guid.NewGuid().ToByteArray().ToHexString();

    public static string DllFileSuffix => ".Nethermind.g.c.dll";
    internal static string GetTargetFileName()
    {
        return $"{Precompiler._currentPersistentAsmBuilder.Value.GetName()}{DllFileSuffix}";
    }

    private static ILExecutionStep? CompileContractInternal(
        ModuleBuilder moduleBuilder,
        string identifier,
        CodeInfo codeinfo,
        ContractCompilerMetadata metadata,
        IVMConfig config,
        bool runtimeTarget)
    {
        try
        {
            if (!runtimeTarget)
            {
                Interlocked.Increment(ref _currentBundleSize);
            }

            var typeBuilder = moduleBuilder.DefineType(identifier,
                TypeAttributes.Public | TypeAttributes.Class);

            ConstructorInfo attributeCtor = typeof(NethermindPrecompileAttribute).GetConstructor(Type.EmptyTypes)!;
            var attributeBuilder = new CustomAttributeBuilder(attributeCtor, Array.Empty<object>());
            typeBuilder.SetCustomAttribute(attributeBuilder);

            EmitMoveNext(Emit<ILExecutionStep>.BuildMethod(
                typeBuilder, nameof(ILExecutionStep), MethodAttributes.Public | MethodAttributes.Static,
                CallingConventions.Standard,
                allowUnverifiableCode: true, doVerify: false),
                codeinfo, metadata, config).CreateMethod(out string ilCode, OptimizationOptions.All);

            var finalizedType = typeBuilder.CreateType();

            if (!runtimeTarget)
            {
                return null;
            }

            var method = finalizedType.GetMethod(nameof(ILExecutionStep), BindingFlags.Static | BindingFlags.Public);
            var @delegate = (ILExecutionStep)Delegate.CreateDelegate(typeof(ILExecutionStep), method!);

            if (_stepsIL != null)
            {
                _stepsIL.AddOrUpdate(@delegate, ilCode);
            }

            return @delegate;

        }
        catch
        {
            if (!runtimeTarget)
            {
                Interlocked.Decrement(ref _currentBundleSize);
            }

            return null;
        }
    }

    public static bool TryCompileContract(
        string contractName,
        CodeInfo codeInfo,
        ContractCompilerMetadata metadata,
        IVMConfig config,
        ILogger logger,
        out ILExecutionStep? iledCode)
    {

        // Runtime contract
        if (_currentDynamicModBuilder is null)
        {
            var runtimeAsm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("_dynamicAssembly"), AssemblyBuilderAccess.Run);
            _currentDynamicModBuilder = runtimeAsm.DefineDynamicModule("MainModule");
        }

        if ((iledCode = CompileContractInternal(_currentDynamicModBuilder, $"_dynamicAssembly_{(new Random()).Next()}_{contractName}", codeInfo, metadata, config, true)) is null)
        {
            return false;
        }

        // Persisted contract
        if (config.IlEvmPersistPrecompiledContractsOnDisk)
        {
            _currentPersistentModBuilder = _currentPersistentModBuilder ?? _currentPersistentAsmBuilder.Value.DefineDynamicModule("MainModule");
            CompileContractInternal(_currentPersistentModBuilder, contractName, codeInfo, metadata, config, false);

            if (Interlocked.CompareExchange(ref _currentBundleSize, 0, config.IlEvmContractsPerDllCount) == config.IlEvmContractsPerDllCount)
            {
                FlushToDisk(config);
            }
        }
        return true;
    }

    public static void FlushToDisk(IVMConfig config)
    {
        if (_currentPersistentAsmBuilder is null) return;

        if (!Path.Exists(config.IlEvmPrecompiledContractsPath))
        {
            Directory.CreateDirectory(config.IlEvmPrecompiledContractsPath);
        }

        var assemblyPath = Path.Combine(config.IlEvmPrecompiledContractsPath, GetTargetFileName());

        ((PersistedAssemblyBuilder)_currentPersistentAsmBuilder.Value).Save(assemblyPath);  // or pass filename to save into a file
        ResetEnvironment(false);
    }

    public static void ResetEnvironment(bool resetDynamic)
    {
        _currentPersistentAsmBuilder = new Lazy<PersistedAssemblyBuilder>(() => new PersistedAssemblyBuilder(new AssemblyName(GenerateAssemblyName()), typeof(object).Assembly));
        _currentPersistentModBuilder = null;
        if (resetDynamic)
        {
            _currentDynamicModBuilder = null;
        }
        _currentBundleSize = 0;
    }

    public static Emit<ILExecutionStep> EmitMoveNext(Emit<ILExecutionStep> method, CodeInfo codeInfo, ContractCompilerMetadata contractMetadata, IVMConfig config)
    {
        var machineCodeAsSpan = codeInfo.MachineCode.Span;

        using var locals = new Locals<ILExecutionStep>(method);

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
        for (var i = 0; i < codeInfo.MachineCode.Length;)
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

                    if (currentSubsegment.RequiresStaticEnvCheck)
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

                if (opcodeInfo.Metadata.IsNotStaticOpcode)
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

            method.GetOpcodeILEmitter(codeInfo, opcodeInfo.Instruction, config, contractMetadata, currentSubsegment, i, opcodeInfo.Metadata, locals, evmExceptionLabels, (ret, jumpTable, exit));

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
