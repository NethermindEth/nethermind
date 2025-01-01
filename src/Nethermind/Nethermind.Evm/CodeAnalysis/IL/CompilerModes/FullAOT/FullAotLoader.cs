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
    public const string PROP_SPEC = "Spec";
    public const string PROP_BLOCKHASHPROVIDER = "BlockhashProvider";
    public const string PROP_TRACER = "Tracer";
    public const string PROP_LOGGER = "Logger";
    public const string PROP_CURRENT_STATE = "Current";
    public const string PROP_IMMEDIATESDATA = "ImmediatesData";

    public const string FLD_ENV = "env";
    public const string FLD_TXCONTEXT = "txContext";
    public const string FLD_BLOCKCONTEXT = "blockContext";
    public const string FLD_CODEINFOREPOSITORY = "codeInfoRepository";
    public const string FLD_CALLDATA = "calldata";
    public const string FLD_MACHINECODE = "machineCode";

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

        PropertyBuilder currentProp = _contractDynamicType.EmitProperty<ILChunkExecutionState>(PROP_CURRENT_STATE, true, true, out fieldBuilder);
        Fields.Add(PROP_CURRENT_STATE, fieldBuilder);

        PropertyBuilder evmStateProp = _contractDynamicType.EmitProperty<EvmState>(PROP_EVMSTATE, true, true, out fieldBuilder);
        Fields.Add(PROP_EVMSTATE, fieldBuilder);

        PropertyBuilder tracerProp = _contractDynamicType.EmitProperty<ITxTracer>(PROP_TRACER, true, true, out fieldBuilder);
        Fields.Add(PROP_TRACER, fieldBuilder);

        PropertyBuilder loggerProp = _contractDynamicType.EmitProperty<ILogger>(PROP_LOGGER, true, true, out fieldBuilder);
        Fields.Add(PROP_LOGGER, fieldBuilder);

        PropertyBuilder specProp = _contractDynamicType.EmitProperty<IReleaseSpec>(PROP_SPEC, true, true, out fieldBuilder);
        Fields.Add(PROP_SPEC, fieldBuilder);

        PropertyBuilder worldStateProp = _contractDynamicType.EmitProperty<IWorldState>(PROP_WORLSTATE, true, true, out fieldBuilder);
        Fields.Add(PROP_WORLSTATE, fieldBuilder);

        PropertyBuilder blockhashProviderProp = _contractDynamicType.EmitProperty<IBlockhashProvider>(PROP_BLOCKHASHPROVIDER, true, true, out fieldBuilder);
        Fields.Add(PROP_BLOCKHASHPROVIDER, fieldBuilder);

        PropertyBuilder immediatesDataProp = _contractDynamicType.EmitProperty<byte[][]>(PROP_IMMEDIATESDATA, true, true, out fieldBuilder);
        Fields.Add(PROP_IMMEDIATESDATA, fieldBuilder);

        // internal fields for reuse purposes
        Fields[FLD_ENV] = _contractDynamicType.EmitField<ExecutionEnvironment>(FLD_ENV, false);

        Fields[FLD_TXCONTEXT] = _contractDynamicType.EmitField<TxExecutionContext>(FLD_TXCONTEXT, false);

        Fields[FLD_BLOCKCONTEXT] = _contractDynamicType.EmitField<BlockExecutionContext>(FLD_BLOCKCONTEXT, false);

        Fields[FLD_CODEINFOREPOSITORY] = _contractDynamicType.EmitField<ICodeInfoRepository>(FLD_CODEINFOREPOSITORY, false);

        Fields[FLD_CALLDATA] = _contractDynamicType.EmitField<ReadOnlyMemory<byte>>(FLD_CALLDATA, false);

        Fields[FLD_MACHINECODE] = _contractDynamicType.EmitField<ReadOnlyMemory<byte>>(FLD_MACHINECODE, false);
    }


    public override void LoadBlockContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[FLD_BLOCKCONTEXT]);
        else
            il.LoadField(Fields[FLD_BLOCKCONTEXT]);
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
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[FLD_CALLDATA]);
        else
            il.LoadField(Fields[FLD_CALLDATA]);
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
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[FLD_CODEINFOREPOSITORY]);
        else
            il.LoadField(Fields[FLD_CODEINFOREPOSITORY]);
    }

    public override void LoadCurrStackHead(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEADREF_INDEX);
        if (!loadAddress)
            il.LoadObject<Word>();
    }

    public override void LoadEnv(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[FLD_ENV]);
        else
            il.LoadField(Fields[FLD_ENV]);
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
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[FLD_MACHINECODE]);
        else
            il.LoadField(Fields[FLD_MACHINECODE]);
    }

    public override void LoadMemory(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.LoadField(Fields[PROP_EVMSTATE]);
        il.Call(typeof(EvmState).GetProperty(nameof(EvmState.Memory), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
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
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[FLD_TXCONTEXT]);
        else
            il.LoadField(Fields[FLD_TXCONTEXT]);
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
