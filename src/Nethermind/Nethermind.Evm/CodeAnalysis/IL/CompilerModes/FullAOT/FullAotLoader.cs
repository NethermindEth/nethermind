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
    public const int REF_RETURNDATABUFFER_INDEX = 6;

    public Dictionary<string, FieldBuilder> Fields { get; } = new();

    public FullAotEnvLoader(TypeBuilder contractDynamicType, ContractMetadata contractMetadata) 
    {
        _contractDynamicType = contractDynamicType;

        FieldBuilder fieldBuilder;
        // create a property ILChunkExecutionState Current
        PropertyBuilder currentProp = _contractDynamicType.EmitProperty<ILChunkExecutionState>(PROP_CURRENT_STATE, true, true, out fieldBuilder);
        Fields.Add(PROP_CURRENT_STATE, fieldBuilder);
        // create a property EvmState EvmState
        PropertyBuilder evmStateProp = _contractDynamicType.EmitProperty<EvmState>(PROP_EVMSTATE, true, true, out fieldBuilder);
        Fields.Add(PROP_EVMSTATE, fieldBuilder);
        // create a property ITxTracer Tracer
        PropertyBuilder tracerProp = _contractDynamicType.EmitProperty<ITxTracer>(PROP_TRACER, true, true, out fieldBuilder);
        Fields.Add(PROP_TRACER, fieldBuilder);
        // create a property ILogger Logger
        PropertyBuilder loggerProp = _contractDynamicType.EmitProperty<ILogger>(PROP_LOGGER, true, true, out fieldBuilder);
        Fields.Add(PROP_LOGGER, fieldBuilder);
        // create a property IReleaseSpec Spec
        PropertyBuilder specProp = _contractDynamicType.EmitProperty<IReleaseSpec>(PROP_SPEC, true, true, out fieldBuilder);
        Fields.Add(PROP_SPEC, fieldBuilder);
        // create a property IWorldState WorldState
        PropertyBuilder worldStateProp = _contractDynamicType.EmitProperty<IWorldState>(PROP_WORLSTATE, true, true, out fieldBuilder);
        Fields.Add(PROP_WORLSTATE, fieldBuilder);
        // create a property IBlockhashProvider BlockhashProvider
        PropertyBuilder blockhashProviderProp = _contractDynamicType.EmitProperty<IBlockhashProvider>(PROP_BLOCKHASHPROVIDER, true, true, out fieldBuilder);
        Fields.Add(PROP_BLOCKHASHPROVIDER, fieldBuilder);
        // create a property byte[][] ImmediatesData
        PropertyBuilder immediatesDataProp = _contractDynamicType.EmitProperty<byte[][]>(PROP_IMMEDIATESDATA, true, true, out fieldBuilder);
        Fields.Add(PROP_IMMEDIATESDATA, fieldBuilder);

        // create a constructor for the contract
        ConstructorBuilder constructor = _contractDynamicType.DefineDefaultConstructor(MethodAttributes.Public);

        // create a constructor for the contract that takes all the properties
        ConstructorBuilder fullConstructor = _contractDynamicType.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new Type[] { typeof(EvmState), typeof(IWorldState), typeof(IReleaseSpec), typeof(IBlockhashProvider), typeof(ITxTracer), typeof(ILogger), typeof(byte[][]) });
        ILGenerator fullConstructorIL = fullConstructor.GetILGenerator();
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Call, constructor);
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_1);
        fullConstructorIL.Emit(OpCodes.Call, evmStateProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_2);
        fullConstructorIL.Emit(OpCodes.Call, worldStateProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_3);
        fullConstructorIL.Emit(OpCodes.Call, specProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_S, 4);
        fullConstructorIL.Emit(OpCodes.Call, blockhashProviderProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_S, 5);
        fullConstructorIL.Emit(OpCodes.Call, tracerProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_S, 6);
        fullConstructorIL.Emit(OpCodes.Call, loggerProp.GetSetMethod());
        fullConstructorIL.Emit(OpCodes.Ldarg_0);
        fullConstructorIL.Emit(OpCodes.Ldarg_S, 7);
        fullConstructorIL.Emit(OpCodes.Call, immediatesDataProp.GetSetMethod());
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

        if (loadAddress)
            locals.TryLoadLocalAddress(blockContextVarName);
        else
            locals.TryLoadLocal(blockContextVarName);
    }

    public override void LoadBlockhashProvider(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if(loadAddress)
            il.LoadFieldAddress(Fields[PROP_BLOCKHASHPROVIDER]);
        else
            il.LoadField(Fields[PROP_BLOCKHASHPROVIDER]);
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
            LoadTxContext(il, locals, true);
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
        il.LoadArgument(REF_STACKHEADREF_INDEX);
        if (!loadAddress)
            il.LoadObject<Word>();
    }

    public override void LoadEnv(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        var envVarName = "env";
        if (locals.TryDeclareLocal(envVarName, typeof(ExecutionEnvironment)))
        {
            LoadVmState(il, locals, false);
            il.LoadField(typeof(EvmState).GetField(nameof(EvmState.Env)));
            locals.TryStoreLocal(envVarName);
        }

        if(loadAddress)
            locals.TryLoadLocalAddress(envVarName);
        else
            locals.TryLoadLocal(envVarName);
    }

    public override void LoadGasAvailable(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_GASAVAILABLE_INDEX);
        if (!loadAddress)
            il.LoadObject<long>();
    }

    public override void LoadImmediatesData(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[PROP_IMMEDIATESDATA]);
        else
            il.LoadField(Fields[PROP_IMMEDIATESDATA]);
    }

    public override void LoadLogger(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[PROP_LOGGER]);
        else
            il.LoadField(Fields[PROP_LOGGER]);
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

        if (loadAddress)
            locals.TryLoadLocalAddress(machineCodeVarName);
        else
            locals.TryLoadLocal(machineCodeVarName);
    }

    public override void LoadMemory(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        var memoryVarName = "memory";
        if (locals.TryDeclareLocal(memoryVarName, typeof(EvmPooledMemory)))
        {
            LoadVmState(il, locals, false);
            il.Call(typeof(EvmState).GetProperty(nameof(EvmState.Memory)).GetMethod);
            il.LoadObject<EvmPooledMemory>();
            locals.TryStoreLocal(memoryVarName);
        }

        if (loadAddress)
            locals.TryLoadLocalAddress(memoryVarName);
        else
            locals.TryLoadLocal(memoryVarName);
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
        if(loadAddress)
            il.LoadFieldAddress(Fields[PROP_CURRENT_STATE]);
        else
            il.LoadField(Fields[PROP_CURRENT_STATE]);
    }

    public override void LoadSpec(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[PROP_SPEC]);
        else
            il.LoadField(Fields[PROP_SPEC]);
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

        if (loadAddress)
        {
            locals.TryLoadLocalAddress(txContextVarName);
        }
        else
        {
            locals.TryLoadLocal(txContextVarName);
        }
    }

    public override void LoadTxTracer(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[PROP_TRACER]);
        else
            il.LoadField(Fields[PROP_TRACER]);
    }

    public override void LoadVmState(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if(loadAddress)
            il.LoadFieldAddress(Fields[PROP_EVMSTATE]);
        else
            il.LoadField(Fields[PROP_EVMSTATE]);
    }

    public override void LoadWorldState(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[PROP_WORLSTATE]);
        else
            il.LoadField(Fields[PROP_WORLSTATE]);
    }

    public override void LoadReturnDataBuffer(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_RETURNDATABUFFER_INDEX);
        if (!loadAddress)
        {
            il.LoadObject(typeof(ReadOnlyMemory<byte>));
        }
    }
}
