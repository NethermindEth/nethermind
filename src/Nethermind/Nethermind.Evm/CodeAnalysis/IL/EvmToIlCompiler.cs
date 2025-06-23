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
using static Nethermind.Evm.CodeAnalysis.IL.EmitIlChunkStateExtensions;
using Label = Sigil.Label;
using ILogger = Nethermind.Logging.ILogger;
using Nethermind.Evm.CodeAnalysis.IL.Delegates;
using Nethermind.Core.Crypto;
using Org.BouncyCastle.Ocsp;
using Sigil.NonGeneric;
using System.Reflection.PortableExecutable;

namespace Nethermind.Evm.CodeAnalysis.IL;

public static class Precompiler
{
    /// <summary>
    /// Use for testing. All delegates will have their IL memoized.
    /// </summary>
    public static void MemoizeILForSteps()
    {
        _stepsIL = new ConditionalWeakTable<ILEmittedMethod, string>();
    }

    public static bool TryGetEmittedIL(ILEmittedMethod step, out string? il)
    {
        il = null;
        return _stepsIL != null && _stepsIL.TryGetValue(step, out il);
    }

    private static ConditionalWeakTable<ILEmittedMethod, string>? _stepsIL;

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

    private static ILEmittedMethod? CompileContractInternal(
        ModuleBuilder moduleBuilder,
        string identifier,
        CodeInfo codeinfo,
        ContractCompilerMetadata metadata,
        IVMConfig config,
        bool runtimeTarget)
    {
#pragma warning disable CS0168 // Variable is declared but never used
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

            EmitEntryPoint(Emit<ILEmittedMethod>.BuildMethod(
                typeBuilder, nameof(ILEmittedMethod), MethodAttributes.Public | MethodAttributes.Static,
                CallingConventions.Standard,
                allowUnverifiableCode: true, doVerify: false), typeBuilder,
                codeinfo, metadata, config).CreateMethod(out string ilCode, OptimizationOptions.All);

            var finalizedType = typeBuilder.CreateType();

            if (!runtimeTarget)
            {
                return null;
            }

            var method = finalizedType.GetMethod(nameof(ILEmittedMethod), BindingFlags.Static | BindingFlags.Public);
            var @delegate = (ILEmittedMethod)Delegate.CreateDelegate(typeof(ILEmittedMethod), method!);

            if (_stepsIL != null)
            {
                _stepsIL.AddOrUpdate(@delegate, ilCode);
            }

            return @delegate;

        }
        catch(Exception e)
        {
            if (!runtimeTarget)
            {
                Interlocked.Decrement(ref _currentBundleSize);
            }

            throw;
        }
#pragma warning restore CS0168 // Variable is declared but never used
    }

    public static bool TryCompileContract(
        string contractName,
        CodeInfo codeInfo,
        ContractCompilerMetadata metadata,
        IVMConfig config,
        ILogger logger,
        out ILEmittedMethod? iledCode)
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

    public static Emit<ILEmittedMethod> EmitInternalMethod(Emit<ILEmittedMethod> method, int pc, CodeInfo codeInfo, ContractCompilerMetadata contractMetadata, IVMConfig config)
    {
        var machineCodeAsSpan = codeInfo.MachineCode.Span;

        SubSegmentMetadata currentSubsegment = contractMetadata.SubSegments[pc];
        using var locals = new Locals<ILEmittedMethod>(method);
        var envLoader = EnvirementLoader.Instance;

        Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();

        Label exit = method.DefineLabel(locals.GetLabelName()); // the label just before return
        Label ret = method.DefineLabel(locals.GetLabelName());

        envLoader.LoadResult(method, locals, true);
        method.LoadConstant((int)ContractState.Running);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

        envLoader.LoadCurrStackHead(method, locals, true);
        method.StoreLocal(locals.stackHeadRef);

        // set gas to local
        envLoader.LoadGasAvailable(method, locals, false);
        method.StoreLocal(locals.gasAvailable);

        if (currentSubsegment.RequiredStack != 0)
        {
            envLoader.LoadStackHead(method, locals, false);
            method.LoadConstant(currentSubsegment.RequiredStack);
            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackUnderflow));
        }
        // we check if locals.stackHeadRef overflow can occur
        if (currentSubsegment.MaxStack != 0)
        {
            envLoader.LoadStackHead(method, locals, false);
            method.LoadConstant(currentSubsegment.MaxStack);
            method.Add();
            method.LoadConstant(EvmStack.MaxStackSize);
            method.BranchIfGreaterOrEqual(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StackOverflow));
        }

        method.LoadConstant(currentSubsegment.End);
        method.StoreLocal(locals.programCounter);

        for (pc = currentSubsegment.Start; pc <= currentSubsegment.End;)
        {
            (Instruction Instruction, OpcodeMetadata Metadata) opcodeInfo = ((Instruction)machineCodeAsSpan[pc], OpcodeMetadata.GetMetadata((Instruction)machineCodeAsSpan[pc]));


            if (contractMetadata.StaticGasSubSegmentes.TryGetValue(pc, out var gasCost) && gasCost > 0)
            {
                method.EmitStaticGasCheck(locals.gasAvailable, gasCost, evmExceptionLabels);
            }
            method.GetOpcodeILEmitter(codeInfo, opcodeInfo.Instruction, config, contractMetadata, currentSubsegment, pc, opcodeInfo.Metadata, locals, envLoader, evmExceptionLabels, (ret, exit));
            pc += opcodeInfo.Metadata.AdditionalBytes + 1;
        }

        method.MarkLabel(ret);

        UpdateStackHeadAndPushRerSegmentMode(method, envLoader, locals, currentSubsegment);

        // set gas available
        envLoader.LoadGasAvailable(method, locals, true);
        method.LoadLocal(locals.gasAvailable);
        method.StoreIndirect<long>();

        // set program counter
        envLoader.LoadProgramCounter(method, locals, true);
        method.LoadLocal(locals.programCounter);
        method.StoreIndirect<int>();

        method.MarkLabel(exit);

        envLoader.LoadResult(method, locals, true);
        method.Call(typeof(ILChunkExecutionState).GetProperty(nameof(ILChunkExecutionState.ShouldAbort)).GetMethod);
        method.Return();

        foreach (KeyValuePair<EvmExceptionType, Label> kvp in evmExceptionLabels)
        {
            method.MarkLabel(kvp.Value);

            envLoader.LoadResult(method, locals, true);
            method.Duplicate();
            method.LoadConstant((int)kvp.Key);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

            method.LoadConstant((int)ContractState.Failed);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
            method.Branch(exit);
        }

        return method;
    }

    public static Emit<ILEmittedMethod> EmitEntryPoint(Emit<ILEmittedMethod> method, TypeBuilder typeBuilder,  CodeInfo codeInfo, ContractCompilerMetadata contractMetadata, IVMConfig config)
    {
        var machineCodeAsSpan = codeInfo.MachineCode.Span;

        using var locals = new Locals<ILEmittedMethod>(method);
        var envLoader = EnvirementLoader.Instance;

        Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();
        Dictionary<int, Label> jumpDestinations = new();
        Dictionary<int, Label> entryPoints = new();

        // set up spec
        ReleaseSpecEmit.DeclareOpcodeValidityCheckVariables(method, contractMetadata, locals);

        Label jumpTable = method.DefineLabel(locals.GetLabelName()); // jump table
        Label isContinuation = method.DefineLabel(locals.GetLabelName()); // jump table
        Label ret = method.DefineLabel(locals.GetLabelName());

        envLoader.LoadResult(method, locals, false);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ContractState)));
        method.LoadConstant((int)ContractState.Halted);
        method.BranchIfEqual(isContinuation);

        int endOfSegment = codeInfo.MachineCode.Length;

        foreach (var (programCounter, currentSubsegment) in contractMetadata.SubSegments)
        {
            string methodName = $"Segment[{currentSubsegment.Start}::{currentSubsegment.End}]";
            var internalMethod = Sigil.Emit<ILEmittedMethod>.BuildMethod(
                typeBuilder,
                methodName,
                MethodAttributes.Private | MethodAttributes.Static,
                CallingConventions.Standard,
                allowUnverifiableCode: true, doVerify: false);

            EmitInternalMethod(internalMethod, currentSubsegment.Start, codeInfo, contractMetadata, config)
                .CreateMethod(out string ilCode, OptimizationOptions.All);

            if (currentSubsegment.IsEntryPoint)
            {
                method.MarkLabel(entryPoints[currentSubsegment.Start] = method.DefineLabel(locals.GetLabelName()));
            }

            if (currentSubsegment.IsReachable)
            {
                method.MarkLabel(jumpDestinations[currentSubsegment.Start] = method.DefineLabel(locals.GetLabelName()));
            }
            else continue;

            if (currentSubsegment.IsFailing)
            {
                method.FakeBranch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                continue;
            }

            if (currentSubsegment.RequiresStaticEnvCheck)
            {
                method.EmitAmortizedStaticEnvCheck(envLoader, currentSubsegment,  locals, evmExceptionLabels);
            }

            if (currentSubsegment.RequiresOpcodeCheck)
            {
                method.EmitAmortizedOpcodeCheck(envLoader, currentSubsegment, locals, evmExceptionLabels);
            }
            // and we emit failure for failing jumpless segment at start
            envLoader.LoadArguments(method, locals, true);
            envLoader.LoadResult(method, locals, true);

            method.Call(internalMethod);
            method.BranchIfTrue(ret);

            if (currentSubsegment.IsEphemeralCall)
            {
                method.EmitIsHalting(envLoader, locals, ret);
            }

            if(currentSubsegment.IsEphemeralJump)
            {
                method.EmitIsJumping(envLoader, locals, jumpTable);
            }
        }
        
        envLoader.LoadResult(method, locals, true);
        method.LoadConstant((int)ContractState.Finished);
        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

        method.MarkLabel(ret);

        envLoader.LoadResult(method, locals, true);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ContractState)));
        method.LoadConstant((int)ContractState.Halted);
        method.CompareEqual();
        method.Return();

        // isContinuation
        method.MarkLabel(isContinuation);

        envLoader.LoadProgramCounter(method, locals, false);
        method.StoreLocal(locals.jmpDestination);
        envLoader.LoadResult(method, locals, true);
        method.LoadConstant((int)ContractState.Running);
        method.StoreField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.ContractState)));

        method.LoadLocal(locals.jmpDestination);
        method.LoadConstant(codeInfo.MachineCode.Length);
        method.BranchIfGreaterOrEqual(ret);

        foreach (KeyValuePair<int, Label> continuationSites in entryPoints)
        {
            method.LoadLocal(locals.jmpDestination);
            method.LoadConstant(continuationSites.Key);
            method.BranchIfEqual(continuationSites.Value);
        }

        method.Branch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.InvalidJumpDestination));

        method.MarkLabel(jumpTable);
        envLoader.LoadResult(method, locals, true);
        method.LoadField(typeof(ILChunkExecutionState).GetField(nameof(ILChunkExecutionState.JumpDestination)));
        method.StoreLocal(locals.jmpDestination);

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

            envLoader.LoadResult(method, locals, true);
            method.Duplicate();
            method.LoadConstant((int)kvp.Key);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

            method.LoadConstant((int)ContractState.Failed);
            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
            method.Branch(ret);
        }

        return method;
    }
}
