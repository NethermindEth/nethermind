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

    public const string PROP_SPEC_PROVIDER = "SpecProvider";
    public const string PROP_BLOCKHASH_PROVIDER = "BlockhashProvider";
    public const string PROP_CODEINFO_REPOSITORY = "codeInfoRepository";
    public const string PROP_MACHINECODE = "machineCode";
    public const string PROP_IMMEDIATES_DATA = "EmbeddedData";

    public const int IMPLICIT_THIS_INDEX = 0;
    public const int REF_EVMSTATE_INDEX = IMPLICIT_THIS_INDEX + 1;
    public const int OBJ_WORLDSTATE_INDEX = REF_EVMSTATE_INDEX + 1;
    public const int REF_GASAVAILABLE_INDEX = OBJ_WORLDSTATE_INDEX + 1;
    public const int REF_PROGRAMCOUNTER_INDEX = REF_GASAVAILABLE_INDEX + 1;
    public const int REF_STACKHEAD_INDEX = REF_PROGRAMCOUNTER_INDEX + 1;
    public const int REF_STACKHEADREF_INDEX = REF_STACKHEAD_INDEX + 1;
    public const int REF_RETURNDATABUFFER_INDEX = REF_STACKHEADREF_INDEX + 1;
    public const int OBJ_TXTRACER_INDEX = REF_RETURNDATABUFFER_INDEX + 1;
    public const int OBJ_LOGGER_INDEX = OBJ_TXTRACER_INDEX + 1;
    public const int REF_CURRENT_STATE = OBJ_LOGGER_INDEX + 1;

    public Dictionary<string, FieldBuilder> Fields { get; } = new();

    public FullAotEnvLoader(TypeBuilder contractDynamicType, ContractMetadata contractMetadata) 
    {
        _contractDynamicType = contractDynamicType;

        FieldBuilder fieldBuilder;

        PropertyBuilder specProviderProp = _contractDynamicType.EmitProperty<ISpecProvider>(PROP_SPEC_PROVIDER, true, true, out fieldBuilder);
        Fields.Add(PROP_SPEC_PROVIDER, fieldBuilder);

        PropertyBuilder blockhashProviderProp = _contractDynamicType.EmitProperty<IBlockhashProvider>(PROP_BLOCKHASH_PROVIDER, true, true, out fieldBuilder);
        Fields.Add(PROP_BLOCKHASH_PROVIDER, fieldBuilder);

        PropertyBuilder immediatesDataProp = _contractDynamicType.EmitProperty<byte[][]>(PROP_IMMEDIATES_DATA, true, true, out fieldBuilder);
        Fields.Add(PROP_IMMEDIATES_DATA, fieldBuilder);

        PropertyBuilder codeInfoRepositoryProp = _contractDynamicType.EmitProperty<ICodeInfoRepository>(PROP_CODEINFO_REPOSITORY, true, true, out fieldBuilder);
        Fields.Add(PROP_CODEINFO_REPOSITORY, fieldBuilder);

        PropertyBuilder machineCodeProp = _contractDynamicType.EmitProperty<ReadOnlyMemory<byte>>(PROP_MACHINECODE, true, true, out fieldBuilder);
        Fields.Add(PROP_MACHINECODE, fieldBuilder);
    }


    public void CacheBlockContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals)
    {
        LoadBlockContext(il, locals, false);
        locals.TryDeclareLocal("blockContext", typeof(BlockExecutionContext));
        locals.TryStoreLocal("blockContext");
    }
    public override void LoadBlockContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        string blockContext = "blockContext";
        if (locals.TryLoadLocal(blockContext, loadAddress))
        {
            return;
        }

        LoadTxContext(il, locals, false);
        il.LoadField(typeof(TxExecutionContext).GetField(nameof(TxExecutionContext.BlockExecutionContext)));
        if (loadAddress)
        {
            using Local local = il.DeclareLocal<BlockExecutionContext>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
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

    public void CacheCalldata(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals)
    {
        LoadCalldata(il, locals, false);
        locals.TryDeclareLocal("calldata", typeof(ReadOnlyMemory<byte>));
        locals.TryStoreLocal("calldata");
    }

    public override void LoadCalldata(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        string calldata = "calldata";

        if (locals.TryLoadLocal(calldata, loadAddress))
        {
            return;
        }

        LoadEnv(il, locals, false);
        il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.InputData), BindingFlags.Public | BindingFlags.Instance));
        if (loadAddress)
        {
            using Local local = il.DeclareLocal<ReadOnlyMemory<byte>>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }
    }

    public override void LoadChainId(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(IMPLICIT_THIS_INDEX);
        // load spec provider
        il.LoadField(Fields[PROP_SPEC_PROVIDER]);
        il.CallVirtual(typeof(ISpecProvider).GetProperty(nameof(ISpecProvider.ChainId), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());

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
        LoadEnv(il, locals, false);
        locals.TryDeclareLocal("env", typeof(ExecutionEnvironment));
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
        il.Call(typeof(EvmState).GetProperty(nameof(EvmState.Env), BindingFlags.Public | BindingFlags.Instance).GetMethod);
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
        LoadSpec(il, locals, false);
        locals.TryDeclareLocal("spec", typeof(IReleaseSpec));
        locals.TryStoreLocal("spec");
    }

    public override void LoadSpec(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        string spec = "spec";

        if (locals.TryLoadLocal(spec, loadAddress))
        {
            return;
        }

        il.LoadArgument(IMPLICIT_THIS_INDEX);
        il.LoadField(Fields[PROP_SPEC_PROVIDER]);

        LoadHeader(il, locals, false);
        il.Call(typeof(SpecProviderExtensions).GetMethod(nameof(SpecProviderExtensions.GetSpec), [typeof(ISpecProvider), typeof(BlockHeader)]));

        if (loadAddress)
        {
            using Local local = il.DeclareLocal<IReleaseSpec>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }

    }

    public override void LoadStackHead(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public void CacheTxContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals)
    {
        LoadTxContext(il, locals, false);
        locals.TryDeclareLocal("txContext", typeof(TxExecutionContext));
        locals.TryStoreLocal("txContext");
    }

    public override void LoadTxContext(Emit<MoveNextDelegate> il, Locals<MoveNextDelegate> locals, bool loadAddress)
    {
        string txContext = "txContext";

        if (locals.TryLoadLocal(txContext, loadAddress))
        {
            return;
        }

        LoadEnv(il, locals, true);
        il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.TxExecutionContext), BindingFlags.Public | BindingFlags.Instance));
        if (loadAddress)
        {
            using Local local = il.DeclareLocal<TxExecutionContext>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }
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
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_WORLDSTATE_INDEX);
        else
            il.LoadArgument(OBJ_WORLDSTATE_INDEX);
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
