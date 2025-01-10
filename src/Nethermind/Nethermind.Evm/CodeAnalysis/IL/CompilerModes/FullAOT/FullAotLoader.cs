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
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes.FullAOT;
internal class FullAotEnvLoader : EnvLoader<MoveNextDelegate>
{
    private TypeBuilder _contractDynamicType;

    public const string PROP_METADATA = "ContractMetadata";
    public const string PROP_WORLSTATE = "WorldState";
    public const string PROP_SPEC_PROVIDER = "SpecProvider";
    public const string PROP_BLOCKHASH_PROVIDER = "BlockhashProvider";
    public const string PROP_CODEINFO_REPOSITORY = "codeInfoRepository";
    public const string PROP_MACHINECODE = "machineCode";
    public const string PROP_IMMEDIATES_DATA = "ImmediatesData";

    public const int IMPLICIT_THIS_INDEX = 0;
    public const int REF_EVMSTATE_INDEX = 1;
    public const int REF_GASAVAILABLE_INDEX = 2;
    public const int REF_PROGRAMCOUNTER_INDEX = 3;
    public const int REF_STACKHEAD_INDEX = 4;
    public const int REF_STACKHEADREF_INDEX = 5;
    public const int REF_RETURNDATABUFFER_INDEX = 6;
    public const int OBJ_TXTRACER_INDEX = 7;
    public const int OBJ_LOGGER_INDEX = 8;
    public const int REF_CURRENT_STATE = 9;

    public Dictionary<string, FieldBuilder> Fields { get; } = new();

    public FullAotEnvLoader(TypeBuilder contractDynamicType, ContractMetadata contractMetadata) 
    {
        _contractDynamicType = contractDynamicType;

        FieldBuilder fieldBuilder;

        PropertyBuilder specProviderProp = _contractDynamicType.EmitProperty<IReleaseSpec>(PROP_SPEC_PROVIDER, true, true, out fieldBuilder);
        Fields.Add(PROP_SPEC_PROVIDER, fieldBuilder);

        PropertyBuilder worldStateProp = _contractDynamicType.EmitProperty<IWorldState>(PROP_WORLSTATE, true, true, out fieldBuilder);
        Fields.Add(PROP_WORLSTATE, fieldBuilder);

        PropertyBuilder blockhashProviderProp = _contractDynamicType.EmitProperty<IBlockhashProvider>(PROP_BLOCKHASH_PROVIDER, true, true, out fieldBuilder);
        Fields.Add(PROP_BLOCKHASH_PROVIDER, fieldBuilder);

        PropertyBuilder immediatesDataProp = _contractDynamicType.EmitProperty<byte[][]>(PROP_IMMEDIATES_DATA, true, true, out fieldBuilder);
        Fields.Add(PROP_IMMEDIATES_DATA, fieldBuilder);

        PropertyBuilder codeInfoRepositoryProp = _contractDynamicType.EmitProperty<ICodeInfoRepository>(PROP_CODEINFO_REPOSITORY, true, true, out fieldBuilder);
        Fields.Add(PROP_CODEINFO_REPOSITORY, fieldBuilder);

        PropertyBuilder machineCodeProp = _contractDynamicType.EmitProperty<byte[]>(PROP_MACHINECODE, true, true, out fieldBuilder);
        Fields.Add(PROP_MACHINECODE, fieldBuilder);

        FieldBuilder metadataField = _contractDynamicType.DefineField(PROP_METADATA, typeof(ContractMetadata), FieldAttributes.Public);
        Fields.Add(PROP_METADATA, metadataField);
    }


    public void CacheBlockContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals)
    {
        locals.TryDeclareLocal("blockContext", typeof(BlockExecutionContext));

        LoadBlockContext(il, locals, false);
        locals.TryStoreLocal("blockContext");
    }
    public override void LoadBlockContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        string blockContext = "blockContext";
        if (locals.TryLoadLocal(blockContext, loadAddress))
        {
            return;
        }

        using Local blockContextLocal = il.DeclareLocal<BlockExecutionContext>(blockContext);
        LoadTxContext(il, locals, false);
        if (loadAddress)
        {
            il.LoadField(typeof(TxExecutionContext).GetField(nameof(TxExecutionContext.BlockExecutionContext)));
        }
        else
        {
            il.LoadFieldAddress(typeof(TxExecutionContext).GetField(nameof(TxExecutionContext.BlockExecutionContext)));
        }
    }

    public override void LoadBlockhashProvider(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if(loadAddress)
            il.LoadFieldAddress(Fields[PROP_BLOCKHASH_PROVIDER]);
        else
            il.LoadField(Fields[PROP_BLOCKHASH_PROVIDER]);
    }

    public override void LoadCalldata(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        LoadEnv(il, locals, false);
        il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.InputData), BindingFlags.Public | BindingFlags.Instance));
        if (!loadAddress)
            il.LoadObject<ReadOnlyMemory<byte>>();
    }

    public override void LoadChainId(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        // load spec provider
        il.LoadField(Fields[PROP_SPEC_PROVIDER]);
        il.Call(typeof(ISpecProvider).GetProperty(nameof(ISpecProvider.ChainId), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());

        if(loadAddress)
        {
            il.StoreLocal(locals.uint64A);
            il.LoadLocalAddress(locals.uint64A);
        }
    }

    public override void LoadCodeInfoRepository(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(Fields[PROP_CODEINFO_REPOSITORY]);
        }
        else
        {
            il.LoadField(Fields[PROP_CODEINFO_REPOSITORY]);
        }
    }

    public override void LoadCurrStackHead(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEADREF_INDEX);
        if (!loadAddress)
            il.LoadObject<Word>();
    }

    public void CacheEnv(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals)
    {
        locals.TryDeclareLocal("env", typeof(ExecutionEnvironment));
        LoadEnv(il, locals, false);
        locals.TryStoreLocal("env");
    }

    public override void LoadEnv(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        string env = "env";

        if (locals.TryLoadLocal(env, loadAddress))
        {
            return;
        }

        LoadVmState(il, locals, false);
        il.LoadFieldAddress(typeof(EvmState).GetField(nameof(EvmState.Env), BindingFlags.Public | BindingFlags.Instance));
        if(!loadAddress)
        {
            il.LoadObject<ExecutionEnvironment>();
        }
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
            il.LoadFieldAddress(Fields[PROP_IMMEDIATES_DATA]);
        else
            il.LoadField(Fields[PROP_IMMEDIATES_DATA]);
    }

    public override void LoadLogger(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_LOGGER_INDEX);
        else
            il.LoadArgument(OBJ_LOGGER_INDEX);
    }

    public override void LoadMachineCode(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        if (loadAddress)
            il.LoadFieldAddress(Fields[PROP_MACHINECODE]);
        else
            il.LoadField(Fields[PROP_MACHINECODE]);
    }

    public override void LoadMemory(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        LoadVmState(il, locals, false);
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
        il.LoadArgument(REF_CURRENT_STATE);
        if (!loadAddress)
            il.LoadObject<ILChunkExecutionState>();
    }

    public void CacheSpec(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals)
    {
        locals.TryDeclareLocal("spec", typeof(IReleaseSpec));
        LoadSpec(il, locals, false);
        locals.TryStoreLocal("spec");
    }

    public override void LoadSpec(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        // IReleaseSpec spec = _specProvider.GetSpec(txExecutionContext.BlockExecutionContext.Header.Number, txExecutionContext.BlockExecutionContext.Header.Timestamp);
        void LoadHeader(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals)
        {
            LoadBlockContext(il, locals, false);
            il.Call(typeof(BlockExecutionContext).GetProperty(nameof(BlockExecutionContext.Header), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
        }

        if(locals.TryLoadLocal("spec", loadAddress))
        {
            return;
        }

        using Local spec = il.DeclareLocal<IReleaseSpec>();

        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.LoadField(Fields[PROP_SPEC_PROVIDER]);

        LoadHeader(il, locals);
        il.Call(typeof(SpecProviderExtensions).GetMethod(nameof(SpecProviderExtensions.GetSpec), [typeof(ISpecProvider), typeof(BlockHeader)]));
        il.StoreLocal(spec);

        if (loadAddress)
            il.LoadLocalAddress(spec);
        else
            il.LoadLocal(spec);

    }

    public override void LoadStackHead(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public void CacheTxContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals)
    {
        locals.TryDeclareLocal("txContext", typeof(TxExecutionContext));
        LoadTxContext(il, locals, false);
        locals.TryStoreLocal("txContext");
    }

    public override void LoadTxContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        string txContext = "txContext";

        if (locals.TryLoadLocal(txContext, loadAddress))
        {
            return;
        }

        il.LoadArgument(REF_EVMSTATE_INDEX);
        il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.TxExecutionContext), BindingFlags.Public | BindingFlags.Instance));
        if (!loadAddress)
            il.LoadObject<TxExecutionContext>();
    }

    public override void LoadTxTracer(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_TXTRACER_INDEX);
        else
            il.LoadArgument(OBJ_TXTRACER_INDEX);
    }

    public override void LoadVmState(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        if(loadAddress)
        {
            il.LoadArgumentAddress(REF_EVMSTATE_INDEX);
        }
        else
        {
            il.LoadArgument(REF_EVMSTATE_INDEX);
        }
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

    public override void LoadHeader(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        LoadBlockContext(il, locals, true);
        il.Call(typeof(BlockExecutionContext).GetProperty(nameof(BlockExecutionContext.Header), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
    }
}
