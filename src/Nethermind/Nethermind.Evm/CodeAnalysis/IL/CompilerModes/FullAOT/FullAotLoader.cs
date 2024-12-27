// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
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
using static Nethermind.Evm.CodeAnalysis.IL.CompilerModes.FullAOT.FullAOT;
using Nethermind.Logging;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes.FullAOT;
internal class FullAotEnvLoader : EnvLoader<MoveNextDelegate>
{
    private TypeBuilder _contractDynamicType;

    public const string PROP_EVMSTATE = "EvmState";
    public const string PROP_WORLSTATE = "WorldState";
    public const string PROP_CODEINFOREPOSITORY = "CodeInfoRepository";
    public const string PROP_SPEC = "Spec";
    public const string PROP_BLOCKHASHPROVIDER = "BlockhashProvider";
    public const string PROP_TRACER = "Tracer";
    public const string PROP_LOGGER = "Logger";
    public const string PROP_CURRENT_STATE = "Current";
    public const string PROP_IMMEDIATESDATA = "ImmediatesData";

    public const int IMPLICIT_THIS_INDEX = 0;
    public const int INT_CHAINID_INDEX = 1;
    public const int REF_GASAVAILABLE_INDEX = 2;
    public const int REF_PROGRAMCOUNTER_INDEX = 3;
    public const int REF_STACKHEAD_INDEX = 4;
    public const int REF_STACKHEADREF_INDEX = 5;

    public FullAotEnvLoader(TypeBuilder contractDynamicType) 
    {
        _contractDynamicType = contractDynamicType;

        // create a property ILChunkExecutionState Current
        PropertyBuilder currentProp = _contractDynamicType.EmitProperty<ILChunkExecutionState>(PROP_CURRENT_STATE, true, true);
        // create a property EvmState EvmState
        PropertyBuilder evmStateProp = _contractDynamicType.EmitProperty<EvmState>(PROP_EVMSTATE, true, false);
        // create a property ITxTracer Tracer
        PropertyBuilder tracerProp = _contractDynamicType.EmitProperty<ITxTracer>(PROP_TRACER, true, false);
        // create a property ILogger Logger
        PropertyBuilder loggerProp = _contractDynamicType.EmitProperty<ILogger>(PROP_LOGGER, true, false);
        // create a property IReleaseSpec Spec
        PropertyBuilder specProp = _contractDynamicType.EmitProperty<IReleaseSpec>(PROP_SPEC, true, false);
        // create a property IWorldState WorldState
        PropertyBuilder worldStateProp = _contractDynamicType.EmitProperty<IWorldState>(PROP_WORLSTATE, true, false);
        // create a property IBlockhashProvider BlockhashProvider
        PropertyBuilder blockhashProviderProp = _contractDynamicType.EmitProperty<IBlockhashProvider>(PROP_BLOCKHASHPROVIDER, true, false);

        // create a constructor for the contract
        ConstructorBuilder constructor = _contractDynamicType.DefineDefaultConstructor(MethodAttributes.Public);

        // create a constructor for the contract that takes all the properties
        ConstructorBuilder fullConstructor = _contractDynamicType.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new Type[] { typeof(EvmState), typeof(IWorldState), typeof(IReleaseSpec), typeof(IBlockhashProvider), typeof(ITxTracer), typeof(ILogger) });
        ILGenerator fullConstructorIL = fullConstructor.GetILGenerator();
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_1);
        fullConstructorIL.Emit(OpCodes.Call, evmStateProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_2);
        fullConstructorIL.Emit(OpCodes.Call, worldStateProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_S, 4);
        fullConstructorIL.Emit(OpCodes.Call, specProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_S, 5);
        fullConstructorIL.Emit(OpCodes.Call, blockhashProviderProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_S, 6);
        fullConstructorIL.Emit(OpCodes.Call, tracerProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_S, 7);
        fullConstructorIL.Emit(OpCodes.Call, loggerProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ret);

    }


    public override void LoadBlockContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        var blockContextVarName = "blockContext";
        if (locals.TryDeclareLocal(blockContextVarName, typeof(BlockExecutionContext)))
        {
            LoadTxContext(il, locals, false);
            il.LoadField(typeof(TxExecutionContext).GetField(nameof(TxExecutionContext.BlockExecutionContext)));
            locals.TryStoreLocal(blockContextVarName);
        }

        locals.TryLoadLocal(blockContextVarName);
        if (!loadAddress)
            il.LoadObject<BlockExecutionContext>();
    }

    public override void LoadBlockhashProvider(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.Call(_contractDynamicType.GetProperty(PROP_BLOCKHASHPROVIDER).GetGetMethod());
    }

    public override void LoadCalldata(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        var calldataVarName = "calldata";
        if (locals.TryDeclareLocal(calldataVarName, typeof(ReadOnlyMemory<byte>)))
        {
            LoadEnv(il, locals, true);
            il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.InputData)));
            locals.TryStoreLocal(calldataVarName);
        }

        if (loadAddress)
            locals.TryLoadLocalAddress(calldataVarName);
        else
            locals.TryLoadLocal(calldataVarName);
    }

    public override void LoadChainId(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(INT_CHAINID_INDEX);
        else
        {
            il.LoadArgument(INT_CHAINID_INDEX);
        }
    }

    public override void LoadCodeInfoRepository(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        var codeInfoRepositoryVarName = "codeInfoRepository";
        if (locals.TryDeclareLocal(codeInfoRepositoryVarName, typeof(ICodeInfoRepository)))
        {
            LoadTxContext(il, locals, false);
            il.Call(typeof(TxExecutionContext).GetProperty(nameof(TxExecutionContext.CodeInfoRepository)).GetGetMethod());
            locals.TryStoreLocal(codeInfoRepositoryVarName);
        }

        if (loadAddress)
            locals.TryLoadLocalAddress(codeInfoRepositoryVarName);
        else
            locals.TryLoadLocal(codeInfoRepositoryVarName);

    }

    public override void LoadCurrStackHead(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public override void LoadEnv(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        var envVarName = "env";
        if (locals.TryDeclareLocal(envVarName, typeof(ExecutionEnvironment)))
        {
            LoadVmState(il, locals, true);
            il.LoadField(typeof(EvmState).GetField(nameof(EvmState.Env)));
            locals.TryStoreLocal(envVarName);
        }

        locals.TryLoadLocal(envVarName);
        if (!loadAddress)
            il.LoadObject<ExecutionEnvironment>();
    }

    public override void LoadGasAvailable(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_GASAVAILABLE_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public override void LoadImmediatesData(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.Call(_contractDynamicType.GetProperty(PROP_IMMEDIATESDATA).GetGetMethod());
    }

    public override void LoadLogger(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.Call(_contractDynamicType.GetProperty(PROP_LOGGER).GetGetMethod());
    }

    public override void LoadMachineCode(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        var machineCodeVarName = "machineCode";
        if (locals.TryDeclareLocal(machineCodeVarName, typeof(ReadOnlyMemory<byte>)))
        {
            LoadEnv(il, locals, false);
            il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.CodeInfo)));
            il.Call(typeof(CodeInfo).GetProperty(nameof(CodeInfo.MachineCode)).GetGetMethod());
            locals.TryStoreLocal(machineCodeVarName);
        }

        locals.TryLoadLocal(machineCodeVarName);
        if (!loadAddress)
            il.LoadObject<ReadOnlyMemory<byte>>();
    }

    public override void LoadMemory(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        var memoryVarName = "memory";
        if (locals.TryDeclareLocal(memoryVarName, typeof(EvmPooledMemory)))
        {
            LoadVmState(il, locals, true);
            il.LoadField(typeof(EvmState).GetField(nameof(EvmState.Memory)));
            locals.TryStoreLocal(memoryVarName);
        }

        locals.TryLoadLocalAddress(memoryVarName);
        if (!loadAddress)
            il.LoadObject<EvmPooledMemory>();
    }

    public override void LoadProgramCounter(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_PROGRAMCOUNTER_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public override void LoadResult(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.Call(_contractDynamicType.GetProperty(PROP_CURRENT_STATE).GetGetMethod());
    }

    public override void LoadSpec(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.Call(_contractDynamicType.GetProperty(PROP_SPEC).GetGetMethod());
    }

    public override void LoadStackHead(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public override void LoadTxContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        var txContextVarName = "txContext";
        if (locals.TryDeclareLocal(txContextVarName, typeof(TxExecutionContext)))
        {
            LoadEnv(il, locals, false);
            il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.TxExecutionContext)));
            locals.TryStoreLocal(txContextVarName);
        }

        locals.TryLoadLocal(txContextVarName);
        if (!loadAddress)
            il.LoadObject<TxExecutionContext>();
    }

    public override void LoadTxTracer(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.Call(_contractDynamicType.GetProperty(PROP_TRACER).GetGetMethod());
    }

    public override void LoadVmState(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.Call(_contractDynamicType.GetProperty(PROP_EVMSTATE).GetGetMethod());
    }

    public override void LoadWorldState(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.Call(_contractDynamicType.GetProperty(PROP_WORLSTATE).GetGetMethod());
    }
}
